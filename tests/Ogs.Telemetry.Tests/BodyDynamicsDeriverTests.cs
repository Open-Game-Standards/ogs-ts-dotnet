using System.Numerics;
using Ogs.Telemetry;
using Xunit;

namespace Ogs.Telemetry.Tests;

/// <summary>
/// Pins the pose→body-dynamics derivation used by senders without native
/// accelerometer-class data (BepInEx/UE4SS plugins, and the C++ twin in
/// ogs-ts-unreal). Two properties matter:
///   1. specific force is accelerometer-style (convention A): at rest it
///      reads +9.80665 on the axis that points away from gravity.
///   2. angular velocity uses the C-4 component sense — for pure-axis
///      attitude sweeps it EQUALS the rate the receiver's EulerC4
///      extraction would differentiate to (yaw +nose-right, pitch
///      +nose-up, roll +right-side-down), so GameConn's native-rate path
///      and its Euler fallback agree.
/// </summary>
public class BodyDynamicsDeriverTests
{
    const float G = BodyDynamicsDeriver.Gravity;
    const double Dt = 1.0 / 60.0;

    static void AssertNear(double expected, double actual, double tol,
                           string what)
        => Assert.True(System.Math.Abs(actual - expected) <= tol,
            $"{what}: expected {expected}, got {actual} (tol {tol})");

    [Fact]
    public void First_sample_reads_static_gravity_reaction_level()
    {
        var d = new BodyDynamicsDeriver();
        d.Sample(0.0, new Vector3(5f, 0f, 5f), Quaternion.Identity);
        // at rest, level: +1 g on the up axis (what an IMU reads)
        AssertNear(0, d.SpecificForce.X, 1e-4, "sf.x");
        AssertNear(G, d.SpecificForce.Y, 1e-4, "sf.y");
        AssertNear(0, d.SpecificForce.Z, 1e-4, "sf.z");
        AssertNear(0, d.AngularVelocity.X, 1e-6, "w.x");
        AssertNear(0, d.AngularVelocity.Y, 1e-6, "w.y");
        AssertNear(0, d.AngularVelocity.Z, 1e-6, "w.z");
    }

    [Fact]
    public void Nose_up_rest_reads_gravity_on_surge()
    {
        // pitched straight up (EulerC4 pitch +90 = nose up): constructed as
        // CreateFromYawPitchRoll(0, -90°, 0) — the stored-math angle is
        // negative because C-4 pitch is anti-stored-math (see class doc)
        var d = new BodyDynamicsDeriver();
        var q = Quaternion.CreateFromYawPitchRoll(0f, -FloatPi / 2f, 0f);
        d.Sample(0.0, Vector3.Zero, q);
        // no body axis points world-up except forward: accelerometer-style
        // specific force reads +1 g on +surge
        AssertNear(G, d.SpecificForce.Z, 1e-3, "sf.z (surge)");
        AssertNear(0, d.SpecificForce.X, 1e-3, "sf.x");
        AssertNear(0, d.SpecificForce.Y, 1e-3, "sf.y");
    }

    [Fact]
    public void Level_coast_keeps_gravity_reaction_and_zero_rates()
    {
        var d = new BodyDynamicsDeriver();
        double t = 0;
        Vector3 v = new(0f, 0f, 10f); // 10 m/s forward, level
        for (int i = 0; i < 120; i++) // 2 s
        {
            d.Sample(t, v * (float)t, Quaternion.Identity, hasVelocity: true, engineVelocity: v);
            t += Dt;
        }
        AssertNear(G, d.SpecificForce.Y, 0.05, "sf.y");
        AssertNear(0, d.SpecificForce.Z, 0.05, "sf.z");
        AssertNear(0, d.AngularVelocity.Y, 1e-6, "w.y");
    }

