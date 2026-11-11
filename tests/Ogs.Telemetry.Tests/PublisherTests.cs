using Ogs.Telemetry;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Capnp;
using Xunit;

/// <summary>Unit conversion table (bind-path massaging layer).</summary>
public class ConversionsTests
{
    [Theory]
    [InlineData(72.0, Unit.KilometrePerHour, Unit.MetrePerSecond, 20.0)]
    [InlineData(1.0, Unit.MilePerHour, Unit.MetrePerSecond, 0.44704)]
    [InlineData(1.0, Unit.Knot, Unit.MetrePerSecond, 1852.0 / 3600.0)]
    [InlineData(180.0, Unit.Degree, Unit.Radian, System.Math.PI)]
    [InlineData(1.0, Unit.Psi, Unit.Pascal, 6894.757293168361)]
    [InlineData(1.0, Unit.Bar, Unit.Pascal, 1e5)]
    [InlineData(1.0, Unit.GForce, Unit.MetrePerSecondSquared, 9.80665)]
    [InlineData(1.0, Unit.FootPerSecondSquared, Unit.MetrePerSecondSquared, 0.3048)]
    [InlineData(2.0, Unit.RevPerSecond, Unit.Rpm, 120.0)]
    [InlineData(50.0, Unit.Percent, Unit.Scalar, 0.5)]
    [InlineData(101.325, Unit.Kilopascal, Unit.Pascal, 101325.0)]
    public void Factors_Convert_Source_To_Target(double value, Unit source,
                                                 Unit target, double expected)
    {
        var convert = Conversions.TryGetConverter(source, target);
        Assert.NotNull(convert);
        Assert.Equal(expected, convert!(value), 9);
    }

    [Fact]
    public void Fahrenheit_To_Celsius_Has_Offset()
    {
        var convert = Conversions.TryGetConverter(Unit.Fahrenheit, Unit.Celsius);
        Assert.NotNull(convert);
        Assert.Equal(0.0, convert!(32.0), 6);
        Assert.Equal(100.0, convert(212.0), 6);
    }

    [Fact]
    public void Same_Unit_Is_Identity()
    {
        var convert = Conversions.TryGetConverter(Unit.Rpm, Unit.Rpm);
        Assert.NotNull(convert);
        Assert.Equal(42.0, convert!(42.0));
    }

    [Fact]
    public void Incompatible_Units_Return_Null()
    {
        Assert.Null(Conversions.TryGetConverter(Unit.KilometrePerHour, Unit.Radian));
        Assert.Null(Conversions.TryGetConverter(Unit.Psi, Unit.Kelvin));
    }

    [Fact]
    public void Source_Only_Units_Cannot_Be_Declared()
    {
        Assert.False(Unit.KilometrePerHour.IsTargetUnit());
        Assert.False(Unit.Psi.IsTargetUnit());
        Assert.False(Unit.FootPerSecondSquared.IsTargetUnit());
        Assert.False(Unit.Kilopascal.IsTargetUnit());
        Assert.True(Unit.MetrePerSecond.IsTargetUnit());
        Assert.True(Unit.Rpm.IsTargetUnit());
    }

    [Fact]
    public void Wire_Strings_Match_Conventions()
    {
        Assert.Equal("rpm", Unit.Rpm.ToWireString());
        Assert.Equal("m/s", Unit.MetrePerSecond.ToWireString());
        Assert.Equal("degC", Unit.Celsius.ToWireString());
        Assert.Equal("Pa", Unit.Pascal.ToWireString());
        Assert.Equal("Nm", Unit.NewtonMetre.ToWireString());
        Assert.Equal("", Unit.Scalar.ToWireString());
    }
}

