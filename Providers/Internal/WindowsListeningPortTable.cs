using System.Runtime.InteropServices;
using Gauge.Services;

namespace Gauge.Providers.Internal;

/// <summary>
/// One IPv4 TCP listener row from the OS, as the fields are laid out in memory: the local
/// address and port are in network byte order, the owning PID in host order.
/// </summary>
internal readonly record struct TcpListenerRow(uint LocalAddress, uint LocalPortRaw, uint OwningProcessId);

/// <summary>
/// Resolves which loopback TCP ports a given process is listening on, via the IP Helper API's
/// <c>GetExtendedTcpTable</c> — not by shelling out to <c>netstat</c> each refresh. Antigravity's
/// language server opens its ports with <c>--https_server_port 0</c> (random), so the port can
/// only be learned from the live listener table and must be re-confirmed against the owning PID
/// before any token is sent (Windows reuses PIDs).
/// </summary>
internal static class WindowsListeningPortTable
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint Loopback = 0x0100007F; // 127.0.0.1 as stored (network byte order).
    private const int RowSize = 24;           // 6 DWORDs: state, localAddr, localPort, remoteAddr, remotePort, pid.

    public static IReadOnlyList<int> LoopbackListeningPorts(int processId)
        => LoopbackPortsForPid(QueryListenerRows(), processId);

    /// <summary>
    /// Filters the listener rows to the loopback ports owned by <paramref name="processId"/>,
    /// converting each port from network to host byte order. Pure so the decoding is testable
    /// without the OS table.
    /// </summary>
    internal static IReadOnlyList<int> LoopbackPortsForPid(IEnumerable<TcpListenerRow> rows, int processId)
    {
        var ports = new List<int>();
        foreach (var row in rows)
        {
            if (row.OwningProcessId != (uint)processId || row.LocalAddress != Loopback)
            {
                continue;
            }

            var port = NetworkToHostPort(row.LocalPortRaw);
            if (port > 0 && !ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    // The low two bytes hold the port in network (big-endian) order; swap to host order.
    internal static int NetworkToHostPort(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static IReadOnlyList<TcpListenerRow> QueryListenerRows()
    {
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size == 0)
        {
            return Array.Empty<TcpListenerRow>();
        }

        var table = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(table, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (status != 0)
            {
                return Array.Empty<TcpListenerRow>();
            }

            var count = Marshal.ReadInt32(table);
            var rows = new List<TcpListenerRow>(count);
            var first = IntPtr.Add(table, 4); // skip dwNumEntries
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(first, i * RowSize);
                rows.Add(new TcpListenerRow(
                    LocalAddress: (uint)Marshal.ReadInt32(row, 4),
                    LocalPortRaw: (uint)Marshal.ReadInt32(row, 8),
                    OwningProcessId: (uint)Marshal.ReadInt32(row, 20)));
            }

            return rows;
        }
        catch (Exception ex) when (ex is AccessViolationException or OutOfMemoryException)
        {
            DiagnosticsLog.Write("provider", $"TCP listener table read failed: {ex.GetType().Name}");
            return Array.Empty<TcpListenerRow>();
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);
}