    [Fact]
    public void Forward_acceleration_reads_positive_surge()
    {
        // accelerating at 2 m/s² from rest, level: f = a − g ⇒
        // (0, +9.80665, +2) once the low-pass has converged
        var d = new BodyDynamicsDeriver();
        double t = 0, v = 0;
        for (int i = 0; i < 90; i++) // 1.5 s ≈ 15 filter time constants
        {
            v += 2.0 * Dt;
            d.Sample(t, new Vector3(0, 0, (float)(0.5 * 2.0 * t * t)),
                     Quaternion.Identity, hasVelocity: true,
                     engineVelocity: new Vector3(0, 0, (float)v));
            t += Dt;
        }
        AssertNear(2.0, d.SpecificForce.Z, 0.1, "sf.z (surge)");
        AssertNear(G, d.SpecificForce.Y, 0.1, "sf.y");
    }

    [Fact]
    public void Yaw_sweep_gives_positive_yaw_rate()
    {
        // the DemoSource yaw construction: yaw(Ωt) — nose-right for +Ω
        const double omega = 1.5; // rad/s
        var d = new BodyDynamicsDeriver();
        double t = 0;
        for (int i = 0; i < 60; i++)
        {
            d.Sample(t, Vector3.Zero,
                     Quaternion.CreateFromYawPitchRoll((float)(omega * t), 0, 0));
            t += Dt;
        }
        AssertNear(omega, d.AngularVelocity.Y, 1e-3, "w.y");
        AssertNear(0, d.AngularVelocity.X, 1e-3, "w.x");
        AssertNear(0, d.AngularVelocity.Z, 1e-3, "w.z");
    }

    [Fact]
    public void Pitch_sweep_rate_equals_euler_pitch_rate()
    {
        // stored-math pitch sweep +w tilts the nose DOWN (EulerC4 pitch
        // = −θ), so the C-4-consistent wire rate is NEGATIVE — this is the
        // case that pins the (−x, +y, −z) component mapping
        const double w = 0.8; // rad/s of the sweep parameter
        var d = new BodyDynamicsDeriver();
        double t = 0;
        for (int i = 0; i < 60; i++)
        {
            d.Sample(t, Vector3.Zero,
                     Quaternion.CreateFromYawPitchRoll(0, (float)(w * t), 0));
            t += Dt;
        }
        AssertNear(-w, d.AngularVelocity.X, 1e-3, "w.x (C-4 pitch rate)");
        AssertNear(0, d.AngularVelocity.Y, 1e-3, "w.y");
        AssertNear(0, d.AngularVelocity.Z, 1e-3, "w.z");
    }

    [Fact]
    public void Roll_sweep_rate_equals_euler_roll_rate()
    {
        // stored-math roll sweep +w rolls LEFT-side-down (EulerC4 roll
        // = −θ), so the C-4-consistent wire rate is NEGATIVE
        const double w = 1.1;
        var d = new BodyDynamicsDeriver();
        double t = 0;
        for (int i = 0; i < 60; i++)
        {
            d.Sample(t, Vector3.Zero,
                     Quaternion.CreateFromYawPitchRoll(0, 0, (float)(w * t)));
            t += Dt;
        }
        AssertNear(-w, d.AngularVelocity.Z, 1e-3, "w.z (C-4 roll rate)");
        AssertNear(0, d.AngularVelocity.X, 1e-3, "w.x");
    }

    [Fact]
    public void Composite_rotation_rate_is_body_frame()
    {
        // body pitched 30° up, spinning about its OWN body x at stored rate
        // S: the wire rate must be body-frame (C-4 flip of the stored log).
        // Catches body/world delta-order mistakes, which pure-axis sweeps
        // cannot (their world and body deltas coincide).
        const double S = 2.0; // rad/s about body x (stored sense)
        var attitude = Quaternion.CreateFromYawPitchRoll(0f, -FloatPi / 6f, 0f);
        // integrate one step analytically: q1 = q0 ⊗ exp(½·S·dt·x̂)
        double half = 0.5 * S * Dt;
        var step = new Quaternion((float)System.Math.Sin(half), 0, 0,
                                  (float)System.Math.Cos(half));
        var d = new BodyDynamicsDeriver();
        d.Sample(0.0, Vector3.Zero, attitude);
        d.Sample(Dt, Vector3.Zero, Quaternion.Concatenate(attitude, step));
        // stored body-x rate = +S ⇒ C-4 wire rate = −S on x
        AssertNear(-S, d.AngularVelocity.X, 1e-2, "w.x (body-frame pitch rate)");
        AssertNear(0, d.AngularVelocity.Y, 1e-2, "w.y");
        AssertNear(0, d.AngularVelocity.Z, 1e-2, "w.z");
    }

