using Switcheroonie.Broker;

static class AutomaticRoutingTests
{
    public static void Run(Action<bool, string> check)
    {
        var policy = new AutomaticRouting();
        var ready = new DriverSnapshot(Alive: true, HasHead: true, HeadAge: 5, ProximityKnown: true, ProximityActive: false);
        check(policy.Evaluate(new(), 1000, true, null, false).Target is null, "Auto keeps service ready with no runtime and never invents a route");
        check(policy.Evaluate(ready, 1000, true, null, false).Target is null, "Headset removal starts a dwell rather than switching immediately");
        check(policy.Evaluate(ready, 1999, true, null, false).Target is null, "Auto does not switch before the full removal dwell");
        check(policy.Evaluate(ready, 2000, true, null, false).Target == "Desktop", "Stable independent removal requests desktop controls");
        policy.Complete(false, 2000);
        check(policy.Evaluate(ready, 6999, true, null, false).Target is null, "Rejected automatic change is bounded by retry backoff");
        check(policy.Evaluate(ready, 7000, true, null, false).Target == "Desktop", "Automatic change can recover after a failed acknowledgement");
        var worn = ready with { Desktop = true, ProximityActive = true };
        check(policy.Evaluate(worn, 7010, true, null, false).Target is null, "Wear event begins a separate dwell");
        check(policy.Evaluate(worn, 7759, true, null, false).Target is null, "Auto does not return to VR on a brief proximity spike");
        check(policy.Evaluate(worn, 7760, true, null, false).Target == "Physical", "Stable wear requests physical VR");
        check(policy.Evaluate(worn, 7800, true, "Desktop", false).Target is null, "Explicit desktop choice overrides automatic wear routing");
        check(policy.Evaluate(ready, 9000, true, "Physical", false).Target is null, "Explicit physical choice overrides automatic removal routing");
        check(policy.Evaluate(worn, 10000, false, null, false).Target is null, "Paused auto does not change mode");
        check(policy.Evaluate(ready with { ProximityKnown = false }, 11000, true, null, false).Target is null, "An unknown sensor never substitutes synthetic proximity for user intent");
        check(policy.Evaluate(ready, 12000, true, null, false).Target is null, "Sensor recovery starts a fresh dwell");
        check(policy.Evaluate(ready with { HeadAge = 200 }, 14000, true, null, false).Target is null, "Exactly stale physical tracking prevents an automatic change");
        check(policy.Evaluate(ready, 15000, true, null, false).Target is null, "Physical-source recovery cannot inherit the old dwell");
        check(policy.Evaluate(ready with { Error = 10 }, 17000, true, null, false).Target is null, "A startup configuration fault prevents automatic routing");
        check(policy.Evaluate(ready with { HasHead = false }, 18000, true, null, false).Target is null, "An advertised sensor without independently captured HMD poses is insufficient");
        check(policy.Evaluate(ready with { HeadAge = double.NaN }, 19000, true, null, false).Target is null, "Nonfinite tracking age is rejected by auto policy");
        check(policy.Evaluate(ready with { HeadAge = -1 }, 20000, true, null, false).Target is null, "Future-invalid tracking age is rejected by auto policy");
        check(policy.Evaluate(ready, 21000, true, null, false).Target is null, "Healthy source starts a fresh dwell after faults");
        check(policy.Evaluate(ready, 23000, true, null, true).Target is null, "A mode transaction in progress is never overlapped");
        check(policy.Evaluate(ready, 23000, true, null, false).Target == "Desktop", "Auto resumes after a serialized transaction completes");
        check(policy.Evaluate(ready with { Desktop = true }, 24000, true, null, false).Target is null, "Already selected route does not cause repeated mode changes");
        policy.Reset();
        check(policy.Evaluate(ready with { Error = 9, ProximityAge = 60000 }, 25000, true, null, false).Target is null, "Change-only wear source with an old event begins normal dwell");
        check(policy.Evaluate(ready with { Error = 9, ProximityAge = 61000 }, 26000, true, null, false).Target == "Desktop", "Change-only proximity is not mistaken for a missing input heartbeat");
        check(policy.Evaluate(worn, 26100, true, null, false).Target is null && policy.Evaluate(ready, 26500, true, null, false).Target is null,
            "Wear/remove flapping resets the candidate dwell");
        check(policy.Evaluate(ready, 27499, true, null, false).Target is null, "Flapping does not reuse elapsed time from an earlier removal");
        check(policy.Evaluate(ready, 27500, true, null, false).Target == "Desktop", "A stable final removal can complete after sensor flapping");
        policy.Reset();
        check(policy.Evaluate(new(), 28000, false, "Desktop", false).Target is null, "A saved manual preference never commits an absent runtime");
        check(policy.Evaluate(ready with { ProximityKnown = false }, 29000, false, "Desktop", false).Target == "Desktop", "A queued manual preference applies on healthy tracking without needing a wear sensor or Auto");
        policy.Complete(false, 29000);
        check(policy.Evaluate(ready, 33999, false, "Desktop", false).Target is null, "Queued manual preference retries are bounded after a driver rejection");
        check(policy.Evaluate(ready, 34000, false, "Desktop", false).Target == "Desktop", "Queued manual preference can recover without another panel click");
        check(policy.Evaluate(ready with { Desktop = true }, 35000, false, "Desktop", false).Target is null, "An applied manual preference stays put when Auto is paused");
    }
}
