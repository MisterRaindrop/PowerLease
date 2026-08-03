using System.Diagnostics;
using PowerLease.Ipc.Contracts;
using Xunit;

namespace PowerLease.IntegrationTests;

/// <summary>
/// The physical-hardware release gate. These tests are deliberately excluded from CI and must be run one at a
/// time, in numbered order, using RealMachine.md. They only add or remove keep-awake protection; Windows alone
/// decides whether and when the machine enters a low-power state.
/// </summary>
public sealed class RealMachineScenarioTests
{
    private static readonly TimeSpan HoldObservationWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SleepObservationWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BuildObservationWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan OrdinaryTransitionTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;
    private readonly RealMachineTestSupport _machine;

    public RealMachineScenarioTests(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _machine = new RealMachineTestSupport(output);
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Scenario_1_A_hold_keeps_the_machine_awake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RealMachineTestSupport.AssertMachinePreconditions();
        await RealMachineTestSupport.AssertCleanBaselineAsync(cancellationToken);
        await RealMachineTestSupport.AssertMainsPowerCanHonourRequestsAsync(cancellationToken);
        await using var powerPlan = await _machine.SetTwoMinuteAcSleepTimeoutAsync(cancellationToken);

        string? leaseId = null;
        try
        {
            leaseId = await RealMachineTestSupport.CreateHoldAsync(
                "1h",
                $"M7 scenario 1 {Guid.NewGuid():N}",
                cancellationToken);
            var held = await RealMachineTestSupport.WaitForStatusAsync(
                status => status.ProtectionState == ReportedProtectionState.Protected
                    && status.Inhibitors.Any(inhibitor => inhibitor.Kind == "CliLease"),
                OrdinaryTransitionTimeout,
                "The manual hold never became protected.",
                cancellationToken);
            Assert.True(held.ShouldHold);

            var initialRequests = await RealMachineTestSupport.WaitForPowerRequestAsync(
                shouldBePresent: true,
                OrdinaryTransitionTimeout,
                cancellationToken);
            Assert.False(initialRequests.HasServiceDisplayRequest);

            var observationStartedAt = DateTimeOffset.UtcNow;
            _output.WriteLine(
                $"Scenario 1 quiet window started at {observationStartedAt:O}; do not touch the machine for "
                + $"{HoldObservationWindow.TotalMinutes:F0} minutes.");
            await Task.Delay(HoldObservationWindow, cancellationToken);
            var observationEndedAt = DateTimeOffset.UtcNow;

            var finalRequests = await RealMachineTestSupport.GetPowerRequestsAsync(cancellationToken);
            Assert.True(finalRequests.HasServiceSystemRequest, finalRequests.Raw);
            Assert.False(finalRequests.HasServiceDisplayRequest, finalRequests.Raw);
            await RealMachineTestSupport.AssertNoSleepEntryAsync(
                observationStartedAt,
                observationEndedAt,
                cancellationToken);
        }
        finally
        {
            if (leaseId is not null)
            {
                await RealMachineTestSupport.ReleaseHoldAsync(leaseId);
            }
        }
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Scenario_2_Releasing_lets_Windows_sleep_on_its_own()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RealMachineTestSupport.AssertMachinePreconditions();
        await RealMachineTestSupport.AssertCleanBaselineAsync(cancellationToken);
        await RealMachineTestSupport.AssertMainsPowerCanHonourRequestsAsync(cancellationToken);
        await using var powerPlan = await _machine.SetTwoMinuteAcSleepTimeoutAsync(cancellationToken);

        string? leaseId = null;
        try
        {
            leaseId = await RealMachineTestSupport.CreateHoldAsync(
                "1h",
                $"M7 scenario 2 {Guid.NewGuid():N}",
                cancellationToken);
            _ = await RealMachineTestSupport.WaitForPowerRequestAsync(
                shouldBePresent: true,
                OrdinaryTransitionTimeout,
                cancellationToken);
            var serviceProcessId = await RealMachineTestSupport.GetServiceProcessIdAsync(cancellationToken);

            var releaseStartedAt = DateTimeOffset.UtcNow;
            await RealMachineTestSupport.ReleaseHoldAsync(leaseId);
            leaseId = null;
            _ = await RealMachineTestSupport.WaitForStatusAsync(
                status => status.ProtectionState == ReportedProtectionState.Released && !status.ShouldHold,
                OrdinaryTransitionTimeout,
                "PowerLease continued requesting protection after its only hold was released.",
                cancellationToken);
            _ = await RealMachineTestSupport.WaitForPowerRequestAsync(
                shouldBePresent: false,
                OrdinaryTransitionTimeout,
                cancellationToken);

            _output.WriteLine(
                $"SCENARIO 2 RELEASE: {releaseStartedAt:O}. Leave the machine untouched; after it becomes "
                + "unreachable, wait until six minutes after this timestamp before waking it without SSH.");
            await Task.Delay(SleepObservationWindow, cancellationToken);

            var transition = await _machine.AssertWindowsSleepTransitionAsync(
                releaseStartedAt,
                releaseStartedAt + SleepObservationWindow,
                cancellationToken);
            var latency = transition.Sleep.TimeCreatedUtc - releaseStartedAt;
            Assert.InRange(latency, TimeSpan.Zero, SleepObservationWindow);
            Assert.Equal(
                serviceProcessId,
                await RealMachineTestSupport.GetServiceProcessIdAsync(cancellationToken));
            _output.WriteLine(
                $"SCENARIO 2 RECORD: actual sleep latency after release was {latency.TotalSeconds:F1} seconds.");
        }
        finally
        {
            if (leaseId is not null)
            {
                await RealMachineTestSupport.ReleaseHoldAsync(leaseId);
            }
        }
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Scenario_3_An_SSH_login_creates_a_hold()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RealMachineTestSupport.AssertMachinePreconditions();
        await RealMachineTestSupport.AssertCleanBaselineAsync(cancellationToken);
        await RealMachineTestSupport.AssertMainsPowerCanHonourRequestsAsync(cancellationToken);

        var before = await RealMachineTestSupport.GetLeasesAsync(cancellationToken);
        var previousIds = before.Leases.Select(lease => lease.Id).ToHashSet(StringComparer.Ordinal);
        _output.WriteLine(
            "Scenario 3 is waiting up to five minutes. From the second host, establish SSH now and keep that "
            + "session open after this test passes.");

        var lease = await RealMachineTestSupport.WaitForNewSshLeaseAsync(
            previousIds,
            TimeSpan.FromMinutes(5),
            cancellationToken);
        Assert.Equal("SshSession", lease.Source);
        Assert.True(lease.Remaining > TimeSpan.Zero);

        var status = await RealMachineTestSupport.WaitForStatusAsync(
            response => response.ProtectionState == ReportedProtectionState.Protected
                && response.Inhibitors.Any(inhibitor => inhibitor.Kind == "SshSession"),
            OrdinaryTransitionTimeout,
            "The SSH lease appeared, but status never reported the live SSH session as an inhibitor.",
            cancellationToken);
        Assert.True(status.ShouldHold);

        var requests = await RealMachineTestSupport.WaitForPowerRequestAsync(
            shouldBePresent: true,
            OrdinaryTransitionTimeout,
            cancellationToken);
        Assert.False(requests.HasServiceDisplayRequest, requests.Raw);
        _output.WriteLine(
            $"Scenario 3 created SSH lease {lease.Id} with {lease.Remaining.TotalSeconds:F0} seconds remaining. "
            + "Keep the remote shell open and start scenario 4 immediately.");
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Scenario_4_A_disconnect_keeps_the_remaining_lease_then_Windows_sleeps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RealMachineTestSupport.AssertMachinePreconditions();
        await RealMachineTestSupport.AssertMainsPowerCanHonourRequestsAsync(cancellationToken);
        await using var powerPlan = await _machine.SetTwoMinuteAcSleepTimeoutAsync(cancellationToken);

        var leasesBefore = await RealMachineTestSupport.GetLeasesAsync(cancellationToken);
        var lease = Assert.Single(leasesBefore.Leases, candidate => candidate.Source == "SshSession");
        Assert.True(lease.Remaining > TimeSpan.FromSeconds(30));
        _ = await RealMachineTestSupport.WaitForStatusAsync(
            status => status.Inhibitors.Any(inhibitor => inhibitor.Kind == "SshSession"),
            OrdinaryTransitionTimeout,
            "Scenario 4 must start while the scenario 3 SSH connection is still open.",
            cancellationToken);
        _ = await RealMachineTestSupport.WaitForPowerRequestAsync(
            shouldBePresent: true,
            OrdinaryTransitionTimeout,
            cancellationToken);

        _output.WriteLine("Scenario 4 is waiting up to two minutes. Type `exit` in the remote SSH shell now.");
        _ = await RealMachineTestSupport.WaitForStatusAsync(
            status => status.Inhibitors.All(inhibitor => inhibitor.Kind != "SshSession"),
            TimeSpan.FromMinutes(2),
            "The live SSH inhibitor did not disappear after the remote session disconnected.",
            cancellationToken);

        var disconnectedAt = DateTimeOffset.UtcNow;
        var leasesAfterDisconnect = await RealMachineTestSupport.GetLeasesAsync(cancellationToken);
        var remainingLease = Assert.Single(leasesAfterDisconnect.Leases, candidate => candidate.Id == lease.Id);
        Assert.True(
            remainingLease.Remaining > TimeSpan.Zero,
            "The SSH hold ended with the TCP connection instead of keeping its remaining lease.");
        var expectedExpiryUtc = DateTimeOffset.UtcNow + remainingLease.Remaining;
        var requestsAfterDisconnect = await RealMachineTestSupport.GetPowerRequestsAsync(cancellationToken);
        Assert.True(requestsAfterDisconnect.HasServiceSystemRequest, requestsAfterDisconnect.Raw);
        Assert.False(requestsAfterDisconnect.HasServiceDisplayRequest, requestsAfterDisconnect.Raw);

        await RealMachineTestSupport.AssertLeaseAndRequestRemainUntilAsync(
            remainingLease.Id,
            expectedExpiryUtc - TimeSpan.FromSeconds(15),
            cancellationToken);

        _output.WriteLine(
            "The request was present through the remaining-lease sample window. The test is now making no "
            + "calls across expiry and Windows' idle-sleep decision.");
        var waitUntilUtc = expectedExpiryUtc + SleepObservationWindow;
        var wait = waitUntilUtc - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }

