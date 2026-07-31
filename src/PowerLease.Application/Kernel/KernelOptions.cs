namespace PowerLease.Application.Kernel;

/// <summary>
/// The limits the kernel judges its inputs by.
/// <para>
/// Every one of these has the same shape: how much doubt is tolerated before a source stops being
/// believed. They are deliberately not generous. The only dangerous outcome in this product is
/// releasing protection too early, and every one of these thresholds guards against exactly that.
/// </para>
/// </summary>
public sealed record KernelOptions
{
    /// <summary>
    /// How old an observation may be and still be acted on. Past this the source is treated as unable
    /// to say anything, because a stale report that nothing is happening is the one input that can
    /// release protection while the machine is in use.
    /// </summary>
    public TimeSpan ObservationFreshness { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a source may go without reporting before it counts as gone.</summary>
    public TimeSpan HeartbeatFreshness { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long protection is held unconditionally after the machine resumes. Nothing is known about
    /// what happened while it was asleep, and every sample taken before it is meaningless.
    /// </summary>
    public TimeSpan ResumeGracePeriod { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long protection is held unconditionally after the service starts. The operating system drops
    /// the power request when the process dies, so the machine was unprotected until now and no source
    /// has reported yet.
    /// </summary>
    public TimeSpan StartupGracePeriod { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How many consecutive healthy observations clear a transient fault.</summary>
    public int ConsecutiveHealthyToClearTransient { get; init; } = 3;

    /// <summary>
    /// Sources that must be reporting for a release to mean anything. Anything missing from this set is
    /// simply not consulted; anything in it that goes quiet holds the machine awake.
    /// </summary>
    public IReadOnlyList<string> ExpectedSources { get; init; } = [];

    /// <summary>
    /// The inhibitor kinds some source in this configuration is actually evaluating. Reported so the
    /// user can see the boundary of what protection covers.
    /// </summary>
    public IReadOnlyList<Domain.InhibitorKind> CoveredKinds { get; init; } = [];
}
