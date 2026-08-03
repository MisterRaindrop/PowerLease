using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PowerLease.Application.Inhibitors;
using PowerLease.Application.Kernel;
using PowerLease.Infrastructure.Windows.Power;
using PowerLease.Infrastructure.Windows.Sources;
using Xunit;

namespace PowerLease.IntegrationTests;

/// <summary>
/// What can be asserted about the Windows adapters without special hardware or privileges. None of these puts
/// the machine to sleep, and none of them needs a session to be connected.
/// </summary>
public sealed class WindowsAdapterTests
{
    [Fact]
    public void The_real_power_request_bindings_resolve_and_the_system_accepts_a_request()
    {
        // Every other request test drives a fake. That proves the manager's logic and nothing about whether
        // the P/Invoke declarations are right: a wrong entry point name, a mis-marshalled reason context or a
        // bad struct layout is invisible to a fake and would only appear on a real machine. Replacing the old
        // real-API test with fakes closed a hole in the assertions and opened this one, so both now exist.
        //
        // Safe on a CI runner by construction: this only ever prevents sleep, and it lets go in the finally.
        var manager = new PowerRequestManager();

        try
        {
            var result = manager.Acquire(generation: 1);

            Assert.True(
                result == PowerInhibitResult.Held,
                $"The system did not accept a keep-awake request ({result}). If the bindings are correct, "
                + "this machine's active power plan is refusing application requests, which is itself worth "
                + "knowing -- PowerLease cannot work here until that setting changes.");
        }
        finally
        {
            // Unconditional: a strengthened assertion that fails must not leave a real request outstanding.
            manager.Close(generation: 1);
        }
    }

    [Fact]
    public void Acquire_asks_Windows_for_system_required_and_never_display_required()
    {
        // POWER_REQUEST_TYPE does not start where one would guess: zero is PowerRequestDisplayRequired, and
        // PowerRequestSystemRequired is one. Getting it wrong is close to invisible -- a display request keeps
        // the system awake as a side effect, so the machine would still stay up while the product held the
        // wrong thing, lit the screen of a headless machine, and appeared under the wrong heading in
        // powercfg /requests. Nothing but this test would notice.
        var native = new RecordingPowerRequestNativeMethods();
        var manager = new PowerRequestManager(native);

        try
        {
            Assert.Equal(PowerInhibitResult.Held, manager.Acquire(generation: 1));
        }
        finally
        {
            manager.Close(generation: 1);
        }

        var set = Assert.Single(native.SetRequests);
        Assert.Equal(PowerRequestType.SystemRequired, set.RequestType);
        Assert.DoesNotContain(
            native.SetRequests,
            request => request.RequestType == PowerRequestType.DisplayRequired);
    }

    [Fact]
    public void Letting_go_of_a_generation_that_was_never_held_does_nothing()
    {
        // The coordinator closes a generation whose acquisition outcome it could not establish, so this is a
        // path taken in ordinary operation rather than an edge case.
        var native = new RecordingPowerRequestNativeMethods();
        var manager = new PowerRequestManager(native);

        manager.Close(generation: 42);
        manager.Close(generation: 42);

        Assert.Empty(native.ClearRequests);
        Assert.Empty(native.Handles);
    }

    [Fact]
    public void Each_generation_creates_sets_clears_and_closes_exactly_one_handle()
    {
        var native = new RecordingPowerRequestNativeMethods();
        var manager = new PowerRequestManager(native);

        try
        {
            Assert.Equal(PowerInhibitResult.Held, manager.Acquire(generation: 1));
            Assert.Equal(PowerInhibitResult.Held, manager.Acquire(generation: 2));
        }
        finally
        {
            manager.Close(generation: 1);
            manager.Close(generation: 2);
        }

        manager.Close(generation: 1);
        manager.Close(generation: 2);

        Assert.Equal(2, native.Handles.Count);
        Assert.Equal(2, native.SetRequests.Count);
        Assert.Equal(2, native.ClearRequests.Count);
        Assert.All(native.ClearRequests, request => Assert.Equal(PowerRequestType.SystemRequired, request.RequestType));
        Assert.All(native.Handles, handle => Assert.Equal(1, handle.CloseCount));
    }

