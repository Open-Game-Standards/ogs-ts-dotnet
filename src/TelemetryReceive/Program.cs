using Ogs.Telemetry;
using CapnpGen;

// Reference receiver: joins the multicast group, tells senders apart by
// source address and port (implementation-udp.md § Senders and sessions),
// merges each sender's discovery pages (printing each generation once it is
// complete), prints motion throttled, and runs conformance checks on every
// frame.

using var receiver = new TelemetryReceiver();
var printedDiscovery = false;
long motionSeen = 0;
var motionPerObject = new Dictionary<string, long>();
long violations = 0;
var seenEventIds = new HashSet<(System.Net.IPEndPoint, uint)>();
// one tracker: per-sender descriptor tables, each merging paged discovery by
// generation (implementation-udp.md § Paged discovery)
var sessions = new SessionTracker();
sessions.SessionStarted += s => Console.WriteLine(
    $"Session: {s} from {s.Source} start={s.SessionStartUnixUs}" +
    (sessions.Sessions.Count > 1 ? $" ({sessions.Sessions.Count} sessions; following {sessions.Selected})" : ""));
sessions.SessionEnded += s => Console.WriteLine($"Session ended: {s}");
var printedGenerations = new HashSet<(System.Net.IPEndPoint, uint)>();
long deduped = 0;
// session gravity from the latest discovery (CONVENTIONS, Gravity);
// standard gravity until the first discovery arrives
var sessionGravity = new System.Numerics.Vector3(0f, -9.80665f, 0f);

receiver.DiscoveryReceivedFrom += (source, d) =>
{
    if (d.SchemaVersion != 1) Warn($"schemaVersion {d.SchemaVersion} != 1 (attempting anyway)");
    if (d.PageCount > 1 && d.Page >= d.PageCount) Warn($"page {d.Page} of {d.PageCount}");
    var generation = sessions.OnDiscovery(source, d).Tables.Current!;
    // the primary object is declared on the first page listing it; check
    // once the generation is complete, and print it then (once)
    if (!generation.Complete || !printedGenerations.Add((source, generation.Number))) return;
    if (!generation.Objects.Values.Any(o => o.Primary)) Warn("no primary object in discovery");
    sessionGravity = Gravity.Session(d.Environment);
    if (d.Environment.which == CapnpGen.Environment.WHICH.GravityVector &&
        MathF.Abs(sessionGravity.Length() - d.Environment.Gravity) > 1e-3f * MathF.Max(1f, d.Environment.Gravity))
        Warn($"gravity {d.Environment.Gravity} != |gravityVector| {sessionGravity.Length():F3}");
    Console.WriteLine($"Discovery: game={d.GameName} instance='{d.InstanceLabel}' host='{d.Host}' " +
                      $"from={source} schema={d.SchemaVersion} " +
                      $"generation={generation.Number} pages={generation.PageCount} " +
                      $"objects={generation.Objects.Count} medium={d.Environment.Medium} " +
                      $"gravity={Vec(sessionGravity)}");
    foreach (var o in generation.Objects.Values)
    {
        Console.WriteLine($"  object '{o.Name}' type={o.Type} typeLabel='{o.TypeLabel}' " +
                          $"primary={o.Primary} drivePoints={o.DrivePointCount} " +
                          $"channels={o.Channels.Count}");
        foreach (var ch in o.Channels.Values.OrderBy(c => c.Id))
            Console.WriteLine($"    channel {ch.Id}: {ch.Name} [{ch.Unit}] type={ch.Type} range={DescribeRange(ch)}");
        foreach (var (id, value) in o.Values)
            Console.WriteLine($"    value {id}: {value}");
    }
    printedDiscovery = true;
};

receiver.EventReceivedFrom += (source, e) =>
{
    sessions.OnFrame(source);
    foreach (var ev in e.Events)
    {
        var dir = ev.which == CapnpGen.Event.WHICH.Direction
            ? $" dir=({ev.Direction.X:F1},{ev.Direction.Y:F1},{ev.Direction.Z:F1})" : "";
        // ids are session-monotonic per sender: dedup per source
        if (!seenEventIds.Add((source, ev.Id))) { deduped++; continue; }
        Console.WriteLine($"Event: id={ev.Id} {ev.Name} intensity={ev.Intensity:F2} " +
                          $"duration={ev.Duration:F2}s{dir} object='{ev.Object}'");
    }
};

