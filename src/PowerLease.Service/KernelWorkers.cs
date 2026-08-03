using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLease.Application.Hosting;
using PowerLease.Application.Kernel;
using PowerLease.Domain;

namespace PowerLease.Service;

internal sealed class LeaseCommandDispatcher
{
    private readonly KernelLoop _loop;
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);

    public LeaseCommandDispatcher(KernelLoop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
    }

    public Task<LeaseCommandResult> PostAndWaitAsync(LeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = new PendingCommand(
            command.Kind,
            command.PayloadHash,
            new TaskCompletionSource<LeaseCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously));

        var selected = _pending.GetOrAdd(command.RequestId, pending);

        if (ReferenceEquals(selected, pending))
        {
            _loop.Post(command);
        }
        else if (selected.Kind != command.Kind
            || !string.Equals(selected.PayloadHash, command.PayloadHash, StringComparison.Ordinal))
        {
            // The same identifier carrying a different request. Waiting on the one already in flight would
            // hand this caller somebody else's answer -- for a release, a success naming a lease it never
            // asked about. The idempotency record in the database enforces exactly this rule, but only once a
            // request is durable; while one is still in flight this is the only thing that can.
            return Task.FromResult(new LeaseCommandResult(
                command.RequestId,
                LeaseCommandStatus.Rejected,
                Error: "That request identifier is already in flight for a different request."));
        }

        // Cancelling a connection only stops that connection waiting. The queued command remains registered
        // and is completed durably by a later pump; disconnecting never rolls a lease operation back.
        return selected.Completion.Task.WaitAsync(cancellationToken);
    }

    public void Complete(LeaseCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_pending.TryRemove(result.RequestId, out var pending))
        {
            _ = pending.Completion.TrySetResult(result);
        }
    }

    /// <summary>
    /// One in-flight request, remembered with enough of its identity to tell a retry of the same command from
    /// a different command that happens to reuse the identifier.
    /// </summary>
    private sealed record PendingCommand(
        LeaseCommandKind Kind,
        string PayloadHash,
        TaskCompletionSource<LeaseCommandResult> Completion);
}

internal sealed class InhibitKernelWorker : BackgroundService
{
    // Fast enough that command latency remains small and well inside the source freshness window.
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(2);

    private static readonly Action<ILogger, string, Exception?> LogPumpFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, "KernelPumpFailure"),
            "The kernel pump failed and will be retried: {Message}");

    private readonly KernelLoop _loop;
    private readonly LeaseCommandDispatcher _commands;
    private readonly ILogger<InhibitKernelWorker> _logger;

    public InhibitKernelWorker(
        KernelLoop loop,
        LeaseCommandDispatcher commands,
        ILogger<InhibitKernelWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(logger);
        _loop = loop;
        _commands = commands;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _loop.PumpAsync(stoppingToken).ConfigureAwait(false);
                foreach (var command in result.CompletedCommands)
                {
                    _commands.Complete(command);
                }

                _loop.Post(new FaultHealthy("kernel-pump"));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A dead pump silently removes protection; every failure must become a fault and retry.
            catch (Exception error)
#pragma warning restore CA1031
            {
                LogPumpFailure(_logger, error.Message, error);
                _loop.Post(new FaultObserved(
                    "kernel-pump",
                    FaultSeverity.Transient,
                    $"The kernel pump failed: {error.Message}"));
            }

            try
            {
                await Task.Delay(PumpInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

internal sealed class PowerEventRelay
{
    public event EventHandler<PowerLeasePowerEventArgs>? PowerEvent;

    public void Raise(PowerLeasePowerEvent powerEvent) =>
        PowerEvent?.Invoke(this, new PowerLeasePowerEventArgs(powerEvent));
}

internal enum PowerLeasePowerEvent
{
    SuspendPending,
    Resumed
}

internal sealed class PowerLeasePowerEventArgs : EventArgs
{
    public PowerLeasePowerEventArgs(PowerLeasePowerEvent powerEvent) => PowerEvent = powerEvent;

    public PowerLeasePowerEvent PowerEvent { get; }
}

internal sealed class LifecycleWorker : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogPowerEvent =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, "PowerEvent"),
            "Windows power event: {PowerEvent}");

    private readonly KernelLoop _loop;
    private readonly PowerEventRelay _events;
    private readonly IClock _clock;
    private readonly ILogger<LifecycleWorker> _logger;

    public LifecycleWorker(
        KernelLoop loop,
        PowerEventRelay events,
        IClock clock,
        ILogger<LifecycleWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _loop = loop;
        _events = events;
        _clock = clock;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _events.PowerEvent += OnPowerEvent;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _events.PowerEvent -= OnPowerEvent;
        return Task.CompletedTask;
    }

    private void OnPowerEvent(object? sender, PowerLeasePowerEventArgs eventArgs)
    {
        // The clock and the message come first, and logging afterwards. Windows delivers this on its service
        // control thread, and the file logger writes synchronously -- so logging first would let a slow or full
        // disk delay the only notification that re-establishes protection after a resume.
        if (eventArgs.PowerEvent == PowerLeasePowerEvent.Resumed)
        {
            // Before the message, never after. Every elapsed value measured before the machine slept belongs to
            // an origin that is now meaningless, and the kernel decides a lease's remaining time by comparing
            // against this epoch. Posting first would let one evaluation run against the old origin -- which,
            // because the Windows performance counter keeps counting through sleep, reads as though the whole
            // outage came out of the lease.
            _clock.BeginNewEpoch();
            _loop.Post(new ResumedFromSleep("Windows reported that the machine resumed"));
        }

        LogPowerEvent(_logger, eventArgs.PowerEvent.ToString(), null);
    }
}
