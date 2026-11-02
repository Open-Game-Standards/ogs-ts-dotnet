#nullable enable
using System.Text;
using Ogs.Telemetry;

// ConformanceGen — deterministic OGS-TS conformance corpus generator.
// Writes golden datagrams (hex) + semantic manifest into the standard repo
// (default ../OGS-Telemetry-Standard/conformance). The frames come from
// Ogs.Telemetry.ConformanceVectors (which engine hosts also encode to
// check their runtime against the corpus) in canonical OGS encoding, so
// the bytes are a function of the content alone. Each manifest input below
// lists every field that differs from its default — an independent encoder
// can rebuild the vector from it; the standard's validate.py checks each
// vector equals its input exactly.
// Usage: dotnet run --project src/ConformanceGen [outputDir]

var outDir = args.Length > 0 ? args[0]
    : "../OGS-Telemetry-Standard/conformance";
var vecDir = Path.Combine(outDir, "vectors");
Directory.CreateDirectory(vecDir);

const long sessionUs = ConformanceVectors.SessionStartUnixUs;
var envInput = new { airDensity = 1.25, temperature = 20.0, pressure = 1.0, gravity = 9.5, medium = "asphalt" };

var described = new Dictionary<string, (string frameType, string description, object input)>
{
    ["discovery-minimal"] = ("discovery",
        "Smallest valid DiscoveryFrame: one primary object, no channels or drive points",
        new
        {
            schemaVersion = 1, gameName = "conformance", sessionStartUnixUs = sessionUs,
            environment = envInput,
            objects = new[] { new { name = "obj", type = "vehicle", primary = true } },
        }),
    ["discovery-channels"] = ("discovery",
        "Object with three channels incl. ranges, a description and a unitless boolean channel",
        new
        {
            schemaVersion = 1, gameName = "conformance", sessionStartUnixUs = sessionUs,
            environment = envInput,
            objects = new[] { new { name = "demo_car", type = "vehicle", typeLabel = "conformance car",
                                    primary = true, channels = new object[]
            {
                new { id = 0, name = "engine.rpm", unit = "rpm", range = new { min = 0.0, max = 9000.0 },
                      description = "Engine revolutions per minute" },
                new { id = 1, name = "vehicle.speed", unit = "m/s", range = new { max = 30.0 } },
                new { id = 2, name = "lights.headlights" },
            } } },
        }),
    ["motion-pose"] = ("motion",
        "Pose core only: position + identity quaternion",
        new
        {
            timestamp = 1.25,
            objects = new[] { new { name = "obj",
                position = new { x = 1.5, y = 0.25, z = -3.75 },
                orientation = new { w = 1.0 } } },
        }),
    ["motion-channels"] = ("motion",
        "Pose + channel values: numbers and a boolean keyed by discovery ids",
        new
        {
            timestamp = 2.5,
            objects = new[] { new { name = "demo_car",
                position = new { x = -2.0, y = 0.5, z = 3.0 },
                orientation = new { y = 1.0 },
                channels = new object[]
                {
                    new { number = 3250.5 },
                    new { id = 1, number = 8.0 },
                    new { id = 2, boolean = true },
                } } },
        }),
    ["motion-multi"] = ("motion",
        "Primary object first, second object after (multi-car grids)",
        new
        {
            timestamp = 3.75,
            objects = new object[]
            {
                new { name = "player_car", position = new { x = 1.0 }, orientation = new { w = 1.0 } },
                new { name = "wingman", position = new { x = -1.0, y = 0.5, z = 2.5 },
                      orientation = new { y = 1.0 } },
            },
        }),
    ["motion-bodydynamics"] = ("motion",
        "Pose + bodyDynamics union variant: accelerometer-style specific force " +
        "(+heave gravity reaction at rest) and C-4 body angular velocity",
        new
        {
            timestamp = 4.5,
            objects = new[] { new { name = "obj",
                position = new { x = 0.5, y = 1.25, z = -2.5 },
                orientation = new { y = 0.5, w = 0.875 },
                bodyDynamics = new
                {
                    specificForce = new { x = 0.25, y = 9.5, z = -1.5 },
                    angularVelocity = new { x = -0.125, y = 0.375, z = 0.75 },
                } } },
        }),
    ["event-impact"] = ("event",
        "One body.impact stimulus with direction, intensity and duration",
        new
        {
            timestamp = 0.125,
            events = new[] { new { id = 7u, name = "body.impact", intensity = 0.75,
                                   duration = 0.25, direction = new { x = -1.0 } } },
        }),
    ["discovery-gravity-vector"] = ("discovery",
        "Environment gravityVector union variant: tilted session gravity, " +
        "scalar gravity = its magnitude (CONVENTIONS C-12)",
        new
        {
            schemaVersion = 1, gameName = "conformance", sessionStartUnixUs = sessionUs,
            environment = new
            {
                airDensity = 1.25, temperature = 20.0, pressure = 1.0, gravity = 5.0, medium = "vacuum",
                gravityVector = new { x = 3.0, y = -4.0 },
            },
            objects = new[] { new { name = "obj", type = "spacecraft", primary = true } },
        }),
    ["motion-local-gravity"] = ("motion",
        "MotionObject localGravity.vector with bodyDynamics: specific force is the " +
        "reaction to the object's effective gravity (CONVENTIONS C-12)",
        new
        {
            timestamp = 5.25,
            objects = new[] { new { name = "obj",
                position = new { x = 12.0, y = 0.5, z = -4.0 },
                orientation = new { w = 1.0 },
                bodyDynamics = new { specificForce = new { x = 2.0 } },
                localGravity = new { vector = new { x = -2.0 } } } },
        }),
    ["motion-local-gravity-zero"] = ("motion",
        "MotionObject localGravity.vector selected with an all-zero vector: weightless, " +
        "distinct from noLocalGravity (CONVENTIONS C-12)",
        new
        {
            timestamp = 6.5,
            objects = new[] { new { name = "obj",
                position = new { x = 1.0, y = 2.0, z = 3.0 },
                orientation = new { w = 1.0 },
                localGravity = new { vector = new { } } } },
        }),
    ["discovery-paged-p0"] = ("discovery",
        "Paged discovery, page 0 of 2 (generation 3): the object's identity and its first channel",
        new
        {
            schemaVersion = 1, gameName = "conformance", sessionStartUnixUs = sessionUs,
            environment = envInput,
            objects = new[] { new { name = "demo_car", type = "vehicle", typeLabel = "conformance car",
                                    primary = true, channels = new object[]
            {
                new { name = "engine.rpm", unit = "rpm" },
            } } },
            generation = ConformanceVectors.PagedGeneration, pageCount = 2,
        }),
    ["discovery-paged-p1"] = ("discovery",
        "Paged discovery, page 1 of 2 (generation 3): the object by name only, with a session " +
        "text channel and its value, and a streamed channel",
        new
        {
            schemaVersion = 1, gameName = "conformance", sessionStartUnixUs = sessionUs,
            environment = envInput,
            objects = new[] { new { name = "demo_car", channels = new object[]
            {
                new { id = 1, name = "session.track" },
                new { id = 2, name = "vehicle.speed", unit = "m/s" },
            }, values = new object[] { new { id = 1, text = "Test Ring" } } } },
            generation = ConformanceVectors.PagedGeneration, page = 1, pageCount = 2,
        }),
    ["motion-parts-p0"] = ("motion",
        "Motion tick in parts, part 0 of 2 (generation 3): pose and the first channel",
        new
        {
            timestamp = 7.75,
            objects = new[] { new { name = "demo_car",
                position = new { x = 4.0, y = 0.5, z = -1.0 },
                orientation = new { w = 1.0 },
                channels = new object[] { new { number = 4100.0 } } } },
            generation = ConformanceVectors.PagedGeneration, partCount = 2,
        }),
    ["motion-parts-p1"] = ("motion",
        "Motion tick in parts, part 1 of 2 (generation 3): the object by name with channel values only",
        new
        {
            timestamp = 7.75,
            objects = new[] { new { name = "demo_car",
                channels = new object[] { new { id = 2, number = 12.5 } } } },
            generation = ConformanceVectors.PagedGeneration, part = 1, partCount = 2,
        }),
};

