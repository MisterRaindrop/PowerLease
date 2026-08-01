using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;
using PowerLease.Application.Inhibitors;

namespace PowerLease.Infrastructure.Windows.Sources;

/// <summary>Reads system load without converting failed measurements into quiet zeroes.</summary>
public sealed class SystemMetricProvider : ISystemMetricProvider, IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhFormatDouble = 0x00000200;
    private const uint PdhCStatusValidData = 0x00000000;
    private const uint PdhCStatusNewData = 0x00000001;
    private const string DiskCounterPath = @"\PhysicalDisk(_Total)\Disk Bytes/sec";

    private readonly object _sync = new();
    private CpuReading? _previousCpu;
    private NetworkReading? _previousNetwork;
    private SafePdhQueryHandle? _diskQuery;
    private IntPtr _diskCounter;
    private bool _diskPrimed;
    private bool _disposed;

    public SystemMetricSample Read()
    {
        lock (_sync)
        {
            return new SystemMetricSample(
                ReadCpuPercent(),
                ReadMemoryPercent(),
                ReadDiskBytesPerSecond(),
                ReadNetworkBytesPerSecond());
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _diskQuery?.Dispose();
            _diskQuery = null;
            _diskCounter = IntPtr.Zero;
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private double? ReadCpuPercent()
    {
        try
        {
            if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                return null;
            }

            var current = new CpuReading(
                ToUInt64(idleTime),
                ToUInt64(kernelTime),
                ToUInt64(userTime));
            var previous = _previousCpu;
            _previousCpu = current;

            if (previous is null
                || current.Idle < previous.Idle
                || current.Kernel < previous.Kernel
                || current.User < previous.User)
            {
                return null;
            }

            var idleDelta = current.Idle - previous.Idle;
            var kernelDelta = current.Kernel - previous.Kernel;
            var userDelta = current.User - previous.User;
            var totalDelta = kernelDelta + userDelta;
            if (totalDelta == 0 || idleDelta > totalDelta)
            {
                return null;
            }

            var percent = 100d * (totalDelta - idleDelta) / totalDelta;
            return IsValidNonnegative(percent) && percent <= 100d ? percent : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static double? ReadMemoryPercent()
    {
        var status = new MemoryStatusEx
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>())
        };

        try
        {
            if (!NativeMethods.GlobalMemoryStatusEx(ref status)
                || status.TotalPhysical == 0
                || status.AvailablePhysical > status.TotalPhysical)
            {
                return null;
            }

            var percent = 100d * (status.TotalPhysical - status.AvailablePhysical) / status.TotalPhysical;
            return IsValidNonnegative(percent) && percent <= 100d ? percent : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private double? ReadDiskBytesPerSecond()
    {
        if (_disposed || !EnsureDiskCounter())
        {
            return null;
        }

        try
        {
            if (NativeMethods.PdhCollectQueryData(_diskQuery!) != ErrorSuccess)
            {
                ResetDiskCounter();
                return null;
            }

            if (!_diskPrimed)
            {
                _diskPrimed = true;
                return null;
            }

            var status = NativeMethods.PdhGetFormattedCounterValue(
                _diskCounter,
                PdhFormatDouble,
                out _,
                out var value);

            if (status != ErrorSuccess
                || value.Status is not PdhCStatusValidData and not PdhCStatusNewData
                || !IsValidNonnegative(value.DoubleValue))
            {
                return null;
            }

            return value.DoubleValue;
        }
        catch (DllNotFoundException)
        {
            ResetDiskCounter();
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            ResetDiskCounter();
            return null;
        }
    }

    private bool EnsureDiskCounter()
    {
        if (_diskQuery is not null && !_diskQuery.IsInvalid && !_diskQuery.IsClosed)
        {
            return true;
        }

        SafePdhQueryHandle? query = null;
        try
        {
            var openStatus = NativeMethods.PdhOpenQuery(null, UIntPtr.Zero, out var openedQuery);
            query = openedQuery;
            if (openStatus != ErrorSuccess)
            {
                return false;
            }

            if (NativeMethods.PdhAddEnglishCounter(
                    query,
                    DiskCounterPath,
                    UIntPtr.Zero,
                    out var counter) != ErrorSuccess)
            {
                query.Dispose();
                return false;
            }

            _diskQuery = query;
            query = null;
            _diskCounter = counter;
            _diskPrimed = false;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            query?.Dispose();
        }
    }

    private double? ReadNetworkBytesPerSecond()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            if (interfaces.Length == 0)
            {
                return null;
            }

            ulong totalBytes = 0;
            foreach (var networkInterface in interfaces)
            {
                var statistics = networkInterface.GetIPStatistics();
                totalBytes = checked(totalBytes + (ulong)statistics.BytesReceived + (ulong)statistics.BytesSent);
            }

            var current = new NetworkReading(totalBytes, Stopwatch.GetTimestamp());
            var previous = _previousNetwork;
            _previousNetwork = current;

            if (previous is null || current.TotalBytes < previous.TotalBytes)
            {
                return null;
            }

            var elapsedTicks = current.Timestamp - previous.Timestamp;
            if (elapsedTicks <= 0)
            {
                return null;
            }

            var seconds = elapsedTicks / (double)Stopwatch.Frequency;
            var rate = (current.TotalBytes - previous.TotalBytes) / seconds;
            return IsValidNonnegative(rate) ? rate : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private void ResetDiskCounter()
    {
        _diskQuery?.Dispose();
        _diskQuery = null;
        _diskCounter = IntPtr.Zero;
        _diskPrimed = false;
    }

    private static bool IsValidNonnegative(double value) =>
        value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value);

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    private sealed record CpuReading(ulong Idle, ulong Kernel, ulong User);

    private sealed record NetworkReading(ulong TotalBytes, long Timestamp);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    // MEMORYSTATUSEX is 64 bytes; only the initialized length and physical-memory fields are projected.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct MemoryStatusEx
    {
        [FieldOffset(0)]
        public uint Length;

        [FieldOffset(8)]
        public ulong TotalPhysical;

        [FieldOffset(16)]
        public ulong AvailablePhysical;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    private sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafePdhQueryHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.PdhCloseQuery(handle) == ErrorSuccess;
        }
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // DllImport is used to keep the reviewed Win32 signatures explicit.
        // GetSystemTimes fills idle, kernel, and user counters; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        // GlobalMemoryStatusEx fills MEMORYSTATUSEX; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        // PdhOpenQueryW creates a performance query; failure is a nonzero PDH status code.
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
        internal static extern uint PdhOpenQuery(
            string? dataSource,
            UIntPtr userData,
            out SafePdhQueryHandle query);

        // PdhAddEnglishCounterW adds a locale-independent counter; failure is a nonzero PDH status code.
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        internal static extern uint PdhAddEnglishCounter(
            SafePdhQueryHandle query,
            string counterPath,
            UIntPtr userData,
            out IntPtr counter);

        // PdhCollectQueryData samples all query counters; failure is a nonzero PDH status code.
        [DllImport("pdh.dll")]
        internal static extern uint PdhCollectQueryData(SafePdhQueryHandle query);

        // PdhGetFormattedCounterValue formats one counter; failure is a nonzero PDH status code.
        [DllImport("pdh.dll")]
        internal static extern uint PdhGetFormattedCounterValue(
            IntPtr counter,
            uint format,
            out uint counterType,
            out PdhFormattedCounterValue value);

        // PdhCloseQuery releases a performance query; failure is a nonzero PDH status code.
        [DllImport("pdh.dll")]
        internal static extern uint PdhCloseQuery(IntPtr query);
#pragma warning restore SYSLIB1054
    }
}
