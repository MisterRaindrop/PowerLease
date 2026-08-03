using System.ServiceProcess;
using Microsoft.Extensions.Hosting;

namespace PowerLease.Service;

/// <summary>
/// Windows service lifetime that preserves the normal Generic Host lifecycle while also surfacing
/// SERVICE_CONTROL_POWEREVENT, which the stock Windows service lifetime does not expose.
/// </summary>
internal sealed class PowerEventWindowsServiceLifetime : ServiceBase, IHostLifetime
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly PowerEventRelay _events;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _serviceStopReceived;

    public PowerEventWindowsServiceLifetime(
        IHostApplicationLifetime applicationLifetime,
        PowerEventRelay events)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(events);
        _applicationLifetime = applicationLifetime;
        _events = events;
        ServiceName = "PowerLease";
        CanHandlePowerEvent = true;
        CanShutdown = true;
    }

    public Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        var dispatcherThread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "PowerLease service dispatcher"
        };
        dispatcherThread.Start();
        return _started.Task.WaitAsync(cancellationToken);
    }

    private void RunDispatcher()
    {
        try
        {
            Run(this);
        }
#pragma warning disable CA1031 // A dispatcher startup failure must unblock and fail host startup clearly.
        catch (Exception error)
#pragma warning restore CA1031
        {
            _ = _started.TrySetException(error);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _serviceStopReceived) == 0)
        {
            Stop();
        }

        return Task.CompletedTask;
    }

    protected override void OnStart(string[] args)
    {
        _ = _started.TrySetResult();
        base.OnStart(args);
    }

    protected override void OnStop()
    {
        Interlocked.Exchange(ref _serviceStopReceived, 1);
        _applicationLifetime.StopApplication();
        base.OnStop();
    }

    protected override void OnShutdown()
    {
        Interlocked.Exchange(ref _serviceStopReceived, 1);
        _applicationLifetime.StopApplication();
        base.OnShutdown();
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _events.Raise(PowerLeasePowerEvent.SuspendPending);
                break;

            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
                _events.Raise(PowerLeasePowerEvent.Resumed);
                break;
        }

        return true;
    }
}
