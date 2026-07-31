namespace PowerLease.Domain;

/// <summary>
/// What one source has to say about the inhibitors it is responsible for observing.
/// <para>
/// A source can say one of three things, and the distinction is the whole safety model:
/// it observed inhibitors (<see cref="Observed" />), it positively confirmed there are none
/// (<see cref="ConfirmedAbsent" />), or it could not tell (<see cref="Indeterminate" />).
/// Only the second one permits releasing protection, so it is a named factory rather than an
/// empty list: every place in the product that claims "nothing is happening" is greppable.
/// </para>
/// </summary>
public sealed class InhibitorSourceReport
{
    private InhibitorSourceReport(string sourceId, IReadOnlyList<Inhibitor> inhibitors, string? indeterminateReason)
    {
        SourceId = sourceId;
        Inhibitors = inhibitors;
        IndeterminateReason = indeterminateReason;
    }

    /// <summary>Identifier of the producer that made this observation.</summary>
    public string SourceId { get; }

    /// <summary>Inhibitors this source observed. Empty when the source confirmed their absence.</summary>
    public IReadOnlyList<Inhibitor> Inhibitors { get; }

    /// <summary>Why the source could not determine its state, or null when it could.</summary>
    public string? IndeterminateReason { get; }

    /// <summary>
    /// True when this report can be taken at face value. Derived from
    /// <see cref="IndeterminateReason" />, so an indeterminate report always carries a reason and
    /// a determinate one never does.
    /// </summary>
    public bool IsDeterminate => IndeterminateReason is null;

    /// <summary>
    /// The source observed at least one inhibitor.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="inhibitors" /> is empty. An empty observation would silently
    /// mean the same thing as <see cref="ConfirmedAbsent" />, which is the only report that can
    /// release protection; claiming absence has to be deliberate.
    /// </exception>
    public static InhibitorSourceReport Observed(string sourceId, params Inhibitor[] inhibitors)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentNullException.ThrowIfNull(inhibitors);

        if (inhibitors.Length == 0)
        {
            throw new ArgumentException(
                $"Source '{sourceId}' reported no inhibitors. Use {nameof(ConfirmedAbsent)} to state " +
                "positively that there are none, or Indeterminate if it could not tell.",
                nameof(inhibitors));
        }

        var copy = new Inhibitor[inhibitors.Length];
        for (var i = 0; i < inhibitors.Length; i++)
        {
            copy[i] = inhibitors[i] ?? throw new ArgumentException(
                $"Source '{sourceId}' reported a null inhibitor at index {i}.", nameof(inhibitors));
        }

        return new InhibitorSourceReport(sourceId, copy, indeterminateReason: null);
    }

    /// <summary>
    /// The source positively confirmed that none of the inhibitors it watches are present.
    /// <para>
    /// This is the only report that contributes nothing to the aggregate, and therefore the only
    /// one that lets protection be released. Use it only when absence was actually established,
    /// never when a read failed or returned nothing.
    /// </para>
    /// </summary>
    public static InhibitorSourceReport ConfirmedAbsent(string sourceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        return new InhibitorSourceReport(sourceId, [], indeterminateReason: null);
    }

    /// <summary>
    /// The source could not determine its state: a read failed, its data is stale, or it has not
    /// reported yet. The aggregator turns this into an inhibitor, so the machine stays awake.
    /// </summary>
    public static InhibitorSourceReport Indeterminate(string sourceId, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new InhibitorSourceReport(sourceId, [], reason);
    }
}
