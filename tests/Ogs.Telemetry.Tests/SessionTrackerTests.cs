using System.Net;
using Ogs.Telemetry;
using Ogs.Telemetry.Schema;
using Ogs.Telemetry.Wire;
using Xunit;

/// <summary>Several senders on one group (implementation-udp.md § Senders
/// and sessions): per-source tables, selection, pinning and session end.</summary>
public class SessionTrackerTests
{
    static readonly IPAddress Here = IPAddress.Parse("192.168.1.10");
    static readonly IPEndPoint LocalSim = new(Here, 50001);
    static readonly IPEndPoint RemoteSim = new(IPAddress.Parse("192.168.1.20"), 50002);
    static readonly IPEndPoint RemoteSim2 = new(IPAddress.Parse("192.168.1.30"), 50003);

    long _now;
    SessionTracker Tracker() => new(() => _now, 1, new[] { IPAddress.Loopback, Here });

    static CapnpGen.DiscoveryFrame.READER Discovery(string game, long start, string label = "",
                                                   string host = "", string channel = "engine.rpm")
    {
        var frame = new DiscoveryFrame
        {
            SchemaVersion = 1, GameName = game, SessionStartUnixUs = start,
            InstanceLabel = label, Host = host,
            Objects = new List<ObjectDescriptor>
            {
                new()
                {
                    Name = "player", Primary = true,
                    Channels = new List<ChannelDescriptor>
                    {
                        new() { Id = 0, Name = channel, Unit = "rpm", Type = ChannelType.Number },
                    },
                },
            },
        };
        var datagram = CanonicalEncoder.EncodeDatagram(FrameType.Discovery, frame);
        Assert.True(Envelope.TryUnwrap(datagram, out _, out var payload));
        return CapnpGen.DiscoveryFrame.READER.create(MessageDecoder.DecodeRoot(payload.Span));
    }

    [Fact]
    public void Senders_Keep_Their_Own_Tables()
    {
        var t = Tracker();
        t.OnDiscovery(LocalSim, Discovery("Sim A", 100, channel: "engine.rpm"));
        t.OnDiscovery(RemoteSim, Discovery("Sim B", 200, channel: "vehicle.speed"));

        // the same id names different channels per sender
        Assert.True(t.Find(LocalSim)!.Tables.TryGetChannel(0, "player", 0, out var a));
        Assert.True(t.Find(RemoteSim)!.Tables.TryGetChannel(0, "player", 0, out var b));
        Assert.Equal("engine.rpm", a.Name);
        Assert.Equal("vehicle.speed", b.Name);
        Assert.Equal(CapnpGen.ChannelType.number, a.Type);
        Assert.Equal(2, t.Sessions.Count);
        Assert.True(t.Ambiguous);
    }

    [Fact]
    public void Automatic_Choice_Prefers_This_Host_Then_The_Newest_Session()
    {
        var t = Tracker();
        t.OnDiscovery(RemoteSim, Discovery("Sim B", 300));
        t.OnDiscovery(LocalSim, Discovery("Sim A", 100));
        Assert.Same(t.Find(LocalSim), t.Selected);   // local beats newer remote

        var r = Tracker();
        r.OnDiscovery(RemoteSim, Discovery("Sim B", 300));
        r.OnDiscovery(RemoteSim2, Discovery("Sim C", 400));
        Assert.Same(r.Find(RemoteSim2), r.Selected); // newest remote
    }

    [Fact]
    public void Pinned_Identity_Selects_Its_Sender_And_Waits_For_It()
    {
        var t = Tracker();
        t.Pinned = new SessionPin("Sim B", "Rig 2", "sim-pc");
        t.OnDiscovery(LocalSim, Discovery("Sim A", 100));
        Assert.Null(t.Selected);                     // never follows another game
        Assert.False(t.Ambiguous);

        // the pinned instance appears on a new address and port
        t.OnDiscovery(RemoteSim2, Discovery("Sim B", 500, "Rig 2", "sim-pc"));
        Assert.Same(t.Find(RemoteSim2), t.Selected);
        Assert.Equal("Sim B (Rig 2)", t.Selected!.ToString());
    }

    [Fact]
    public void New_Session_Start_From_A_Sender_Restarts_It()
    {
        var t = Tracker();
        var started = new List<long>();
        t.SessionStarted += s => started.Add(s.SessionStartUnixUs);
        t.OnDiscovery(LocalSim, Discovery("Sim A", 100));
        t.OnDiscovery(LocalSim, Discovery("Sim A", 100)); // re-send: same session
        t.OnDiscovery(LocalSim, Discovery("Sim A", 900)); // new session
        Assert.Equal(new long[] { 100, 900 }, started);
        Assert.Single(t.Sessions);
    }

    [Fact]
    public void Silent_Senders_End_After_Ten_Seconds_And_Frames_Keep_Them_Alive()
    {
        var t = Tracker();
        var ended = new List<string>();
        t.SessionEnded += s => ended.Add(s.GameName);
        t.OnDiscovery(LocalSim, Discovery("Sim A", 100));
        t.OnDiscovery(RemoteSim, Discovery("Sim B", 200));

        _now = 8;
        Assert.NotNull(t.OnFrame(LocalSim));         // motion keeps A alive
        Assert.Null(t.OnFrame(RemoteSim2));          // no discovery yet: hold back

        _now = 15;
        t.Prune();
        Assert.Equal(new[] { "Sim B" }, ended);
        Assert.Single(t.Sessions);
        Assert.Same(t.Find(LocalSim), t.Selected);
    }

    [Fact]
    public void Declared_Type_Rejects_Other_Variants()
    {
        var t = Tracker();
        t.OnDiscovery(LocalSim, Discovery("Sim A", 100));
        Assert.True(t.Find(LocalSim)!.Tables.TryGetChannel(0, "player", 0, out var ch));
        Assert.True(ch.Accepts(CapnpGen.ChannelValue.WHICH.Number));
        Assert.False(ch.Accepts(CapnpGen.ChannelValue.WHICH.Boolean));
    }
}
