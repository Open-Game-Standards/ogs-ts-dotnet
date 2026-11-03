using Ogs.Telemetry;
using Ogs.Telemetry.Schema;
using Ogs.Telemetry.Wire;
using Xunit;

/// <summary>Canonical writers → Capnp.Net readers: an independent Cap'n
/// Proto implementation must read back what the writers produce.</summary>
public class CapnpRoundtripTests
{
    static Capnp.DeserializerState Decode(WireStruct frame) =>
        MessageDecoder.DecodeRoot(Envelope.EncodeMessage(CanonicalEncoder.Encode(frame)));

    [Fact]
    public void Discovery_Frame_Serializes_And_Decodes()
    {
        var discovery = new DiscoveryFrame
        {
            SchemaVersion = 1,
            GameName = "test-game",
            Objects = new List<ObjectDescriptor>
            {
                new()
                {
                    Name = "car", Type = ObjectType.Vehicle, Primary = true,
                    Channels = new List<ChannelDescriptor>
                    {
                        new() { Id = 7, Name = "engine.rpm", Unit = "rpm" },
                    },
                },
            },
        };

        var r = CapnpGen.DiscoveryFrame.READER.create(Decode(discovery));

        Assert.Equal(1u, r.SchemaVersion);
        Assert.Equal("test-game", r.GameName);
        var obj = Assert.Single(r.Objects);
        Assert.Equal("car", obj.Name);
        Assert.True(obj.Primary);
        Assert.Equal(CapnpGen.ObjectType.vehicle, obj.Type);
        Assert.Equal("", obj.TypeLabel); // text is always written, never null
        var ch = Assert.Single(obj.Channels);
        Assert.Equal(7u, ch.Id);
        Assert.Equal("engine.rpm", ch.Name);
        Assert.Equal("rpm", ch.Unit);
        Assert.Equal(CapnpGen.ChannelDescriptor.WHICH.NoRange, ch.which);
    }

    [Fact]
    public void Motion_Frame_Quaternion_Survives_Roundtrip()
    {
        var motion = new MotionFrame
        {
            Timestamp = 12.5,
            Objects = new List<MotionObject>
            {
                new()
                {
                    Name = "drone",
                    Position = { X = 1.5f, Y = -2.5f, Z = 3.5f },
                    Orientation =
                    {
                        X = 0.1f, Y = 0.2f, Z = -0.3f,
                        W = MathF.Sqrt(1f - 0.01f - 0.04f - 0.09f),
                    },
                },
            },
        };

        var r = CapnpGen.MotionFrame.READER.create(Decode(motion));

        Assert.Equal(12.5, r.Timestamp, 6);
        var o = Assert.Single(r.Objects);
        Assert.Equal("drone", o.Name);
        Assert.Equal(CapnpGen.MotionObject.WHICH.NoDynamics, o.which);
        Assert.Equal(1.5f, o.Position.X);
        Assert.Equal(3.5f, o.Position.Z);
        Assert.Equal(-0.3f, o.Orientation.Z);
        Assert.InRange(o.Orientation.W, 0.91f, 0.95f);
    }

    [Fact]
    public void Every_Drive_Point_Variant_Roundtrips()
    {
        var obj = new ObjectDescriptor { Name = "rig" };
        var wheel = new DrivePointDescriptor { Id = 0, Name = "w" };
        var spec = wheel.InitWheel();
        spec.Radius = 0.25f; spec.Driven = true;
        spec.InitSuspension().Travel.Max = 0.125;
        var prop = new DrivePointDescriptor { Id = 1, Name = "p" };
        prop.InitPropeller().Blades = 3;
        var leg = new DrivePointDescriptor { Id = 2, Name = "l" };
        leg.SetLeg();
        obj.DrivePoints = new List<DrivePointDescriptor> { wheel, prop, leg };

        var state = new MotionObject { Name = "rig" };
        var dp = new DrivePoint { Id = 0 };
        var ws = dp.InitWheel();
        ws.Rpm = 1200f; ws.Contact = ContactMedium.Gravel;
        ws.InitExtended().TyreWear = 0.5f;
        var brake = ws.BrakePressure; brake.Value = 2.5e6f;   // torque stays noTorque
        var jet = new DrivePoint { Id = 1 };
        jet.InitJet().Thrust = 4000f;
        state.DrivePoints = new List<DrivePoint> { dp, jet };
        state.Texts = new List<TextValue> { new() { Id = 9, Text = "drift" } };

        var d = CapnpGen.DiscoveryFrame.READER.create(Decode(new DiscoveryFrame
            { Objects = new List<ObjectDescriptor> { obj } }));
        var dps = d.Objects[0].DrivePoints;
        Assert.Equal(CapnpGen.DrivePointDescriptor.WHICH.Wheel, dps[0].which);
        Assert.Equal(0.25f, dps[0].Wheel.Radius);
        Assert.True(dps[0].Wheel.Driven);
        Assert.Equal(0.125, dps[0].Wheel.Suspension.Travel.Max);
        Assert.Equal(CapnpGen.DrivePointDescriptor.WHICH.Propeller, dps[1].which);
        Assert.Equal(3, dps[1].Propeller.Blades);
        Assert.Equal(CapnpGen.DrivePointDescriptor.WHICH.Leg, dps[2].which);

        var m = CapnpGen.MotionFrame.READER.create(Decode(new MotionFrame
            { Objects = new List<MotionObject> { state } }));
        var o = m.Objects[0];
        Assert.Equal(1200f, o.DrivePoints[0].Wheel.Rpm);
        Assert.Equal(CapnpGen.ContactMedium.gravel, o.DrivePoints[0].Wheel.Contact);
        Assert.Equal(0.5f, o.DrivePoints[0].Wheel.Extended.TyreWear);
        Assert.Equal(CapnpGen.DrivePoint.WHICH.Jet, o.DrivePoints[1].which);
        Assert.Equal(4000f, o.DrivePoints[1].Jet.Thrust);
        Assert.Equal(CapnpGen.WheelState.brakePressure.WHICH.Value, o.DrivePoints[0].Wheel.BrakePressure.which);
        Assert.Equal(2.5e6f, o.DrivePoints[0].Wheel.BrakePressure.Value);
        Assert.Equal(CapnpGen.WheelState.torque.WHICH.NoTorque, o.DrivePoints[0].Wheel.Torque.which);
        Assert.Equal(9, o.Texts[0].Id);
        Assert.Equal("drift", o.Texts[0].Text);
    }
}