/// <summary>Publisher over the real loopback: declarations land in discovery,
/// bound source units are converted on the wire, push values ride motion.</summary>
public class PublisherLoopbackTests
{
    [Fact]
    public async Task Publisher_Declares_Converts_And_Sends()
    {
        const int port = 41236;
        using var receiver = new TelemetryReceiver(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        var discovery = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var motion = new TaskCompletionSource<CapnpGen.MotionFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var evnt = new TaskCompletionSource<CapnpGen.EventFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        receiver.DiscoveryReceived += d => discovery.TrySetResult(d);
        receiver.MotionReceived += m =>
        {
            if (m.Objects.Count == 1 && m.Objects[0].Channels is { Count: 3 })
                motion.TrySetResult(m);
        };
        receiver.EventReceived += e =>
        {
            if (e.Events.Count > 0 && e.Events[0].Name == "body.impact")
                evnt.TrySetResult(e);
        };
        receiver.Start();

        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "publisher-test", motionHz: 60);

        pub.DeclareObject("test_car", "Publisher loopback car");
        var speed = pub.DeclareChannel("vehicle.speed", Unit.MetrePerSecond,
                                        "Speed over ground", 0, 100);
        var rpm = pub.DeclareChannel("engine.rpm", Unit.Rpm,
                                     "Engine revolutions per minute");
        var running = pub.DeclareBooleanChannel("engine.running");

        // game code natively measures km/h — bind-time conversion
        pub.BindChannel(speed, () => 72.0, Unit.KilometrePerHour); // → 20 m/s
        pub.BindChannelRaw(rpm, () => 3100.0);
        pub.BindBooleanChannel(running, () => true);

        pub.BindPose(() => new Vector3(1f, 2f, 3f),
                     () => new Quaternion(0f, 0.70710678118f, 0f, 0.70710678118f));
        pub.BindBodyDynamics(() => (
            new Vector3(0.5f, 9.80665f, 2.0f),           // specific force m/s²
            new Vector3(0.10f, -0.25f, 0.40f)));          // angular rate rad/s

        double clock = 0;
        pub.Tick(clock);          // discovery + first motion
        pub.FireEvent("body.impact", 0.8f, 0.25f);
        clock += 0.060;
        pub.Tick(clock);          // event frame send

        var disc = await discovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var mot = await motion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CapnpGen.MotionObject.WHICH.BodyDynamics, mot.Objects[0].which);
        Assert.Equal(0.10f, mot.Objects[0].BodyDynamics.AngularVelocity.X, 5);
        Assert.Equal(-0.25f, mot.Objects[0].BodyDynamics.AngularVelocity.Y, 5);
        Assert.Equal(0.40f, mot.Objects[0].BodyDynamics.AngularVelocity.Z, 5);
        Assert.Equal(9.80665f, mot.Objects[0].BodyDynamics.SpecificForce.Y, 4);
        var ev = await evnt.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("publisher-test", disc.GameName);
        var obj = disc.Objects[0];
        Assert.Equal("test_car", obj.Name);
        Assert.True(obj.Primary);
        Assert.Equal(3, obj.Channels.Count);
        Assert.Equal("vehicle.speed", obj.Channels[0].Name);
        Assert.Equal("m/s", obj.Channels[0].Unit);

        var m = mot.Objects[0];
        Assert.Equal("test_car", m.Name);
        Assert.Equal(1f, m.Position.X, 5);
        Assert.Equal(2f, m.Position.Y, 5);
        Assert.Equal(3f, m.Position.Z, 5);
        Assert.Equal(0f, m.Orientation.X, 5);
        Assert.Equal(0.70710677f, m.Orientation.Y, 5);
        Assert.Equal(0f, m.Orientation.Z, 5);
        Assert.Equal(0.70710677f, m.Orientation.W, 5);

        var values = m.Channels.OrderBy(c => c.Id).ToList();
        Assert.Equal(20.0, values[0].Number, 4);   // 72 km/h converted → 20 m/s
        Assert.Equal(3100.0, values[1].Number, 2); // raw bind passes through
        Assert.True(values[2].Boolean);

