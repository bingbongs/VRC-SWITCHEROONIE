namespace Switcheroonie.Broker;

public readonly record struct AutomaticDecision(string? Target, string Detail);

// Wear events are independent vendor input, not the proximity=true value we send
// to keep desktop rendering awake. Change-only events do not require a heartbeat.
// A fresh physical head pose and a healthy routing backend remain mandatory.
public sealed class AutomaticRouting
{
    string? candidate;
    long candidateSince, retryAfter;
    public void Reset() { candidate = null; candidateSince = retryAfter = 0; }
    public void Complete(bool accepted, long nowMilliseconds) => retryAfter = nowMilliseconds + (accepted ? 1000 : 5000);
    public AutomaticDecision Evaluate(DriverSnapshot source, long nowMilliseconds, bool enabled, string? manualMode, bool preparing)
    {
        if (!enabled && manualMode is null) { Reset(); return new(null, "Automatic switching is paused."); }
        if (!source.Alive) { candidate = null; return new(null, manualMode is null ? "Service ready · waiting for SteamVR and the selected headset route." : "Manual preference saved · waiting for the headset route."); }
        if (!source.HasHead || !double.IsFinite(source.HeadAge) || source.HeadAge is < 0 or >= 200 || source.Error is not 0 and not 9)
        { candidate = null; return new(null, "Service ready · waiting for a healthy independent headset source."); }
        if (manualMode is not null)
        {
            if (candidate != "Manual:" + manualMode) { candidate = "Manual:" + manualMode; retryAfter = 0; }
            if (preparing) return new(null, "Waiting for the selected manual route to be acknowledged.");
            if (source.Desktop == (manualMode == "Desktop")) return new(null, "Manual " + (manualMode == "Desktop" ? "desktop controls" : "Physical VR") + " selected. Resume Auto to follow the headset again.");
            if (nowMilliseconds < retryAfter) return new(null, "Manual preference retained · waiting before retrying the driver.");
            return new(manualMode, "Applying the saved manual preference to the ready headset route.");
        }
        if (!source.ProximityKnown)
        { candidate = null; return new(null, "Headset route ready · wear sensor not reported; use the mode buttons."); }
        string target = source.ProximityActive ? "Physical" : "Desktop";
        if (candidate != target) { candidate = target; candidateSince = nowMilliseconds; retryAfter = 0; }
        if (preparing) return new(null, "Waiting for the current routing change to finish.");
        if (source.Desktop == (target == "Desktop"))
            return new(null, source.ProximityActive ? "Auto · headset worn, Physical VR active." : "Auto · headset removed, desktop controls active.");
        if (nowMilliseconds < retryAfter) return new(null, "Routing change was not acknowledged; waiting before retrying.");
        long dwell = source.ProximityActive ? 750 : 1000;
        if (nowMilliseconds - candidateSince < dwell)
            return new(null, source.ProximityActive ? "Headset wear detected · checking stability." : "Headset removal detected · checking stability.");
        return new(target, source.ProximityActive ? "Headset worn · returning to Physical VR." : "Headset removed · preparing desktop controls.");
    }
}

public sealed record RoutingPreferences(bool Automatic = true, string? ManualMode = null);
