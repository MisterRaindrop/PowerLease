using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLease.Application.Hosting;
using PowerLease.Application.Inhibitors;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows.Sources;
using PowerLease.Persistence;
using PowerLease.Persistence.Configuration;

namespace PowerLease.Service;

internal abstract class SourceProducerWorker : BackgroundService
{
    private static long _nextGeneration;

    private static readonly Action<ILogger, string, string, Exception?> LogProducerFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1, "ProducerFailure"),
            "Producer {SourceId} failed and reported uncertainty: {Message}");

    private readonly string _sourceId;
    private readonly TimeSpan _interval;
    private readonly long _sourceGeneration = Interlocked.Increment(ref _nextGeneration);
    private long _sequence;

    protected SourceProducerWorker(
        string sourceId,
        TimeSpan interval,
        KernelLoop loop,
        IClock clock,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _sourceId = sourceId;
        _interval = interval;
        Loop = loop;
        Clock = clock;
        Logger = logger;
    }

    protected KernelLoop Loop { get; }

    protected IClock Clock { get; }

    protected ILogger Logger { get; }

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProduceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A failed producer must report uncertainty and continue, never silently die.
            catch (Exception error)
#pragma warning restore CA1031
            {
                LogProducerFailure(Logger, _sourceId, error.Message, error);
                Post(InhibitorSourceReport.Indeterminate(
                    _sourceId,
                    $"The {_sourceId} producer failed: {error.Message}"));
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    protected abstract Task ProduceAsync(CancellationToken stoppingToken);

    protected void Post(InhibitorSourceReport report, MonotonicStamp? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var at = observedAt ?? Clock.Now;
        Loop.Post(new SourceObservation(
            new SourceStamp(_sourceId, _sourceGeneration, ++_sequence, at, ConfigGeneration: 0),
            report));
    }
}

internal sealed class ActivityProducerWorker : SourceProducerWorker
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly SystemActivityEvaluator _evaluator;
    private readonly SystemMetricProvider _metrics;

    public ActivityProducerWorker(
        PowerLeaseConfig config,
        KernelLoop loop,
        IClock clock,
        ILogger<ActivityProducerWorker> logger)
        : base(SystemActivityEvaluator.SourceId, Interval, loop, clock, logger)
    {
        _evaluator = ServiceComposition.CreateActivityEvaluator(config.IdleRules);
        _metrics = new SystemMetricProvider();
    }

    public override void Dispose()
    {
        _metrics.Dispose();
        base.Dispose();
    }

    protected override Task ProduceAsync(CancellationToken stoppingToken)
    {
        var now = Clock.Now;
        _evaluator.Observe(_metrics.Read(), now);
        Post(_evaluator.Evaluate(now, Clock.UtcNow), now);
        return Task.CompletedTask;
    }
}

internal sealed class ProcessProducerWorker : SourceProducerWorker
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private readonly ProtectedProcessEvaluator _evaluator;
    private readonly ProcessSnapshotProvider _processes = new();

    public ProcessProducerWorker(
        PowerLeaseConfig config,
        KernelLoop loop,
        IClock clock,
        ILogger<ProcessProducerWorker> logger)
        : base(ProtectedProcessEvaluator.SourceId, Interval, loop, clock, logger)
    {
        _evaluator = new ProtectedProcessEvaluator(new PowerLease.Application.Inhibitors.ProtectedProcessOptions
        {
            Names = config.ProtectedProcesses.Names,
            CommandLinePatterns = config.ProtectedProcesses.CommandLinePatterns
        });
    }

    protected override Task ProduceAsync(CancellationToken stoppingToken)
    {
        Post(_evaluator.Evaluate(_processes.GetProcesses(), Clock.UtcNow));
        return Task.CompletedTask;
    }
}

internal sealed class LockFileProducerWorker : SourceProducerWorker
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly LockFileEvaluator _evaluator;
    private readonly WindowsLockFileProbe _probe = new();

    public LockFileProducerWorker(
        PowerLeaseConfig config,
        PowerLeasePaths paths,
        KernelLoop loop,
        IClock clock,
        ILogger<LockFileProducerWorker> logger)
        : base(LockFileEvaluator.SourceId, Interval, loop, clock, logger)
    {
        _evaluator = new LockFileEvaluator(config.ProtectedProcesses.LockFile ?? paths.LockFilePath);
    }

    protected override Task ProduceAsync(CancellationToken stoppingToken)
    {
        Post(_evaluator.Evaluate(_probe, Clock.UtcNow));
        return Task.CompletedTask;
    }
}

internal sealed class ScheduleProducerWorker : SourceProducerWorker
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly ScheduleEvaluator _evaluator;

    public ScheduleProducerWorker(
        PowerLeaseConfig config,
        ITimeZoneProvider timeZones,
        KernelLoop loop,
        IClock clock,
        ILogger<ScheduleProducerWorker> logger)
        : base(ScheduleEvaluator.SourceId, Interval, loop, clock, logger)
    {
        _evaluator = new ScheduleEvaluator(ConfigSchedules.ToSchedule(config.Schedules), timeZones);
    }

    protected override Task ProduceAsync(CancellationToken stoppingToken)
    {
        Post(_evaluator.Evaluate(Clock.UtcNow));
        return Task.CompletedTask;
    }
}