        var leasesAfterExpiry = await RealMachineTestSupport.GetLeasesAsync(cancellationToken);
        Assert.DoesNotContain(leasesAfterExpiry.Leases, candidate => candidate.Id == remainingLease.Id);
        _ = await RealMachineTestSupport.WaitForStatusAsync(
            status => status.ProtectionState == ReportedProtectionState.Released && !status.ShouldHold,
            OrdinaryTransitionTimeout,
            "Protection did not stop after the remaining SSH lease expired.",
            cancellationToken);
        _ = await RealMachineTestSupport.WaitForPowerRequestAsync(
            shouldBePresent: false,
            OrdinaryTransitionTimeout,
            cancellationToken);

        await RealMachineTestSupport.AssertNoSleepEntryAsync(
            disconnectedAt,
            expectedExpiryUtc - TimeSpan.FromSeconds(10),
            cancellationToken);
        var transition = await _machine.AssertWindowsSleepTransitionAsync(
            expectedExpiryUtc - TimeSpan.FromSeconds(10),
            expectedExpiryUtc + SleepObservationWindow,
            cancellationToken);
        Assert.True(
            transition.Sleep.TimeCreatedUtc >= expectedExpiryUtc - TimeSpan.FromSeconds(10),
            "Windows slept before the remaining SSH lease should have expired.");
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Scenario_5_A_protected_long_build_keeps_the_machine_awake()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RealMachineTestSupport.AssertMachinePreconditions();
        await RealMachineTestSupport.AssertCleanBaselineAsync(cancellationToken);
        await RealMachineTestSupport.AssertMainsPowerCanHonourRequestsAsync(cancellationToken);
        await using var powerPlan = await _machine.SetTwoMinuteAcSleepTimeoutAsync(cancellationToken);

