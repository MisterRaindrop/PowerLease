using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PowerLease.Application.Inhibitors;

namespace PowerLease.Infrastructure.Windows.Sources;

/// <summary>Enumerates Windows processes and reads command lines where the process token permits it.</summary>
public sealed class ProcessSnapshotProvider : IProcessSnapshotProvider
{
    private const uint ToolhelpSnapshotProcesses = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const int ErrorNoMoreFiles = 18;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusSuccess = 0;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const uint MaximumCommandLineBytes = 1024 * 1024;

    public ProcessSnapshot GetProcesses()
    {
        try
        {
            using var snapshot = NativeMethods.CreateToolhelp32Snapshot(ToolhelpSnapshotProcesses, 0);
            if (snapshot.IsInvalid)
            {
                return ProcessSnapshot.Unavailable(
                    $"CreateToolhelp32Snapshot could not enumerate processes: " +
                    $"{new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            var entry = CreateEntry();
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                var error = Marshal.GetLastPInvokeError();
                return error == ErrorNoMoreFiles
                    ? ProcessSnapshot.Of()
                    : ProcessSnapshot.Unavailable(
                        $"Process32First could not read the process snapshot: " +
                        $"{new Win32Exception(error).Message}");
            }

            var processes = new List<ProcessInfo>();
            while (true)
            {
                if (entry.ProcessId > int.MaxValue)
                {
                    return ProcessSnapshot.Unavailable(
                        $"Windows reported process ID {entry.ProcessId}, which cannot be represented by the application.");
                }

                var processId = (int)entry.ProcessId;
                var name = Path.GetFileNameWithoutExtension(entry.ExecutableFile) ?? entry.ExecutableFile;
                processes.Add(new ProcessInfo(processId, name, ReadCommandLine(entry.ProcessId)));

                entry = CreateEntry();
                if (NativeMethods.Process32Next(snapshot, ref entry))
                {
                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorNoMoreFiles)
                {
                    return ProcessSnapshot.Unavailable(
                        $"Process32Next could not finish reading the process snapshot: " +
                        $"{new Win32Exception(error).Message}");
                }

                break;
            }

            return ProcessSnapshot.Of([.. processes]);
        }
        catch (DllNotFoundException exception)
        {
            return ProcessSnapshot.Unavailable($"The Windows process APIs could not be loaded: {exception.Message}");
        }
        catch (EntryPointNotFoundException exception)
        {
            return ProcessSnapshot.Unavailable($"A required Windows process API is unavailable: {exception.Message}");
        }
        catch (OutOfMemoryException exception)
        {
            return ProcessSnapshot.Unavailable($"The process snapshot could not be allocated: {exception.Message}");
        }
    }

    private static ProcessEntry32 CreateEntry()
    {
        return new ProcessEntry32
        {
            Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
            ExecutableFile = string.Empty
        };
    }

    private static string? ReadCommandLine(uint processId)
    {
        try
        {
            using var process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
            if (process.IsInvalid)
            {
                return null;
            }

            var status = NativeMethods.NtQueryInformationProcess(
                process,
                ProcessCommandLineInformation,
                IntPtr.Zero,
                0,
                out var requiredBytes);

            if (status is not StatusInfoLengthMismatch and not StatusBufferTooSmall
                || requiredBytes < checked((uint)Marshal.SizeOf<UnicodeString>())
                || requiredBytes > MaximumCommandLineBytes)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                status = NativeMethods.NtQueryInformationProcess(
                    process,
                    ProcessCommandLineInformation,
                    buffer,
                    requiredBytes,
                    out _);
                if (status != StatusSuccess)
                {
                    return null;
                }

                var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (commandLine.Length == 0)
                {
                    return string.Empty;
                }

                if (commandLine.Length > commandLine.MaximumLength
                    || commandLine.Buffer == IntPtr.Zero
                    || !PointsInsideBuffer(buffer, requiredBytes, commandLine.Buffer, commandLine.Length))
                {
                    return null;
                }

                return Marshal.PtrToStringUni(commandLine.Buffer, commandLine.Length / sizeof(char));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool PointsInsideBuffer(IntPtr buffer, uint bufferLength, IntPtr value, ushort valueLength)
    {
        var start = buffer.ToInt64();
        var end = checked(start + bufferLength);
        var valueStart = value.ToInt64();
        var valueEnd = checked(valueStart + valueLength);
        return valueStart >= start && valueEnd >= valueStart && valueEnd <= end;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private sealed class SafeToolhelpSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeToolhelpSnapshotHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // DllImport is used to keep the reviewed Win32 signatures explicit.
        // CreateToolhelp32Snapshot creates a process snapshot; failure returns INVALID_HANDLE_VALUE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeToolhelpSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        // Process32FirstW reads the first snapshot entry; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(SafeToolhelpSnapshotHandle snapshot, ref ProcessEntry32 entry);

        // Process32NextW reads the next snapshot entry; failure returns FALSE and sets ERROR_NO_MORE_FILES at the end.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(SafeToolhelpSnapshotHandle snapshot, ref ProcessEntry32 entry);

        // OpenProcess opens one process; failure returns NULL and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        // NtQueryInformationProcess returns an NTSTATUS; nonzero means the command line could not be read.
        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            SafeProcessHandle process,
            int informationClass,
            IntPtr processInformation,
            uint processInformationLength,
            out uint returnLength);

        // CloseHandle releases a Toolhelp snapshot; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
    }
}
