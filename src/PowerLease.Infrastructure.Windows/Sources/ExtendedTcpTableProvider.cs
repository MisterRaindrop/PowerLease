using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using PowerLease.Application.Inhibitors;

namespace PowerLease.Infrastructure.Windows.Sources;

/// <summary>Reads established IPv4 and IPv6 TCP rows from the Windows IP Helper API.</summary>
public sealed class ExtendedTcpTableProvider : ITcpConnectionProvider
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;
    private const uint TcpStateEstablished = 5;
    private const int TableHeaderSize = sizeof(uint);
    private const int Ipv4RowSize = 24;
    private const int Ipv6RowSize = 56;

    public TcpSnapshot GetEstablishedConnections()
    {
        try
        {
            var ipv4 = ReadFamily(AddressFamilyInet, ParseIpv4Rows, "IPv4");
            if (!ipv4.Succeeded)
            {
                return TcpSnapshot.Unavailable(ipv4.Detail!);
            }

            var ipv6 = ReadFamily(AddressFamilyInet6, ParseIpv6Rows, "IPv6");
            if (!ipv6.Succeeded)
            {
                return TcpSnapshot.Unavailable(ipv6.Detail!);
            }

            return TcpSnapshot.Of([.. ipv4.Connections, .. ipv6.Connections]);
        }
        catch (DllNotFoundException exception)
        {
            return TcpSnapshot.Unavailable($"GetExtendedTcpTable could not be called: {exception.Message}");
        }
        catch (EntryPointNotFoundException exception)
        {
            return TcpSnapshot.Unavailable($"GetExtendedTcpTable is not available: {exception.Message}");
        }
        catch (OutOfMemoryException exception)
        {
            return TcpSnapshot.Unavailable($"The TCP table buffer could not be allocated: {exception.Message}");
        }
        catch (OverflowException exception)
        {
            return TcpSnapshot.Unavailable($"The TCP table was too large to represent safely: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return TcpSnapshot.Unavailable($"The TCP table contained an invalid address: {exception.Message}");
        }
    }

    private static FamilyRead ReadFamily(
        int addressFamily,
        Func<IntPtr, uint, FamilyRead> parse,
        string familyName)
    {
        uint size = 0;
        var status = NativeMethods.GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            addressFamily,
            TcpTableClass.OwnerPidConnections,
            reserved: 0);

        if (status is not ErrorInsufficientBuffer and not ErrorSuccess)
        {
            return FamilyRead.Unavailable(
                $"GetExtendedTcpTable could not size the {familyName} table: {DescribeStatus(status)}");
        }

        if (size < TableHeaderSize || size > int.MaxValue)
        {
            return FamilyRead.Unavailable(
                $"GetExtendedTcpTable returned an invalid {familyName} table size of {size} bytes.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                var suppliedSize = size;
                status = NativeMethods.GetExtendedTcpTable(
                    buffer,
                    ref suppliedSize,
                    order: false,
                    addressFamily,
                    TcpTableClass.OwnerPidConnections,
                    reserved: 0);

                if (status == ErrorInsufficientBuffer && attempt == 0)
                {
                    if (suppliedSize < TableHeaderSize || suppliedSize > int.MaxValue)
                    {
                        return FamilyRead.Unavailable(
                            $"GetExtendedTcpTable returned an invalid resized {familyName} table size of " +
                            $"{suppliedSize} bytes.");
                    }

                    size = suppliedSize;
                    continue;
                }

                if (status != ErrorSuccess)
                {
                    return FamilyRead.Unavailable(
                        $"GetExtendedTcpTable could not read the {familyName} table: {DescribeStatus(status)}");
                }

                return parse(buffer, suppliedSize);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return FamilyRead.Unavailable(
            $"GetExtendedTcpTable changed the {familyName} table size twice while it was being read.");
    }

    private static FamilyRead ParseIpv4Rows(IntPtr table, uint tableSize)
    {
        if (!TryGetRowCount(table, tableSize, Ipv4RowSize, out var count, out var detail))
        {
            return FamilyRead.Unavailable($"The IPv4 TCP table was malformed: {detail}");
        }

        var connections = new List<TcpConnection>(count);
        for (var index = 0; index < count; index++)
        {
            var row = checked(TableHeaderSize + (index * Ipv4RowSize));
            var state = ReadUInt32(table, row);
            if (state != TcpStateEstablished)
            {
                continue;
            }

            var addressBytes = new byte[4];
            Marshal.Copy(IntPtr.Add(table, row + 12), addressBytes, 0, addressBytes.Length);
            var processId = ReadProcessId(table, row + 20);

            connections.Add(new TcpConnection(
                FormatAddress(new IPAddress(addressBytes)),
                ConvertNetworkPort(ReadUInt32(table, row + 16)),
                ConvertNetworkPort(ReadUInt32(table, row + 8)),
                processId));
        }

        return FamilyRead.Success(connections);
    }

    private static FamilyRead ParseIpv6Rows(IntPtr table, uint tableSize)
    {
        if (!TryGetRowCount(table, tableSize, Ipv6RowSize, out var count, out var detail))
        {
            return FamilyRead.Unavailable($"The IPv6 TCP table was malformed: {detail}");
        }

        var connections = new List<TcpConnection>(count);
        for (var index = 0; index < count; index++)
        {
            var row = checked(TableHeaderSize + (index * Ipv6RowSize));
            var state = ReadUInt32(table, row + 48);
            if (state != TcpStateEstablished)
            {
                continue;
            }

            var addressBytes = new byte[16];
            Marshal.Copy(IntPtr.Add(table, row + 24), addressBytes, 0, addressBytes.Length);
            var scopeId = ReadUInt32(table, row + 40);
            var address = new IPAddress(addressBytes, scopeId);
            var processId = ReadProcessId(table, row + 52);

            connections.Add(new TcpConnection(
                FormatAddress(address),
                ConvertNetworkPort(ReadUInt32(table, row + 44)),
                ConvertNetworkPort(ReadUInt32(table, row + 20)),
                processId));
        }

        return FamilyRead.Success(connections);
    }

    private static bool TryGetRowCount(
        IntPtr table,
        uint tableSize,
        int rowSize,
        out int count,
        out string? detail)
    {
        var unsignedCount = ReadUInt32(table, 0);
        if (unsignedCount > int.MaxValue)
        {
            count = 0;
            detail = $"the row count {unsignedCount} is too large";
            return false;
        }

        var required = TableHeaderSize + ((long)unsignedCount * rowSize);
        if (required > tableSize)
        {
            count = 0;
            detail = $"{unsignedCount} rows require {required} bytes but only {tableSize} were returned";
            return false;
        }

        count = (int)unsignedCount;
        detail = null;
        return true;
    }

    internal static int ConvertNetworkPort(uint port)
    {
        var value = (ushort)(port & ushort.MaxValue);
        return (value >> 8) | ((value & 0x00ff) << 8);
    }

    internal static string FormatAddress(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }

    private static int ReadProcessId(IntPtr table, int offset)
    {
        var processId = ReadUInt32(table, offset);
        if (processId > int.MaxValue)
        {
            throw new OverflowException($"Owning process ID {processId} is outside the supported range.");
        }

        return (int)processId;
    }

    private static uint ReadUInt32(IntPtr buffer, int offset) =>
        unchecked((uint)Marshal.ReadInt32(buffer, offset));

    private static string DescribeStatus(uint status) =>
        $"{new Win32Exception(unchecked((int)status)).Message} (error {status})";

    private enum TcpTableClass
    {
        OwnerPidConnections = 4
    }

    private sealed record FamilyRead(bool Succeeded, IReadOnlyList<TcpConnection> Connections, string? Detail)
    {
        public static FamilyRead Success(IReadOnlyList<TcpConnection> connections) => new(true, connections, null);

        public static FamilyRead Unavailable(string detail) => new(false, [], detail);
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // DllImport is used to keep the reviewed Win32 signature explicit.
        // GetExtendedTcpTable returns a Win32 status code; ERROR_INSUFFICIENT_BUFFER supplies the needed size.
        [DllImport("iphlpapi.dll")]
        internal static extern uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref uint size,
            [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily,
            TcpTableClass tableClass,
            uint reserved);
#pragma warning restore SYSLIB1054
    }
}
