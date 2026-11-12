using System.Net;
using System.Net.Sockets;
using Ogs.Telemetry;
using Ogs.Telemetry.Schema;
using Ogs.Telemetry.Wire;
using Xunit;
using Xunit.Abstractions;

namespace Ogs.Telemetry.Tests;

/// <summary>
/// What a send costs the garbage collector, and proof that the buffer-reusing
/// send path puts exactly the bytes of the allocating encoder on the wire.
/// </summary>
public class EncoderAllocationTests(ITestOutputHelper output)
{
    static MotionFrame ManyChannels(int count)
    {
        var values = new List<ChannelValue>();
        for (int i = 0; i < count; i++) values.Add(new ChannelValue { Id = (ushort)i, Number = i * 1.5f });
        return new MotionFrame
        {
            Timestamp = 12.5,
            Objects = new List<MotionObject>
            {
                new() { Name = "player_car", Position = { X = 1, Y = 2, Z = 3 }, Orientation = { W = 1 }, Channels = values },
            },
        };
    }

    static EventFrame Events(int count)
    {
        var list = new List<Event>();
        for (uint i = 1; i <= count; i++)
        {
            var e = new Event { Id = i, Name = "body.impact", Intensity = 0.5f };
            var d = e.InitDirection(); d.Z = -1;
            list.Add(e);
        }
        return new EventFrame { Timestamp = 3.25, Events = list };
    }

    sealed class Listener : IDisposable
    {
        readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public void Dispose() => _udp.Dispose();
    }

