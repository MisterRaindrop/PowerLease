using PowerLease.Application.Inhibitors;
using PowerLease.Domain;
using Xunit;

namespace PowerLease.Application.Tests;

internal sealed class FakeLockFileProbe : ILockFileProbe
{
    public LockFileProbe Result { get; set; } = LockFileProbe.Absent();

    public string? LastPath { get; private set; }

    public LockFileProbe Check(string path)
    {
        LastPath = path;
        return Result;
    }
}

internal sealed class FakeTimeZoneProvider : ITimeZoneProvider
{
    public FakeTimeZoneProvider(TimeZoneInfo zone) => Zone = zone;

    public TimeZoneInfo Zone { get; set; }

    public Exception? Throws { get; set; }

    public TimeZoneInfo Current => Throws is null ? Zone : throw Throws;
}

public sealed class ProtectedProcessEvaluatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static ProtectedProcessEvaluator Evaluator(
        IReadOnlyList<string>? names = null,
        IReadOnlyList<string>? patterns = null) =>
        new(new ProtectedProcessOptions { Names = names ?? [], CommandLinePatterns = patterns ?? [] });

    [Fact]
    public void A_matching_process_name_holds_the_machine_awake()
    {
        var report = Evaluator(names: ["msbuild"]).Evaluate(
            ProcessSnapshot.Of(new ProcessInfo(100, "MSBuild", "msbuild /t:Build")),
            Noon);

        var inhibitor = Assert.Single(report.Inhibitors);
        Assert.Equal(InhibitorKind.ProtectedProcess, inhibitor.Kind);
        Assert.Equal("MSBuild:100", inhibitor.Detail);
    }

    [Fact]
    public void A_process_nobody_asked_about_is_not_a_reason_to_stay_awake()
    {
        var report = Evaluator(names: ["msbuild"]).Evaluate(
            ProcessSnapshot.Of(new ProcessInfo(100, "notepad", "notepad")),
            Noon);

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void A_matching_command_line_holds_the_machine_awake()
    {
        var report = Evaluator(patterns: ["dotnet build"]).Evaluate(
            ProcessSnapshot.Of(new ProcessInfo(100, "dotnet", "dotnet build -c Release")),
            Noon);

        Assert.Contains("dotnet build", Assert.Single(report.Inhibitors).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_line_that_could_not_be_read_is_treated_as_a_match()
    {
        // It might be the four-hour build the user is protecting, and there is no way to tell. Keeping the
        // machine awake unnecessarily costs some electricity; sleeping through the build costs the build.
        var report = Evaluator(patterns: ["dotnet build"]).Evaluate(
            ProcessSnapshot.Of(new ProcessInfo(100, "dotnet", CommandLine: null)),
            Noon);

        var inhibitor = Assert.Single(report.Inhibitors);
        Assert.Contains("could not be read", inhibitor.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_command_line_does_not_matter_when_no_rule_needs_one()
    {
        // Only rules that depend on the command line are affected. A name rule can still be decided.
        var report = Evaluator(names: ["msbuild"]).Evaluate(
            ProcessSnapshot.Of(new ProcessInfo(100, "dotnet", CommandLine: null)),
            Noon);

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void A_process_list_that_could_not_be_read_is_never_taken_as_nothing_running()
    {
        var report = Evaluator(names: ["msbuild"]).Evaluate(
            ProcessSnapshot.Unavailable("access denied"),
            Noon);

        Assert.False(report.IsDeterminate);
    }

    [Fact]
    public void With_no_rules_configured_there_is_genuinely_nothing_to_hold_for()
    {
        // And a failed listing does not matter either, because nothing was going to be looked for.
        Assert.True(Evaluator().Evaluate(ProcessSnapshot.Unavailable("access denied"), Noon).IsDeterminate);
    }

    [Fact]
    public void Names_and_patterns_are_matched_without_regard_to_case()
    {
        Assert.NotEmpty(Evaluator(names: ["MSBUILD"])
            .Evaluate(ProcessSnapshot.Of(new ProcessInfo(1, "msbuild", null)), Noon).Inhibitors);
        Assert.NotEmpty(Evaluator(patterns: ["DOTNET BUILD"])
            .Evaluate(ProcessSnapshot.Of(new ProcessInfo(1, "dotnet", "dotnet build")), Noon).Inhibitors);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        Assert.Throws<ArgumentNullException>(() => new ProtectedProcessEvaluator(null!));
        Assert.Throws<ArgumentNullException>(() => Evaluator().Evaluate(null!, Noon));
    }
}

public sealed class LockFileEvaluatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private const string Path = @"C:\ProgramData\PowerLease\keep-awake.lock";

    [Fact]
    public void A_lock_file_that_exists_holds_the_machine_awake()
    {
        var probe = new FakeLockFileProbe { Result = LockFileProbe.Present() };

        var report = new LockFileEvaluator(Path).Evaluate(probe, Noon);

        Assert.Equal(InhibitorKind.LockFile, Assert.Single(report.Inhibitors).Kind);
        Assert.Equal(Path, probe.LastPath);
    }

    [Fact]
    public void A_lock_file_that_is_not_there_is_not_a_reason_to_stay_awake()
    {
        var report = new LockFileEvaluator(Path).Evaluate(new FakeLockFileProbe(), Noon);

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void A_check_that_failed_is_not_evidence_the_file_is_gone()
    {
        // Someone may have created it precisely because they are about to start something long.
        var probe = new FakeLockFileProbe { Result = LockFileProbe.Unavailable("the path is unreachable") };

        var report = new LockFileEvaluator(Path).Evaluate(probe, Noon);

        Assert.False(report.IsDeterminate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_path_configured_the_rule_is_off(string? path)
    {
        var probe = new FakeLockFileProbe { Result = LockFileProbe.Present() };

        var report = new LockFileEvaluator(path).Evaluate(probe, Noon);

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
        Assert.Null(probe.LastPath);
    }

    [Fact]
    public void A_probe_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => new LockFileEvaluator(Path).Evaluate(null!, Noon));
    }
}

public sealed class ScheduleEvaluatorTests
{
    private static readonly TimeZoneInfo Plus8 = TimeZoneInfo.CreateCustomTimeZone(
        "PowerLease Test +08", TimeSpan.FromHours(8), "PowerLease Test +08", "PLT8");

    private static Schedule Workdays() =>
        new([new TimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0), DayOfWeekSet.Weekdays)]);

    private static DateTimeOffset Utc(int hour) => new(2026, 7, 29, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Inside_a_guaranteed_awake_window_the_machine_is_held()
    {
        // 2026-07-29 is a Wednesday; 02:00 UTC is 10:00 local in +08:00.
        var report = new ScheduleEvaluator(Workdays(), new FakeTimeZoneProvider(Plus8)).Evaluate(Utc(2));

        var inhibitor = Assert.Single(report.Inhibitors);
        Assert.Equal(InhibitorKind.ScheduleWindow, inhibitor.Kind);
        Assert.Contains("09:00-18:00", inhibitor.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_the_window_the_schedule_has_nothing_to_say()
    {
        var report = new ScheduleEvaluator(Workdays(), new FakeTimeZoneProvider(Plus8)).Evaluate(Utc(0));

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void With_no_windows_configured_the_rule_is_off()
    {
        var provider = new FakeTimeZoneProvider(Plus8) { Throws = new TimeZoneNotFoundException("never asked") };

        var report = new ScheduleEvaluator(Schedule.Empty, provider).Evaluate(Utc(2));

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void A_time_zone_that_cannot_be_determined_holds_the_machine_awake()
    {
        // A schedule that cannot be placed on a clock cannot be honoured, and the window the user asked for may
        // be running right now.
        var provider = new FakeTimeZoneProvider(Plus8) { Throws = new TimeZoneNotFoundException("no zone data") };

        var report = new ScheduleEvaluator(Workdays(), provider).Evaluate(Utc(2));

        Assert.False(report.IsDeterminate);
        Assert.Contains("time zone", report.IndeterminateReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_time_zone_that_is_not_usable_holds_the_machine_awake()
    {
        var provider = new FakeTimeZoneProvider(Plus8) { Throws = new InvalidTimeZoneException("corrupt rules") };

        Assert.False(new ScheduleEvaluator(Workdays(), provider).Evaluate(Utc(2)).IsDeterminate);
    }

    [Fact]
    public void Changing_the_time_zone_changes_the_answer_on_the_next_evaluation()
    {
        // Read through the provider every time, not captured once: a machine that travels must start honouring
        // the new zone straight away.
        var provider = new FakeTimeZoneProvider(Plus8);
        var evaluator = new ScheduleEvaluator(Workdays(), provider);

        Assert.NotEmpty(evaluator.Evaluate(Utc(2)).Inhibitors);

        provider.Zone = TimeZoneInfo.CreateCustomTimeZone(
            "PowerLease Test -08", TimeSpan.FromHours(-8), "PowerLease Test -08", "PLTM8");

        Assert.Empty(evaluator.Evaluate(Utc(2)).Inhibitors);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        Assert.Throws<ArgumentNullException>(() => new ScheduleEvaluator(null!, new FakeTimeZoneProvider(Plus8)));
        Assert.Throws<ArgumentNullException>(() => new ScheduleEvaluator(Workdays(), null!));
    }
}

public sealed class SystemActivityEvaluatorTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Epoch = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private static MonotonicStamp At(double seconds) => new(Epoch, TimeSpan.FromSeconds(seconds));

    private static SystemActivityEvaluator CpuOnly(double threshold = 10, double quietMinutes = 2) =>
        new(new SystemActivityOptions
        {
            Cpu = new ActivityRule(true, threshold, TimeSpan.FromMinutes(quietMinutes)),
            Memory = new ActivityRule(false, 70, TimeSpan.FromMinutes(20)),
            Disk = new ActivityRule(false, 1, TimeSpan.FromMinutes(15)),
            Network = new ActivityRule(false, 1, TimeSpan.FromMinutes(15)),
            MaxSampleGap = TimeSpan.FromSeconds(30)
        });

    [Fact]
    public void With_no_readings_yet_nothing_can_be_concluded_so_the_machine_is_held()
    {
        var report = CpuOnly().Evaluate(At(0), Noon);

        Assert.Equal(InhibitorKind.SystemActivity, Assert.Single(report.Inhibitors).Kind);
    }

    [Fact]
    public void Load_above_the_threshold_holds_the_machine_awake()
    {
        // The case Windows' own idle timer cannot see: a long compile with nobody touching the keyboard.
        var evaluator = CpuOnly();
        evaluator.Observe(new SystemMetricSample(CpuPercent: 80), At(0));

        var report = evaluator.Evaluate(At(0), Noon);

        Assert.Contains("above the idle threshold", Assert.Single(report.Inhibitors).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuous_quiet_for_the_configured_time_stops_holding()
    {
        var evaluator = CpuOnly(quietMinutes: 2);
        for (var second = 0.0; second <= 150; second += 15)
        {
            evaluator.Observe(new SystemMetricSample(CpuPercent: 1), At(second));
        }

        var report = evaluator.Evaluate(At(150), Noon);

        Assert.True(report.IsDeterminate);
        Assert.Empty(report.Inhibitors);
    }

    [Fact]
    public void Quiet_that_has_not_lasted_long_enough_still_holds_and_says_how_far_it_has_got()
    {
        var evaluator = CpuOnly(quietMinutes: 2);
        for (var second = 0.0; second <= 60; second += 15)
        {
            evaluator.Observe(new SystemMetricSample(CpuPercent: 1), At(second));
        }

        var report = evaluator.Evaluate(At(60), Noon);

        Assert.Contains("not been quiet long enough", Assert.Single(report.Inhibitors).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_metric_that_could_not_be_read_is_not_recorded_so_quiet_cannot_be_established()
    {
        // Recording a missing reading as zero would be the fail-open version of this: it would look like the
        // quietest machine imaginable.
        var evaluator = CpuOnly(quietMinutes: 2);
        for (var second = 0.0; second <= 150; second += 15)
        {
            evaluator.Observe(new SystemMetricSample(CpuPercent: null), At(second));
        }

        Assert.NotEmpty(evaluator.Evaluate(At(150), Noon).Inhibitors);
    }

    [Fact]
    public void A_reading_that_is_not_a_number_is_ignored_rather_than_treated_as_quiet()
    {
        var evaluator = CpuOnly(quietMinutes: 2);
        for (var second = 0.0; second <= 150; second += 15)
        {
            evaluator.Observe(new SystemMetricSample(CpuPercent: double.NaN), At(second));
        }

        Assert.NotEmpty(evaluator.Evaluate(At(150), Noon).Inhibitors);
    }

    [Fact]
    public void A_rule_that_is_switched_off_is_not_consulted()
    {
        // Memory is off by default because memory in use says little about whether work is happening.
        var evaluator = CpuOnly(quietMinutes: 2);
        for (var second = 0.0; second <= 150; second += 15)
        {
            evaluator.Observe(new SystemMetricSample(CpuPercent: 1, MemoryPercent: 99), At(second));
        }

        Assert.Empty(evaluator.Evaluate(At(150), Noon).Inhibitors);
    }

    [Fact]
    public void With_every_rule_switched_off_this_source_has_nothing_to_say()
    {
        var evaluator = new SystemActivityEvaluator(new SystemActivityOptions
        {
            Cpu = new ActivityRule(false, 10, TimeSpan.FromMinutes(20)),
            Memory = new ActivityRule(false, 70, TimeSpan.FromMinutes(20)),
            Disk = new ActivityRule(false, 1, TimeSpan.FromMinutes(15)),
            Network = new ActivityRule(false, 1, TimeSpan.FromMinutes(15))
        });

        Assert.False(evaluator.HasEnabledRules);
        Assert.True(evaluator.Evaluate(At(0), Noon).IsDeterminate);
        Assert.Empty(evaluator.Evaluate(At(0), Noon).Inhibitors);
    }

    [Fact]
    public void The_memory_rule_works_when_it_is_switched_on()
    {
        // Off by default because memory in use says little about whether work is happening, but the user can
        // turn it on and it has to behave like any other rule.
        var evaluator = new SystemActivityEvaluator(new SystemActivityOptions
        {
            Cpu = new ActivityRule(false, 10, TimeSpan.FromMinutes(20)),
            Memory = new ActivityRule(true, 70, TimeSpan.FromMinutes(2)),
            Disk = new ActivityRule(false, 1, TimeSpan.FromMinutes(15)),
            Network = new ActivityRule(false, 1, TimeSpan.FromMinutes(15)),
            MaxSampleGap = TimeSpan.FromSeconds(30)
        });

        evaluator.Observe(new SystemMetricSample(MemoryPercent: 90), At(0));
        Assert.NotEmpty(evaluator.Evaluate(At(0), Noon).Inhibitors);

        for (var second = 15.0; second <= 165; second += 15)
        {
            evaluator.Observe(new SystemMetricSample(MemoryPercent: 10), At(second));
        }

        Assert.Empty(evaluator.Evaluate(At(165), Noon).Inhibitors);
    }

    [Fact]
    public void Each_busy_metric_is_its_own_reason_to_stay_awake()
    {
        var evaluator = new SystemActivityEvaluator(new SystemActivityOptions
        {
            Cpu = new ActivityRule(true, 10, TimeSpan.FromMinutes(2)),
            Memory = new ActivityRule(false, 70, TimeSpan.FromMinutes(20)),
            Disk = new ActivityRule(true, 1000, TimeSpan.FromMinutes(2)),
            Network = new ActivityRule(false, 1, TimeSpan.FromMinutes(15)),
            MaxSampleGap = TimeSpan.FromSeconds(30)
        });

        evaluator.Observe(new SystemMetricSample(CpuPercent: 80, DiskBytesPerSecond: 9999), At(0));

        Assert.Equal(2, evaluator.Evaluate(At(0), Noon).Inhibitors.Count);
    }

    [Fact]
    public void The_arguments_are_validated()
    {
        Assert.Throws<ArgumentNullException>(() => new SystemActivityEvaluator(null!));
        Assert.Throws<ArgumentNullException>(() => CpuOnly().Observe(null!, At(0)));
    }
}

public sealed class EnergyEstimatorTests
{
    [Fact]
    public void A_measured_reading_is_reported_as_measured()
    {
        var estimate = new EnergyEstimator(idleBaselineWatts: 30).Estimate(TimeSpan.FromHours(2), 50);

        Assert.Equal(100, estimate.WattHours);
        Assert.Equal(EnergySource.Measured, estimate.Source);
        Assert.False(estimate.IsEstimated);
    }

    [Fact]
    public void Without_a_reading_the_configured_baseline_is_used_and_marked_as_a_guess()
    {
        // The flag travels on the value itself, so a caller that forgot to check where the number came from
        // cannot present a guess as a reading.
        var estimate = new EnergyEstimator(idleBaselineWatts: 30).Estimate(TimeSpan.FromHours(2), null);

        Assert.Equal(60, estimate.WattHours);
        Assert.Equal(EnergySource.Estimated, estimate.Source);
        Assert.True(estimate.IsEstimated);
        Assert.Contains("not measured", estimate.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void With_neither_a_reading_nor_a_baseline_no_figure_is_produced()
    {
        // Inventing a plausible default would be worse than admitting there is nothing to report.
        var estimate = new EnergyEstimator(idleBaselineWatts: null).Estimate(TimeSpan.FromHours(2), null);

        Assert.Null(estimate.WattHours);
        Assert.Equal(EnergySource.Unavailable, estimate.Source);
        Assert.False(estimate.IsEstimated);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void A_baseline_that_is_not_a_usable_number_is_ignored(double baseline)
    {
        var estimate = new EnergyEstimator(baseline).Estimate(TimeSpan.FromHours(1), null);

        Assert.Equal(EnergySource.Unavailable, estimate.Source);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5)]
    public void A_reading_that_is_not_a_usable_number_falls_back_to_the_baseline(double measured)
    {
        var estimate = new EnergyEstimator(idleBaselineWatts: 30).Estimate(TimeSpan.FromHours(1), measured);

        Assert.Equal(EnergySource.Estimated, estimate.Source);
        Assert.Equal(30, estimate.WattHours);
    }

    [Fact]
    public void A_period_with_no_length_produces_nothing()
    {
        Assert.Equal(
            EnergySource.Unavailable,
            new EnergyEstimator(30).Estimate(TimeSpan.Zero, 50).Source);
    }
}
