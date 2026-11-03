#nullable enable
using System.Net;
using System.Net.Sockets;

using Ogs.Telemetry;
using Ogs.Telemetry.Schema;
using Ogs.Telemetry.Wire;
using Xunit;

/// <summary>Datagram budget (implementation-udp.md § Datagram budget):
/// paged discovery, motion parts, and receiver-side page merging.</summary>
public class PagingTests
{
    static DiscoveryFrame Header() => new DiscoveryFrame
    {
        SchemaVersion = 1, GameName = "paging-test", SessionStartUnixUs = 42,
        Environment = { Gravity = 9.80665f },
    };

    static ObjectDescriptor BigObject(int channels, int values = 0)
    {
        var obj = new ObjectDescriptor
        {
            Name = "sim_car", Type = ObjectType.Vehicle, TypeLabel = "Paging test car",
            Location = "Test Ring", Primary = true,
            DrivePoints = new List<DrivePointDescriptor>
            {
                new DrivePointDescriptor { Id = 0, Name = "wheel.fl" },
            },
            Channels = new List<ChannelDescriptor>(),
            Values = new List<ChannelValue>(),
            Texts = new List<TextValue>(),
        };
        for (int i = 0; i < channels; i++)
            obj.Channels!.Add(new ChannelDescriptor
            {
                Id = (ushort)i, Name = $"sim.channel{i:D3}", Unit = "m/s",
                Description = $"Simulator channel number {i} with a realistic description",
            });
        for (int i = 0; i < values; i++)
            obj.Texts!.Add(new TextValue { Id = (ushort)i, Text = $"label {i}" });
        return obj;
    }

    static CapnpGen.DiscoveryFrame.READER Read(DiscoveryFrame frame)
    {
        var datagram = CanonicalEncoder.EncodeDatagram(FrameType.Discovery, frame);
        Assert.True(Envelope.TryUnwrap(datagram, out _, out var payload));
        return CapnpGen.DiscoveryFrame.READER.create(MessageDecoder.DecodeRoot(payload.Span));
    }

    [Fact]
    public void Small_Set_Is_One_Unpaged_Frame()
    {
        var pages = DiscoveryPager.Paginate(Header, new[] { BigObject(5) }, generation: 7);
        Assert.Single(pages);
        Assert.Equal(7u, pages[0].Generation);
        Assert.Equal(0, pages[0].Page);
        Assert.Equal(0, pages[0].PageCount);
        Assert.Equal(5, pages[0].Objects![0].Channels!.Count);
    }

    [Fact]
    public void Large_Set_Pages_Within_Budget_And_Merges_Back()
    {
        var pages = DiscoveryPager.Paginate(Header, new[] { BigObject(300, values: 3) }, generation: 4);
        Assert.True(pages.Count > 10, $"{pages.Count} pages");

        var tables = new DescriptorTables();
        for (int i = 0; i < pages.Count; i++)
        {
            int size = DatagramBudget.SizeOf(FrameType.Discovery, pages[i]);
            Assert.True(size <= DatagramBudget.Bytes, $"page {i} is {size} bytes");
            Assert.Equal(4u, pages[i].Generation);
            Assert.Equal(i, pages[i].Page);
            Assert.Equal(pages.Count, pages[i].PageCount);
            Assert.Equal("paging-test", pages[i].GameName);
            Assert.Equal(9.80665f, pages[i].Environment.Gravity);
            tables.Apply(Read(pages[i]));
        }

        // identity only on the first page listing the object
        Assert.True(pages[0].Objects![0].Primary);
        Assert.All(pages.Skip(1), p => Assert.False(p.Objects![0].Primary));

        var gen = tables.Current!;
        Assert.True(gen.Complete);
        var obj = gen.Objects["sim_car"];
        Assert.Equal(300, obj.Channels.Count);
        Assert.Equal("Paging test car", obj.TypeLabel);
        Assert.Equal("Test Ring", obj.Location);
        Assert.True(obj.Primary);
        Assert.Equal(1, obj.DrivePointCount);
        Assert.Equal("sim.channel299", obj.Channels[299].Name);
        Assert.Equal("label 2", obj.Values[2]);

        // session values ride their own page
        var valuePages = pages.Where(p => p.Objects!.Any(o => o.Texts is { Count: > 0 })).ToList();
        Assert.All(valuePages, p => Assert.Null(p.Objects![0].Channels));
    }

