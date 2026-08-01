using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PowerLease.Application.Kernel;

namespace PowerLease.Infrastructure.Windows.Power;

/// <summary>Owns one Windows power-request handle for each kernel generation.</summary>
public sealed class PowerRequestManager : IPowerInhibitor
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x00000001;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorAccessDisabledByPolicy = 1260;

    private readonly object _sync = new();
    private readonly Dictionary<long, SafePowerRequestHandle> _requests = [];

    public PowerInhibitResult Acquire(long generation)
    {
        SafePowerRequestHandle request;

        try
        {
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = "PowerLease is holding this machine awake."
            };

            request = NativeMethods.PowerCreateRequest(ref context);
        }
        catch (DllNotFoundException)
        {
            return PowerInhibitResult.Uncertain;
        }
        catch (EntryPointNotFoundException)
        {
            return PowerInhibitResult.Uncertain;
        }

        if (request.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            request.Dispose();
            return ClassifyFailure(error);
        }

        bool wasSet;
        try
        {
            wasSet = NativeMethods.PowerSetRequest(request, PowerRequestType.SystemRequired);
        }
        catch (DllNotFoundException)
        {
            request.Dispose();
            return PowerInhibitResult.Uncertain;
        }
        catch (EntryPointNotFoundException)
        {
            request.Dispose();
            return PowerInhibitResult.Uncertain;
        }

        if (!wasSet)
        {
            var error = Marshal.GetLastPInvokeError();
            request.Dispose();
            return ClassifyFailure(error);
        }

        SafePowerRequestHandle? previous = null;
        try
        {
            lock (_sync)
            {
                _requests.Remove(generation, out previous);
                _requests.Add(generation, request);
            }
        }
#pragma warning disable CA1031 // A bookkeeping failure must not strand a live power request.
        catch (Exception)
#pragma warning restore CA1031
        {
            ReleaseNoThrow(request);
            ReleaseNoThrow(previous);
            return PowerInhibitResult.Uncertain;
        }

        // A repeated generation is not expected from the coordinator. If it happens, the new request is
        // established before the old one is closed, so protection never has a gap and the generation still
        // owns exactly one handle.
        ReleaseNoThrow(previous);
        return PowerInhibitResult.Held;
    }

    public void Close(long generation)
    {
        SafePowerRequestHandle? request;
        lock (_sync)
        {
            _requests.Remove(generation, out request);
        }

        ReleaseNoThrow(request);
    }

    private static PowerInhibitResult ClassifyFailure(int error)
    {
        return error is ErrorAccessDenied or ErrorNotSupported or ErrorAccessDisabledByPolicy
            ? PowerInhibitResult.Rejected
            : PowerInhibitResult.Uncertain;
    }

    private static void ReleaseNoThrow(SafePowerRequestHandle? request)
    {
        if (request is null || request.IsClosed)
        {
            return;
        }

        try
        {
            if (!request.IsInvalid)
            {
                _ = NativeMethods.PowerClearRequest(request, PowerRequestType.SystemRequired);
            }
        }
#pragma warning disable CA1031 // Closing the handle is the authoritative cleanup even if clearing fails.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Safe-handle disposal below removes every request associated with this generation.
        }
        finally
        {
            request.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    /// <summary>
    /// POWER_REQUEST_TYPE. The values matter and are not in the order one would guess: display comes first, so
    /// zero is a request to keep the SCREEN on, not the machine. Getting this wrong is invisible -- a display
    /// request keeps the system awake as a side effect, so a machine would still stay up and the mistake would
    /// only show as the wrong category in powercfg /requests and a lit screen on a headless machine.
    /// </summary>
    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3
    }

    private sealed class SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafePowerRequestHandle()
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
        // PowerCreateRequest creates a request handle; failure returns INVALID_HANDLE_VALUE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafePowerRequestHandle PowerCreateRequest(ref ReasonContext context);

        // PowerSetRequest increments this request type; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PowerSetRequest(
            SafePowerRequestHandle powerRequest,
            PowerRequestType requestType);

        // PowerClearRequest decrements this request type; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PowerClearRequest(
            SafePowerRequestHandle powerRequest,
            PowerRequestType requestType);

        // CloseHandle releases a kernel handle; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
    }
}
