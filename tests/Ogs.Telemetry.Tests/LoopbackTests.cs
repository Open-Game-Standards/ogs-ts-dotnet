using Ogs.Telemetry;
using Schema = Ogs.Telemetry.Schema;
using System.Net;
using Xunit;

/// <summary>End-to-end loopback over real UDP multicast (requires loopback
/// multicast to be permitted by the OS/network environment).</summary>
public class LoopbackTests
{
    [Fact]
    public async Task Sender_And_Receiver_Roundtrip_Over_Multicast()
    {
        const int port = 41235; // away from the default 40123 to avoid interference
        using var receiver = new TelemetryReceiver(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        var discovery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var motion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evnt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.DiscoveryReceived += d =>
            discovery.TrySetResult($"{d.GameName}/{d.Objects.Count}");
        receiver.MotionReceived += m =>
            motion.TrySetResult($"{m.Timestamp:F1}/{m.Objects[0].Name}");
        receiver.EventReceived += e =>
        {
            var first = e.Events.FirstOrDefault();
            if (first.Name != null)
                evnt.TrySetResult($"{first.Id}/{first.Name}");
        };
        receiver.Start();

        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);

        sender.Send(FrameType.Discovery, new Schema.DiscoveryFrame
        {
            SchemaVersion = 1,
            GameName = "loopback-test",
            Objects = new List<Schema.ObjectDescriptor>
            {
                new() { Name = "car", Primary = true },
            },
        });

        sender.Send(FrameType.Motion, new Schema.MotionFrame
        {
            Timestamp = 42.0,
            Objects = new List<Schema.MotionObject>
            {
                new() { Name = "car", Orientation = { W = 1f } },
            },
        });

        sender.Send(FrameType.Event, new Schema.EventFrame
        {
            Timestamp = 42.5,
            Events = new List<Schema.Event>
            {
                new() { Id = 1, Name = "body.impact", Intensity = 1f, Duration = 0f },
            },
        });

        var done = await Task.WhenAll(discovery.Task, motion.Task, evnt.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("loopback-test/1", done[0]);
        Assert.Equal("42.0/car", done[1]);
        Assert.Equal("1/body.impact", done[2]);
    }
}