    /// <summary>Bytes allocated per send, averaged over many sends.</summary>
    static double BytesPerSend(Action send, int iterations = 3000)
    {
        for (int i = 0; i < 200; i++) send(); // warm up: JIT, scratch growth, socket state
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) send();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
    }

    [Fact]
    public void Report_allocation_per_send()
    {
        using var listener = new Listener();
        using var sender = new TelemetrySender("127.0.0.1", listener.Port);
        var cases = new (string Name, FrameType Type, WireStruct Frame)[]
        {
            ("pose-only motion", FrameType.Motion, ConformanceVectors.All().First(v => v.Id == "motion-pose").Frame),
            ("motion, 30 channels", FrameType.Motion, ManyChannels(30)),
            ("motion, 150 channels", FrameType.Motion, ManyChannels(150)),
            ("events, 10 directional", FrameType.Event, Events(10)),
            ("discovery, 3 channels", FrameType.Discovery, ConformanceVectors.All().First(v => v.Id == "discovery-channels").Frame),
        };
        foreach (var (name, type, frame) in cases)
        {
            double perSend = BytesPerSend(() => sender.Send(type, frame));
            int size = CanonicalEncoder.EncodeDatagram(type, frame).Length;
            output.WriteLine($"{name,-26} {size,5} B datagram   {perSend,8:0} B allocated per send");
        }
    }

    // ---- the reusing path puts the same bytes on the wire ----------------

    static IReadOnlyList<(string Name, FrameType Type, WireStruct Frame)> Frames()
    {
        var all = ConformanceVectors.All().Select(v => (v.Id, v.Type, v.Frame)).ToList();
        all.Add(("motion-150", FrameType.Motion, ManyChannels(150)));
        all.Add(("events-60", FrameType.Event, Events(60)));
        return all;
    }

    [Fact]
    public void The_remaining_allocation_is_the_socket_s_own()
    {
        // Socket.SendTo(EndPoint) serializes the endpoint to a new address object per call
        using var listener = new Listener();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var endpoint = new IPEndPoint(IPAddress.Loopback, listener.Port);
        var datagram = new byte[112];
        double raw = BytesPerSend(() => socket.SendTo(datagram, 0, datagram.Length, SocketFlags.None, endpoint));
        output.WriteLine($"bare Socket.SendTo: {raw:0} B/send");
        Assert.InRange(raw, 50, 100);
    }

    [Fact]
    public void Reused_buffers_encode_exactly_what_the_allocating_encoder_does()
    {
        var frames = Frames();
        var words = new List<ulong>();
        var buffer = new byte[8]; // forces growth
        // every order that matters: big after small, small after big, repeats
        var order = frames.Concat(frames.Reverse()).Concat(frames.OrderBy(f => f.Name.Length)).ToList();
        foreach (var (name, type, frame) in order)
        {
            var expected = CanonicalEncoder.EncodeDatagram(type, frame);
            CanonicalEncoder.EncodeInto(words, frame);
            int length = Envelope.EncodeDatagram(ref buffer, type, words);
            Assert.True(expected.AsSpan().SequenceEqual(buffer.AsSpan(0, length)), $"{name}: bytes differ");
        }
    }

    [Fact]
    public void The_sender_puts_the_allocating_encoders_bytes_on_the_wire()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveTimeout = 2000;
        using var sender = new TelemetrySender("127.0.0.1", ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
        var frames = Frames();
        foreach (var (name, type, frame) in frames.Concat(frames.Reverse()))
        {
            sender.Send(type, frame);
            IPEndPoint? any = null;
            var received = udp.Receive(ref any);
            var expected = CanonicalEncoder.EncodeDatagram(type, frame);
            Assert.True(expected.AsSpan().SequenceEqual(received), $"{name}: bytes on the wire differ");
        }
        Assert.Equal(0, sender.SendFailures);
    }

    [Fact]
    public void Text_survives_the_scratch_buffer_whatever_its_encoding_or_length()
    {
        // multi-byte, a surrogate pair, and a name longer than the scratch buffer's initial size
        foreach (var name in new[] { "ünï_車", "car😀", new string('x', 700), "a", "" })
        {
            var frame = new MotionFrame
            {
                Timestamp = 1,
                Objects = new List<MotionObject> { new() { Name = name, Orientation = { W = 1 } } },
            };
            var datagram = CanonicalEncoder.EncodeDatagram(FrameType.Motion, frame);
            string? decoded = null;
            using var rx = new TelemetryReceiver(TelemetryReceiver.LoopbackGroup, 41503, localInterface: IPAddress.Loopback);
            rx.MotionReceived += m => decoded = m.Objects[0].Name;
            rx.Inject(new IPEndPoint(IPAddress.Loopback, 1), datagram);
            Assert.Equal(name, decoded);
        }
    }

    [Fact]
    public void Concurrent_sends_never_interleave_buffers()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udp.Client.ReceiveBufferSize = 4 << 20;
        udp.Client.ReceiveTimeout = 2000;
        using var sender = new TelemetrySender("127.0.0.1", ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
        var frames = Frames().Where(f => f.Name is "motion-pose" or "motion-150" or "events-60" or "discovery-channels").ToList();
        var valid = frames.Select(f => Convert.ToHexString(CanonicalEncoder.EncodeDatagram(f.Type, f.Frame))).ToHashSet();
        const int perThread = 200;
        var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                var f = frames[(i + t) % frames.Count];
                sender.Send(f.Type, f.Frame);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        int got = 0, bad = 0;
        try
        {
            while (true)
            {
                IPEndPoint? any = null;
                var d = udp.Receive(ref any);
                got++;
                if (!valid.Contains(Convert.ToHexString(d))) bad++;
            }
        }
        catch (SocketException) { }
        Assert.True(got > 0);
        Assert.Equal(0, bad);
    }

    [Fact]
    public void Sending_allocates_far_less_than_the_allocating_encoder()
    {
        using var listener = new Listener();
        using var sender = new TelemetrySender("127.0.0.1", listener.Port);
        foreach (var (name, type, frame) in Frames().Where(f => f.Name is "motion-pose" or "motion-150" or "events-60"))
        {
            double reused = BytesPerSend(() => sender.Send(type, frame));
            double allocating = BytesPerSend(() => sender.Send(CanonicalEncoder.EncodeDatagram(type, frame)));
            output.WriteLine($"{name,-14} reused {reused,7:0} B/send   allocating {allocating,7:0} B/send");
            Assert.True(reused < allocating * 0.5, $"{name}: {reused:0} B vs {allocating:0} B");
        }
    }
}
