using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PowerLease.Infrastructure.Windows.Power;

/// <summary>The power facts used by the wake-status response, with unknown kept distinct from false.</summary>
public sealed record PowerCapabilitySnapshot(
    bool? SystemRequiredHonouredOnMains,
    bool? SystemRequiredHonouredOnBattery,
    bool? ModernStandby,
    bool? RunningOnBattery,
    IReadOnlyList<string> Unavailable);

internal interface IPowerCapabilityNativeMethods
{
    uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    uint PowerReadACValue(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid powerSettingGuid,
        out uint type,
        out uint buffer,
        ref uint bufferSize);

    uint PowerReadDCValue(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid powerSettingGuid,
        out uint type,
        out uint buffer,
        ref uint bufferSize);

    bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    IntPtr LocalFree(IntPtr memory);

    int GetLastError();
}

// SYSTEM_POWER_CAPABILITIES is 76 bytes in the Windows SDK ABI; AoAc is the byte at offset 20.
[StructLayout(LayoutKind.Explicit, Size = 76)]
internal struct SystemPowerCapabilities
{
    [FieldOffset(20)]
    public byte AoAc;
}

// SYSTEM_POWER_STATUS is 12 bytes; ACLineStatus is its first byte.
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct SystemPowerStatus
{
    [FieldOffset(0)]
    public byte AcLineStatus;
}

/// <summary>
/// Reads what the machine's power configuration allows.
/// <para>
/// An interface because the host has to answer <c>powerlease wake-status</c> from it, and because a fake is the
/// only way to test the case that matters: a fact that could not be determined must reach the user as "unknown"
/// rather than as "no". Sending someone to change a setting that was never the problem is the failure this
/// guards against.
/// </para>
/// </summary>
public interface IPowerCapabilityProbe
{
    PowerCapabilitySnapshot Read();
}

/// <summary>Reads the active Windows power scheme and hardware power capabilities.</summary>
public sealed class PowerCapabilityProbe : IPowerCapabilityProbe
{
    private const uint ErrorSuccess = 0;
    private const uint RegDword = 4;

    // GUID_SLEEP_SUBGROUP: the Sleep subgroup in a Windows power scheme.
    private static readonly Guid SleepSubgroup = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");

    // GUID_ALLOW_SYSTEM_REQUIRED: "Allow system required requests" (powercfg alias SYSTEMREQUIRED).
    private static readonly Guid AllowSystemRequired = new("A4B195F5-8225-47D8-8012-9D41369786E2");

    private readonly IPowerCapabilityNativeMethods _native;

    public PowerCapabilityProbe()
        : this(new WindowsPowerCapabilityNativeMethods())
    {
    }

