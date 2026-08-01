using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

/// <summary>
/// Keeps exactly one power request outstanding, or none.
/// <para>
/// Holding the machine awake is the entire product, so whether the request actually took effect is a
/// first-class outcome rather than something assumed. When the system refuses the request the state
/// becomes <see cref="ProtectionState.Unprotected" />: the kernel wanted to hold and could not, which
/// means the machine may sleep while someone is connected to it, and that has to be said out loud
/// rather than hidden behind a request that silently did nothing.
/// </para>
/// </summary>
public sealed class PowerInhibitCoordinator
{
    private readonly IPowerInhibitor _inhibitor;
    private long _generation;
    private long? _heldGeneration;

    public PowerInhibitCoordinator(IPowerInhibitor inhibitor)
    {
        ArgumentNullException.ThrowIfNull(inhibitor);
        _inhibitor = inhibitor;
    }

    public ProtectionState State { get; private set; } = ProtectionState.Released;

    /// <summary>The generation of the outstanding request, or null when nothing is held.</summary>
    public long? HeldGeneration => _heldGeneration;

    /// <summary>How many times a request has been taken out, for diagnostics and tests.</summary>
    public int AcquireCount { get; private set; }

    /// <summary>
    /// Bring the outstanding request into line with <paramref name="shouldHold" />.
    /// </summary>
    public ProtectionState Ensure(bool shouldHold)
    {
        if (!shouldHold)
        {
            CloseIfHeld();
            State = ProtectionState.Released;
            return State;
        }

        if (_heldGeneration is not null)
        {
            // Already held. Asking again must not take out a second request: the underlying call is
            // reference counted, and a count that has drifted upwards can never be brought back to zero
            // reliably, which would leave the machine pinned awake for as long as the service runs.
            return State;
        }

        var generation = ++_generation;
        AcquireCount++;

        switch (Attempt(generation))
        {
            case PowerInhibitResult.Held:
                _heldGeneration = generation;
                State = ProtectionState.Protected;
                break;

            case PowerInhibitResult.Rejected:
                // No handle exists, so there is nothing to close. Wanting to hold and being refused is
                // not the same as not wanting to hold.
                State = ProtectionState.Unprotected;
                break;

            default:
                // It may or may not have taken effect. Close this generation outright rather than
                // guessing how many clears the reference count needs, and let the next cycle try again
                // on a fresh generation.
                _heldGeneration = generation;
                CloseIfHeld();
                State = ProtectionState.Unprotected;
                break;
        }

        return State;
    }

    /// <summary>
    /// Forget any outstanding request without assuming it is still valid.
    /// <para>
    /// Used after a resume, when the operating system has already dropped the request underneath us.
    /// The handle is closed anyway, because closing one the system has discarded is harmless while
    /// leaving one open that we think is held is not.
    /// </para>
    /// </summary>
    public void Invalidate()
    {
        CloseIfHeld();
        State = ProtectionState.Released;
    }

    /// <summary>
    /// Ask the adapter to take out a request, treating a throw as an uncertain outcome.
    /// <para>
    /// The adapter is meant not to throw, but an exception escaping here would leave the kernel unable to latch
    /// its power-request fault and unable to try again. Uncertain is the right reading of a throw anyway: it is
    /// not known whether the request took effect, so the handle is closed outright.
    /// </para>
    /// </summary>
    private PowerInhibitResult Attempt(long generation)
    {
        try
        {
            return _inhibitor.Acquire(generation);
        }
#pragma warning disable CA1031 // Any adapter failure must become a state, never an escape.
        catch (Exception)
#pragma warning restore CA1031
        {
            return PowerInhibitResult.Uncertain;
        }
    }

    /// <summary>
    /// Let go of the outstanding request.
    /// <para>
    /// The generation is cleared whatever happens. If Close released the request and then threw, keeping the
    /// generation would make every later Ensure return early without reacquiring -- the machine reporting itself
    /// protected while holding nothing, which is the worst of the available outcomes. Forgetting a handle that
    /// was not actually closed leaks one request for the life of the process; that only keeps the machine awake.
    /// </para>
    /// </summary>
    private void CloseIfHeld()
    {
        if (_heldGeneration is not { } generation)
        {
            return;
        }

        try
        {
            _inhibitor.Close(generation);
        }
#pragma warning disable CA1031 // Any adapter failure must become a state, never an escape.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Nothing to do but stop believing the request is held.
        }
        finally
        {
            _heldGeneration = null;
        }
    }
}
