using System.Net;
using System.Net.Sockets;
using Ogs.Telemetry;
using Xunit;

/// <summary>A sender pinned to loopback can move a group's default-route
/// traffic onto loopback (seen on Windows: the port doesn't matter, and it
/// lasts after that sender closes). A receiver left on default settings has
/// to keep hearing the stream when that happens.</summary>
public class LoopbackSenderTests
{
    [Fact]
    public async Task Default_Receiver_Keeps_The_Stream_When_A_Loopback_Sender_Starts()
    {
        // once moved, a group stays moved on this host: use one nothing has touched
        string group = $"239.255.{Random.Shared.Next(18, 200)}.{Random.Shared.Next(1, 250)}";
        const int port = 41396;

        using var receiver = new TelemetryReceiver(group, port);
        int received = 0;
        receiver.MotionReceived += _ => Interlocked.Increment(ref received);
        receiver.Start();

        using var game = new TelemetrySender(group, port);
        async Task<int> Stream(int frames, Action? alongside = null)
        {
            int before = Volatile.Read(ref received);
            for (int i = 0; i < frames; i++)
            {
                game.Send(FrameType.Motion, new Ogs.Telemetry.Schema.MotionFrame { Timestamp = i });
                alongside?.Invoke();
                await Task.Delay(10);
            }
            await Task.Delay(100);
            return Volatile.Read(ref received) - before;
        }

        // no default-route multicast on this host (some CI runners): nothing to test
        if (await Stream(30) == 0) return;

        using (var localReceiver = new TelemetryReceiver(group, port + 1, IPAddress.Loopback))
        using (var loopback = new TelemetrySender(group, port + 1, localInterface: IPAddress.Loopback))
        {
            await Stream(30, () =>
            {
                try { loopback.Send(FrameType.Motion, new Ogs.Telemetry.Schema.MotionFrame()); }
                catch (SocketException) { } // no loopback multicast route on this host
            });
        }

        Assert.True(await Stream(30) > 0, "the stream stopped after a loopback sender used its group");
    }
}