    [Fact]
    public void World_yaw_of_banked_body_is_body_frame()
    {
        // body rolled 90° (right side down, C-4) then world-yawed nose-right
        // at Ω: physically the spin axis is world +y, which in the rolled
        // body frame is −x_body... via C-4 mapping the wire shows +yaw
        // NEGATED? No — compute: C-4 roll right-down = stored −z rotation.
        // rolled body: body x (right) now points world DOWN, body y (up)
        // points world RIGHT. World yaw Ω (stored +y): stored body-frame
        // components = rotate world (0,Ω,0) by q⁻¹ → (0, −Ω, 0)?? — assert
        // via the formula the deriver itself must equal: flip(stored).
        const double omega = 1.0;
        // attitude: stored roll −90° (right side down per C-4)
        var rolled = Quaternion.CreateFromYawPitchRoll(0, 0, -FloatPi / 2f);
        // one-step world-frame yaw: q1 = exp(½Ωdt ŷ_world) ⊗ q0
        double half = 0.5 * omega * Dt;
        var step = new Quaternion(0, (float)System.Math.Sin(half), 0,
                                  (float)System.Math.Cos(half));
        var d = new BodyDynamicsDeriver();
        d.Sample(0.0, Vector3.Zero, rolled);
        // world-frame pre-multiply: q1 = step ⊗ q0 — Concatenate(a, b)
        // returns b ⊗ a, hence the (rolled, step) operand order here
        d.Sample(Dt, Vector3.Zero, Quaternion.Concatenate(rolled, step));
        // expected: stored log of q0⁻¹q1 ≈ body-frame of world-ŷ spin
        // expressed in the rolled body axes, then the C-4 flip
        var qinv = Quaternion.Conjugate(rolled);
        var worldOmega = new Vector3(0, (float)omega, 0);
        var storedBody = Vector3.Transform(worldOmega, qinv);
        AssertNear(-storedBody.X, d.AngularVelocity.X, 1e-2, "w.x");
        AssertNear(storedBody.Y, d.AngularVelocity.Y, 1e-2, "w.y");
        AssertNear(-storedBody.Z, d.AngularVelocity.Z, 1e-2, "w.z");
    }

    [Fact]
    public void Teleport_resets_to_static_values()
    {
        var d = new BodyDynamicsDeriver();
        double t = 0;
        for (int i = 0; i < 30; i++)
        {
            d.Sample(t, new Vector3(0, 0, (float)t), Quaternion.Identity,
                     hasVelocity: true, engineVelocity: new Vector3(0, 0, 1f));
            t += Dt;
        }
        // teleport 10 m in one tick; engine velocity supplied, so the move
        // it cannot explain is a teleport immediately (single-strike path)
        d.Sample(t, new Vector3(10f, 0f, 10f), Quaternion.Identity,
                 hasVelocity: true, engineVelocity: new Vector3(0, 0, 1f));
        AssertNear(G, d.SpecificForce.Y, 1e-4, "sf.y after teleport");
        AssertNear(0, d.SpecificForce.Z, 1e-4, "sf.z after teleport");
        AssertNear(0, d.AngularVelocity.Y, 1e-6, "w.y after teleport");
    }

    [Fact]
    public void Fast_mover_with_engine_velocity_accelerates_without_reset()
    {
        // flight/space sims: 300 m/s cruise + 15 m/s² sustained thrust. The
        // old fixed 2.5 m/sample cap (=125 m/s) reset the estimator every
        // tick, freezing specific force at the static gravity reaction
        var d = new BodyDynamicsDeriver();
        double t = 0, v = 300.0;
        for (int i = 0; i < 120; i++) // 2 s
        {
            v += 15.0 * Dt;
            // physical kinematics: x = v0·t + ½at²
            d.Sample(t, new Vector3(0, 0, (float)(300.0 * t + 0.5 * 15.0 * t * t)),
                     Quaternion.Identity,
                     hasVelocity: true, engineVelocity: new Vector3(0, 0, (float)v));
            t += Dt;
        }
        AssertNear(15.0, d.SpecificForce.Z, 0.5, "sf.z (surge under thrust)");
        AssertNear(G, d.SpecificForce.Y, 0.5, "sf.y");
    }

