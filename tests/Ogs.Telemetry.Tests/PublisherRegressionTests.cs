using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Ogs.Telemetry;
using Xunit;

namespace Ogs.Telemetry.Tests;

/// <summary>
/// Publisher behaviour found by a game integration (XWVM): motion cadence at
/// common frame rates, events surviving slow ticks, event datagram budget,
/// declared object type, unbound orientation, pause and send failures.
/// Datagrams go to a unicast UDP socket on 127.0.0.1 and are inspected raw.
/// </summary>
public class PublisherRegressionTests
{
    sealed class Capture : IDisposable
    {
        readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly TelemetrySender Sender;
        public Capture() =>
            Sender = new TelemetrySender("127.0.0.1", ((IPEndPoint)_udp.Client.LocalEndPoint!).Port);

        /// <summary>Everything sent so far, in order. Loopback delivery is not
        /// synchronous on every OS (macOS), so this waits until the socket has
        /// been quiet for a moment rather than reading what has arrived.</summary>
        public List<byte[]> Drain()
        {
            var list = new List<byte[]>();
            _udp.Client.ReceiveTimeout = 40;
            while (true)
            {
                try
                {
                    IPEndPoint? any = null;
                    list.Add(_udp.Receive(ref any));
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    return list;
                }
            }
        }

        public void Dispose() { Sender.Dispose(); _udp.Dispose(); }
    }

    static FrameType TypeOf(byte[] d) => (FrameType)d[4];

    static int Count(List<byte[]> l, FrameType t) => l.Count(d => TypeOf(d) == t);

    static byte[] First(List<byte[]> l, FrameType t) => l.First(d => TypeOf(d) == t);

    // ---- U1: motion rate ------------------------------------------------

