using PowerLease.Ipc.Contracts;

namespace PowerLease.Cli;

/// <summary>
/// Talks to the service.
/// <para>
/// An interface so the command behaviour -- what is printed and what is returned to the shell -- can be tested
/// without a running service or a named pipe, which exist only on Windows.
/// </para>
/// </summary>
public interface IPowerLeaseClient
{
    Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken);

    Task<ListLeasesResponse> ListLeasesAsync(CancellationToken cancellationToken);

    Task<CommandResponse> CreateLeaseAsync(TimeSpan duration, string? reason, CancellationToken cancellationToken);

    Task<CommandResponse> ReleaseLeaseAsync(string? leaseId, CancellationToken cancellationToken);

    Task<WakeStatusResponse> GetWakeStatusAsync(CancellationToken cancellationToken);
}

/// <summary>The service could not be reached, as distinct from having refused the request.</summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException(string message)
        : base(message)
    {
    }

    public ServiceUnavailableException()
        : base("The PowerLease service could not be reached.")
    {
    }

    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