    [Fact]
    public void Pages_Are_Usable_In_Any_Order_And_Identity_Comes_From_Lowest_Page()
    {
        var pages = DiscoveryPager.Paginate(Header, new[] { BigObject(80) }, generation: 1);
        var tables = new DescriptorTables();
        tables.Apply(Read(pages[^1]));                  // a late page first
        Assert.True(tables.TryGetChannel(1, "sim_car", 79, out var last));
        Assert.Equal("sim.channel079", last.Name);
        Assert.False(tables.TryGetChannel(1, "sim_car", 0, out _)); // its page not here yet
        Assert.Equal("", tables.Current!.Objects["sim_car"].TypeLabel);
        Assert.False(tables.Current.Complete);

        foreach (var page in pages.Take(pages.Count - 1)) tables.Apply(Read(page));
        Assert.True(tables.Current.Complete);
        Assert.Equal("Paging test car", tables.Current.Objects["sim_car"].TypeLabel);
        Assert.True(tables.TryGetChannel(1, "sim_car", 0, out _));
    }

    [Fact]
    public void Previous_Generation_Resolves_Until_Superseded_Twice()
    {
        DiscoveryFrame Gen(uint g, string channelName)
        {
            var obj = BigObject(1);
            obj.Channels![0].Name = channelName;
            return DiscoveryPager.Paginate(Header, new[] { obj }, g)[0];
        }
        var tables = new DescriptorTables();
        tables.Apply(Read(Gen(1, "old.name")));
        tables.Apply(Read(Gen(2, "new.name")));
        Assert.True(tables.TryGetChannel(1, "sim_car", 0, out var old));
        Assert.Equal("old.name", old.Name);                 // in-flight frames still resolve
        Assert.True(tables.TryGetChannel(2, "sim_car", 0, out var current));
        Assert.Equal("new.name", current.Name);
        tables.Apply(Read(Gen(3, "newer.name")));
        Assert.False(tables.TryGetChannel(1, "sim_car", 0, out _));
    }

    [Fact]
    public void New_Session_Starts_Over()
    {
        var tables = new DescriptorTables();
        tables.Apply(Read(DiscoveryPager.Paginate(Header, new[] { BigObject(2) }, 5)[0]));
        var next = DiscoveryPager.Paginate(() => { var h = Header(); h.SessionStartUnixUs = 43; return h; },
                                           new[] { BigObject(2) }, 0)[0];
        tables.Apply(Read(next));
        Assert.Null(tables.Find(5));
        Assert.NotNull(tables.Find(0));
    }

    [Fact]
    public void Motion_Plan_Keeps_Every_Part_Within_Budget()
    {
        MotionFrame Frame(bool part0) => new MotionFrame
        {
            Timestamp = 1,
            Objects = new List<MotionObject>
            {
                new() { Name = "sim_car", Channels = new List<ChannelValue> { new() { Number = 1 } } },
            },
        };
        var probe0 = Frame(true);
        probe0.Objects![0].InitBodyDynamics().SpecificForce.X = 1;
        probe0.Objects[0].LocalGravity.InitVector().X = 1;
        var plan = MotionPartPlanner.Plan(500, probe0, Frame(false));
        Assert.True(plan.Count > 1);
        Assert.Equal(500, plan.Sum(p => p.Count));
        Assert.Equal(0, plan[0].Start);

        // build each part as sent, with the largest values, and measure
        for (int p = 0; p < plan.Count; p++)
        {
            var obj = new MotionObject { Name = "sim_car", Channels = new List<ChannelValue>() };
            if (p == 0)
            {
                obj.Position.X = 1; obj.Orientation.W = 1;
                obj.InitBodyDynamics().SpecificForce.X = 1;
                obj.LocalGravity.InitVector().X = 1;
            }
            for (int i = 0; i < plan[p].Count; i++)
                obj.Channels.Add(new ChannelValue { Id = ushort.MaxValue, Number = float.MaxValue });
            var frame = new MotionFrame
            {
                Timestamp = double.MaxValue, Generation = uint.MaxValue,
                Part = (ushort)p, PartCount = (ushort)plan.Count,
                Objects = new List<MotionObject> { obj },
            };
            int size = DatagramBudget.SizeOf(FrameType.Motion, frame);
            Assert.True(size <= DatagramBudget.Bytes, $"part {p} is {size} bytes");
        }
    }

    // ---- publisher over the loopback: raw datagrams ----

