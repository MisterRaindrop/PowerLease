using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PowerLease.Ipc.Contracts;
using Xunit;

namespace PowerLease.IntegrationTests;

internal sealed partial class RealMachineTestSupport
{
    internal const string ServiceExecutableName = "PowerLease.Service.exe";
    internal const string RealMachineOptInVariable = "POWERLEASE_REAL_MACHINE";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ITestOutputHelper _output;

    public RealMachineTestSupport(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    public static void AssertMachinePreconditions()
    {
        Assert.True(OperatingSystem.IsWindows(), "Real-machine scenarios require physical Windows hardware.");
        Assert.True(
            string.Equals(Environment.GetEnvironmentVariable(RealMachineOptInVariable), "1", StringComparison.Ordinal),
            $"Set ${RealMachineOptInVariable}=1 only after completing the preparation in RealMachine.md.");

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        Assert.True(
            principal.IsInRole(WindowsBuiltInRole.Administrator),
            "Real-machine scenarios require an Administrator PowerShell. Reopen PowerShell with Run as "
            + "administrator; this is a test precondition, not a PowerLease failure.");
    }

    public static async Task AssertCleanBaselineAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        Assert.Equal(ReportedProtectionState.Released, status.ProtectionState);
        Assert.False(status.ShouldHold);
        Assert.False(status.GracePeriodActive);
        Assert.Empty(status.Faults);
        Assert.Empty(status.UnhealthySources);
        Assert.Empty(status.Sources);

        var leases = await GetLeasesAsync(cancellationToken);
        Assert.Empty(leases.Leases);

        var requests = await GetPowerRequestsAsync(cancellationToken);
        Assert.False(
            requests.HasServiceSystemRequest,
            "The prepared baseline still has a PowerLease SYSTEM request. Run `powerlease status` and clear "
            + "the reported inhibitor before continuing.");
        Assert.False(requests.HasServiceDisplayRequest);
    }

    public static async Task AssertMainsPowerCanHonourRequestsAsync(CancellationToken cancellationToken)
    {
        var wake = await RunPowerLeaseJsonAsync<WakeStatusResponse>(cancellationToken, "wake-status", "--json");
        var unavailable = wake.Unavailable.Count == 0 ? "none" : string.Join("; ", wake.Unavailable);

        Assert.True(
            wake.RunningOnBattery is false,
            $"The machine must be plugged into mains power for these results to be comparable. "
            + $"RunningOnBattery={wake.RunningOnBattery?.ToString() ?? "unknown"}; unavailable: {unavailable}");
        Assert.True(
            wake.SystemRequiredHonouredOnMains is true,
            "The active plan does not demonstrably honour SYSTEMREQUIRED on mains. This is a machine power "
            + $"configuration result, not a PowerLease defect. Unavailable: {unavailable}");
    }

    public static async Task<string> CreateHoldAsync(
        string duration,
        string reason,
        CancellationToken cancellationToken)
    {
        var response = await RunPowerLeaseJsonAsync<CommandResponse>(
            cancellationToken,
            "hold",
            duration,
            reason,
            "--json");

        Assert.Null(response.Error);
        Assert.Equal("Created", response.Status);
        Assert.False(string.IsNullOrWhiteSpace(response.LeaseId));
        return response.LeaseId!;
    }

    public static async Task ReleaseHoldAsync(string leaseId)
    {
        var response = await RunPowerLeaseJsonAsync<CommandResponse>(
            CancellationToken.None,
            "release",
            leaseId,
            "--json");

        Assert.Null(response.Error);
        Assert.True(
            response.Status is "Released" or "AlreadyDone",
            $"The cleanup release returned status '{response.Status}'.");
    }

    public static Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        RunPowerLeaseJsonAsync<StatusResponse>(cancellationToken, "status", "--json");

    public static Task<ListLeasesResponse> GetLeasesAsync(CancellationToken cancellationToken) =>
        RunPowerLeaseJsonAsync<ListLeasesResponse>(cancellationToken, "list", "--json");

    public static async Task<StatusResponse> WaitForStatusAsync(
        Func<StatusResponse, bool> predicate,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = DateTimeOffset.UtcNow + timeout;
        StatusResponse? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await GetStatusAsync(cancellationToken);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.True(last is not null && predicate(last), failureMessage + Describe(last));
        return last!;
    }