        Assert.Equal("body.impact", ev.Events[0].Name);
        Assert.Equal(0.8f, ev.Events[0].Intensity, 5);
    }

    [Fact]
    public void Declarations_Lock_After_First_Tick()
    {
        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: 41237, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "lock-test");
        pub.DeclareChannel("engine.rpm", Unit.Rpm);
        // The lock is taken before anything is sent, and this test is not about
        // the network: some CI hosts have no loopback multicast route.
        try { pub.Tick(0.0); }
        catch (SocketException) { }
        Assert.Throws<InvalidOperationException>(
            () => pub.DeclareChannel("late.channel", Unit.Scalar));
    }

    [Fact]
    public async Task Cached_Motion_Frames_Carry_Updated_Values_Across_Ticks()
    {
        // The frame object graph is cached after the first tick; pushed and
        // pulled values must still update on every subsequent frame.
        const int port = 41241;
        using var receiver = new TelemetryReceiver(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        double latestPush = double.NaN, latestPull = double.NaN;
        receiver.MotionReceived += m =>
        {
            frames++;
            var ch = m.Objects[0].Channels;
            latestPush = ch[0].Number;
            latestPull = ch[1].Number;
            if (frames >= 3) done.TrySetResult();
        };
        receiver.Start();

        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "cache-test", motionHz: 60);
        pub.DeclareObject("car");
        var push = pub.DeclareChannel("push.channel", Unit.Scalar);
        var pull = pub.DeclareChannel("pull.channel", Unit.MetrePerSecond);
        double pulled = 1.0;
        pub.BindChannel(pull, () => pulled, Unit.MetrePerSecond);

        pub.SetChannel(push, 10.0);
        pub.Tick(0.0);
        pub.SetChannel(push, 20.0);
        pub.Tick(0.017);
        pub.SetChannel(push, 30.0);
        pulled = 5.0;
        pub.Tick(0.034);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(30.0, latestPush, 3); // third frame carries the latest push
        Assert.Equal(5.0, latestPull, 3);  // and the latest pulled value
    }

    [Fact]
    public void BindChannel_Rejects_Incompatible_Source_Unit()
    {
        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: 41238, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "unit-guard");
        var rpm = pub.DeclareChannel("engine.rpm", Unit.Rpm);
        Assert.Throws<ArgumentException>(
            () => pub.BindChannel(rpm, () => 20.0, Unit.KilometrePerHour));
    }

    [Fact]
    public async Task SetObjectTypeLabel_ReSends_Discovery_With_New_Label()
    {
        // Runtime-tracked senders (injected mods) surface what they are
        // tracking via typeLabel while the object NAME stays stable —
        // changing the label must re-send discovery before the interval.
        const int port = 41242;
        using var receiver = new TelemetryReceiver(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        var first = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DiscoveryReceived += d =>
        {
            if (!first.Task.IsCompleted) first.TrySetResult(d);
            else if (d.Objects[0].TypeLabel == "BP_Sportscar_C") second.TrySetResult(d);
        };
        receiver.Start();

        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "label-test",
                                              motionHz: 60, discoveryIntervalSeconds: 3600);
        pub.DeclareObject("player"); // stable role name, no label yet
        pub.Tick(0.0);                // discovery sent; interval is 1 h

        var d1 = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("player", d1.Objects[0].Name);
        Assert.Equal("", d1.Objects[0].TypeLabel.ToString());

        pub.SetObjectTypeLabel("BP_Sportscar_C");
        pub.Tick(0.017);              // far inside the 1 h interval

        var d2 = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("player", d2.Objects[0].Name); // name unchanged
        Assert.Equal("BP_Sportscar_C", d2.Objects[0].TypeLabel.ToString());
    }

    [Fact]
    public async Task SetEnvironment_Resends_Discovery_With_New_Values()
    {
        // per-level conditions (space: no gravity, vacuum) must re-send
        // discovery before the interval — the schema allows the
        // environment to be re-sent on change
        const int port = 41243;
        using var receiver = new TelemetryReceiver(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        var first = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.DiscoveryReceived += d =>
        {
            if (!first.Task.IsCompleted) first.TrySetResult(d);
            else if (d.Environment.Medium == CapnpGen.ContactMedium.vacuum) second.TrySetResult(d);
        };
        receiver.Start();

        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "env-test",
                                              motionHz: 60, discoveryIntervalSeconds: 3600);
        pub.DeclareObject("player");
        pub.Tick(0.0); // discovery sent with defaults

        var d1 = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BodyDynamicsDeriver.Gravity, d1.Environment.Gravity); // standard gravity
        // no atmosphere until the host declares one (never zeros that read as vacuum)
        Assert.Equal(CapnpGen.Environment.atmosphere.WHICH.NoAtmosphere, d1.Environment.Atmosphere.which);
        Assert.Equal(CapnpGen.ContactMedium.asphalt, d1.Environment.Medium);

        pub.SetEnvironment(gravity: 0f, airDensity: 0f, temperature: -270f,
                           pressure: 0f, medium: Ogs.Telemetry.Schema.ContactMedium.Vacuum);
        pub.Tick(0.017); // far inside the 1 h interval

        var d2 = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0f, d2.Environment.Gravity, 5);
        Assert.Equal(CapnpGen.Environment.atmosphere.WHICH.Known, d2.Environment.Atmosphere.which);
        Assert.Equal(0f, d2.Environment.Atmosphere.Known.AirDensity, 5);
        Assert.Equal(CapnpGen.ContactMedium.vacuum, d2.Environment.Medium);
    }

    [Fact]
    public async Task Pause_Rides_Every_Motion_Frame_Without_Dynamics()
    {
        // CONVENTIONS C-11: session.paused is declared in discovery, carried
        // in every motion frame, and a paused frame sends noDynamics; the
        // stream keeps flowing so receivers never read a pause as staleness
        const int port = 41245;
        using var receiver = new TelemetryReceiver(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        var discovery = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<(bool Paused, CapnpGen.MotionObject.WHICH Dynamics)>();
        int pausedId = -1;
        receiver.DiscoveryReceived += d =>
        {
            var chs = d.Objects[0].Channels;
            for (int i = 0; i < chs.Count; i++)
                if (chs[i].Name == TelemetryPublisher.PausedChannelName)
                    pausedId = chs[i].Id;
            discovery.TrySetResult(d);
        };
        receiver.MotionReceived += m =>
        {
            var o = m.Objects[0];
            var paused = o.Channels.Single(c => c.Id == pausedId).Boolean;
            lock (frames)
            {
                frames.Add((paused, o.which));
                if (frames.Count >= 3) done.TrySetResult();
            }
        };
        receiver.Start();

        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "pause-test", motionHz: 60);
        pub.DeclareObject("car");
        pub.DeclarePause();
        pub.BindBodyDynamics(() => (new Vector3(0, 9.80665f, 0), Vector3.Zero));

        pub.Tick(0.0);             // live
        await discovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pub.SetPaused(true);
        pub.Tick(0.017);           // paused: still sending
        pub.SetPaused(false);
        pub.Tick(0.034);           // resumed

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pausedId >= 0, "session.paused declared in discovery");
        Assert.Equal(new[]
        {
            (false, CapnpGen.MotionObject.WHICH.BodyDynamics),
            (true, CapnpGen.MotionObject.WHICH.NoDynamics),
            (false, CapnpGen.MotionObject.WHICH.BodyDynamics),
        }, frames.Take(3));
    }

    [Fact]
    public async Task Gravity_Rides_Discovery_And_Every_Motion_Frame()
    {
        // tilted session gravity rides discovery as gravityVector + its
        // magnitude; bound local gravity rides every motion frame
        const int port = 41248;
        using var receiver = new TelemetryReceiver(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        var discovery = new TaskCompletionSource<CapnpGen.DiscoveryFrame.READER>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locals = new List<Vector3?>();
        receiver.DiscoveryReceived += d => discovery.TrySetResult(d);
        receiver.MotionReceived += m =>
        {
            var lg = m.Objects[0].LocalGravity;
            lock (locals)
            {
                locals.Add(lg.which == CapnpGen.MotionObject.localGravity.WHICH.Vector
                    ? new Vector3(lg.Vector.X, lg.Vector.Y, lg.Vector.Z) : null);
                if (locals.Count >= 3) done.TrySetResult();
            }
        };
        receiver.Start();

        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: port, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "gravity-wire", motionHz: 60);
        pub.DeclareObject("ship");
        pub.SetGravity(new Vector3(3, -4, 0));
        var local = new Vector3(-2, 0, 0);
        pub.BindLocalGravity(() => local);

        pub.Tick(0.0);
        var d1 = await discovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CapnpGen.Environment.WHICH.GravityVector, d1.Environment.which);
        Assert.Equal(5f, d1.Environment.Gravity);
        Assert.Equal(new Vector3(3, -4, 0), Gravity.Session(d1.Environment));

        local = Vector3.Zero;            // weightless: still a selected vector
        pub.Tick(0.017);
        pub.BindLocalGravity(null);      // back to the session gravity
        pub.Tick(0.034);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new Vector3?[] { new Vector3(-2, 0, 0), Vector3.Zero, null }, locals.Take(3));
    }

    [Fact]
    public void Effective_Gravity_Resolves_Local_Then_Environment()
    {
        // CONVENTIONS C-12 resolution order, as the deriver is fed it
        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: 41247, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "gravity");
        Assert.Equal(new Vector3(0, -BodyDynamicsDeriver.Gravity, 0), pub.EffectiveGravity);

        pub.SetGravity(new Vector3(0, -1.62f, 0));            // straight down: scalar form
        Assert.Equal(new Vector3(0, -1.62f, 0), pub.EnvironmentGravity);
        pub.SetGravity(new Vector3(3, -4, 0));                 // tilted: vector form
        Assert.Equal(new Vector3(3, -4, 0), pub.EffectiveGravity);

        var local = new Vector3(-2, 0, 0);
        pub.BindLocalGravity(() => local);
        Assert.Equal(local, pub.EffectiveGravity);
        pub.BindLocalGravity(null);
        Assert.Equal(new Vector3(3, -4, 0), pub.EffectiveGravity);
    }

    [Fact]
    public void SetPaused_After_First_Tick_Requires_DeclarePause()
    {
        using var sender = new TelemetrySender(group: TelemetryReceiver.LoopbackGroup, port: 41246, localInterface: IPAddress.Loopback);
        using var pub = new TelemetryPublisher(sender, "pause-guard");
        pub.Tick(0.0);
        Assert.Throws<InvalidOperationException>(() => pub.SetPaused(true));

        using var declared = new TelemetryPublisher(sender, "pause-ok");
        declared.DeclarePause();
        declared.DeclarePause(); // idempotent
        declared.Tick(0.0);
        declared.SetPaused(true);
        Assert.True(declared.Paused);
    }
}
