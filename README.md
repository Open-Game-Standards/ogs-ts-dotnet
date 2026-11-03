# ogs-ts-dotnet

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/telemetry-standard-dark.svg">
  <img align="right" width="120" src="assets/telemetry-standard.svg" alt="OGS Telemetry Standard logo">
</picture>

.NET reference implementation of the [OGS Telemetry Standard (OGS-TS)](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard)
— a two-layer, UDP-multicast motion telemetry protocol for motion platforms,
haptics, and cueing systems. Loopback-testable on any single machine
(macOS, Linux, Windows); the same code is the starting point for Unity
integrations and Windows-based receivers.

Community & support: [Discord](https://discord.gg/tseF4Q6U99) · Development is funded by [sponsorship](https://github.com/sponsors/BrianGilbert)

Status: **v1, conformance-tested** — the dependency-free sender core
(canonical encoder, `TelemetryPublisher` declare/bind/push wiring,
bind-time unit conversion, body-dynamics deriver), the receiving library
(`Ogs.Telemetry.Receiving`), a reference receiver and a demo sender. The
tests include byte-identical encoding of the standard's conformance corpus,
which `ConformanceGen` here produces. The same core is vendored by the
Unity and Godot SDKs, and CI checks they don't drift. The schema layer is
generated from the standard's schema (`ogs_telemetry.capnp`, v1); wire
compatibility follows the standard's evolution rules (unknown
fields/enums/frame types skipped).

The sending library (`Ogs.Telemetry`) has **no Cap'n Proto runtime
dependency**: frames are written by generated writers over a small
pure-managed encoder (`Wire.cs`) in the standard's canonical OGS encoding
(implementation-capnproto.md, Encoding rules) — no reflection, no unsafe
code, no host-endian memory views, so it runs on Mono, IL2CPP, NativeAOT,
ARM and big-endian hosts alike. Decoding (receivers, tests) lives in
`Ogs.Telemetry.Receiving`, which uses Capnp.Net.Runtime.

**Built with AI assistance.** Significant portions of this code
(including the wire codec, unit conversions, and body-dynamics
derivation) were written with AI coding assistance under
human direction and review. Review and test before relying on it,
especially around networking and the conversion/derivation math.

## Layout

| Path | Contents |
|---|---|
| `src/Ogs.Telemetry` | Sending library, no dependencies: `Envelope` (OMT1 header + message framing), `Wire` (canonical encoder), `TelemetrySender`, `TelemetryPublisher` (declare/bind/push wiring), `Paging` (datagram budget: `DiscoveryPager`, `MotionPartPlanner`), `Unit` + `Conversions` (bind-time unit massaging) |
| `src/Ogs.Telemetry/Schema` | Generated canonical writers (`capnp/gen_writers.py` regenerates) |
| `src/Ogs.Telemetry.Receiving` | Receiving library: `TelemetryReceiver`, `MessageDecoder`, `DescriptorTables` (merges discovery pages per generation), `SessionTracker` (per-sender sessions and selection), generated Capnp.Net readers in `Schema/` (`capnp/build.sh` regenerates) |
| `src/TelemetryDemo` | Demo sender: one car, discovery every 2 s, ~60 Hz motion |
| `src/TelemetryReceive` | Reference receiver with conformance checks |
| `src/ConformanceGen` | Deterministic conformance-corpus generator (writes to the standard repo) |
| `tests/Ogs.Telemetry.Tests` | xunit: canonical encoding, writer → Capnp.Net roundtrip, envelope, UDP multicast loopback |

## Build & test

```bash
dotnet build
dotnet test
```

## Multi-target (Unity/Godot compat)