    public static async Task<ReportedLease> WaitForNewSshLeaseAsync(
        IReadOnlySet<string> previousIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ReportedLease? found = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var leases = await GetLeasesAsync(cancellationToken);
            found = leases.Leases.FirstOrDefault(
                lease => lease.Source == "SshSession" && !previousIds.Contains(lease.Id));
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.NotNull(found);
        return found!;
    }

    public static async Task AssertLeaseAndRequestRemainUntilAsync(
        string leaseId,
        DateTimeOffset sampleUntilUtc,
        CancellationToken cancellationToken)
    {
        var sampled = false;

        while (DateTimeOffset.UtcNow < sampleUntilUtc)
        {
            var leases = await GetLeasesAsync(cancellationToken);
            Assert.Contains(leases.Leases, lease => lease.Id == leaseId);

            var requests = await GetPowerRequestsAsync(cancellationToken);
            Assert.True(
                requests.HasServiceSystemRequest,
                $"SSH lease '{leaseId}' still existed but its SYSTEM request had already disappeared.");
            sampled = true;
            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.True(sampled, "The remaining SSH lease was too short to sample safely before its expiry.");
    }

    public static async Task<PowerRequestSnapshot> WaitForPowerRequestAsync(
        bool shouldBePresent,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PowerRequestSnapshot? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await GetPowerRequestsAsync(cancellationToken);
            if (last.HasServiceSystemRequest == shouldBePresent)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        Assert.True(
            last is not null && last.HasServiceSystemRequest == shouldBePresent,
            shouldBePresent
                ? $"{ServiceExecutableName} did not appear under SYSTEM in `powercfg /requests`.\n{last?.Raw}"
                : $"{ServiceExecutableName} remained under SYSTEM after protection was released.\n{last?.Raw}");
        return last!;
    }

    public static async Task<PowerRequestSnapshot> GetPowerRequestsAsync(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("powercfg.exe", ["/requests"], cancellationToken);
        AssertCommandSucceeded(result, "powercfg /requests");
        var snapshot = PowerRequestSnapshot.Parse(result.StandardOutput);
        Assert.True(snapshot.HasSystemSection, "`powercfg /requests` did not contain a SYSTEM heading.");
        Assert.True(snapshot.HasDisplaySection, "`powercfg /requests` did not contain a DISPLAY heading.");
        return snapshot;
    }

    public static async Task<int> GetServiceProcessIdAsync(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("sc.exe", ["queryex", "PowerLease"], cancellationToken);
        AssertCommandSucceeded(result, "sc queryex PowerLease");
        var match = ServiceProcessIdRegex().Match(result.StandardOutput);
        Assert.True(match.Success, $"SCM output did not report a PowerLease process ID.\n{result.StandardOutput}");
        var processId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(processId > 0, $"The PowerLease service is not running.\n{result.StandardOutput}");
        return processId;
    }

    public static async Task ForceKillServiceAsync(int processId, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            "taskkill.exe",
            ["/PID", processId.ToString(CultureInfo.InvariantCulture), "/F"],
            cancellationToken);
        AssertCommandSucceeded(result, $"taskkill /PID {processId} /F");
    }

    public async Task<TimeSpan> ObserveServiceRequestRecoveryAsync(
        int oldProcessId,
        Func<CancellationToken, Task> kill,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kill);
        var samples = new ConcurrentQueue<PowerRequestSample>();
        using var samplerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampler = SamplePowerRequestsAsync(samples, samplerCancellation.Token);

        try
        {
            await WaitForSampleAsync(
                samples,
                sample => sample.Snapshot.HasServiceSystemRequest,
                TimeSpan.FromSeconds(10),
                "The request sampler never observed the initial SYSTEM request.",
                cancellationToken);

            var killStartedAt = DateTimeOffset.UtcNow;
            await kill(cancellationToken);

            var recovered = await WaitForAbsentThenRestoredAsync(
                samples,
                killStartedAt,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            var newProcessId = await WaitForNewServiceProcessAsync(oldProcessId, cancellationToken);
            Assert.NotEqual(oldProcessId, newProcessId);

            var observedAbsent = recovered.RestoredAt - recovered.FirstAbsentAt;
            var conservativeWindow = recovered.RestoredAt - killStartedAt;
            Assert.True(observedAbsent > TimeSpan.Zero);
            Assert.True(conservativeWindow >= observedAbsent);

            _output.WriteLine(
                $"SCENARIO 6 RECORD: observed request absent for {observedAbsent.TotalMilliseconds:F0} ms; "
                + $"kill command to first restored observation {conservativeWindow.TotalMilliseconds:F0} ms; "
                + $"service PID {oldProcessId} -> {newProcessId}.");
            return conservativeWindow;
        }
        finally
        {
            await samplerCancellation.CancelAsync();
            await sampler;
        }
    }

