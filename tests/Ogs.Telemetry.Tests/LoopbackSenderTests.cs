using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Ogs.Telemetry;
using Xunit;

/// <summary>A sender pinned to loopback can move a group's default-route
/// traffic onto loopback (seen on Windows: the port doesn't matter, and it
/// lasts after that sender closes). A game on default settings has to keep
/// reaching other machines when that happens, and a receiver on default
/// settings has to keep hearing it. A listener joined only on the real
/// interface stands in for another machine.</summary>
public class LoopbackSenderTests
{
    static readonly Ogs.Telemetry.Schema.MotionFrame Frame = new() { Timestamp = 1.0 };

    /// <summary>An untouched group: once moved, a group stays moved on this host.</summary>
    static string FreshGroup(bool v6) => v6
        ? $"ff12::4f47:{Random.Shared.Next(0x6000, 0xf000):x}"
        : $"239.255.{Random.Shared.Next(18, 200)}.{Random.Shared.Next(1, 250)}";

    /// <summary>Joins only on the default route's interface; null when the
    /// host has none (some CI runners), and there is nothing to test.</summary>
    static Socket? ListenOnRealInterface(string group, int port, bool v6)
    {
        var address = IPAddress.Parse(group);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, port));
        try
        {
            if (v6)
            {
                if (TelemetrySender.DefaultInterfaceIndexV6() is not int index) { socket.Dispose(); return null; }
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                    new IPv6MulticastOption(address, index));
            }
            else
            {
                if (TelemetrySender.DefaultRouteAddress() is not { } nic) { socket.Dispose(); return null; }
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(address, nic));
            }
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    static int Drain(Socket socket)
    {
        int count = 0;
        var buffer = new byte[2048];
        while (socket.Available > 0) { socket.Receive(buffer); count++; }
        return count;
    }

    static IPAddress LoopbackInterface(bool v6) => v6
        ? new IPAddress(IPAddress.IPv6Loopback.GetAddressBytes(), NetworkInterface.IPv6LoopbackInterfaceIndex)
        : IPAddress.Loopback;

    [Theory]
    [InlineData(false, 41396)]
    [InlineData(true, 41398)]
    public async Task Stream_Survives_A_Loopback_Sender_On_Its_Group(bool v6, int port)
    {
        if (v6 && !Socket.OSSupportsIPv6) return;
        string group = FreshGroup(v6);

        using var otherMachine = ListenOnRealInterface(group, port, v6);
        if (otherMachine is null) return;

        using var receiver = new TelemetryReceiver(group, port);
        int received = 0;
        receiver.MotionReceived += _ => Interlocked.Increment(ref received);
        receiver.Start();

        using var game = new TelemetrySender(group, port);
        async Task<(int Local, int Remote)> Stream(int frames, Action? alongside = null)
        {
            int before = Volatile.Read(ref received);
            Drain(otherMachine);
            for (int i = 0; i < frames; i++)
            {
                game.Send(FrameType.Motion, Frame);
                alongside?.Invoke();
                await Task.Delay(10);
            }
            await Task.Delay(100);
            return (Volatile.Read(ref received) - before, Drain(otherMachine));
        }

        // no multicast on this host's real interface (some CI runners): nothing to test
        var baseline = await Stream(30);
        if (baseline.Remote == 0) return;
        Assert.True(baseline.Local <= 30, $"the receiver got {baseline.Local} copies of 30 frames");

        var nic = LoopbackInterface(v6);
        try
        {
            using var localReceiver = new TelemetryReceiver(group, port + 1, nic);
            using var loopback = new TelemetrySender(group, port + 1, localInterface: nic);
            await Stream(30, () =>
            {
                try { loopback.Send(FrameType.Motion, Frame); }
                catch (SocketException) { } // no loopback multicast route on this host
            });
        }
        catch (SocketException)
        {
            return; // this host can't join on loopback: it can't move the group either
        }

        var after = await Stream(30);
        Assert.True(after.Remote > 0, "the stream stopped leaving this machine after a loopback sender used its group");
        Assert.True(after.Local > 0, "the local receiver stopped hearing the stream after a loopback sender used its group");
        Assert.True(after.Local <= 30, $"the receiver got {after.Local} copies of 30 frames");
    }
}
