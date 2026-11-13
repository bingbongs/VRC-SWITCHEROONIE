using Switcheroonie.Broker;

// Injected time/liveness only: no process, private helper, hook, cursor, runtime,
// or network operation is performed by these fixtures.
internal static class HelperRestartPolicyTests
{
    public static void Run(Action<bool, string> check)
    {
        const long frequency = 1000;
        var policy = new HelperRestartPolicy(frequency);
        long now = 0;
        check(policy.TryBeginStart(now, false, false), "Private helper gets an immediate first launch opportunity");
        int[] delays = [2, 4, 8, 16, 32, 60, 60, 60];
        for (int i = 0; i < delays.Length; ++i)
        {
            long deadline = now + delays[i] * frequency;
            check(policy.ScheduledDelaySeconds == delays[i] && policy.NextAttemptTimestamp == deadline,
                $"Helper crash-loop retry {i + 1} is bounded at {delays[i]} seconds");
            check(!policy.TryBeginStart(deadline - 1, false, false) && policy.TryBeginStart(deadline, false, false),
                $"Helper retry {i + 1} cannot start early and remains available at its deadline");
            now = deadline;
        }
        int attempts = policy.UnstableStarts;
        for (int i = 0; i < 200; ++i)
        {
            now = policy.NextAttemptTimestamp;
            if (!policy.TryBeginStart(now, false, false)) break;
        }
        check(policy.UnstableStarts == attempts + 200 && policy.ScheduledDelaySeconds == 60,
            "More than three failed starts never exhaust helper recovery or exceed the 60 second cap");

        now = policy.NextAttemptTimestamp;
        check(!policy.TryBeginStart(now, true, false) && !policy.TryBeginStart(now + 60000, true, true),
            "A living helper is never replaced even if its heartbeat is stale or backoff elapsed");
        check(policy.ScheduledDelaySeconds == 60 && policy.UnstableStarts == attempts + 200,
            "One heartbeat after a long observation gap cannot reset crash-loop backoff");
        now += 60000;
        long stableStart = now;
        for (; now < stableStart + 10000; now += 100)
            policy.TryBeginStart(now, true, true);
        check(policy.ScheduledDelaySeconds == 60 && policy.UnstableStarts == attempts + 200,
            "Less than 10 seconds of continuously observed health does not reset backoff");
        policy.TryBeginStart(now, true, true);
        check(policy.ScheduledDelaySeconds == 2 && policy.UnstableStarts == 0,
            "Ten seconds of stable child and private heartbeat resets retry backoff");
        check(policy.TryBeginStart(now + 1, false, false) && policy.ScheduledDelaySeconds == 2,
            "A crash after demonstrated stable recovery starts a new initial retry sequence");

        var interrupted = new HelperRestartPolicy(frequency);
        interrupted.TryBeginStart(0, false, false);
        interrupted.TryBeginStart(2000, false, false);
        interrupted.TryBeginStart(6000, false, false);
        for (long t = 6100; t <= 15000; t += 100) interrupted.TryBeginStart(t, true, true);
        interrupted.TryBeginStart(15100, true, false);
        for (long t = 15200; t < 25200; t += 100) interrupted.TryBeginStart(t, true, true);
        check(interrupted.UnstableStarts == 3 && interrupted.ScheduledDelaySeconds == 8,
            "A heartbeat interruption restarts the full stability dwell without resetting failures");
        interrupted.TryBeginStart(25200, true, true);
        check(interrupted.UnstableStarts == 0 && interrupted.ScheduledDelaySeconds == 2,
            "Health must remain continuous for 10 seconds after the interruption");

        var unknownChild = new HelperRestartPolicy(frequency);
        check(!unknownChild.TryBeginStart(0, false, true) && unknownChild.UnstableStarts == 0,
            "A fresh helper heartbeat without a child reference prevents duplicate launch");
        check(unknownChild.TryBeginStart(1000, false, false),
            "Recovery remains available when the surviving helper heartbeat expires");
        check(!unknownChild.TryBeginStart(-1, false, false), "Invalid negative clock never authorizes helper launch");
        check(!unknownChild.TryBeginStart(500, false, false) && unknownChild.NextAttemptTimestamp == 2500 &&
            unknownChild.TryBeginStart(2500, false, false) && unknownChild.ScheduledDelaySeconds == 4,
            "Clock rollback preserves crash-loop level and rearms a bounded retry deadline");

        var sparse = new HelperRestartPolicy(frequency);
        sparse.TryBeginStart(0, false, false);
        sparse.TryBeginStart(2000, false, false);
        sparse.TryBeginStart(3000, true, true);
        sparse.TryBeginStart(20000, true, true);
        check(sparse.UnstableStarts == 2 && sparse.ScheduledDelaySeconds == 4,
            "Unobserved health across a long gap does not pretend to be continuous recovery");
        var overflow = new HelperRestartPolicy(frequency);
        check(overflow.TryBeginStart(long.MaxValue - 1, false, false) &&
            overflow.NextAttemptTimestamp == long.MaxValue &&
            !overflow.TryBeginStart(long.MaxValue - 1, false, false) &&
            !overflow.TryBeginStart(long.MaxValue, false, false),
            "Retry deadline saturation cannot overflow into an immediate busy launch loop");
    }
}