    [Fact]
    public void A_released_request_can_be_reestablished_under_a_new_generation()
    {
        var native = new RecordingPowerRequestNativeMethods();
        var manager = new PowerRequestManager(native);

        try
        {
            Assert.Equal(PowerInhibitResult.Held, manager.Acquire(generation: 10));
            manager.Close(generation: 10);

            var first = Assert.Single(native.Handles);
            Assert.Equal(1, first.CloseCount);
            Assert.Single(native.ClearRequests);

            Assert.Equal(PowerInhibitResult.Held, manager.Acquire(generation: 11));
            Assert.Equal(2, native.Handles.Count);
            Assert.Equal(2, native.SetRequests.Count);
        }
        finally
        {
            manager.Close(generation: 10);
            manager.Close(generation: 11);
        }

        Assert.Equal(2, native.ClearRequests.Count);
        Assert.All(native.Handles, handle => Assert.Equal(1, handle.CloseCount));
    }

    [Fact]
    public void The_power_configuration_is_read_as_facts_or_admitted_gaps_never_as_a_silent_no()
    {
        // "The power plan forbids this" and "nobody could find out" call for different actions from the user,
        // so a fact that could not be determined must stay null and say which call failed.
        var native = new UnavailablePowerCapabilityNativeMethods();
        var probe = new PowerCapabilityProbe(native);

        var snapshot = probe.Read();

        Assert.Null(snapshot.SystemRequiredHonouredOnMains);
        Assert.Null(snapshot.SystemRequiredHonouredOnBattery);
        Assert.Null(snapshot.ModernStandby);
        Assert.Null(snapshot.RunningOnBattery);
        Assert.Equal(4, snapshot.Unavailable.Count);
        Assert.Single(snapshot.Unavailable, detail => detail.Contains("PowerReadACValue", StringComparison.Ordinal));
        Assert.Single(snapshot.Unavailable, detail => detail.Contains("PowerReadDCValue", StringComparison.Ordinal));
        Assert.Single(snapshot.Unavailable, detail => detail.Contains("GetPwrCapabilities", StringComparison.Ordinal));
        Assert.Single(snapshot.Unavailable, detail => detail.Contains("GetSystemPowerStatus", StringComparison.Ordinal));
        Assert.Equal(1, native.LocalFreeCalls);
    }

    [Fact]
    public void The_real_systemrequired_probe_returns_each_fact_or_names_why_it_is_unknown()
    {
        var snapshot = new PowerCapabilityProbe().Read();

        AssertFactOrUnavailable(
            snapshot.SystemRequiredHonouredOnMains,
            snapshot.Unavailable,
            "PowerReadACValue",
            "PowerGetActiveScheme");
        AssertFactOrUnavailable(
            snapshot.SystemRequiredHonouredOnBattery,
            snapshot.Unavailable,
            "PowerReadDCValue",
            "PowerGetActiveScheme");
        AssertFactOrUnavailable(snapshot.ModernStandby, snapshot.Unavailable, "GetPwrCapabilities");
        AssertFactOrUnavailable(snapshot.RunningOnBattery, snapshot.Unavailable, "GetSystemPowerStatus");
    }

    [Fact]
    public void A_lock_file_that_exists_is_reported_present_and_one_that_does_not_is_reported_absent()
    {
        var probe = new WindowsLockFileProbe();
        var path = Path.Combine(Path.GetTempPath(), $"powerlease-{Guid.NewGuid():n}.lock");

        var absent = probe.Check(path);
        Assert.True(absent.Succeeded);
        Assert.False(absent.Exists);

        File.WriteAllText(path, string.Empty);
        try
        {
            var present = probe.Check(path);
            Assert.True(present.Succeeded);
            Assert.True(present.Exists);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_metric_that_needs_two_readings_is_null_the_first_time_rather_than_zero()
    {
        // Zero would read as the quietest machine imaginable and release protection. A rate cannot be known
        // from one reading, so the honest answer is that it is not known yet.
        using var provider = new SystemMetricProvider();

        var first = provider.Read();

        Assert.Null(first.CpuPercent);
        Assert.Null(first.NetworkBytesPerSecond);
    }

    [Fact]
    public void The_connection_table_is_read_as_a_success_or_as_an_admitted_failure_never_as_emptiness()
    {
        // An empty successful snapshot is a positive claim that nobody is connected, which releases
        // protection. A failed read must not be able to masquerade as one.
        var snapshot = new ExtendedTcpTableProvider().GetEstablishedConnections();

        if (snapshot.Succeeded)
        {
            Assert.All(snapshot.Connections, connection =>
            {
                Assert.InRange(connection.LocalPort, 0, 65535);
                Assert.InRange(connection.RemotePort, 0, 65535);
                Assert.False(string.IsNullOrWhiteSpace(connection.RemoteAddress));

                // An IPv4-mapped address must have been unwrapped, or the same connection would be tracked
                // under two different keys depending on which family enumerated it.
                Assert.DoesNotContain("::ffff:", connection.RemoteAddress, StringComparison.OrdinalIgnoreCase);
            });
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Detail));
        }
    }

