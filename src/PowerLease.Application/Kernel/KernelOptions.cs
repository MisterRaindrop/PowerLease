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
    /// How often a running lease's remaining time is written to disk.
    /// <para>
    /// This is what makes a restart honest. A lease is re-established from its stored checkpoint, and the
    /// checkpoint is the only evidence of how much of it is left -- so if it were written only when the lease
    /// was created or renewed, every restart would hand back the duration the lease began with. On a machine
    /// that crashes and restarts, a three-hour hold would never end.
    /// </para>
    /// <para>
    /// The interval is the amount of lease progress a crash may lose, and losing progress means holding for
    /// longer, which is the safe direction. Set it to zero to stop writing checkpoints, which is only
    /// sensible in a test.
    /// </para>
    /// </summary>
    public TimeSpan LeaseCheckpointInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sources that must be reporting for a release to mean anything. Anything in this set that goes quiet
    /// holds the machine awake; anything missing from it is simply never consulted.
    /// <para>
    /// Required, with no default, on purpose. Before a source has reported for the first time it contributes
    /// nothing, so a kernel that expects nothing and has heard nothing gathers only confirmed absences and
    /// concludes there is no reason to stay awake -- having heard from nobody at all. That is the correct
    /// answer for a build genuinely watching nothing, and a silent fail-open for a host whose producers have
    /// not finished starting. Making it required turns the difference into something the compiler asks about
    /// rather than something a host can omit by accident.
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> ExpectedSources { get; init; }

    /// <summary>
    /// The inhibitor kinds some source in this configuration is actually evaluating. Reported so the
    /// user can see the boundary of what protection covers.
    /// </summary>
    public IReadOnlyList<Domain.InhibitorKind> CoveredKinds { get; init; } = [];
}
