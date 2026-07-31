using PowerLease.Persistence;

namespace PowerLease.Persistence.Tests;

/// <summary>
/// A throwaway data directory. Every test gets its own so nothing leaks between them, and the
/// directory is deleted afterwards even when a test fails.
/// </summary>
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "powerlease-tests", Guid.NewGuid().ToString("n"));
        Paths = new PowerLeasePaths(Path);
    }

    public string Path { get; }

    public PowerLeasePaths Paths { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
