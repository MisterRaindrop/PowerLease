namespace PowerLease.Cli;

/// <summary>
/// What the command returns to the shell.
/// <para>
/// Distinct codes because a script needs to tell the cases apart. In particular
/// <see cref="ProtectionFailing" /> is not an error in the command: it ran, it got an answer, and the answer is
/// that the machine may sleep while somebody is using it. Reporting that as success would hide the one condition
/// the product exists to prevent behind a green tick.
/// </para>
/// </summary>
public static class ExitCode
{
    public const int Success = 0;

    /// <summary>The kernel wants to hold the machine awake and the system is refusing.</summary>
    public const int ProtectionFailing = 1;

    public const int UsageError = 2;

    public const int ServiceUnavailable = 3;

    /// <summary>The service answered and declined.</summary>
    public const int RequestRefused = 4;
}