    [Fact]
    public void Fast_mover_without_engine_velocity_is_not_a_teleport()
    {
        // no velocity measurement: 250 m/s (4.2 m/sample) + gentle thrust.
        // One inconsistent sample is acceleration onset, not a teleport —
        // the deriver must converge on the real surge, not reset-loop
        var d = new BodyDynamicsDeriver();
        double t = 0, v = 250.0;
        for (int i = 0; i < 150; i++) // 2.5 s: filter converges after the
        {                            // first-sample strike recovers
            v += 5.0 * Dt;
            // physical kinematics: x = v0·t + ½at² (v·t would double the accel)
            d.Sample(t, new Vector3(0, 0, (float)(250.0 * t + 0.5 * 5.0 * t * t)),
                     Quaternion.Identity);
            t += Dt;
        }
        AssertNear(5.0, d.SpecificForce.Z, 0.5, "sf.z (surge, no velocity feed)");
        AssertNear(G, d.SpecificForce.Y, 0.5, "sf.y");
    }

    [Fact]
    public void Teleport_without_velocity_resets_on_sustained_inconsistency()
    {
        // without a velocity measurement a single weird sample could be
        // acceleration onset; a teleport is a one-time jump followed by
        // normal motion, which is inconsistent with the garbage velocity
        // the jump derived — strike two, reset (one sample late)
        var d = new BodyDynamicsDeriver();
        double t = 0;
        for (int i = 0; i < 30; i++) { d.Sample(t, new Vector3(0, 0, (float)t), Quaternion.Identity); t += Dt; }
        d.Sample(t, new Vector3(0, 0, (float)t + 50f), Quaternion.Identity); t += Dt; // strike 1: jump
        AssertNear(0, d.SpecificForce.Z, 1.0, "sf.z strike1: one jump is filtered out");
        d.Sample(t, new Vector3(0, 0, (float)t), Quaternion.Identity); // back on the normal path
        AssertNear(G, d.SpecificForce.Y, 1e-4, "sf.y after teleport");
        AssertNear(0, d.SpecificForce.Z, 1e-4, "sf.z after teleport");
        AssertNear(0, d.AngularVelocity.Y, 1e-6, "w.y after teleport");
    }

    static System.Collections.Generic.List<float> SurgeFromSteppedPoses(
        double fps, double physicsHz, double speed, double acceleration)
    {
        var d = new BodyDynamicsDeriver();
        var surge = new System.Collections.Generic.List<float>();
        for (int i = 0; i < (int)(fps * 6); i++)
        {
            double t = (i + 0.1 * System.Math.Sin(i * 1.7)) / fps;
            double s = System.Math.Floor(t * physicsHz) / physicsHz;
            d.Sample(t, new Vector3(0, 0, (float)(speed * s + 0.5 * acceleration * s * s)), Quaternion.Identity);
            if (t > 1.5) surge.Add(d.SpecificForce.Z);
        }
        return surge;
    }

    static float NearlyWorst(System.Collections.Generic.List<float> surge, double truth)
    {
        var errors = surge.ConvertAll(x => (float)System.Math.Abs(x - truth));
        errors.Sort();
        return errors[(int)(0.99 * errors.Count)];
    }

    [Theory]
    [InlineData(60, 50)]
    [InlineData(60, 61)]
    [InlineData(90, 50)]
    [InlineData(144, 50)]
    [InlineData(144, 60)]
    [InlineData(165, 60)]
    [InlineData(60, 100)]
    [InlineData(60, 120)]
    [InlineData(45, 60)]
    public void Uneven_physics_steps_without_velocity_do_not_spike(double fps, double physicsHz)
    {
        float worst = NearlyWorst(SurgeFromSteppedPoses(fps, physicsHz, 30, 0), 0);
        Assert.True(worst < 100, $"false surge at {fps} fps / {physicsHz} Hz: {worst:F0}");
    }