    [Theory]
    [InlineData(60, 30)]
    [InlineData(90, 30)]
    [InlineData(144, 30)]
    [InlineData(60, 60)]
    [InlineData(75, 20)]
    public void Motion_rate_matches_the_configured_rate(int fps, int hz)
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t", motionHz: hz);
        pub.DeclareObject("o");
        const double seconds = 10;
        int frames = (int)(seconds * fps);
        for (int i = 0; i <= frames; i++) pub.Tick(100.0 + (double)i / fps);
        int motion = Count(cap.Drain(), FrameType.Motion);
        Assert.InRange(motion / seconds, hz * 0.99, hz * 1.01);
    }

    [Fact]
    public void Motion_with_jittered_frames_still_averages_to_the_rate()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t", motionHz: 30);
        pub.DeclareObject("o");
        var rng = new Random(7);
        double t = 5;
        for (; t < 15; t += 1.0 / 60 * (0.8 + 0.4 * rng.NextDouble())) pub.Tick(t);
        double rate = Count(cap.Drain(), FrameType.Motion) / (t - 5);
        Assert.InRange(rate, 29.0, 31.0);
    }

    [Fact]
    public void A_long_stall_resyncs_instead_of_bursting()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t", motionHz: 30);
        pub.DeclareObject("o");
        pub.Tick(0); pub.Tick(0.034);
        cap.Drain();
        pub.Tick(5.0);                         // 5 s stall
        Assert.Equal(1, Count(cap.Drain(), FrameType.Motion));
        pub.Tick(5.001);                       // no catch-up burst
        Assert.Equal(0, Count(cap.Drain(), FrameType.Motion));
    }

    // ---- U2 / U10: events fired between ticks ---------------------------

    [Theory]
    [InlineData(60)]
    [InlineData(10)]
    [InlineData(9)]
    [InlineData(4)]
    public void Every_event_fired_between_ticks_is_sent(int fps)
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t", motionHz: 30);
        pub.DeclareObject("o");
        double t = 1000;                       // clock does not start at 0 (U10)
        pub.FireEvent("body.impact");          // before the first tick
        pub.Tick(t);
        var ids = new HashSet<uint>();
        Collect(cap, ids);
        for (int i = 0; i < 19; i++)
        {
            pub.FireEvent("body.impact", 0.5f);
            t += 1.0 / fps;
            pub.Tick(t);
            Collect(cap, ids);
        }
        for (int i = 0; i < 12; i++)           // let the 50 ms send gate flush the tail
        {
            t += 1.0 / fps;
            pub.Tick(t);
            Collect(cap, ids);
        }
        Assert.Equal(20, ids.Count);

        static void Collect(Capture c, HashSet<uint> ids)
        {
            foreach (var d in c.Drain().Where(d => TypeOf(d) == FrameType.Event))
                foreach (var id in EventIds(d)) ids.Add(id);
        }
    }

    // event ids via the receiver's decoder
    static IEnumerable<uint> EventIds(byte[] datagram)
    {
        var ids = new List<uint>();
        using var rx = new TelemetryReceiver(TelemetryReceiver.LoopbackGroup, 41501, localInterface: IPAddress.Loopback);
        rx.EventReceived += e => { for (int i = 0; i < e.Events.Count; i++) ids.Add(e.Events[i].Id); };
        rx.Inject(new IPEndPoint(IPAddress.Loopback, 1), datagram);
        return ids;
    }

    [Fact]
    public void Events_are_replayed_for_about_100ms_when_ticks_are_frequent()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t", motionHz: 30);
        pub.DeclareObject("o");
        pub.Tick(0);
        cap.Drain();
        pub.FireEvent("body.impact");
        int sends = 0;
        for (int i = 1; i <= 12; i++)          // 12 ticks of 20 ms = 240 ms
        {
            pub.Tick(0.02 * i);
            sends += Count(cap.Drain(), FrameType.Event);
        }
        Assert.InRange(sends, 2, 3);           // re-carried every 50 ms, then dropped
    }

    // ---- U3: event datagram budget --------------------------------------

    [Fact]
    public void An_event_burst_is_split_to_the_datagram_budget()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t", motionHz: 30);
        pub.DeclareObject("o");
        pub.Tick(0);
        cap.Drain();
        for (int i = 0; i < 60; i++)
            pub.FireEvent("body.impact", 0.9f, 0f, new Vector3(0f, 0f, -1f));
        pub.Tick(0.016);
        var events = cap.Drain().Where(d => TypeOf(d) == FrameType.Event).ToList();
        Assert.True(events.Count > 1);
        Assert.All(events, d => Assert.True(d.Length <= DatagramBudget.Bytes, $"{d.Length} B"));
        var ids = events.SelectMany(EventIds).ToList();
        Assert.Equal(60, ids.Distinct().Count());
    }

    // ---- U5: declared object type ---------------------------------------

    [Fact]
    public void DeclareObject_carries_type_location_and_reference_point()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t");
        pub.DeclareObject("ship", "XWing", Schema.ObjectType.Spacecraft, "local", "pilot_seat");
        pub.Tick(0);
        var d = First(cap.Drain(), FrameType.Discovery);
        using var rx = new TelemetryReceiver(TelemetryReceiver.LoopbackGroup, 41502, localInterface: IPAddress.Loopback);
        CapnpGen.ObjectType? type = null; string? point = null, label = null;
        rx.DiscoveryReceived += f =>
        {
            type = f.Objects[0].Type; point = f.Objects[0].ReferencePoint.Name; label = f.Objects[0].TypeLabel;
        };
        rx.Inject(new IPEndPoint(IPAddress.Loopback, 1), d);
        Assert.Equal(CapnpGen.ObjectType.spacecraft, type);
        Assert.Equal("pilot_seat", point);
        Assert.Equal("XWing", label);
    }

    // ---- U6: unbound orientation ----------------------------------------

    [Fact]
    public void Unbound_orientation_is_the_identity_quaternion()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t");
        pub.DeclareObject("o");
        pub.Tick(0);
        var m = First(cap.Drain(), FrameType.Motion);
        using var rx = new TelemetryReceiver(TelemetryReceiver.LoopbackGroup, 41503, localInterface: IPAddress.Loopback);
        float[]? q = null;
        rx.MotionReceived += f =>
        {
            var o = f.Objects[0].Orientation;
            q = new[] { o.X, o.Y, o.Z, o.W };
        };
        rx.Inject(new IPEndPoint(IPAddress.Loopback, 1), m);
        Assert.Equal(new[] { 0f, 0f, 0f, 1f }, q);
    }

    // ---- U7: pause and ClearEvents --------------------------------------

    [Fact]
    public void Pausing_drops_queued_events_and_ignores_new_ones()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t");
        pub.DeclareObject("o");
        pub.DeclarePause();
        pub.Tick(0);
        cap.Drain();
        pub.FireEvent("body.impact");
        pub.SetPaused(true);
        pub.FireEvent("body.impact");
        pub.Tick(0.02);
        Assert.Equal(0, Count(cap.Drain(), FrameType.Event));
        pub.SetPaused(false);
        pub.FireEvent("body.impact");
        pub.Tick(0.1);
        Assert.Equal(1, Count(cap.Drain(), FrameType.Event));
    }

    [Fact]
    public void ClearEvents_drops_events_not_yet_sent_and_in_the_replay_window()
    {
        using var cap = new Capture();
        using var pub = new TelemetryPublisher(cap.Sender, "t");
        pub.DeclareObject("o");
        pub.Tick(0);
        cap.Drain();
        pub.FireEvent("body.impact");
        pub.ClearEvents();
        pub.Tick(0.02);
        Assert.Equal(0, Count(cap.Drain(), FrameType.Event));
        pub.FireEvent("body.impact");
        pub.Tick(0.1);
        Assert.Equal(1, Count(cap.Drain(), FrameType.Event));
        pub.ClearEvents();
        pub.Tick(0.2);
        Assert.Equal(0, Count(cap.Drain(), FrameType.Event));
    }

    // ---- U8: send failures ----------------------------------------------

    [Fact]
    public void A_socket_error_is_counted_not_thrown()
    {
        using var sender = new TelemetrySender("127.0.0.1", 9);
        // a datagram larger than the UDP maximum fails with a SocketException
        sender.Send(new byte[70000]);
        Assert.Equal(1, sender.SendFailures);
        Assert.NotNull(sender.LastSendError);
    }

    // ---- U9: deriver after reset ----------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_at_a_moving_spawn_does_not_produce_a_false_acceleration(bool measuredVelocity)
    {
        var d = new BodyDynamicsDeriver();
        var v = new Vector3(0f, 0f, 300f);     // spacecraft at cruise
        Vector3 p = default;
        double t = 0;
        d.Sample(t, p, Quaternion.Identity, measuredVelocity, v);   // first sample: reset
        for (int i = 0; i < 5; i++)
        {
            t += 0.016; p += v * 0.016f;
            d.Sample(t, p, Quaternion.Identity, measuredVelocity, v);
            Assert.InRange(d.SpecificForce.Z, -0.5f, 0.5f);
            Assert.InRange(d.SpecificForce.Y, 9.3f, 10.3f);         // gravity reaction only
        }
    }
}