    public static async Task<IReadOnlyList<PowerEvent>> ReadPowerEventsAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        const string kernelQuery =
            "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and "
            + "(EventID=42 or EventID=107 or EventID=506 or EventID=507)]]";
        const string wakeQuery =
            "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]";

        var kernel = await QueryEventsAsync(kernelQuery, cancellationToken);
        var wake = await QueryEventsAsync(wakeQuery, cancellationToken);
        return kernel
            .Concat(wake)
            .Where(powerEvent => powerEvent.TimeCreatedUtc >= sinceUtc)
            .OrderBy(powerEvent => powerEvent.TimeCreatedUtc)
            .ToArray();
    }

    public async Task<SleepTransition> AssertWindowsSleepTransitionAsync(
        DateTimeOffset windowStartUtc,
        DateTimeOffset latestSleepUtc,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        IReadOnlyList<PowerEvent> events = [];
        PowerEvent? sleep = null;
        PowerEvent? wake = null;
        PowerEvent? wakeReason = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            events = await ReadPowerEventsAsync(windowStartUtc, cancellationToken);
            sleep = events.FirstOrDefault(
                powerEvent => powerEvent.IsSleepEntry && powerEvent.TimeCreatedUtc <= latestSleepUtc);
            if (sleep is not null)
            {
                wake = events.FirstOrDefault(
                    powerEvent => powerEvent.IsWake && powerEvent.TimeCreatedUtc >= sleep.TimeCreatedUtc);
                wakeReason = events.FirstOrDefault(
                    powerEvent => powerEvent.IsWakeReason && powerEvent.TimeCreatedUtc >= sleep.TimeCreatedUtc);
            }

            if (sleep is not null && wake is not null && (wake.EventId == 507 || wakeReason is not null))
            {
                break;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.NotNull(sleep);
        Assert.NotNull(wake);
        Assert.True(
            wake!.EventId == 507 || wakeReason is not null,
            "The System log recorded the wake transition but not its wake reason (Power-Troubleshooter event 1). "
            + DescribeEvents(events));

        AssertWindowsInitiatedIdleSleep(sleep!);
        WriteEvent("sleep", sleep!);
        WriteEvent("wake", wake);
        if (wakeReason is not null)
        {
            WriteEvent("wake reason", wakeReason);
        }

        var lastWake = await RunCommandAsync("powercfg.exe", ["/lastwake"], cancellationToken);
        AssertCommandSucceeded(lastWake, "powercfg /lastwake");
        _output.WriteLine("powercfg /lastwake:\n" + lastWake.StandardOutput.Trim());
        return new SleepTransition(sleep!, wake, wakeReason);
    }

    public static async Task AssertNoSleepEntryAsync(
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken cancellationToken)
    {
        var events = await ReadPowerEventsAsync(windowStartUtc, cancellationToken);
        var sleepEvents = events
            .Where(powerEvent => powerEvent.IsSleepEntry && powerEvent.TimeCreatedUtc <= windowEndUtc)
            .ToArray();
        Assert.Empty(sleepEvents);
    }

    public async Task<SleepTimeoutScope> SetTwoMinuteAcSleepTimeoutAsync(CancellationToken cancellationToken)
    {
        var activeResult = await RunCommandAsync("powercfg.exe", ["/getactivescheme"], cancellationToken);
        AssertCommandSucceeded(activeResult, "powercfg /getactivescheme");
        var schemeMatch = GuidRegex().Match(activeResult.StandardOutput);
        Assert.True(schemeMatch.Success, $"Could not read the active power scheme GUID.\n{activeResult.StandardOutput}");
        var scheme = schemeMatch.Value;

        var queryResult = await RunCommandAsync(
            "powercfg.exe",
            ["/query", scheme, "SUB_SLEEP", "STANDBYIDLE"],
            cancellationToken);
        AssertCommandSucceeded(queryResult, "powercfg /query <scheme> SUB_SLEEP STANDBYIDLE");
        var values = HexSettingRegex().Matches(queryResult.StandardOutput);
        Assert.True(
            values.Count >= 2,
            "Could not identify the current AC and DC standby timeout values. Record and restore the active "
            + $"plan manually before investigating this test failure.\n{queryResult.StandardOutput}");
        var previousAcSeconds = uint.Parse(
            values[0].Value.AsSpan(2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

        var scope = new SleepTimeoutScope(this, scheme, previousAcSeconds);
        try
        {
            await scope.SetAsync(120, cancellationToken);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }

        _output.WriteLine(
            $"Power plan {scheme}: AC sleep timeout was {previousAcSeconds} seconds; set to 120 seconds for "
            + "this scenario. The test will restore it in cleanup.");
        return scope;
    }

    public static async Task<Process> StartBuildLoopAsync(
        string batchPath,
        string logPath,
        string successPath,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = FindRepositoryRoot();
        var solutionFilter = Path.Combine(repositoryRoot, "PowerLease.CrossPlatform.slnf");
        var batch = string.Join(
            "\r\n",
            "@echo off",
            ":build",
            $"dotnet build \"{solutionFilter}\" -c Release --no-restore "
            + $"-p:PowerLeaseM7LongBuild=true -v:minimal >>\"{logPath}\" 2>&1",
            "if errorlevel 1 goto build",
            $">\"{successPath}\" echo succeeded",
            "goto build",
            string.Empty);
        await File.WriteAllTextAsync(batchPath, batch, cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(batchPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the long-build command wrapper.");
    }

    public static async Task StopProcessTreeAsync(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    public void WritePowerRequests(PowerRequestSnapshot requests) =>
        _output.WriteLine("SCENARIO 7 RECORD: powercfg /requests\n" + requests.Raw.Trim());

    private static async Task<T> RunPowerLeaseJsonAsync<T>(
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var result = await RunCommandAsync("powerlease.exe", arguments, cancellationToken);
        AssertCommandSucceeded(result, "powerlease " + string.Join(' ', arguments));

        try
        {
            var value = JsonSerializer.Deserialize<T>(result.StandardOutput, Json);
            Assert.NotNull(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The installed CLI did not return the expected JSON for {typeof(T).Name}. "
                + $"Output: {result.StandardOutput}",
                exception);
        }
    }

    private static async Task<IReadOnlyList<PowerEvent>> QueryEventsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            "wevtutil.exe",
            ["qe", "System", $"/q:{query}", "/c:100", "/rd:true", "/f:xml"],
            cancellationToken);
        AssertCommandSucceeded(result, "wevtutil qe System");

        var events = new List<PowerEvent>();
        foreach (Match match in EventXmlRegex().Matches(result.StandardOutput))
        {
            using var text = new StringReader(match.Value);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root ?? throw new InvalidOperationException("An event-log XML record had no root.");
            var system = root.Elements().Single(element => element.Name.LocalName == "System");
            var provider = system.Elements().Single(element => element.Name.LocalName == "Provider")
                .Attribute("Name")?.Value
                ?? throw new InvalidOperationException("An event-log XML record had no provider.");
            var eventId = int.Parse(
                system.Elements().Single(element => element.Name.LocalName == "EventID").Value,
                CultureInfo.InvariantCulture);
            var timeText = system.Elements().Single(element => element.Name.LocalName == "TimeCreated")
                .Attribute("SystemTime")?.Value
                ?? throw new InvalidOperationException("An event-log XML record had no timestamp.");
            var time = DateTimeOffset.Parse(
                timeText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var data = root
                .Descendants()
                .Where(element => element.Name.LocalName == "Data" && element.Attribute("Name") is not null)
                .GroupBy(element => element.Attribute("Name")!.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
            var message = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value;
            events.Add(new PowerEvent(provider, eventId, time, data, message, match.Value));
        }

        return events;
    }

    private static async Task SamplePowerRequestsAsync(
        ConcurrentQueue<PowerRequestSample> samples,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await GetPowerRequestsAsync(cancellationToken);
                samples.Enqueue(new PowerRequestSample(DateTimeOffset.UtcNow, snapshot));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the recovery observation has enough samples.
        }
    }

    private static async Task WaitForSampleAsync(
        ConcurrentQueue<PowerRequestSample> samples,
        Func<PowerRequestSample, bool> predicate,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (samples.Any(predicate))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.True(samples.Any(predicate), failureMessage);
    }

    private static async Task<(DateTimeOffset FirstAbsentAt, DateTimeOffset RestoredAt)>
        WaitForAbsentThenRestoredAsync(
            ConcurrentQueue<PowerRequestSample> samples,
            DateTimeOffset afterUtc,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        PowerRequestSample? firstAbsent = null;
        PowerRequestSample? restored = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var sample in samples.Where(sample => sample.ObservedAtUtc >= afterUtc))
            {
                if (firstAbsent is null && !sample.Snapshot.HasServiceSystemRequest)
                {
                    firstAbsent = sample;
                }
                else if (firstAbsent is not null
                    && sample.ObservedAtUtc >= firstAbsent.ObservedAtUtc
                    && sample.Snapshot.HasServiceSystemRequest)
                {
                    restored = sample;
                    break;
                }
            }

            if (firstAbsent is not null && restored is not null)
            {
                return (firstAbsent.ObservedAtUtc, restored.ObservedAtUtc);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.NotNull(firstAbsent);
        Assert.NotNull(restored);
        return (firstAbsent!.ObservedAtUtc, restored!.ObservedAtUtc);
    }

    private static async Task<int> WaitForNewServiceProcessAsync(
        int oldProcessId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        var processId = oldProcessId;
        while (DateTimeOffset.UtcNow < deadline)
        {
            processId = await GetServiceProcessIdAsync(cancellationToken);
            if (processId > 0 && processId != oldProcessId)
            {
                return processId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        Assert.NotEqual(oldProcessId, processId);
        return processId;
    }

    private static void AssertWindowsInitiatedIdleSleep(PowerEvent sleep)
    {
        if (sleep.EventId != 42)
        {
            // Event 506 is Windows entering Modern Standby. Unlike event 42, its reason field varies by OS
            // build and is not the documented legacy sleep-reason enumeration. The runbook preserves its
            // rendered message for the human evidence record.
            Assert.Equal(506, sleep.EventId);
            return;
        }

        Assert.True(
            sleep.Data.TryGetValue("Reason", out var reason) && reason == "7",
            "Kernel-Power event 42 did not identify System Idle (reason 7). A button, lid, application API, "
            + "or other forced transition does not prove that Windows slept on its own. "
            + sleep.Summary);
    }

    private void WriteEvent(string label, PowerEvent powerEvent) =>
        _output.WriteLine(
            $"{label}: {powerEvent.Provider} event {powerEvent.EventId} at "
            + $"{powerEvent.TimeCreatedUtc:O}: {powerEvent.Summary}");

    private static string Describe(StatusResponse? status) => status is null
        ? " No status response was received."
        : $" Last state: {status.ProtectionState}; ShouldHold={status.ShouldHold}; inhibitors: "
        + string.Join(" | ", status.Inhibitors.Select(inhibitor => $"{inhibitor.Kind}: {inhibitor.Reason}"));

    private static string DescribeEvents(IEnumerable<PowerEvent> events) =>
        string.Join(
            Environment.NewLine,
            events.Select(powerEvent =>
                $"{powerEvent.TimeCreatedUtc:O} {powerEvent.Provider}/{powerEvent.EventId}: {powerEvent.Summary}"));

    private static void AssertCommandSucceeded(CommandResult result, string command)
    {
        Assert.True(
            result.ExitCode == 0,
            $"`{command}` exited {result.ExitCode}. This usually means a missing prerequisite or an "
            + $"unelevated shell, not a PowerLease assertion.\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Windows did not start '{fileName}'.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(
                process.ExitCode,
                await standardOutput,
                await standardError);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "PowerLease.CrossPlatform.slnf")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException(
            "Could not find PowerLease.CrossPlatform.slnf above the test output directory. Run this scenario "
            + "from a complete source checkout.");
    }

    [GeneratedRegex(@"\bPID\s*:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceProcessIdRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]{8}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexSettingRegex();

    [GeneratedRegex(@"<Event(?:\s[^>]*)?>.*?</Event>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex EventXmlRegex();

    internal sealed class SleepTimeoutScope : IAsyncDisposable
    {
        private readonly RealMachineTestSupport _support;
        private readonly string _scheme;
        private readonly uint _previousAcSeconds;
        private bool _restored;

        public SleepTimeoutScope(RealMachineTestSupport support, string scheme, uint previousAcSeconds)
        {
            _support = support;
            _scheme = scheme;
            _previousAcSeconds = previousAcSeconds;
        }

        public async Task SetAsync(uint seconds, CancellationToken cancellationToken)
        {
            var set = await RunCommandAsync(
                "powercfg.exe",
                ["/setacvalueindex", _scheme, "SUB_SLEEP", "STANDBYIDLE", seconds.ToString(CultureInfo.InvariantCulture)],
                cancellationToken);
            AssertCommandSucceeded(set, "powercfg /setacvalueindex <scheme> SUB_SLEEP STANDBYIDLE <seconds>");
            var activate = await RunCommandAsync("powercfg.exe", ["/setactive", _scheme], cancellationToken);
            AssertCommandSucceeded(activate, "powercfg /setactive <scheme>");
        }

        public async ValueTask DisposeAsync()
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
            await SetAsync(_previousAcSeconds, CancellationToken.None);
            _support._output.WriteLine(
                $"Restored power plan {_scheme} AC sleep timeout to {_previousAcSeconds} seconds.");
        }
    }

    internal sealed record PowerRequestSnapshot(
        string Raw,
        string SystemSection,
        string DisplaySection,
        bool HasSystemSection,
        bool HasDisplaySection)
    {
        public bool HasServiceSystemRequest =>
            SystemSection.Contains(ServiceExecutableName, StringComparison.OrdinalIgnoreCase);

        public bool HasServiceDisplayRequest =>
            DisplaySection.Contains(ServiceExecutableName, StringComparison.OrdinalIgnoreCase);

        public static PowerRequestSnapshot Parse(string output)
        {
            var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var (system, hasSystem) = ReadSection(lines, "SYSTEM:");
            var (display, hasDisplay) = ReadSection(lines, "DISPLAY:");
            return new PowerRequestSnapshot(output, system, display, hasSystem, hasDisplay);
        }

        private static (string Content, bool Found) ReadSection(string[] lines, string heading)
        {
            var start = Array.FindIndex(lines, line => string.Equals(line.Trim(), heading, StringComparison.Ordinal));
            if (start < 0)
            {
                return (string.Empty, false);
            }

            var content = new List<string>();
            for (var index = start + 1; index < lines.Length; index++)
            {
                var trimmed = lines[index].Trim();
                if (trimmed.EndsWith(':')
                    && trimmed.Length > 1
                    && trimmed.All(character => char.IsAsciiLetterUpper(character) || character == ':'))
                {
                    break;
                }

                content.Add(lines[index]);
            }

            return (string.Join('\n', content), true);
        }
    }

    internal sealed partial record PowerEvent(
        string Provider,
        int EventId,
        DateTimeOffset TimeCreatedUtc,
        IReadOnlyDictionary<string, string> Data,
        string? Message,
        string RawXml)
    {
        public bool IsSleepEntry =>
            Provider == "Microsoft-Windows-Kernel-Power" && EventId is 42 or 506;

        public bool IsWake =>
            Provider == "Microsoft-Windows-Kernel-Power" && EventId is 107 or 507;

        public bool IsWakeReason =>
            Provider == "Microsoft-Windows-Power-Troubleshooter" && EventId == 1;

        public string Summary => string.IsNullOrWhiteSpace(Message)
            ? string.Join(", ", Data.Select(pair => $"{pair.Key}={pair.Value}"))
            : WhitespaceRegex().Replace(Message, " ").Trim();

        [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
        private static partial Regex WhitespaceRegex();
    }

    internal sealed record SleepTransition(PowerEvent Sleep, PowerEvent Wake, PowerEvent? WakeReason);

    private sealed record PowerRequestSample(DateTimeOffset ObservedAtUtc, PowerRequestSnapshot Snapshot);

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);
}
