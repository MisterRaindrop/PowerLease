using System.Security;
using PowerLease.Application.Inhibitors;

namespace PowerLease.Infrastructure.Windows.Sources;

/// <summary>Checks the configured keep-awake file without treating access failures as absence.</summary>
public sealed class WindowsLockFileProbe : ILockFileProbe
{
    public LockFileProbe Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return LockFileProbe.Unavailable("The lock-file path is empty.");
        }

        try
        {
            if (File.Exists(path))
            {
                return LockFileProbe.Present();
            }

            // File.Exists intentionally suppresses access and I/O errors. GetAttributes distinguishes those
            // failures from an object that genuinely is not present, preserving "could not tell" semantics.
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0
                ? LockFileProbe.Present()
                : LockFileProbe.Absent();
        }
        catch (FileNotFoundException)
        {
            return LockFileProbe.Absent();
        }
        catch (DirectoryNotFoundException)
        {
            return LockFileProbe.Absent();
        }
        catch (DriveNotFoundException exception)
        {
            return LockFileProbe.Unavailable($"The drive for lock file '{path}' is unavailable: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return LockFileProbe.Unavailable($"Access to lock file '{path}' was denied: {exception.Message}");
        }
        catch (IOException exception)
        {
            return LockFileProbe.Unavailable($"Lock file '{path}' could not be checked: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return LockFileProbe.Unavailable($"Lock-file path '{path}' is invalid: {exception.Message}");
        }
        catch (NotSupportedException exception)
        {
            return LockFileProbe.Unavailable($"Lock-file path '{path}' is not supported: {exception.Message}");
        }
        catch (SecurityException exception)
        {
            return LockFileProbe.Unavailable($"Security policy prevented checking lock file '{path}': {exception.Message}");
        }
    }
}
