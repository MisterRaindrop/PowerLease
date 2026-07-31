namespace PowerLease.Domain;

/// <summary>
/// Who asked for the machine to be kept awake. Mirrors the keep-awake sources in the product
/// specification; not every one is produced by this version.
/// </summary>
public enum LeaseSource
{
    SshSession,
    Manual,
    Schedule,
    ProtectedProcess,
    Cli,
    DriftClient,
    LongRunningTask,
    HighSystemActivity,
    LockFile
}
