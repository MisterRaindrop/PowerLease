namespace PowerLease.Ipc.Contracts;

public static class IpcProtocol
{
    public const int Version = 1;
    public const string PipeName = "PowerLease.Service.v1";
    public const int MaxMessageBytes = 256 * 1024;
}
