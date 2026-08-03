namespace PowerLease.Application.Kernel;

/// <summary>
/// The stored leases the kernel could safely restore, and the active rows it could not use.
/// <para>
/// Invalid rows are returned rather than thrown because restore is a startup path. The host must latch a
/// fault when <see cref="HasInvalidRows" /> is true: skipping an active lease loses protection, while the
/// fault replaces that lost inhibitor and keeps the failure visible.
/// </para>
/// </summary>
public sealed record LeaseRestoreResult(
    IReadOnlyList<string> RestoredLeaseIds,
    IReadOnlyList<string> InvalidRows)
{
    public bool HasInvalidRows => InvalidRows.Count > 0;
}