        var batchPath = Path.Combine(Path.GetTempPath(), $"PowerLease-M7-{Guid.NewGuid():N}.cmd");
        var logPath = Path.Combine(Path.GetTempPath(), $"PowerLease-M7-{Guid.NewGuid():N}.log");
        var successPath = Path.Combine(Path.GetTempPath(), $"PowerLease-M7-{Guid.NewGuid():N}.succeeded");
        Process? build = null;
        try
        {
            build = await RealMachineTestSupport.StartBuildLoopAsync(
                batchPath,
                logPath,
                successPath,
                cancellationToken);
            var detail = $"cmd:{build.Id}";
            _ = await RealMachineTestSupport.WaitForStatusAsync(
                status => status.ProtectionState == ReportedProtectionState.Protected
                    && status.Inhibitors.Any(inhibitor =>
                        inhibitor.Kind == "ProtectedProcess" && inhibitor.Detail == detail),
                TimeSpan.FromSeconds(45),
                "The configured cmd process rule did not recognize the long-build wrapper.",
                cancellationToken);
            var observationStartedAt = DateTimeOffset.UtcNow;

            while (DateTimeOffset.UtcNow < observationStartedAt + BuildObservationWindow)
            {
                Assert.False(build.HasExited, "The long-build wrapper exited before the observation completed.");
                var status = await RealMachineTestSupport.GetStatusAsync(cancellationToken);
                Assert.Contains(
                    status.Inhibitors,
                    inhibitor => inhibitor.Kind == "ProtectedProcess" && inhibitor.Detail == detail);
                var requests = await RealMachineTestSupport.GetPowerRequestsAsync(cancellationToken);
                Assert.True(requests.HasServiceSystemRequest, requests.Raw);
                Assert.False(requests.HasServiceDisplayRequest, requests.Raw);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }

            var observationEndedAt = DateTimeOffset.UtcNow;
            await RealMachineTestSupport.AssertNoSleepEntryAsync(
                observationStartedAt,
                observationEndedAt,
                cancellationToken);

            await RealMachineTestSupport.StopProcessTreeAsync(build);
            var buildLog = await File.ReadAllTextAsync(logPath, cancellationToken);
            Assert.True(
                File.Exists(successPath),
                "The build wrapper ran, but no build completed successfully. Build output:\n" + buildLog);
            _ = await RealMachineTestSupport.WaitForStatusAsync(
                status => status.Inhibitors.All(inhibitor => inhibitor.Detail != detail),
                TimeSpan.FromSeconds(45),
                "The protected-process inhibitor remained after the build wrapper stopped.",
                cancellationToken);
            _ = await RealMachineTestSupport.WaitForStatusAsync(
                status => status.ProtectionState == ReportedProtectionState.Released && !status.ShouldHold,
                OrdinaryTransitionTimeout,
                "With the build stopped and the prepared config otherwise quiet, protection did not release.",
                cancellationToken);
            _ = await RealMachineTestSupport.WaitForPowerRequestAsync(
                shouldBePresent: false,
                OrdinaryTransitionTimeout,
                cancellationToken);
        }
        finally
        {
            if (build is not null)
            {
                await RealMachineTestSupport.StopProcessTreeAsync(build);
                build.Dispose();
            }

            File.Delete(batchPath);
            File.Delete(logPath);
            File.Delete(successPath);
        }
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Scenario_6_A_killed_service_recovers_and_records_the_unprotected_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RealMachineTestSupport.AssertMachinePreconditions();
        await RealMachineTestSupport.AssertCleanBaselineAsync(cancellationToken);
        await RealMachineTestSupport.AssertMainsPowerCanHonourRequestsAsync(cancellationToken);

