using Gauge.Providers.Internal;

namespace Gauge.Tests;

/// <summary>
/// Row decoding for <see cref="WindowsListeningPortTable"/>: only loopback ports owned by the
/// target PID are returned, and each is converted from network to host byte order. The native
/// table read itself is exercised in live delegate testing, not here.
/// </summary>
public sealed class WindowsListeningPortTableTests
{
    private const uint Loopback = 0x0100007F;   // 127.0.0.1 as stored (network byte order)
    private const uint AnyAddress = 0x00000000; // 0.0.0.0
    private const uint Port2654 = 0x00005E0A;   // 2654 in network byte order, low two bytes

    [Fact]
    public void DecodesPortAndKeepsOnlyMatchingLoopbackRows()
    {
        var rows = new[]
        {
            new TcpListenerRow(Loopback, Port2654, 1000),     // ours, loopback → kept
            new TcpListenerRow(Loopback, NetPort(2655), 1000), // ours, second port (HTTP) → kept
            new TcpListenerRow(Loopback, NetPort(9999), 2000), // different PID → dropped
            new TcpListenerRow(AnyAddress, NetPort(8080), 1000), // ours but not loopback → dropped
        };

        var ports = WindowsListeningPortTable.LoopbackPortsForPid(rows, processId: 1000);

        Assert.Equal(new[] { 2654, 2655 }, ports);
    }

    [Fact]
    public void NetworkToHostPortSwapsBytes()
    {
        Assert.Equal(2654, WindowsListeningPortTable.NetworkToHostPort(Port2654));
        Assert.Equal(443, WindowsListeningPortTable.NetworkToHostPort(NetPort(443)));
    }

    [Fact]
    public void DeduplicatesRepeatedPorts()
    {
        var rows = new[]
        {
            new TcpListenerRow(Loopback, Port2654, 1000),
            new TcpListenerRow(Loopback, Port2654, 1000),
        };

        Assert.Equal(new[] { 2654 }, WindowsListeningPortTable.LoopbackPortsForPid(rows, 1000));
    }

    [Fact]
    public void ReturnsEmptyWhenNoRowMatches()
    {
        var rows = new[] { new TcpListenerRow(Loopback, Port2654, 4242) };
        Assert.Empty(WindowsListeningPortTable.LoopbackPortsForPid(rows, processId: 1));
    }

    // Encode a host-order port into the network-order low two bytes the OS table stores.
    private static uint NetPort(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
}