    internal PowerCapabilityProbe(IPowerCapabilityNativeMethods native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public PowerCapabilitySnapshot Read()
    {
        var unavailable = new List<string>();
        var (onMains, onBattery) = ReadSystemRequiredPolicy(unavailable);
        var modernStandby = ReadModernStandby(unavailable);
        var runningOnBattery = ReadBatteryState(unavailable);

        return new PowerCapabilitySnapshot(
            onMains,
            onBattery,
            modernStandby,
            runningOnBattery,
            unavailable.ToArray());
    }

    private (bool? OnMains, bool? OnBattery) ReadSystemRequiredPolicy(List<string> unavailable)
    {
        IntPtr schemePointer = IntPtr.Zero;
        try
        {
            uint status;
            try
            {
                status = _native.PowerGetActiveScheme(IntPtr.Zero, out schemePointer);
            }
            catch (DllNotFoundException exception)
            {
                RecordPolicyUnavailable(unavailable, $"PowerGetActiveScheme could not be called: {exception.Message}");
                return (null, null);
            }
            catch (EntryPointNotFoundException exception)
            {
                RecordPolicyUnavailable(unavailable, $"PowerGetActiveScheme is not available: {exception.Message}");
                return (null, null);
            }

            if (status != ErrorSuccess || schemePointer == IntPtr.Zero)
            {
                RecordPolicyUnavailable(
                    unavailable,
                    status == ErrorSuccess
                        ? "PowerGetActiveScheme returned no active scheme."
                        : $"PowerGetActiveScheme failed: {DescribeStatus(status)}");
                return (null, null);
            }

            Guid scheme;
            try
            {
                scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            }
            catch (ArgumentException exception)
            {
                RecordPolicyUnavailable(
                    unavailable,
                    $"PowerGetActiveScheme returned an unreadable scheme identifier: {exception.Message}");
                return (null, null);
            }

            var onMains = ReadPolicyValue(scheme, useAcValue: true, unavailable);
            var onBattery = ReadPolicyValue(scheme, useAcValue: false, unavailable);
            return (onMains, onBattery);
        }
        finally
        {
            if (schemePointer != IntPtr.Zero)
            {
                _ = _native.LocalFree(schemePointer);
            }
        }
    }

    private bool? ReadPolicyValue(Guid scheme, bool useAcValue, List<string> unavailable)
    {
        var subgroup = SleepSubgroup;
        var setting = AllowSystemRequired;
        uint type;
        uint value;
        uint size = sizeof(uint);
        uint status;

        try
        {
            status = useAcValue
                ? _native.PowerReadACValue(
                    IntPtr.Zero,
                    ref scheme,
                    ref subgroup,
                    ref setting,
                    out type,
                    out value,
                    ref size)
                : _native.PowerReadDCValue(
                    IntPtr.Zero,
                    ref scheme,
                    ref subgroup,
                    ref setting,
                    out type,
                    out value,
                    ref size);
        }
        catch (DllNotFoundException exception)
        {
            unavailable.Add($"{PolicyCall(useAcValue)} could not be called: {exception.Message}");
            return null;
        }
        catch (EntryPointNotFoundException exception)
        {
            unavailable.Add($"{PolicyCall(useAcValue)} is not available: {exception.Message}");
            return null;
        }

        if (status != ErrorSuccess)
        {
            unavailable.Add($"{PolicyCall(useAcValue)} failed for SYSTEMREQUIRED: {DescribeStatus(status)}");
            return null;
        }

        if (type != RegDword || size != sizeof(uint) || value > 1)
        {
            unavailable.Add(
                $"{PolicyCall(useAcValue)} returned an unexpected SYSTEMREQUIRED value " +
                $"(type {type}, size {size}, value {value}).");
            return null;
        }

        return value == 1;
    }

    private bool? ReadModernStandby(List<string> unavailable)
    {
        try
        {
            if (_native.GetPwrCapabilities(out var capabilities))
            {
                return capabilities.AoAc != 0;
            }

            unavailable.Add(
                $"GetPwrCapabilities failed, so modern standby is unknown: " +
                $"{new Win32Exception(_native.GetLastError()).Message}");
        }
        catch (DllNotFoundException exception)
        {
            unavailable.Add($"GetPwrCapabilities could not be called: {exception.Message}");
        }
        catch (EntryPointNotFoundException exception)
        {
            unavailable.Add($"GetPwrCapabilities is not available: {exception.Message}");
        }

        return null;
    }

    private bool? ReadBatteryState(List<string> unavailable)
    {
        try
        {
            if (!_native.GetSystemPowerStatus(out var status))
            {
                unavailable.Add(
                    $"GetSystemPowerStatus failed, so the current power source is unknown: " +
                    $"{new Win32Exception(_native.GetLastError()).Message}");
                return null;
            }

            return status.AcLineStatus switch
            {
                0 => true,
                1 => false,
                _ => RecordUnknownPowerSource(unavailable)
            };
        }
        catch (DllNotFoundException exception)
        {
            unavailable.Add($"GetSystemPowerStatus could not be called: {exception.Message}");
        }
        catch (EntryPointNotFoundException exception)
        {
            unavailable.Add($"GetSystemPowerStatus is not available: {exception.Message}");
        }

        return null;
    }

    private static bool? RecordUnknownPowerSource(List<string> unavailable)
    {
        unavailable.Add("GetSystemPowerStatus reported an unknown AC-line state.");
        return null;
    }

    private static void RecordPolicyUnavailable(List<string> unavailable, string detail)
    {
        unavailable.Add($"Keep-awake policy on mains is unknown: {detail}");
        unavailable.Add($"Keep-awake policy on battery is unknown: {detail}");
    }

    private static string PolicyCall(bool useAcValue) => useAcValue ? "PowerReadACValue" : "PowerReadDCValue";

    private static string DescribeStatus(uint status) =>
        $"{new Win32Exception(unchecked((int)status)).Message} (error {status})";

}

internal sealed class WindowsPowerCapabilityNativeMethods : IPowerCapabilityNativeMethods
{
    public uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid) =>
        NativeMethods.PowerGetActiveScheme(userRootPowerKey, out activePolicyGuid);

    public uint PowerReadACValue(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid powerSettingGuid,
        out uint type,
        out uint buffer,
        ref uint bufferSize) =>
        NativeMethods.PowerReadACValue(
            rootPowerKey,
            ref schemeGuid,
            ref subgroupGuid,
            ref powerSettingGuid,
            out type,
            out buffer,
            ref bufferSize);

    public uint PowerReadDCValue(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid powerSettingGuid,
        out uint type,
        out uint buffer,
        ref uint bufferSize) =>
        NativeMethods.PowerReadDCValue(
            rootPowerKey,
            ref schemeGuid,
            ref subgroupGuid,
            ref powerSettingGuid,
            out type,
            out buffer,
            ref bufferSize);

    public bool GetPwrCapabilities(out SystemPowerCapabilities capabilities) =>
        NativeMethods.GetPwrCapabilities(out capabilities);

    public bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus) =>
        NativeMethods.GetSystemPowerStatus(out systemPowerStatus);

    public IntPtr LocalFree(IntPtr memory) => NativeMethods.LocalFree(memory);

    public int GetLastError() => Marshal.GetLastPInvokeError();

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // DllImport is used to keep the reviewed Win32 signatures explicit.
        // PowerGetActiveScheme allocates the active GUID; failure is a nonzero status code.
        [DllImport("powrprof.dll")]
        internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        // PowerReadACValue reads an AC scheme value; failure is a nonzero status code.
        [DllImport("powrprof.dll")]
        internal static extern uint PowerReadACValue(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupGuid,
            ref Guid powerSettingGuid,
            out uint type,
            out uint buffer,
            ref uint bufferSize);

        // PowerReadDCValue reads a DC scheme value; failure is a nonzero status code.
        [DllImport("powrprof.dll")]
        internal static extern uint PowerReadDCValue(
            IntPtr rootPowerKey,
            ref Guid schemeGuid,
            ref Guid subgroupGuid,
            ref Guid powerSettingGuid,
            out uint type,
            out uint buffer,
            ref uint bufferSize);

        // GetPwrCapabilities fills SYSTEM_POWER_CAPABILITIES; failure returns zero and sets last error.
        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

        // GetSystemPowerStatus fills SYSTEM_POWER_STATUS; failure returns FALSE and sets last error.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

        // LocalFree releases PowerGetActiveScheme memory; failure returns the original pointer.
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
    }
}
