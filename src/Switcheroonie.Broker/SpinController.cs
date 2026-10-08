using System.Diagnostics;

namespace Switcheroonie.Broker;

public readonly record struct SpinQuaternion(double W = 1, double X = 0, double Y = 0, double Z = 0)
{
    public static SpinQuaternion Identity => new(1, 0, 0, 0);
    public bool Valid => double.IsFinite(W) && double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) &&
        Math.Abs(W * W + X * X + Y * Y + Z * Z - 1) < 1e-6;
    public SpinQuaternion Conjugate => new(W, -X, -Y, -Z);
    public SpinQuaternion Normalized
    {
        get
        {
            double n = Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
            return double.IsFinite(n) && n > 1e-12 ? new(W / n, X / n, Y / n, Z / n) : Identity;
        }
    }
    public static SpinQuaternion operator *(SpinQuaternion a, SpinQuaternion b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);
    public static SpinQuaternion Axis(double x, double y, double z, double angle) =>
        new(Math.Cos(angle / 2), x * Math.Sin(angle / 2), y * Math.Sin(angle / 2), z * Math.Sin(angle / 2));
}

// Separate, opt-in authority from desktop input. Nothing persists except the
// preference: a new process, focus loss or emergency always starts at identity.
public sealed class SpinController
{
    static long generationFloor;
    public const double MaximumSpeed = 5.2, Acceleration = 1.8, CoastBrake = 4.5, OppositeBrake = 8;
    public bool Active { get; private set; }
    public bool Typing { get; private set; }
    public double RollSpeed { get; private set; }
    public double PitchSpeed { get; private set; }
    public double PivotX { get; private set; }
    public double PivotY { get; private set; }
    public double PivotZ { get; private set; }
    // QPC survives a broker restart on the same boot. The process-wide floor
    // also makes separate controllers and same-clock-tick activations distinct.
    public ulong Generation { get; private set; } = NextGeneration();
    public SpinQuaternion Rotation { get; private set; } = SpinQuaternion.Identity;
    SpinQuaternion local = SpinQuaternion.Identity, facing = SpinQuaternion.Identity;
    long window, typingWindow;
    double lastSeconds;
    bool haveTime;

    static ulong NextGeneration()
    {
        while (true)
        {
            long previous = Volatile.Read(ref generationFloor);
            if (previous == long.MaxValue) throw new InvalidOperationException("Spin generation exhausted.");
            long next = Math.Max(previous + 1, Math.Max(1, Stopwatch.GetTimestamp()));
            if (Interlocked.CompareExchange(ref generationFloor, next, previous) == previous) return (ulong)next;
        }
    }

    public void Reset()
    {
        Active = false; RollSpeed = PitchSpeed = 0;
        local = Rotation = facing = SpinQuaternion.Identity;
        haveTime = false; window = 0;
    }
    static double Approach(double current, double target, double step) =>
        current < target ? Math.Min(target, current + step) : Math.Max(target, current - step);
    static double Speed(double current, int direction, double dt)
    {
        double rate = direction == 0 ? CoastBrake : current * direction < 0 ? OppositeBrake : Acceleration;
        return Approach(current, direction * MaximumSpeed, rate * dt);
    }
    public void Step(GameInputSample sample, bool eligible, bool sharedChatState, bool observedTyping, DriverSnapshot head, double seconds)
    {
        if (!eligible || sample.Emergency || !sample.Focused || sample.Window == 0 || !double.IsFinite(seconds))
        { Reset(); return; }
        // Motion resets retain known chat state in its original game window.
        // A new validated game window must not inherit that typing gate.
        if (typingWindow != 0 && typingWindow != sample.Window) Typing = false;
        typingWindow = sample.Window;
        if (window != 0 && window != sample.Window) Reset();
        window = sample.Window;
        if (sharedChatState) Typing = observedTyping;
        else if (sample.ChatCancel) Typing = false;
        else if (sample.ChatToggle) Typing = !Typing;
        if (Typing) { Reset(); return; }
        double dt = haveTime ? Math.Clamp(seconds - lastSeconds, 0, .05) : 0;
        lastSeconds = seconds; haveTime = true;
        if (sample.SpinToggle)
        {
            if (Active) { Reset(); return; }
            if (!head.PhysicalPoseValid) return;
            Generation = NextGeneration(); Active = true;
            PivotX = head.HeadX; PivotY = head.HeadY - .75; PivotZ = head.HeadZ;
            var q = new SpinQuaternion(head.HeadW, head.HeadQx, head.HeadQy, head.HeadQz).Normalized;
            // Horizontal facing basis, independent of the user's initial tilt.
            double yaw = Math.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.Y * q.Y + q.X * q.X));
            facing = SpinQuaternion.Axis(0, 1, 0, yaw);
            local = Rotation = SpinQuaternion.Identity;
            dt = 0;
        }
        if (!Active) return;
        int roll = (sample.SpinLeft ? 1 : 0) - (sample.SpinRight ? 1 : 0);
        int pitch = (sample.SpinForward ? 1 : 0) - (sample.SpinBack ? 1 : 0);
        double oldRoll = RollSpeed, oldPitch = PitchSpeed;
        RollSpeed = Speed(RollSpeed, roll, dt); PitchSpeed = Speed(PitchSpeed, pitch, dt);
        var step = SpinQuaternion.Axis(0, 0, 1, (oldRoll + RollSpeed) * dt / 2) *
            SpinQuaternion.Axis(1, 0, 0, -(oldPitch + PitchSpeed) * dt / 2);
        local = (local * step).Normalized;
        Rotation = (facing * local * facing.Conjugate).Normalized;
    }
}