var manifest = new
{
    corpusVersion = 6,
    schemaVersion = 1,
    envelope = new { magic = "OMT1", headerBytes = 8 },
    floatRule = "all floats in this corpus are float32-exact; implementations compare exactly",
    inputRule = "each input lists every field that differs from its default; omitted fields are " +
                "default (0, false, \"\", empty list, all-default struct, first enumerant, first union variant)",
    vectors = new List<object>(),
};

foreach (var vector in ConformanceVectors.All())
{
    var (frameType, description, input) = described[vector.Id];
    var datagram = vector.Encode();
    var hex = Convert.ToHexString(datagram).ToLowerInvariant();
    var wrapped = new StringBuilder();
    for (int i = 0; i < hex.Length; i += 64)
        wrapped.AppendLine(hex.Substring(i, Math.Min(64, hex.Length - i)));
    File.WriteAllText(Path.Combine(vecDir, vector.Id + ".hex"), wrapped.ToString());
    manifest.vectors.Add(new
    {
        id = vector.Id, file = $"vectors/{vector.Id}.hex", frameType, description, input,
    });
    Console.WriteLine($"  {vector.Id}: {datagram.Length} bytes");
}

var options = new System.Text.Json.JsonSerializerOptions
{
    WriteIndented = true,
};
File.WriteAllText(Path.Combine(outDir, "manifest.json"),
    System.Text.Json.JsonSerializer.Serialize(manifest, options) + "\n");
Console.WriteLine($"corpus written to {Path.GetFullPath(outDir)}");
