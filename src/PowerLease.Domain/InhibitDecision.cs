namespace PowerLease.Domain;

/// <summary>
/// The result of aggregating every source's report: whether to keep the machine awake, why, and
/// what this decision does not cover.
/// </summary>
public sealed class InhibitDecision
{
    public InhibitDecision(IReadOnlyList<Inhibitor> inhibitors, IReadOnlyList<InhibitorKind> uncoveredKinds)
    {
        ArgumentNullException.ThrowIfNull(inhibitors);
        ArgumentNullException.ThrowIfNull(uncoveredKinds);

        Inhibitors = inhibitors;
        UncoveredKinds = uncoveredKinds;
    }

    /// <summary>
    /// Every reason to stay awake, in a stable order. Shown by <c>powerlease status</c>.
    /// </summary>
    public IReadOnlyList<Inhibitor> Inhibitors { get; }

    /// <summary>
    /// Inhibitor kinds no source is currently evaluating, because the rule is disabled or the
    /// capability is unavailable.
    /// <para>
    /// This is reported so the user can see the boundary of what protection actually covers. It
    /// deliberately does not force a hold: a kind the user switched off must not pin the machine
    /// awake forever. Safety against a source that <em>should</em> be reporting but is not comes
    /// from <see cref="ExpectedProducerSet" />, which produces a real inhibitor.
    /// </para>
    /// </summary>
    public IReadOnlyList<InhibitorKind> UncoveredKinds { get; }

    /// <summary>
    /// True when the machine must be kept awake.
    /// <para>
    /// Derived from <see cref="Inhibitors" /> rather than stored, so a decision listing reasons to
    /// stay awake can never also say it is safe to release.
    /// </para>
    /// </summary>
    public bool ShouldHold => Inhibitors.Count > 0;
}
