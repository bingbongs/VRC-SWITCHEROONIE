namespace Switcheroonie.Broker;

// Pure monotonic-clock policy. Callers serialize access and supply current
// child liveness plus the private helper's independently fresh heartbeat.
public sealed class HelperRestartPolicy
{
    static readonly int[] Delays = [2, 4, 8, 16, 32, 60];
    readonly long frequency, maximumObservationGap;
    long lastObservation = -1, healthySince = -1, nextAttempt;
    int backoffLevel;
    bool attempted, stableReset;

    public HelperRestartPolicy(long frequency)
    {
        if (frequency <= 0 || frequency > long.MaxValue / 60)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        this.frequency = frequency;
        maximumObservationGap = Math.Max(1, frequency / 4);
    }

    public int ScheduledDelaySeconds { get; private set; } = 2;
    public int UnstableStarts { get; private set; }
    public long NextAttemptTimestamp => nextAttempt;

    public bool TryBeginStart(long now, bool childAlive, bool heartbeatFresh)
    {
        if (now < 0) { healthySince = -1; stableReset = false; return false; }
        bool backwards = lastObservation >= 0 && now < lastObservation;
        bool gap = lastObservation >= 0 && now - lastObservation > maximumObservationGap;
        if (backwards)
        {
            healthySince = -1; stableReset = false;
            if (attempted) nextAttempt = AddBounded(now, ScheduledDelaySeconds * frequency);
        }
        bool continuouslyHealthy = childAlive && heartbeatFresh;
        if (!continuouslyHealthy)
        {
            healthySince = -1; stableReset = false;
        }
        else
        {
            if (healthySince < 0 || gap || backwards) { healthySince = now; stableReset = false; }
            if (!stableReset && now - healthySince >= 10 * frequency)
            {
                // Brief startup heartbeats cannot collapse a crash loop's backoff.
                backoffLevel = 0; UnstableStarts = 0; ScheduledDelaySeconds = 2;
                attempted = false; nextAttempt = 0; stableReset = true;
            }
        }
        lastObservation = now;

        // A stale but living child is never replaced. A fresh private heartbeat
        // without a process reference can also belong to a surviving helper.
        if (childAlive || heartbeatFresh || (attempted && (now < nextAttempt || nextAttempt == long.MaxValue))) return false;
        attempted = true;
        ScheduledDelaySeconds = Delays[backoffLevel];
        nextAttempt = AddBounded(now, ScheduledDelaySeconds * frequency);
        backoffLevel = Math.Min(backoffLevel + 1, Delays.Length - 1);
        UnstableStarts = Math.Min(UnstableStarts, int.MaxValue - 1) + 1;
        return true;
    }

    static long AddBounded(long now, long interval) => now > long.MaxValue - interval ? long.MaxValue : now + interval;
}
