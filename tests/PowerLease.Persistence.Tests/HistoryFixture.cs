using PowerLease.Domain;
using PowerLease.Persistence.History;
using PowerLease.Persistence.Sqlite;

namespace PowerLease.Persistence.Tests;

/// <summary>A migrated database with a store open on it, plus the session samples have to belong to.</summary>
internal sealed class HistoryFixture : IDisposable
{
    public const string SessionId = "session-1";

    private readonly TempRoot _root;

    public HistoryFixture()
    {
        _root = new TempRoot();
        Clock = new FakeClock(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        new MigrationRunner(_root.Paths, Clock).Run();
        Store = new SqliteHistoryStore(new SqliteConnectionFactory(_root.Paths.DatabasePath));
    }

    public FakeClock Clock { get; }

    public SqliteHistoryStore Store { get; }

    public void StartSession()
    {
        Store.StartPowerSession(new PowerSession
        {
            Id = SessionId,
            BootId = "boot-1",
            StartedAtUtc = Clock.UtcNow,
            StartReason = "Boot"
        });
    }

    /// <summary>Add one measurement, defaulting everything the test does not care about.</summary>
    public void AddSample(
        DateTimeOffset at,
        double? cpu = 0,
        ProtectionState state = ProtectionState.Protected,
        int sshSessions = 0,
        double? memory = null,
        SampleQuality quality = SampleQuality.None)
    {
        Store.InsertRawSample(new RawSample
        {
            PowerSessionId = SessionId,
            SampledAtUtc = at,
            CpuPercent = cpu,
            MemoryPercent = memory,
            State = state,
            SshSessionCount = sshSessions,
            QualityFlags = quality
        });
    }

    public void AddMinuteBucket(DateTimeOffset bucketStartUtc, int sampleCount, double? cpuAverage)
    {
        using var transaction = Store.BeginTransaction();
        transaction.UpsertMinuteAggregate(new MetricAggregate
        {
            BucketStartUtc = bucketStartUtc,
            SampleCount = sampleCount,
            ExpectedSampleCount = 6,
            CpuAverage = cpuAverage,
            CpuMinimum = cpuAverage,
            CpuMaximum = cpuAverage,
            ProtectedFor = TimeSpan.FromSeconds(10 * sampleCount)
        });
        transaction.Commit();
    }

    public KeepAwakeLease Lease(string id = "lease-1", LeaseStatus status = LeaseStatus.Active) => new()
    {
        Id = id,
        Source = LeaseSource.SshSession,
        Reason = "SSH session from 10.0.0.5",
        OwnerUser = "liu",
        RemoteIp = "10.0.0.5",
        ProcessId = 4242,
        ProcessName = "sshd",
        StartedAtUtc = Clock.UtcNow,
        ExpiresAtUtc = Clock.UtcNow.AddHours(3),
        LastRenewedAtUtc = Clock.UtcNow.AddMinutes(10),
        AutoRenew = true,
        Status = status,
        EpochId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        OriginalDuration = TimeSpan.FromHours(3),
        LastRenewDuration = TimeSpan.FromHours(1),
        RemainingAtCheckpoint = TimeSpan.FromMinutes(150),
        CheckpointUtc = Clock.UtcNow.AddMinutes(30)
    };

    public void Dispose()
    {
        Store.Dispose();
        _root.Dispose();
    }
}