        string? leaseId = null;
        try
        {
            leaseId = await RealMachineTestSupport.CreateHoldAsync(
                "1h",
                $"M7 scenario 6 {Guid.NewGuid():N}",
                cancellationToken);
            _ = await RealMachineTestSupport.WaitForPowerRequestAsync(
                shouldBePresent: true,
                OrdinaryTransitionTimeout,
                cancellationToken);
            var oldProcessId = await RealMachineTestSupport.GetServiceProcessIdAsync(cancellationToken);

            var recoveryWindow = await _machine.ObserveServiceRequestRecoveryAsync(
                oldProcessId,
                token => RealMachineTestSupport.ForceKillServiceAsync(oldProcessId, token),
                cancellationToken);
            Assert.InRange(recoveryWindow, TimeSpan.Zero, TimeSpan.FromSeconds(30));

            var recovered = await RealMachineTestSupport.WaitForStatusAsync(
                status => status.ProtectionState == ReportedProtectionState.Protected
                    && status.Inhibitors.Any(inhibitor => inhibitor.Kind == "CliLease"),
                TimeSpan.FromSeconds(30),
                "The restarted service did not restore the durable hold.",
                cancellationToken);
            Assert.True(recovered.ShouldHold);
            var requests = await RealMachineTestSupport.GetPowerRequestsAsync(cancellationToken);
            Assert.True(requests.HasServiceSystemRequest, requests.Raw);
            Assert.False(requests.HasServiceDisplayRequest, requests.Raw);
        }
        finally
        {
            if (leaseId is not null)
            {
                await RealMachineTestSupport.ReleaseHoldAsync(leaseId);
            }
        }
    }

    [Fact]
    [Trait("Category", "RealMachine")]
    public async Task Scenario_7_The_keep_awake_request_is_SYSTEM_and_never_DISPLAY()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        RealMachineTestSupport.AssertMachinePreconditions();
        await RealMachineTestSupport.AssertCleanBaselineAsync(cancellationToken);
        await RealMachineTestSupport.AssertMainsPowerCanHonourRequestsAsync(cancellationToken);

        string? leaseId = null;
        try
        {
            leaseId = await RealMachineTestSupport.CreateHoldAsync(
                "10m",
                $"M7 scenario 7 {Guid.NewGuid():N}",
                cancellationToken);
            var status = await RealMachineTestSupport.WaitForStatusAsync(
                response => response.ProtectionState == ReportedProtectionState.Protected
                    && response.Inhibitors.Any(inhibitor => inhibitor.Kind == "CliLease"),
                OrdinaryTransitionTimeout,
                "The scenario 7 hold never became protected.",
                cancellationToken);
            Assert.True(status.ShouldHold);

            var requests = await RealMachineTestSupport.WaitForPowerRequestAsync(
                shouldBePresent: true,
                OrdinaryTransitionTimeout,
                cancellationToken);
            Assert.Contains(
                RealMachineTestSupport.ServiceExecutableName,
                requests.SystemSection,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                RealMachineTestSupport.ServiceExecutableName,
                requests.DisplaySection,
                StringComparison.OrdinalIgnoreCase);
            _machine.WritePowerRequests(requests);
        }
        finally
        {
            if (leaseId is not null)
            {
                await RealMachineTestSupport.ReleaseHoldAsync(leaseId);
            }
        }
    }
}
