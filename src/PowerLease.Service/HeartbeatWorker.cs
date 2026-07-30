using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLease.Domain;

namespace PowerLease.Service;

public sealed class HeartbeatWorker : BackgroundService
{
    private static readonly Action<ILogger, DateTimeOffset, TimeSpan, Exception?> LogHeartbeat =
        LoggerMessage.Define<DateTimeOffset, TimeSpan>(
            LogLevel.Information,
            new EventId(1, "Heartbeat"),
            "Heartbeat at {UtcNow}; monotonic elapsed {Elapsed}");

    private readonly ILogger<HeartbeatWorker> _logger;
    private readonly IClock _clock;
    private readonly TimeSpan _interval;

    public HeartbeatWorker(ILogger<HeartbeatWorker> logger, IClock clock)
        : this(logger, clock, TimeSpan.FromSeconds(30))
    {
    }

    public HeartbeatWorker(ILogger<HeartbeatWorker> logger, IClock clock, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);

        _logger = logger;
        _clock = clock;
        _interval = interval;

        if (_interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Heartbeat interval must be positive.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    LogHeartbeat(_logger, _clock.UtcNow, _clock.Now.Elapsed, null);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancellation is the normal stopping path for this hosted worker.
        }
    }
}
