namespace PowerLease.Domain;

/// <summary>
/// Combines the reports from every inhibitor source into one decision.
/// <para>
/// The rule is OR: any inhibitor from any source keeps the machine awake, and protection is
/// released only when every source positively confirmed absence. This makes the aggregation
/// monotone -- a source can only ever add protection, never remove another source's -- which is
/// what makes the one dangerous outcome, releasing too early, impossible to reach by accident.
/// </para>
/// <para>
/// Uncertainty is not a separate flag. A source that could not determine its state is turned into
/// a <see cref="InhibitorKind.ProducerUnhealthy" /> inhibitor, so "if unsure, stay awake" falls out
/// of the same OR instead of being a rule someone could forget to apply.
/// </para>
/// </summary>
public static class InhibitAggregator
{
    /// <param name="reports">One report per source that ran this cycle.</param>
    /// <param name="coveredKinds">
    /// The inhibitor kinds some source is actually evaluating. Everything else is reported in
    /// <see cref="InhibitDecision.UncoveredKinds" />.
    /// </param>
    /// <param name="nowUtc">Timestamp given to inhibitors synthesised for indeterminate sources.</param>
    public static InhibitDecision Aggregate(
        IEnumerable<InhibitorSourceReport> reports,
        IEnumerable<InhibitorKind> coveredKinds,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(coveredKinds);

        var collected = new List<Inhibitor>();

        foreach (var report in reports)
        {
            if (report is null)
            {
                throw new ArgumentException("A null report was supplied.", nameof(reports));
            }

            collected.AddRange(report.Inhibitors);

            if (report.IndeterminateReason is { } reason)
            {
                collected.Add(new Inhibitor(
                    InhibitorKind.ProducerUnhealthy,
                    reason,
                    nowUtc,
                    report.SourceId));
            }
        }

        // A total order over every field, so the same inputs always produce the same list. Status
        // output that reshuffles between polls reads like something changed when nothing did.
        collected.Sort(Compare);

        var covered = new HashSet<InhibitorKind>(coveredKinds);
        var uncovered = new List<InhibitorKind>();
        foreach (var kind in Enum.GetValues<InhibitorKind>())
        {
            if (!covered.Contains(kind))
            {
                uncovered.Add(kind);
            }
        }

        return new InhibitDecision(collected, uncovered);
    }

    private static int Compare(Inhibitor left, Inhibitor right)
    {
        var byKind = left.Kind.CompareTo(right.Kind);
        if (byKind != 0)
        {
            return byKind;
        }

        var bySince = left.SinceUtc.CompareTo(right.SinceUtc);
        if (bySince != 0)
        {
            return bySince;
        }

        var byReason = string.CompareOrdinal(left.Reason, right.Reason);
        return byReason != 0 ? byReason : string.CompareOrdinal(left.Detail, right.Detail);
    }
}
