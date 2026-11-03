using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Ogs.Telemetry;
using Xunit;

/// <summary>The IPv6 group (implementation-udp.md § Default endpoint) on
/// the loopback interface: a sender and receiver on ff12::4f47:5453.</summary>
public class Ipv6Tests
{
    [Fact]
    public async Task Motion_Round_Trips_On_The_IPv6_Group()
    {
        if (!Socket.OSSupportsIPv6) return;
        int index = NetworkInterface.IPv6LoopbackInterfaceIndex;
        var loopback = new IPAddress(IPAddress.IPv6Loopback.GetAddressBytes(), index);
        const int port = 41390;

        using var receiver = new TelemetryReceiver(TelemetrySender.DefaultGroupV6, port, loopback);
        var got = new TaskCompletionSource<(IPEndPoint, double)>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MotionReceivedFrom += (src, m) => got.TrySetResult((src, m.Timestamp));
        receiver.Start();

        using var sender = new TelemetrySender(TelemetrySender.DefaultGroupV6, port, localInterface: loopback);
        for (int i = 0; i < 30 && !got.Task.IsCompleted; i++)
        {
            sender.Send(FrameType.Motion, new Ogs.Telemetry.Schema.MotionFrame { Timestamp = 4.5 });
            await Task.Delay(50);
        }
        var (source, timestamp) = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4.5, timestamp);
        Assert.Equal(AddressFamily.InterNetworkV6, source.AddressFamily);
    }
}
