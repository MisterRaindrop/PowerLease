namespace PowerLease.Domain;

public enum InhibitorKind
{
    SshSession,
    ManualLease,
    CliLease,
    ProtectedProcess,
    LockFile,
    ScheduleWindow,
    SystemActivity,
    GracePeriod,
    Fault,
    ProducerUnhealthy
}
