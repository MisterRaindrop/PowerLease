namespace PowerLease.Ipc.Contracts;

/// <summary>What a caller must be to issue a request.</summary>
public enum IpcAccessLevel
{
    /// <summary>
    /// Any authenticated user of this machine. Reading what the service is doing, and managing holds the caller
    /// created itself.
    /// </summary>
    AnyAuthenticatedUser,

    /// <summary>
    /// A machine administrator. Changing configuration, and ending holds that belong to somebody else.
    /// </summary>
    Administrator
}

/// <summary>
/// The requests the service answers, and what each one requires.
/// <para>
/// The permission belongs here, next to the method name, rather than being decided at each call site. A server
/// that has to remember to check is a server that will one day forget, and the thing it would forget to guard is
/// the ability to end somebody else's hold -- which is to say, to make a machine sleep while they are using it.
/// </para>
/// </summary>
public static class IpcMethods
{
    public const string GetStatus = "GetStatus";
    public const string ListLeases = "ListLeases";
    public const string CreateLease = "CreateLease";
    public const string RenewLease = "RenewLease";
    public const string ReleaseLease = "ReleaseLease";
    public const string GetRules = "GetRules";
    public const string UpdateRules = "UpdateRules";
    public const string GetSchedules = "GetSchedules";
    public const string UpdateSchedules = "UpdateSchedules";
    public const string GetHistory = "GetHistory";
    public const string GetLogs = "GetLogs";
    public const string GetWakeStatus = "GetWakeStatus";

    private static readonly Dictionary<string, IpcAccessLevel> Required = new(StringComparer.Ordinal)
    {
        [GetStatus] = IpcAccessLevel.AnyAuthenticatedUser,
        [ListLeases] = IpcAccessLevel.AnyAuthenticatedUser,
        [CreateLease] = IpcAccessLevel.AnyAuthenticatedUser,
        [RenewLease] = IpcAccessLevel.AnyAuthenticatedUser,
        [ReleaseLease] = IpcAccessLevel.AnyAuthenticatedUser,
        [GetHistory] = IpcAccessLevel.AnyAuthenticatedUser,
        [GetWakeStatus] = IpcAccessLevel.AnyAuthenticatedUser,

        // Reading the configuration is harmless; changing it decides when a machine may sleep.
        [GetRules] = IpcAccessLevel.AnyAuthenticatedUser,
        [GetSchedules] = IpcAccessLevel.AnyAuthenticatedUser,
        [UpdateRules] = IpcAccessLevel.Administrator,
        [UpdateSchedules] = IpcAccessLevel.Administrator,

        // Logs can name accounts and remote addresses.
        [GetLogs] = IpcAccessLevel.Administrator
    };

    /// <summary>Every method the service answers.</summary>
    public static IReadOnlyCollection<string> All => Required.Keys;

    /// <summary>
    /// What <paramref name="method" /> requires.
    /// </summary>
    /// <returns>
    /// False when the method is not one the service answers. An unknown method is refused rather than defaulted,
    /// because defaulting it either locks out something that should work or opens something that should not.
    /// </returns>
    public static bool TryGetRequiredAccess(string method, out IpcAccessLevel access) =>
        Required.TryGetValue(method, out access);

    /// <summary>
    /// Whether a caller may issue <paramref name="method" />.
    /// <para>
    /// Note that permission to issue <see cref="ReleaseLease" /> is not permission to release any particular
    /// hold: an ordinary user may only end one they created, and the kernel is what decides that, from the
    /// identity captured when the request arrived rather than from anything in the request body.
    /// </para>
    /// </summary>
    public static bool IsAllowed(string method, bool callerIsAdministrator) =>
        TryGetRequiredAccess(method, out var required)
        && (callerIsAdministrator || required == IpcAccessLevel.AnyAuthenticatedUser);
}
