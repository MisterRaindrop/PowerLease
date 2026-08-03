using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLease.Application.Hosting;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows.Power;
using PowerLease.Ipc.Contracts;
using PowerLease.Persistence;
using PowerLease.Persistence.History;
using PowerLease.Persistence.Sqlite;
using PowerLease.Service;
using Xunit;

namespace PowerLease.IntegrationTests;

public sealed class IpcPipeIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Every_refusal_path_leaves_the_pipe_ready_for_the_next_connection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var router = SimpleRouter();
        await using var server = await TestPipeServer.StartAsync(router, cancellationToken);

        var wrongVersion = await ExchangeAsync(
            server.PipeName,
            new RequestEnvelope(IpcProtocol.Version + 1, Guid.NewGuid(), IpcMethods.GetStatus),
            cancellationToken);
        await AssertRefusedThenHealthyAsync(server.PipeName, wrongVersion, cancellationToken);

        var oversized = await ExchangeLengthOnlyAsync(
            server.PipeName,
            IpcProtocol.MaxMessageBytes + 1,
            cancellationToken);
        await AssertRefusedThenHealthyAsync(server.PipeName, oversized, cancellationToken);

        var unavailable = await ExchangeAsync(
            server.PipeName,
            new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.GetHistory),
            cancellationToken);
        await AssertRefusedThenHealthyAsync(server.PipeName, unavailable, cancellationToken);
    }

    [Fact]
    public async Task A_truncated_frame_cannot_stop_the_next_connection_being_answered()
    {
        // A byte-mode named pipe has no half-close. The only way a server can discover truncation is the
        // client's disconnect, at which point no response can physically travel back to that client. The
        // observable contract is therefore that this connection is discarded and the next one is answered.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await TestPipeServer.StartAsync(SimpleRouter(), cancellationToken);

        using (var pipe = await ConnectAsync(server.PipeName, cancellationToken))
        {
            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, 100);
            await pipe.WriteAsync(prefix, cancellationToken);
            await pipe.WriteAsync("{"u8.ToArray(), cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }

        var healthy = await ExchangeAsync(
            server.PipeName,
            new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.GetStatus),
            cancellationToken);
        Assert.True(healthy.Accepted, healthy.Error);
    }

    [Fact]
    public async Task The_pipe_rate_limit_refuses_a_flood_but_never_a_release()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var actualSid = identity.User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(actualSid));
        var router = SimpleRouter(leases: [Lease("mine", actualSid!)]);
        await using var server = await TestPipeServer.StartAsync(router, cancellationToken);

        for (var count = 0; count < IpcRequestRouter.MaximumRequestsPerWindow; count++)
        {
            var allowed = await ExchangeAsync(
                server.PipeName,
                new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.GetStatus),
                cancellationToken);
            Assert.True(allowed.Accepted, allowed.Error);
        }

        var refused = await ExchangeAsync(
            server.PipeName,
            new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.GetStatus),
            cancellationToken);
        var release = await ExchangeAsync(
            server.PipeName,
            new RequestEnvelope(
                IpcProtocol.Version,
                Guid.NewGuid(),
                IpcMethods.ReleaseLease,
                JsonSerializer.Serialize(new ReleaseLeasePayload(LeaseId: null), Json)),
            cancellationToken);

        Assert.False(refused.Accepted);
        Assert.Contains("too often", refused.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.True(release.Accepted, release.Error);
    }

    [Fact]
    public async Task A_sid_claimed_in_the_body_cannot_replace_the_connection_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        LeaseCommand? dispatched = null;
        var router = SimpleRouter(command => dispatched = command);
        await using var server = await TestPipeServer.StartAsync(router, cancellationToken);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var actualSid = identity.User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(actualSid));
        const string claimedSid = "S-1-5-21-999999";
        var request = new RequestEnvelope(
            IpcProtocol.Version,
            Guid.NewGuid(),
            IpcMethods.CreateLease,
            JsonSerializer.Serialize(
                new
                {
                    Duration = TimeSpan.FromHours(1),
                    Reason = "identity integration test",
                    Sid = claimedSid,
                    OwnerSid = claimedSid
                },
                Json));

        var response = await ExchangeAsync(server.PipeName, request, cancellationToken);

        Assert.True(response.Accepted, response.Error);
        Assert.NotNull(dispatched);
        Assert.Equal(actualSid, dispatched!.Caller.Sid);
        Assert.NotEqual(claimedSid, dispatched.Caller.Sid);
    }

    [Fact]
    public async Task Hold_list_and_release_traverse_real_framing_router_kernel_and_storage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var service = await KernelService.StartAsync(autoPump: true, cancellationToken);
        var cli = new ProtocolCli(service.PipeName);

        var hold = await cli.HoldAsync(TimeSpan.FromHours(2), "CI integration hold", cancellationToken);
        Assert.Equal(LeaseCommandStatus.Created.ToString(), hold.Status);
        Assert.False(string.IsNullOrWhiteSpace(hold.LeaseId));

        var during = await cli.ListAsync(cancellationToken);
        var listed = Assert.Single(during.Leases);
        Assert.Equal(hold.LeaseId, listed.Id);
        Assert.Equal("CI integration hold", listed.Reason);

        var release = await cli.ReleaseAsync(hold.LeaseId!, cancellationToken);
        Assert.Equal(LeaseCommandStatus.Released.ToString(), release.Status);
        Assert.Equal(hold.LeaseId, release.LeaseId);
        Assert.Empty((await cli.ListAsync(cancellationToken)).Leases);
    }

    [Fact]
    public async Task Duplicate_and_conflicting_request_identifiers_are_decided_before_a_second_lease_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var service = await KernelService.StartAsync(autoPump: false, cancellationToken);
        var duplicateId = Guid.NewGuid();
        var request = CreateRequest(duplicateId, "same request");

        var firstTask = ExchangeAsync(service.PipeName, request, cancellationToken);
        var retryTask = ExchangeAsync(service.PipeName, request, cancellationToken);
        await service.WaitForRoutedCommandsAsync(2, cancellationToken);
        await service.PumpCommandsAsync(cancellationToken);

        var first = await firstTask;
        var retry = await retryTask;
        Assert.True(first.Accepted, first.Error);
        Assert.Equal(first, retry);

        var afterDuplicate = await ExchangeAsync(
            service.PipeName,
            new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.ListLeases),
            cancellationToken);
        Assert.Single(Read<ListLeasesResponse>(afterDuplicate).Leases);

        var conflictId = Guid.NewGuid();
        var originalTask = ExchangeAsync(
            service.PipeName,
            CreateRequest(conflictId, "original content"),
            cancellationToken);
        await service.WaitForRoutedCommandsAsync(1, cancellationToken);

        var impostor = await ExchangeAsync(
            service.PipeName,
            CreateRequest(conflictId, "different content"),
            cancellationToken);
        Assert.False(impostor.Accepted);
        Assert.Contains("request identifier", impostor.Error!, StringComparison.OrdinalIgnoreCase);

        await service.PumpCommandsAsync(cancellationToken);
        var original = await originalTask;
        Assert.True(original.Accepted, original.Error);

        var afterConflict = await ExchangeAsync(
            service.PipeName,
            new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.ListLeases),
            cancellationToken);
        Assert.Equal(2, Read<ListLeasesResponse>(afterConflict).Leases.Count);
    }

    private static RequestEnvelope CreateRequest(Guid requestId, string reason) => new(
        IpcProtocol.Version,
        requestId,
        IpcMethods.CreateLease,
        JsonSerializer.Serialize(new CreateLeasePayload(TimeSpan.FromHours(1), reason), Json));

    private static IpcRequestRouter SimpleRouter(
        Action<LeaseCommand>? onDispatch = null,
        IReadOnlyList<KeepAwakeLease>? leases = null) => new(
        () => KernelSnapshot.Initial,
        () => new LeaseLoadResult(leases ?? [], []),
        () => new PowerCapabilitySnapshot(null, null, null, null, []),
        (command, _) =>
        {
            onDispatch?.Invoke(command);
            return Task.FromResult(
                new LeaseCommandResult(command.RequestId, LeaseCommandStatus.Released, command.LeaseId));
        },
        new FakeClock());

    private static KeepAwakeLease Lease(string id, string ownerSid) => new()
    {
        Id = id,
        Source = LeaseSource.Cli,
        OwnerSid = ownerSid,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        ExpiresAtUtc = DateTimeOffset.UnixEpoch.AddHours(1),
        Status = LeaseStatus.Active,
        EpochId = Guid.Empty,
        OriginalDuration = TimeSpan.FromHours(1)
    };

    private static async Task AssertRefusedThenHealthyAsync(
        string pipeName,
        ResponseEnvelope refused,
        CancellationToken cancellationToken)
    {
        Assert.False(refused.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(refused.Error));

        var healthy = await ExchangeAsync(
            pipeName,
            new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.GetStatus),
            cancellationToken);
        Assert.True(healthy.Accepted, healthy.Error);
    }

    private static async Task<ResponseEnvelope> ExchangeLengthOnlyAsync(
        string pipeName,
        int declaredLength,
        CancellationToken cancellationToken)
    {
        using var pipe = await ConnectAsync(pipeName, cancellationToken);
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, declaredLength);
        await pipe.WriteAsync(prefix, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
        return await ReadResponseAsync(pipe, cancellationToken);
    }

    private static async Task<ResponseEnvelope> ExchangeAsync(
        string pipeName,
        RequestEnvelope request,
        CancellationToken cancellationToken)
    {
        using var pipe = await ConnectAsync(pipeName, cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await pipe.WriteAsync(prefix, cancellationToken);
        await pipe.WriteAsync(payload, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
        return await ReadResponseAsync(pipe, cancellationToken);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task<ResponseEnvelope> ReadResponseAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        Assert.InRange(length, 1, IpcProtocol.MaxMessageBytes);
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<ResponseEnvelope>(payload, Json)
            ?? throw new InvalidOperationException("The pipe returned an empty response envelope.");
    }

    private static T Read<T>(ResponseEnvelope response)
    {
        Assert.True(response.Accepted, response.Error);
        return JsonSerializer.Deserialize<T>(response.PayloadJson!, Json)
            ?? throw new InvalidOperationException("The accepted response contained no payload.");
    }

    private sealed class ProtocolCli(string pipeName)
    {
        public async Task<CommandResponse> HoldAsync(
            TimeSpan duration,
            string reason,
            CancellationToken cancellationToken)
        {
            var response = await ExchangeAsync(
                pipeName,
                new RequestEnvelope(
                    IpcProtocol.Version,
                    Guid.NewGuid(),
                    IpcMethods.CreateLease,
                    JsonSerializer.Serialize(new CreateLeasePayload(duration, reason), Json)),
                cancellationToken);
            return Read<CommandResponse>(response);
        }

        public async Task<ListLeasesResponse> ListAsync(CancellationToken cancellationToken)
        {
            var response = await ExchangeAsync(
                pipeName,
                new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.ListLeases),
                cancellationToken);
            return Read<ListLeasesResponse>(response);
        }

        public async Task<CommandResponse> ReleaseAsync(string leaseId, CancellationToken cancellationToken)
        {
            var response = await ExchangeAsync(
                pipeName,
                new RequestEnvelope(
                    IpcProtocol.Version,
                    Guid.NewGuid(),
                    IpcMethods.ReleaseLease,
                    JsonSerializer.Serialize(new ReleaseLeasePayload(leaseId), Json)),
                cancellationToken);
            return Read<CommandResponse>(response);
        }
    }

    private sealed class TestPipeServer : IAsyncDisposable
    {
        private readonly PipeServerWorker _worker;

        public TestPipeServer(IpcRequestRouter router)
        {
            PipeName = $"PowerLease.Tests.{Guid.NewGuid():N}";
            _worker = new PipeServerWorker(router, NullLogger<PipeServerWorker>.Instance, PipeName);
        }

        public string PipeName { get; }

        public static async Task<TestPipeServer> StartAsync(
            IpcRequestRouter router,
            CancellationToken cancellationToken)
        {
            var server = new TestPipeServer(router);
            await server.StartWorkerAsync(cancellationToken);
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _worker.StopAsync(CancellationToken.None);
            }
            finally
            {
                _worker.Dispose();
            }
        }

        public Task StartWorkerAsync(CancellationToken cancellationToken) =>
            _worker.StartAsync(cancellationToken);
    }

    private sealed class KernelService : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteHistoryStore _store;
        private readonly StoreGate _storeGate;
        private readonly KernelLoop _loop;
        private readonly LeaseCommandDispatcher _dispatcher;
        private readonly SemaphoreSlim _routedCommands = new(0);
        private readonly TestPipeServer _server;

        private KernelService(bool autoPump)
        {
            _root = Path.Combine(Path.GetTempPath(), "powerlease-integration", Guid.NewGuid().ToString("N"));
            var paths = new PowerLeasePaths(_root);
            var clock = new FakeClock();
            new MigrationRunner(paths, clock).Run();
            _store = new SqliteHistoryStore(new SqliteConnectionFactory(paths.DatabasePath));
            _storeGate = new StoreGate(_store);

            var kernel = new InhibitKernel(
                new KernelOptions
                {
                    ExpectedSources = [],
                    StartupGracePeriod = TimeSpan.Zero,
                    LeaseCheckpointInterval = TimeSpan.Zero
                },
                new PowerInhibitCoordinator(new NoOpInhibitor()),
                clock);
            var effects = new SynchronizedEffectExecutor(
                new SqliteEffectExecutor(_store, clock),
                _storeGate.SyncRoot);
            _loop = new KernelLoop(kernel, effects, new FakeTimeZones());
            _dispatcher = new LeaseCommandDispatcher(_loop);

            Func<LeaseCommand, CancellationToken, Task<LeaseCommandResult>> dispatch = autoPump
                ? DispatchAndPumpAsync
                : DispatchAndSignal;
            var router = new IpcRequestRouter(
                () => _loop.Snapshot,
                LoadActiveLeases,
                () => new PowerCapabilitySnapshot(null, null, null, null, []),
                dispatch,
                clock);
            _server = new TestPipeServer(router);
        }

        public string PipeName => _server.PipeName;

        public static async Task<KernelService> StartAsync(bool autoPump, CancellationToken cancellationToken)
        {
            var service = new KernelService(autoPump);
            await service._server.StartWorkerAsync(cancellationToken);
            return service;
        }

        public async Task WaitForRoutedCommandsAsync(int count, CancellationToken cancellationToken)
        {
            for (var index = 0; index < count; index++)
            {
                await _routedCommands.WaitAsync(cancellationToken);
            }
        }

        public async Task PumpCommandsAsync(CancellationToken cancellationToken)
        {
            for (var turn = 0; turn < 2; turn++)
            {
                var step = await _loop.PumpAsync(cancellationToken);
                foreach (var completed in step.CompletedCommands)
                {
                    _dispatcher.Complete(completed);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _server.DisposeAsync();
            }
            finally
            {
                _routedCommands.Dispose();
                _store.Dispose();
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
        }

        private Task<LeaseCommandResult> DispatchAndSignal(
            LeaseCommand command,
            CancellationToken cancellationToken)
        {
            var completion = _dispatcher.PostAndWaitAsync(command, cancellationToken);
            _routedCommands.Release();
            return completion;
        }

        private async Task<LeaseCommandResult> DispatchAndPumpAsync(
            LeaseCommand command,
            CancellationToken cancellationToken)
        {
            var completion = _dispatcher.PostAndWaitAsync(command, cancellationToken);
            for (var turn = 0; turn < 2 && !completion.IsCompleted; turn++)
            {
                var step = await _loop.PumpAsync(cancellationToken);
                foreach (var completed in step.CompletedCommands)
                {
                    _dispatcher.Complete(completed);
                }
            }

            return await completion;
        }

        private LeaseLoadResult LoadActiveLeases()
        {
            lock (_storeGate.SyncRoot)
            {
                return _store.LoadLeases(LeaseStatus.Active);
            }
        }
    }

    private sealed class NoOpInhibitor : IPowerInhibitor
    {
        public PowerInhibitResult Acquire(long generation) => PowerInhibitResult.Held;

        public void Close(long generation)
        {
        }
    }

    private sealed class FakeTimeZones : ITimeZoneProvider
    {
        public TimeZoneInfo Current => TimeZoneInfo.Utc;

        public void Refresh()
        {
        }
    }

    private sealed class FakeClock : IClock
    {
        private static readonly Guid Epoch = new("a6398661-3a42-4f53-9109-76c27feacacf");

        public DateTimeOffset UtcNow { get; } = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

        public MonotonicStamp Now { get; private set; } = new(Epoch, TimeSpan.FromMinutes(1));

        public void BeginNewEpoch()
        {
            Now = new MonotonicStamp(Guid.NewGuid(), TimeSpan.Zero);
        }
    }
}
