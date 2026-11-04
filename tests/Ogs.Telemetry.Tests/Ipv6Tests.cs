using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Ogs.Telemetry;
using Xunit;

/// <summary>IPv6 multicast (implementation-udp.md § Default endpoint) on
/// the loopback interface: a sender and receiver on the loopback IPv6 group.</summary>
public class Ipv6Tests
{
    [Fact]
    public async Task Motion_Round_Trips_On_An_IPv6_Group()
    {
        if (!Socket.OSSupportsIPv6) return;
        int index = NetworkInterface.IPv6LoopbackInterfaceIndex;
        var loopback = new IPAddress(IPAddress.IPv6Loopback.GetAddressBytes(), index);
        const int port = 41390;

        using var receiver = new TelemetryReceiver(TelemetryReceiver.LoopbackGroupV6, port, loopback);
        var got = new TaskCompletionSource<(IPEndPoint, double)>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MotionReceivedFrom += (src, m) => got.TrySetResult((src, m.Timestamp));
        receiver.Start();

        using var sender = new TelemetrySender(TelemetryReceiver.LoopbackGroupV6, port, localInterface: loopback);
        for (int i = 0; i < 30 && !got.Task.IsCompleted; i++)
        {
            try
            {
                sender.Send(FrameType.Motion, new Ogs.Telemetry.Schema.MotionFrame { Timestamp = 4.5 });
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.NetworkUnreachable
                                                 or SocketError.HostUnreachable)
            {
                // no IPv6 multicast route on this host's loopback (CI runners
                // on Linux and macOS): nothing to test here
                return;
            }
            await Task.Delay(50);
        }
        var (source, timestamp) = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4.5, timestamp);
        Assert.Equal(AddressFamily.InterNetworkV6, source.AddressFamily);
    }
}
