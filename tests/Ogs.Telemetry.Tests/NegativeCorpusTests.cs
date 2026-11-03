using System.Net;
using System.Text.Json;
using Ogs.Telemetry;
using Xunit;

/// <summary>The standard's negative corpus (conformance/negative): every
/// datagram a receiver must survive, checked against its expectation.</summary>
public class NegativeCorpusTests
{
    static string Dir(string sub) => StandardCorpus.Dir(sub);

    static byte[] Hex(string path) =>
        Convert.FromHexString(string.Concat(File.ReadAllLines(path).Select(l => l.Trim())));

    static byte[] Negative(string id) => Hex(Path.Combine(Dir("negative"), "vectors", id + ".hex"));
    static byte[] Positive(string id) => Hex(Path.Combine(Dir("vectors"), id + ".hex"));

    public static IEnumerable<object[]> Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir("negative"), "manifest.json")));
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
            yield return new object[] { v.GetProperty("id").GetString()!, v.GetProperty("expect").GetString()! };
    }

    static readonly IPEndPoint Source = new(IPAddress.Loopback, 50000);

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Receiver_Survives(string id, string expect)
    {
        var blob = Negative(id);
        bool unwrapped = Envelope.TryUnwrap(blob, out var type, out var payload);
        if (expect == "skip")
        {
            Assert.False(unwrapped);
            return;
        }
        Assert.True(unwrapped);
        if (expect == "decode-error")
        {
            Assert.Throws<Capnp.DeserializationException>(() => MessageDecoder.DecodeRoot(payload.Span));
            return;
        }

        var tracker = new SessionTracker(() => 0, 1, new[] { IPAddress.Loopback });
        var root = MessageDecoder.DecodeRoot(payload.Span);
        switch (id)
        {
            case "multi-segment":
            {
                var m = CapnpGen.MotionFrame.READER.create(root);
                Assert.Equal("obj", m.Objects[0].Name);
                Assert.Equal(1.5f, m.Objects[0].Position.X);
                Assert.Equal(1f, m.Objects[0].Orientation.W);
                break;
            }
            case "non-canonical-null-text":
            {
                var s = tracker.OnDiscovery(Source, CapnpGen.DiscoveryFrame.READER.create(root));
                Assert.Equal("", s.GameName);
                Assert.Equal("", s.Tables.Current!.Objects["obj"].TypeLabel);
                break;
            }
            case "unknown-dynamics-variant":
            {
                var o = CapnpGen.MotionFrame.READER.create(root).Objects[0];
                Assert.False(Enum.IsDefined(o.which));                 // a future variant
                Assert.Equal("obj", o.Name);                           // the rest still reads
                var g = Gravity.Effective(o, new System.Numerics.Vector3(0, -9.5f, 0));
                Assert.Equal(-9.5f, g.Y);
                break;
            }
            case "unknown-drivepoint-variant":
            {
                var p = CapnpGen.MotionFrame.READER.create(root).Objects[0].DrivePoints;
                Assert.Equal(4, p.Count);
                Assert.False(Enum.IsDefined(p[1].which));
                Assert.Equal(1, p[1].Id);
                Assert.Equal(CapnpGen.DrivePoint.WHICH.Jet, p[2].which);
                Assert.Equal(40000f, p[2].Jet.Thrust);
                break;
            }
            case "unknown-wheel-measurement-variant":
            {
                var w = CapnpGen.MotionFrame.READER.create(root).Objects[0].DrivePoints[0].Wheel;
                Assert.NotEqual(CapnpGen.WheelState.rpm.WHICH.Value, w.Rpm.which); // read as not measured
                Assert.Equal(850f, w.Torque.Value);
                break;
            }
            case "page-index-out-of-range":
            {
                var d = CapnpGen.DiscoveryFrame.READER.create(root);
                var tables = new DescriptorTables();
                tables.Apply(d);
                Assert.Null(tables.Current);                           // not merged
                break;
            }
            case "type-mismatch-value":
            case "undeclared-channel":
            {
                Assert.True(Envelope.TryUnwrap(Positive("discovery-channels"), out _, out var dp));
                var s = tracker.OnDiscovery(Source,
                    CapnpGen.DiscoveryFrame.READER.create(MessageDecoder.DecodeRoot(dp.Span)));
                var m = CapnpGen.MotionFrame.READER.create(root);
                var cv = m.Objects[0].Channels[0];
                bool applied = s.Tables.TryGetChannel(m.Generation, m.Objects[0].Name, cv.Id, out var ch)
                               && ch.Accepts(cv.which);
                Assert.False(applied);
                break;
            }
            case "nonfinite-pose":
            {
                var p = CapnpGen.MotionFrame.READER.create(root).Objects[0].Position;
                Assert.True(float.IsNaN(p.X) && float.IsInfinity(p.Y)); // decodes; hardware receivers fault
                break;
            }
            default:
                Assert.Fail($"no expectation coded for negative vector {id}");
                break;
        }
    }
}
