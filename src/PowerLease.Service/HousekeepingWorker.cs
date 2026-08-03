using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLease.Domain;
using PowerLease.Persistence.Configuration;
using PowerLease.Persistence.History;

namespace PowerLease.Service;

internal sealed class HousekeepingWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    private static readonly Action<ILogger, string, Exception?> LogHousekeepingFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, "HousekeepingFailure"),
            "History housekeeping failed and will be retried: {Message}");

    private readonly MinuteAggregator _minutes;
    private readonly HourAggregator _hours;
    private readonly RetentionPolicy _retention;
    private readonly StoreGate _gate;
    private readonly IClock _clock;
    private readonly ILogger<HousekeepingWorker> _logger;

    public HousekeepingWorker(
        SqliteHistoryStore store,
        PowerLeaseConfig config,
        StoreGate gate,
        IClock clock,
        ILogger<HousekeepingWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _minutes = new MinuteAggregator(store, SampleInterval);
        _hours = new HourAggregator(store);
        _retention = new RetentionPolicy(store, config.Retention);
        _gate = gate;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                lock (_gate.SyncRoot)
                {
                    _ = _minutes.Run(_clock.UtcNow);
                    _ = _hours.Run();
                    _ = _retention.Apply(_clock.UtcNow);
                }
            }
#pragma warning disable CA1031 // Housekeeping is best-effort and must not terminate the service host.
            catch (Exception error)
#pragma warning restore CA1031
            {
                LogHousekeepingFailure(_logger, error.Message, error);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
