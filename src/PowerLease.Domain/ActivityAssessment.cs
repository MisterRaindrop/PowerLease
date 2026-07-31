namespace PowerLease.Domain;

/// <summary>
/// A metric's verdict and how long it has been quiet, which <c>powerlease status</c> shows as
/// progress towards the required quiet duration.
/// </summary>
public sealed record ActivityAssessment(ActivityVerdict Verdict, TimeSpan QuietFor)
{
    /// <summary>
    /// True when this metric keeps the machine awake. Anything other than
    /// <see cref="ActivityVerdict.QuietLongEnough" /> inhibits, so a new verdict added later
    /// defaults to holding rather than to releasing.
    /// </summary>
    public bool Inhibits => Verdict != ActivityVerdict.QuietLongEnough;
}
