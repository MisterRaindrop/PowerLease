namespace PowerLease.Domain;

/// <summary>
/// Decides whether a metric such as CPU, disk or network throughput has been quiet long enough to
/// stop counting as a reason to stay awake.
/// <para>
/// This exists because Windows' own idle timer does not notice a long compile, a Docker build or a
/// busy database: nobody is touching the keyboard, so as far as Windows is concerned the machine is
/// idle. The direction is deliberately asymmetric. A single measurement above the threshold is
/// enough to keep the machine awake, while releasing requires continuous evidence of quiet across
/// the whole required duration.
/// </para>
/// </summary>
public sealed class DurationThresholdTracker
{
    private readonly SlidingWindow<double> _window;
    private readonly double _threshold;
    private readonly TimeSpan _requiredQuietDuration;
    private readonly TimeSpan _maxSampleGap;

    /// <param name="threshold">Measurements at or below this count as quiet.</param>
    /// <param name="requiredQuietDuration">
    /// How long the metric must stay quiet before it stops inhibiting.
    /// </param>
    /// <param name="maxSampleGap">
    /// The longest tolerated distance between consecutive samples, and between the newest sample and
    /// now. A larger gap could have hidden a burst of activity, so quiet cannot be claimed across it.
    /// </param>
    public DurationThresholdTracker(double threshold, TimeSpan requiredQuietDuration, TimeSpan maxSampleGap)
    {
        // NaN compares false against everything, so a NaN threshold would classify every sample as
        // quiet and release protection. Reject it rather than fail open.
        if (double.IsNaN(threshold))
        {
            throw new ArgumentException("Threshold must be a number.", nameof(threshold));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requiredQuietDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSampleGap, TimeSpan.Zero);

        _threshold = threshold;
        _requiredQuietDuration = requiredQuietDuration;
        _maxSampleGap = maxSampleGap;

        // Retain one gap beyond the required duration so a sample can exist at or before the start
        // of the window, which is what establishes that the whole window is covered.
        _window = new SlidingWindow<double>(requiredQuietDuration + maxSampleGap);
    }

    /// <summary>
    /// How many times the sample history has been discarded because of an epoch change or an
    /// out-of-order sample.
    /// </summary>
    public int DiscontinuityCount => _window.DiscontinuityCount;

    public int SampleCount => _window.Count;

    /// <summary>Record a measurement.</summary>
    /// <exception cref="ArgumentException">
    /// Thrown for NaN, which would compare below any threshold and be counted as quiet.
    /// </exception>
    public void Observe(MonotonicStamp at, double value)
    {
        if (double.IsNaN(value))
        {
            throw new ArgumentException("Sample value must be a number.", nameof(value));
        }

        _window.Add(at, value);
    }

    /// <summary>
    /// Judge the metric as of <paramref name="now" />.
    /// </summary>
    public ActivityAssessment Assess(MonotonicStamp now)
    {
        _window.Prune(now);

        var samples = _window.Samples;
        if (samples.Count == 0)
        {
            return new ActivityAssessment(ActivityVerdict.InsufficientData, TimeSpan.Zero);
        }

        var newest = samples[^1];
        if (now.Elapsed - newest.At.Elapsed > _maxSampleGap)
        {
            // The source has gone quiet in the wrong sense: it stopped reporting. Anything could
            // have happened since, so nothing can be concluded.
            return new ActivityAssessment(ActivityVerdict.InsufficientData, TimeSpan.Zero);
        }

        // Walk backwards from the newest sample for as long as the evidence is unbroken: every
        // sample at or below the threshold, and no gap wide enough to have hidden a burst. Where the
        // walk stops is the earliest instant quiet can be claimed from.
        var quietStart = newest.At.Elapsed;
        var previous = now.Elapsed;
        var sawActivity = false;

        for (var i = samples.Count - 1; i >= 0; i--)
        {
            var sample = samples[i];

            if (sample.Value > _threshold)
            {
                sawActivity = true;
                break;
            }

            if (previous - sample.At.Elapsed > _maxSampleGap)
            {
                break;
            }

            quietStart = sample.At.Elapsed;
            previous = sample.At.Elapsed;
        }

        var quietFor = now.Elapsed - quietStart;
        if (quietFor >= _requiredQuietDuration)
        {
            return new ActivityAssessment(ActivityVerdict.QuietLongEnough, quietFor);
        }

        return new ActivityAssessment(
            sawActivity ? ActivityVerdict.Active : ActivityVerdict.InsufficientData,
            quietFor);
    }
}
