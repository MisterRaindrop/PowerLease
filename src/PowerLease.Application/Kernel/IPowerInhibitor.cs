namespace PowerLease.Application.Kernel;

public enum PowerInhibitResult
{
    /// <summary>The system accepted the request and a handle is outstanding.</summary>
    Held,

    /// <summary>
    /// The system refused it and no handle was created. Usually the active power plan has the
    /// system-required capability switched off, in which case the request can never work until the user
    /// changes it.
    /// </summary>
    Rejected,

    /// <summary>
    /// It is not known whether the request took effect. The caller must close the whole handle for this
    /// generation rather than guess.
    /// </summary>
    Uncertain
}

/// <summary>
/// Holding the machine awake.
/// <para>
/// The underlying Windows call is reference counted, which is the trap this interface exists to close.
/// Every acquisition gets its own handle identified by a generation, so a failure never leaves the
/// kernel guessing how many times to clear an existing one -- it closes that generation outright and
/// starts a new one.
/// </para>
/// </summary>
public interface IPowerInhibitor
{
    /// <summary>
    /// Take out a request on a fresh handle.
    /// </summary>
    /// <remarks>
    /// Implementations must create a new handle per call, and must guarantee that
    /// <see cref="PowerInhibitResult.Rejected" /> means no handle exists, so that the caller knows there
    /// is nothing to close.
    /// </remarks>
    PowerInhibitResult Acquire(long generation);

    /// <summary>
    /// Close the whole handle belonging to <paramref name="generation" />. Must be safe to call for a
    /// generation whose acquisition outcome was never established.
    /// </summary>
    void Close(long generation);
}
