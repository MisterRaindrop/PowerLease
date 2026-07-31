using PowerLease.Domain;

namespace PowerLease.Persistence.History;

/// <summary>
/// The leases that could be read, and the rows that could not.
/// <para>
/// The two are returned together because the caller has to act on both. Skipping an unreadable row is the
/// only way to keep one bad record from making every lease unloadable, but a skipped lease is protection
/// silently lost -- so the caller must latch a fault for it, and a latched fault is itself a reason to keep
/// the machine awake. Reporting the skip is what keeps the failure pointing in the safe direction.
/// </para>
/// </summary>
public sealed record LeaseLoadResult(IReadOnlyList<KeepAwakeLease> Leases, IReadOnlyList<string> UnreadableRows)
{
    public bool HasUnreadableRows => UnreadableRows.Count > 0;
}