`Ogs.Telemetry` targets `netstandard2.0;netstandard2.1;net10.0` at `LangVersion 9.0`
(Unity's compiler ceiling), no implicit usings — the library source is the
vendored core for engine packages and must compile identically in every
host. Console projects (`TelemetryDemo`, `TelemetryReceive`) stay net10.0.

## Publishing API (engine-agnostic)

`TelemetryPublisher` is the two-phase wiring layer the engine plugins wrap:
declare the object, wheels, and channels (name + required target unit) —
that becomes the DiscoveryFrame; then bind getters (pull) or push values
per tick — they ride MotionFrame/EventFrame. Bind-time source units
(`Unit.KilometrePerHour`, `Unit.Degree`, `Unit.Psi`, …) convert with
precomputed factors (`Conversions`); mismatches throw at bind, not on the
wire. See `tests/Ogs.Telemetry.Tests/PublisherTests.cs` for the pattern.

Datagram budget ([implementation-udp.md § Datagram budget](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard/blob/main/implementation-udp.md#datagram-budget)):
every datagram the publisher sends is at most 1,400 bytes, whatever the
channel count. A descriptor set too large for one datagram is paged by
`DiscoveryPager` — every page at the first tick and after a change, then
spread across each 2-second re-send interval — and a tick with more
channel values than one datagram holds goes out in parts planned by
`MotionPartPlanner`, the pose in part 0. `Generation` increments whenever
the object's identity changes (`SetObjectTypeLabel`), and every discovery
page and motion part carries it.

Receivers feed every discovery frame to `DescriptorTables.Apply` and
resolve a motion frame's channel ids with `TryGetChannel(frame.Generation,
…)`: pages merge by object name and id, each usable on arrival, and the
previous generation stays resolvable while a new one arrives.

Several senders can share a group ([implementation-udp.md § Senders and
sessions](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard/blob/main/implementation-udp.md#senders-and-sessions)).
`TelemetryReceiver`'s `…ReceivedFrom` events carry each frame's source
address and port, and `SessionTracker` keeps one `DescriptorTables` per
sender, ends silent senders after 10 s, and offers `Selected` for receivers
that drive one output: a pinned `SessionPin` (game name, instance label,
host) when set, otherwise a sender on this host, then the newest session.
Senders name their instance with `SetInstance(label, host)`; declared
channels carry their value type, and `DescriptorTables.Channel.Accepts`
drops values whose variant differs from it.

Pause ([CONVENTIONS C-11](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard/blob/main/CONVENTIONS.md#c-11-pause)): hosts that can tell when their game is paused call
`DeclarePause()` during setup and `SetPaused(bool)` as the state changes,
and keep calling `Tick` on a real-time clock while paused. Motion frames
keep flowing with the frozen pose, `session.paused` set and no dynamics,
so receivers bring hardware to rest instead of reading the silence as a
dead session. The engine plugins do all of this for you.

Gravity ([CONVENTIONS C-12](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard/blob/main/CONVENTIONS.md#c-12-gravity)): discovery declares standard gravity along world −Y
unless told otherwise. `SetGravity(Vector3)` (or the `SetEnvironment`
vector overload) declares the session gravity — a straight-down vector
rides as the scalar alone, any other direction also as `gravityVector`.
`BindLocalGravity(getter)` carries per-object gravity in every motion
frame for gravity that varies by position or faster than discovery.
`EffectiveGravity` resolves what the stream declares for the object; set
`BodyDynamicsDeriver.GravityVector` from it so derived specific force
reacts to the same gravity.

Recordings ([recording.md](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard/blob/main/recording.md)):
`RecordingWriter` captures a receiver's datagrams (`RawDatagramReceived`)
into an `.ogsrec` file with arrival time and source, `RecordingReader`
reads one back, and `RecordingReplayer` replays it at its original pace
from one socket per recorded sender. `TelemetryReceive --record <file>`
records from the command line. `TelemetrySender` and `TelemetryReceiver`
take the IPv6 group (`TelemetrySender.DefaultGroupV6`) as well as IPv4.

Atmosphere: discovery declares none (`noAtmosphere`) until `SetEnvironment`
gives air density, temperature and pressure, which ride together as
`Environment.atmosphere.known`; a game without an atmosphere model leaves
it unset rather than sending zeros that read as vacuum.

## Loopback demo

Two terminals:

```bash
dotnet run --project src/TelemetryReceive
dotnet run --project src/TelemetryDemo
```

The receiver merges discovery pages and prints each generation once it is
complete (object, drive points, channels, session values), motion at a
throttled rate with channels named through the generation's tables, and
conformance warnings (magic/schemaVersion handled by the library;
quaternion norm, primary object and page/part indexes checked here).

Default endpoint: multicast `239.255.17.17:40123`, TTL 1 — nothing leaves
the local subnet. See the standard's `implementation-udp.md` for the
liveness rules receivers should apply.

## Regenerating bindings after a schema change

```bash
cp ../OGS-Telemetry-Standard/ogs_telemetry.capnp capnp/
python capnp/gen_writers.py   # writers (sending)  — needs: pip install pycapnp
capnp/build.sh                # readers (receiving) — needs: capnp + capnpc-csharp
```

`capnp` via your package manager (brew/apt install capnp) and
`capnpc-csharp` via `dotnet tool install -g capnpc-csharp`. The writer
generator fails loudly on schema constructs it does not support yet.