    static (float low, int framesToHalfPeak) SuddenHit()
    {
        var d = new BodyDynamicsDeriver();
        var surge = new float[180];
        double x = 0, v = 40;
        for (int i = 0; i < surge.Length; i++)
        {
            if (i >= 90 && i < 99) v -= 100 * Dt;
            if (i > 0) x += v * Dt;
            d.Sample(i * Dt, new Vector3(0, 0, (float)x), Quaternion.Identity);
            surge[i] = d.SpecificForce.Z;
        }
        float low = 0;
        foreach (var s in surge) low = System.Math.Min(low, s);
        int at = 90;
        while (at < surge.Length && surge[at] > low / 2) at++;
        return (low, at - 90);
    }

    [Fact]
    public void A_sudden_hit_survives_the_position_estimate()
    {
        var (low, framesToHalfPeak) = SuddenHit();
        Assert.True(low <= -75, $"peak {low:F0} of -100");
        Assert.True(framesToHalfPeak <= 4, $"half peak after {framesToHalfPeak} frames");
    }

    [Fact]
    public void A_single_position_jump_without_velocity_causes_no_spike()
    {
        var d = new BodyDynamicsDeriver();
        float peak = 0;
        double offset = 0;
        for (int i = 0; i < 120; i++)
        {
            if (i == 60) offset = 50;
            double t = i * Dt;
            d.Sample(t, new Vector3(0, 0, (float)(20 * t + offset)), Quaternion.Identity);
            if (i > 20) peak = System.Math.Max(peak, System.Math.Abs(d.SpecificForce.Z));
        }
        Assert.True(peak < 1, $"peak {peak:F1}");
    }

    [Fact]
    public void Coming_to_rest_without_velocity_settles_to_zero()
    {
        var d = new BodyDynamicsDeriver();
        for (int i = 0; i < 180; i++)
        {
            double t = i * Dt;
            d.Sample(t, new Vector3(0, 0, (float)(2 * System.Math.Min(t, 1.0))), Quaternion.Identity);
        }
        AssertNear(0, d.SpecificForce.Z, 0.05, "sf.z at rest");
    }

    [Theory]
    [InlineData(60, 1000, 0)]
    [InlineData(60, 50, 0)]
    [InlineData(70, 60, 0)]
    [InlineData(60, 50, 0.0004)]
    [InlineData(70, 60, 0.0004)]
    [InlineData(144, 50, 0.0004)]
    [InlineData(45, 60, 0.0004)]
    public void Rest_to_rest_without_velocity_reports_no_net_speed_change(double fps, double physicsHz, double jitter)
    {
        const double speed = 7, startAt = 1.5, stopAt = 4.5;
        var d = new BodyDynamicsDeriver();
        double net = 0, previous = 0;
        for (int i = 0; i < (int)(fps * 6); i++)
        {
            double t = i / fps + jitter * System.Math.Sin(i * 1.7);
            double stepped = System.Math.Floor(t * physicsHz) / physicsHz;
            double travelled = speed * System.Math.Clamp(stepped - startAt, 0, stopAt - startAt);
            d.Sample(t, new Vector3(0, 0, (float)travelled), Quaternion.Identity);
            if (i > 0) net += d.SpecificForce.Z * (t - previous);
            previous = t;
        }
        AssertNear(0, net, 0.05 * speed, $"net speed change at {fps} fps / {physicsHz} Hz, jitter {jitter}");
    }

    [Fact]
    public void StoredToC4Rates_maps_stored_components()
    {
        var c4 = BodyDynamicsDeriver.StoredToC4Rates(new Vector3(1f, 2f, 3f));
        Assert.Equal(-1f, c4.X);
        Assert.Equal(2f, c4.Y);
        Assert.Equal(-3f, c4.Z);
    }