    sealed class RawCapture : IDisposable
    {
        readonly Socket _socket;
        public RawCapture(int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse("239.255.17.17"), IPAddress.Loopback));
            _socket.ReceiveTimeout = 300;
        }

        public List<byte[]> Drain()
        {
            var all = new List<byte[]>();
            var buf = new byte[65536];
            while (true)
            {
                try
                {
                    int n = _socket.Receive(buf);
                    all.Add(buf.AsSpan(0, n).ToArray());
                }
                catch (SocketException) { return all; }
            }
        }

        public void Dispose() => _socket.Dispose();
    }

    static Capnp.DeserializerState Root(byte[] datagram, out FrameType type)
    {
        Assert.True(Envelope.TryUnwrap(datagram, out type, out var payload));
        return MessageDecoder.DecodeRoot(payload.Span);
    }

    [Fact]
    public void Publisher_With_Hundreds_Of_Channels_Pages_And_Parts()
    {
        const int port = 41260;
        using var capture = new RawCapture(port);
        using var sender = new TelemetrySender(port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "paging-publisher", motionHz: 60);
        pub.DeclareObject("sim_car", "Paging test car");
        var handles = new List<ChannelHandle>();
        for (int i = 0; i < 300; i++)
        {
            var h = pub.DeclareChannel($"sim.channel{i:D3}", Unit.MetrePerSecond,
                                       $"Simulator channel number {i} with a realistic description");
            pub.SetChannel(h, i * 0.5);
            handles.Add(h);
        }
        pub.BindPose(() => new System.Numerics.Vector3(1, 2, 3), () => System.Numerics.Quaternion.Identity);
        Assert.True(pub.DiscoveryPageCount > 1);

        pub.Tick(0.0);                    // all pages back-to-back + one tick in parts
        var datagrams = capture.Drain();

        var tables = new DescriptorTables();
        var parts = new List<CapnpGen.MotionFrame.READER>();
        foreach (var d in datagrams)
        {
            Assert.True(d.Length <= DatagramBudget.Bytes, $"{d.Length}-byte datagram");
            var root = Root(d, out var type);
            if (type == FrameType.Discovery) tables.Apply(CapnpGen.DiscoveryFrame.READER.create(root));
            else if (type == FrameType.Motion) parts.Add(CapnpGen.MotionFrame.READER.create(root));
        }

        Assert.True(tables.Current!.Complete);
        Assert.Equal(pub.DiscoveryPageCount, tables.Current.PageCount);
        Assert.Equal(300, tables.Current.Objects["sim_car"].Channels.Count);

        Assert.True(parts.Count > 1, $"{parts.Count} motion parts");
        Assert.All(parts, p => Assert.Equal(parts.Count, p.PartCount));
        Assert.Equal(1f, parts.Single(p => p.Part == 0).Objects[0].Position.X);
        var values = new Dictionary<ushort, float>();
        foreach (var part in parts)
            foreach (var cv in part.Objects[0].Channels)
            {
                Assert.True(tables.TryGetChannel(part.Generation, "sim_car", cv.Id, out _));
                values[cv.Id] = cv.Number;
            }
        Assert.Equal(300, values.Count);
        Assert.Equal(149.5f, values[299]);
    }

    [Fact]
    public void Publisher_Spreads_Periodic_Pages_And_Bumps_Generation_On_Identity_Change()
    {
        const int port = 41261;
        using var capture = new RawCapture(port);
        using var sender = new TelemetrySender(port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "paging-pacing", motionHz: 10,
                                               discoveryIntervalSeconds: 2.0);
        pub.DeclareObject("sim_car", "first label");
        for (int i = 0; i < 120; i++)
            pub.DeclareChannel($"sim.channel{i:D3}", Unit.MetrePerSecond, $"Channel {i} description text");

        static int Discoveries(List<byte[]> ds) => ds.Count(d => d[4] == (byte)FrameType.Discovery);

        pub.Tick(0.0);
        int pages = pub.DiscoveryPageCount;
        Assert.True(pages > 2);
        Assert.Equal(pages, Discoveries(capture.Drain()));          // burst at start

        pub.Tick(1.0);
        Assert.Equal(0, Discoveries(capture.Drain()));              // inside the interval
        pub.Tick(2.0);
        Assert.Equal(1, Discoveries(capture.Drain()));              // a new cycle: first page only
        pub.Tick(2.0 + 2.0 / pages * 1.5);
        Assert.Equal(1, Discoveries(capture.Drain()));              // spread, one at a time

        Assert.Equal(0u, pub.Generation);
        pub.SetObjectTypeLabel("second label");
        pub.Tick(3.5);
        var burst = capture.Drain();
        Assert.Equal(pages, Discoveries(burst));                    // identity change: every page
        foreach (var d in burst)
        {
            var root = Root(d, out var type);
            if (type == FrameType.Discovery)
                Assert.Equal(1u, CapnpGen.DiscoveryFrame.READER.create(root).Generation);
            else if (type == FrameType.Motion)
                Assert.Equal(1u, CapnpGen.MotionFrame.READER.create(root).Generation);
        }
    }
}
