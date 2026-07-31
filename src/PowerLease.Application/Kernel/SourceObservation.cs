using PowerLease.Domain;

namespace PowerLease.Application.Kernel;

/// <summary>What a source saw, and enough provenance to decide whether to believe it.</summary>
public sealed record SourceObservation(SourceStamp Stamp, InhibitorSourceReport Report);

/// <summary>
/// What the kernel did with an arriving observation.
/// <para>
/// There are only two safe things to do with an observation that cannot be trusted, and which one
/// applies depends on what it would leave behind. An observation that is a duplicate or arrives out of
/// order is discarded, because what is already held is newer and therefore not stale. Anything else --
/// a gap, a different clock epoch, configuration that has since been replaced -- replaces the source's
/// state with "cannot tell". Discarding those would leave an older report in place, and if that older
/// report said nothing was happening, the kernel would go on releasing protection on the strength of
/// evidence that has expired.
/// </para>
/// </summary>
public enum ObservationDisposition
{
    /// <summary>Believed, and now the source's current state.</summary>
    Accepted,

    /// <summary>Already seen. What is held is at least as new, so nothing is lost by ignoring it.</summary>
    DiscardedDuplicate,

    /// <summary>Older than what is held. Ignored for the same reason.</summary>
    DiscardedOutOfOrder,

    /// <summary>
    /// Observations were missed, so the source may be dropping messages. This one is not acted on, and
    /// the source counts as unable to say anything until it reports contiguously again.
    /// </summary>
    DistrustedSequenceGap,

    /// <summary>
    /// Taken against a different monotonic clock, so it predates a resume or a restart of the source.
    /// Its age cannot even be computed.
    /// </summary>
    DistrustedClockEpoch,

    /// <summary>Computed under configuration that has since been replaced.</summary>
    DistrustedConfigGeneration
}