    [Fact]
    public void Engine_velocity_beats_position_deltas()
    {
        // caller-measured velocity is trusted even when the position feed
        // is coarse (interpolated camera roots): position says "not moving"
        // but engine velocity accelerates — surge must track the engine
        var d = new BodyDynamicsDeriver();
        double t = 0, v = 0;
        for (int i = 0; i < 90; i++)
        {
            v += 2.0 * Dt;
            d.Sample(t, Vector3.Zero /* stale position */,
                     Quaternion.Identity, hasVelocity: true,
                     engineVelocity: new Vector3(0, 0, (float)v));
            t += Dt;
        }
        AssertNear(2.0, d.SpecificForce.Z, 0.1, "sf.z from engine velocity");
    }

    [Fact]
    public void Angular_velocity_from_delta_handles_zero_and_small()
    {
        Assert.Equal(default, BodyDynamicsDeriver.AngularVelocityFromDelta(
            Quaternion.Identity, Quaternion.Identity, Dt));
        // tiny rotation: near-linear, no NaN
        var tiny = Quaternion.CreateFromYawPitchRoll(1e-6f, 0, 0);
        var w = BodyDynamicsDeriver.AngularVelocityFromDelta(
            Quaternion.Identity, tiny, Dt);
        Assert.False(float.IsNaN(w.X) || float.IsNaN(w.Y) || float.IsNaN(w.Z));
        AssertNear(1e-6 / Dt, w.Y, 1e-6 / Dt * 0.01, "w.y tiny");
    }

    [Fact]
    public void Gravity_vector_sets_the_static_reaction()
    {
        // CONVENTIONS C-12: specific force reacts to the declared gravity —
        // low gravity reads its own magnitude, a sideways pull reads on sway
        var low = new BodyDynamicsDeriver { GravityVector = new Vector3(0f, -1.62f, 0f) };
        low.Sample(0.0, Vector3.Zero, Quaternion.Identity);
        AssertNear(1.62, low.SpecificForce.Y, 1e-5, "sf.y (lunar)");

        var sideways = new BodyDynamicsDeriver { GravityVector = new Vector3(-3f, 0f, 0f) };
        sideways.Sample(0.0, Vector3.Zero, Quaternion.Identity);
        AssertNear(3, sideways.SpecificForce.X, 1e-5, "sf.x (pull toward −x)");
        AssertNear(0, sideways.SpecificForce.Y, 1e-5, "sf.y");

        var weightless = new BodyDynamicsDeriver { GravityVector = Vector3.Zero };
        weightless.Sample(0.0, Vector3.Zero, Quaternion.Identity);
        Assert.Equal(Vector3.Zero, weightless.SpecificForce);
    }

    [Fact]
    public void Gravity_vector_applies_while_accelerating()
    {
        // f = a − g with a = (0, 0, 2) under lunar gravity
        var d = new BodyDynamicsDeriver { GravityVector = new Vector3(0f, -1.62f, 0f) };
        double t = 0, v = 0;
        for (int i = 0; i < 90; i++)
        {
            v += 2.0 * Dt;
            d.Sample(t, new Vector3(0, 0, (float)(0.5 * 2.0 * t * t)),
                     Quaternion.Identity, hasVelocity: true,
                     engineVelocity: new Vector3(0, 0, (float)v));
            t += Dt;
        }
        AssertNear(2.0, d.SpecificForce.Z, 0.1, "sf.z (surge)");
        AssertNear(1.62, d.SpecificForce.Y, 0.1, "sf.y");
    }

    [Fact]
    public void Gravity_change_applies_without_reset()
    {
        // stepping onto a different body mid-stream: the next sample reacts
        // to the new gravity, the estimator keeps its history
        var d = new BodyDynamicsDeriver();
        d.Sample(0.0, Vector3.Zero, Quaternion.Identity);
        d.Sample(Dt, Vector3.Zero, Quaternion.Identity);
        d.GravityVector = new Vector3(0f, -3.71f, 0f);
        d.Sample(2 * Dt, Vector3.Zero, Quaternion.Identity);
        AssertNear(3.71, d.SpecificForce.Y, 1e-4, "sf.y (Mars)");
    }

    static float FloatPi => (float)System.Math.PI;
}
