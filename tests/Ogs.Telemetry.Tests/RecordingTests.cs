using System.Net;
using Ogs.Telemetry;
using Ogs.Telemetry.Wire;
using Xunit;

/// <summary>.ogsrec recordings (recording.md): writing, reading, the
/// corpus sample, and replay into a receiver.</summary>
public class RecordingTests
{
    static readonly IPEndPoint V4 = new(IPAddress.Parse("10.0.0.2"), 50001);
    static readonly IPEndPoint V6 = new(IPAddress.Parse("fe80::2"), 50002);

    static byte[] Motion(double t) =>
        CanonicalEncoder.EncodeDatagram(FrameType.Motion, new Ogs.Telemetry.Schema.MotionFrame { Timestamp = t });

    [Fact]
    public void Records_Round_Trip_With_Time_And_Source()
    {
        var ms = new MemoryStream();
        using (var w = new RecordingWriter(ms, 1_730_000_000_000_000, ownsStream: false))
        {
            w.Write(0, V4, Motion(1));
            w.Write(16_667, V6, Motion(2));
        }
        ms.Position = 0;
        var (start, records) = RecordingReader.Read(ms);
        Assert.Equal(1_730_000_000_000_000, start);
        Assert.Equal(2, records.Count);
        Assert.Equal(16_667, records[1].ArrivalMicros);
        Assert.Equal(V6, records[1].Source);
        Assert.Equal(Motion(2), records[1].Datagram);
    }

    [Fact]
    public void A_Truncated_Final_Record_Is_Dropped()
    {
        var ms = new MemoryStream();
        using (var w = new RecordingWriter(ms, 0, ownsStream: false))
        {
            w.Write(0, V4, Motion(1));
            w.Write(10, V4, Motion(2));
        }
        var cut = new MemoryStream(ms.ToArray().AsSpan(0, (int)ms.Length - 5).ToArray());
        var (_, records) = RecordingReader.Read(cut);
        Assert.Single(records);
    }

    [Fact]
    public void Reads_The_Corpus_Sample()
    {
        var path = Path.Combine(StandardCorpus.Dir("recordings"), "sample.ogsrec");
        var (start, records) = RecordingReader.Read(path);
        Assert.Equal(ConformanceVectors.SessionStartUnixUs, start);
        Assert.Equal(5, records.Count);
        var vectors = ConformanceVectors.All().ToDictionary(v => v.Id, v => v.Encode());
        Assert.Equal(vectors["motion-pose"], records[2].Datagram);
        Assert.Equal(2, records.Select(r => r.Source).Distinct().Count());
        Assert.Contains(records, r => r.Source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
    }

    [Fact]
    public async Task Replay_Reaches_A_Receiver_From_One_Socket_Per_Recorded_Source()
    {
        const int port = 41391;
        using var receiver = new TelemetryReceiver(port: port, localInterface: IPAddress.Loopback);
        var sources = new HashSet<IPEndPoint>();
        var seen = new List<double>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MotionReceivedFrom += (src, m) =>
        {
            lock (seen)
            {
                sources.Add(src);
                seen.Add(m.Timestamp);
                if (seen.Count >= 2) done.TrySetResult();
            }
        };
        receiver.Start();

        var records = new List<RecordedDatagram>
        {
            new(0, V4, Motion(1)),
            new(1_000, V6, Motion(2)),
        };
        for (int i = 0; i < 20 && !done.Task.IsCompleted; i++)
        {
            await RecordingReplayer.ReplayAsync(records, port: port, speed: 10, localInterface: IPAddress.Loopback);
            await Task.Delay(50);
        }
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (seen)
        {
            Assert.Contains(1.0, seen);
            Assert.Contains(2.0, seen);
            Assert.True(sources.Count >= 2); // the two recorded senders stay apart
        }
    }

    [Fact]
    public async Task PlayInto_Feeds_A_Receiver_Without_The_Network()
    {
        // never started: no socket traffic, only injected datagrams
        using var receiver = new TelemetryReceiver(port: 41392, localInterface: IPAddress.Loopback);
        var seen = new List<(IPEndPoint, double)>();
        receiver.MotionReceivedFrom += (src, m) => seen.Add((src, m.Timestamp));
        var records = new List<RecordedDatagram>
        {
            new(0, V4, Motion(1)),
            new(2_000, V6, Motion(2)),
            new(3_000, V4, new byte[] { 1, 2, 3 }), // foreign: skipped
        };
        await RecordingReplayer.PlayIntoAsync(records, receiver, speed: 4);
        Assert.Equal(new[] { (V4, 1.0), (V6, 2.0) }, seen);
        Assert.Equal(1, receiver.ForeignSkipped);
    }
}