receiver.MotionReceivedFrom += (source, m) =>
{
    // ids resolve only against this sender's own tables
    var tables = sessions.OnFrame(source)?.Tables;
    if (m.Objects.Count == 0) return;
    if (m.PartCount > 1 && m.Part >= m.PartCount) Warn($"part {m.Part} of {m.PartCount}");
    // a later part carries channel values only; pose rides part 0
    if (m.Part > 0) return;
    var primary = m.Objects[0];
    var q = primary.Orientation;
    float norm = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
    if (MathF.Abs(norm - 1f) > 1e-3f) Warn($"quaternion norm {norm:F4} != 1 on '{primary.Name}'");
    motionSeen++;
    // throttle per object: a global frame counter would only ever print
    // one sender when several publishers interleave (modulo parity)
    var key = $"{source}/{primary.Name}";
    long seen = motionPerObject.TryGetValue(key, out var n) ? n : 0;
    motionPerObject[key] = seen + 1;
    if (seen % 180 == 0)
    {
        // ids resolve against the generation the frame names; values whose
        // descriptor page has not arrived yet are held back
        // values whose variant differs from the declared type are dropped
        var vals = tables is null ? "" : string.Join(" ", primary.Channels
            .Where(c => tables.TryGetChannel(m.Generation, primary.Name ?? "", c.Id, out var known)
                        && known.Accepts(c.which))
            .Select(c =>
            {
                tables.TryGetChannel(m.Generation, primary.Name ?? "", c.Id, out var ch);
                return c.which == CapnpGen.ChannelValue.WHICH.Boolean
                    ? $"{ch.Name}={c.Boolean}"
                    : $"{ch.Name}={c.Number:F2}";
            }));
        // text channels ride TextValue lists
        var texts = tables is null ? "" : string.Join(" ", primary.Texts
            .Where(t => tables.TryGetChannel(m.Generation, primary.Name ?? "", t.Id, out var known)
                        && known.AcceptsText)
            .Select(t =>
            {
                tables.TryGetChannel(m.Generation, primary.Name ?? "", t.Id, out var ch);
                return $"{ch.Name}=\"{t.Text}\"";
            }));
        if (texts.Length > 0) vals = vals.Length > 0 ? vals + " " + texts : texts;
        var (yaw, pitch, roll) = ToEulerDegrees(q);
        // effective gravity: shown only when the object carries its own
        var local = primary.LocalGravity.which == MotionObject.localGravity.WHICH.Vector
            ? $" g={Vec(Gravity.Effective(primary, sessionGravity))}" : "";
        Console.WriteLine($"Motion: t={m.Timestamp:F2}s '{primary.Name}' " +
                          $"pos=({primary.Position.X:F2},{primary.Position.Y:F2},{primary.Position.Z:F2}) " +
                          $"|q|={norm:F3} yaw={Signed(yaw)}deg pitch={Signed(pitch)}deg roll={Signed(roll)}deg" +
                          local + (vals.Length > 0 ? " " + vals : ""));
    }
};

// Orientation readout in the OGS-TS frame (left-handed: Z forward, X
// right, Y up). ZYX Tait-Bryan angles from the rotated basis vectors:
// forward f = q·(0,0,1), right r = q·(1,0,0) → yaw = atan2(f.X, f.Z)
// (0 = +Z, turning right positive), pitch = asin(f.Y) (nose up
// positive), roll = asin(-r.Y/cos(pitch)) (right side down positive;
// exact for r.Y = -sin(roll)·cos(pitch), degenerate only at ±90° pitch).
static (float yaw, float pitch, float roll) ToEulerDegrees(
    CapnpGen.Quaternion.READER q)
{
    var rot = new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W);
    var f = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, rot);
    var r = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, rot);
    const float rad2deg = 180f / MathF.PI;
    float yaw = MathF.Atan2(f.X, f.Z) * rad2deg;
    float pitch = MathF.Asin(Clamp1(f.Y)) * rad2deg;
    float cosPitch = MathF.Cos(pitch / rad2deg);
    float roll = MathF.Abs(cosPitch) < 1e-3f ? 0f
        : MathF.Asin(Clamp1(-r.Y / cosPitch)) * rad2deg;
    return (yaw, pitch, roll);
}

static string Vec(System.Numerics.Vector3 v) => $"({v.X:F2},{v.Y:F2},{v.Z:F2})";

static float Clamp1(float v) => v < -1f ? -1f : v > 1f ? 1f : v;

static string Signed(float v) => (v >= 0 ? "+" : "") + (v + 0f).ToString("F1"); // +0f folds -0.0

string DescribeRange(DescriptorTables.Channel ch) =>
    ch.HasRange ? $"{ch.Min}..{ch.Max}" : "-";
void Warn(string msg) { violations++; Console.WriteLine($"  [warn] {msg}"); }

receiver.Start();
Console.WriteLine("TelemetryReceive: listening on 239.255.17.17:40123 (Ctrl-C to stop)");
while (!printedDiscovery || motionSeen < 5 || seenEventIds.Count == 0)
    await Task.Delay(100);
Console.WriteLine($"Summary: discoverySeen={printedDiscovery} motionFrames={motionSeen} " +
                  $"events={seenEventIds.Count} dedupedReplays={deduped} " +
                  $"warnings={violations} foreignSkipped={receiver.ForeignSkipped}");
