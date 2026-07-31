namespace PowerLease.Application.Kernel;

/// <summary>
/// A one-way switch a source can throw to force the machine awake immediately.
/// <para>
/// This is the only piece of safety state anything other than the kernel loop may touch, and it can
/// only be moved in the direction that adds protection. A source that has noticed something alarming
/// cannot wait its turn in a queue, but it also cannot be trusted to decide when the alarm is over --
/// so raising is lock-free and available to anyone, while clearing belongs to the kernel and only after
/// every expected source has reported again from a known state.
/// </para>
/// </summary>
public sealed class EmergencyInhibitLatch
{
    private const int Lowered = 0;
    private const int Raised = 1;

    private int _state;
    private string? _reason;

    public bool IsRaised => Volatile.Read(ref _state) == Raised;

    /// <summary>Why it was raised, or null when it is not raised.</summary>
    public string? Reason => Volatile.Read(ref _reason);

    /// <summary>
    /// Raise the latch. Safe to call from any thread at any time.
    /// </summary>
    /// <returns>True when this call raised it; false when it was already raised.</returns>
    public bool Raise(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        // The reason is written first so that anything seeing the raised flag also sees why. Losing the
        // race to another caller leaves the earlier reason in place, which is fine: both are true and
        // the first one is the earliest evidence.
        if (Interlocked.CompareExchange(ref _state, Raised, Lowered) != Lowered)
        {
            return false;
        }

        Volatile.Write(ref _reason, reason);
        return true;
    }

    /// <summary>
    /// Lower the latch. For the kernel only, and only once every expected source has been resynchronised
    /// -- the kernel enforces that precondition, because the latch cannot know it.
    /// </summary>
    /// <returns>True when this call lowered it.</returns>
    public bool Clear()
    {
        if (Interlocked.CompareExchange(ref _state, Lowered, Raised) != Raised)
        {
            return false;
        }

        Volatile.Write(ref _reason, null);
        return true;
    }
}