    [Fact]
    public async Task The_connection_table_finds_ipv4_ipv6_and_ipv4_mapped_connections_with_their_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ipv4Listener = new TcpListener(IPAddress.Loopback, 0);
        var ipv6Listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        var mappedListener = new TcpListener(IPAddress.IPv6Any, 0);
        TcpClient? ipv4Server = null;
        TcpClient? ipv6Server = null;
        TcpClient? mappedServer = null;

        try
        {
            ipv4Listener.Start();
            ipv6Listener.Start();
            mappedListener.Server.DualMode = true;
            mappedListener.Start();

            using var ipv4Client = new TcpClient(AddressFamily.InterNetwork);
            var acceptIpv4 = ipv4Listener.AcceptTcpClientAsync(cancellationToken);
            await ipv4Client.ConnectAsync(
                IPAddress.Loopback,
                ((IPEndPoint)ipv4Listener.LocalEndpoint).Port,
                cancellationToken);
            ipv4Server = await acceptIpv4;

            using var ipv6Client = new TcpClient(AddressFamily.InterNetworkV6);
            var acceptIpv6 = ipv6Listener.AcceptTcpClientAsync(cancellationToken);
            await ipv6Client.ConnectAsync(
                IPAddress.IPv6Loopback,
                ((IPEndPoint)ipv6Listener.LocalEndpoint).Port,
                cancellationToken);
            ipv6Server = await acceptIpv6;

            using var mappedClient = new TcpClient(AddressFamily.InterNetwork);
            var acceptMapped = mappedListener.AcceptTcpClientAsync(cancellationToken);
            await mappedClient.ConnectAsync(
                IPAddress.Loopback,
                ((IPEndPoint)mappedListener.LocalEndpoint).Port,
                cancellationToken);
            mappedServer = await acceptMapped;

            var snapshot = new ExtendedTcpTableProvider().GetEstablishedConnections();
            Assert.True(snapshot.Succeeded, snapshot.Detail);

            AssertOwnedConnection(snapshot, ipv4Listener, IPAddress.Loopback.ToString());
            AssertOwnedConnection(snapshot, ipv6Listener, IPAddress.IPv6Loopback.ToString());
            AssertOwnedConnection(snapshot, mappedListener, IPAddress.Loopback.ToString());
        }
        finally
        {
            ipv4Server?.Dispose();
            ipv6Server?.Dispose();
            mappedServer?.Dispose();
            ipv4Listener.Stop();
            ipv6Listener.Stop();
            mappedListener.Stop();
        }
    }

    [Fact]
    public void The_running_processes_are_listed_and_an_unreadable_command_line_is_null_not_blank()
    {
        // A service cannot read most other accounts' command lines. That is expected, and the evaluator treats
        // null as a match; an empty string would silently match nothing.
        var snapshot = new ProcessSnapshotProvider().GetProcesses();

        Assert.True(snapshot.Succeeded, snapshot.Detail);
        Assert.NotEmpty(snapshot.Processes);
        Assert.All(snapshot.Processes, process =>
        {
            Assert.False(string.IsNullOrWhiteSpace(process.Name));
            Assert.True(process.CommandLine is null || process.CommandLine.Length > 0);
        });
    }

    [Fact]
    public void Reading_the_ssh_log_reports_a_state_and_offers_a_position_only_when_it_can_be_trusted()
    {
        // The OpenSSH channel may or may not exist on this machine; both are first-class answers. What must
        // hold either way is that a position is only handed back from a read that was actually continuous,
        // because storing one from a failed read loses the logins it skipped.
        var read = new OpenSshEventProvider().Read(bookmark: null);

        if (read.State == SshLogChannelState.Available)
        {
            Assert.All(read.Events, entry => Assert.False(string.IsNullOrWhiteSpace(entry.RecordId)));
        }
        else
        {
            Assert.Empty(read.Events);
            Assert.Null(read.Bookmark);
            Assert.False(string.IsNullOrWhiteSpace(read.Detail));
        }
    }

    private static void AssertFactOrUnavailable(
        bool? fact,
        IReadOnlyList<string> unavailable,
        params string[] expectedCalls)
    {
        Assert.True(
            fact.HasValue
            || unavailable.Any(detail => expectedCalls.Any(
                call => detail.Contains(call, StringComparison.Ordinal))),
            $"No fact and no diagnostic naming any of: {string.Join(", ", expectedCalls)}");
    }

    private static void AssertOwnedConnection(TcpSnapshot snapshot, TcpListener listener, string remoteAddress)
    {
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connection = Assert.Single(
            snapshot.Connections,
            item => item.LocalPort == port && item.OwningProcessId == Environment.ProcessId);

        Assert.Equal(remoteAddress, connection.RemoteAddress);
        Assert.Equal(Environment.ProcessId, connection.OwningProcessId);
        Assert.DoesNotContain("::ffff:", connection.RemoteAddress, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingPowerRequestNativeMethods : IPowerRequestNativeMethods
    {
        public List<FakePowerRequestHandle> Handles { get; } = [];

        public List<(SafeHandle Handle, PowerRequestType RequestType)> SetRequests { get; } = [];

        public List<(SafeHandle Handle, PowerRequestType RequestType)> ClearRequests { get; } = [];

        public SafeHandle PowerCreateRequest(string reason)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            var handle = new FakePowerRequestHandle(Handles.Count + 1);
            Handles.Add(handle);
            return handle;
        }

        public bool PowerSetRequest(SafeHandle powerRequest, PowerRequestType requestType)
        {
            SetRequests.Add((powerRequest, requestType));
            return true;
        }

        public bool PowerClearRequest(SafeHandle powerRequest, PowerRequestType requestType)
        {
            ClearRequests.Add((powerRequest, requestType));
            return true;
        }

        public int GetLastError() => 0;
    }

    private sealed class FakePowerRequestHandle : SafeHandle
    {
        public FakePowerRequestHandle(int value)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(new IntPtr(value));
        }

        public int CloseCount { get; private set; }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            CloseCount++;
            return true;
        }
    }

    private sealed class UnavailablePowerCapabilityNativeMethods : IPowerCapabilityNativeMethods
    {
        private readonly Guid _scheme = new("c7f63920-4fc2-49a2-a509-0678fc9f5c8c");

        public int LocalFreeCalls { get; private set; }

        public uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid)
        {
            activePolicyGuid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            Marshal.StructureToPtr(_scheme, activePolicyGuid, fDeleteOld: false);
            return 0;
        }

        public uint PowerReadACValue(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupGuid,
            ref Guid powerSettingGuid,
            out uint type,
            out uint buffer,
            ref uint bufferSize)
        {
            type = 0;
            buffer = 0;
            return 5;
        }

        public uint PowerReadDCValue(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupGuid,
            ref Guid powerSettingGuid,
            out uint type,
            out uint buffer,
            ref uint bufferSize)
        {
            type = 0;
            buffer = 0;
            return 50;
        }

        public bool GetPwrCapabilities(out SystemPowerCapabilities capabilities)
        {
            capabilities = default;
            return false;
        }

        public bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus)
        {
            systemPowerStatus = default;
            return false;
        }

        public IntPtr LocalFree(IntPtr memory)
        {
            Marshal.FreeHGlobal(memory);
            LocalFreeCalls++;
            return IntPtr.Zero;
        }

        public int GetLastError() => 31;
    }
}
