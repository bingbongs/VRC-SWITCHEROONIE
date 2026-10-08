namespace Switcheroonie.UI;

// Monotonic, bounded retry policy. A responsive broker resets the delay; a
// still-running child is never killed/replaced merely because its pipe is busy.
internal sealed class BrokerRestartPolicy
{
    private int attempts;
    private double? lastAttempt;
    private double? readySince;
    internal static bool ChildMayBeRunning(Func<bool>? hasExited)
    {
        if (hasExited is null) return false;
        try { return !hasExited(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return true; } // An uncertain process query never authorizes replacement.
    }
    internal bool CanAttempt(double now, bool childRunning, bool force = false) =>
        !childRunning && (force || lastAttempt is null || now - lastAttempt.Value >= Math.Min(60, 3 * Math.Pow(2, Math.Min(attempts - 1, 5))));
    internal void RecordAttempt(double now) { lastAttempt = now; readySince = null; ++attempts; }
    internal void RecordUnavailable() => readySince = null;
    internal void RecordReady(double now)
    {
        readySince ??= now;
        // A brief successful pipe reply must not reset a crash-loop backoff.
        if (now - readySince.Value >= 10) { lastAttempt = null; attempts = 0; }
    }

    internal static void SelfTest()
    {
        var policy = new BrokerRestartPolicy();
        void Require(bool condition) { if (!condition) throw new InvalidOperationException("Resident retry policy regression"); }
        Require(policy.CanAttempt(0, false));
        Require(!policy.CanAttempt(0, true, force: true));
        policy.RecordAttempt(0);
        Require(!policy.CanAttempt(2.99, false)); Require(policy.CanAttempt(3, false));
        policy.RecordAttempt(3);
        Require(!policy.CanAttempt(8.99, false)); Require(policy.CanAttempt(9, false));
        Require(policy.CanAttempt(3.1, false, force: true));
        Require(!policy.CanAttempt(90, true));
        for (int i = 0; i < 10; ++i) policy.RecordAttempt(100);
        Require(!policy.CanAttempt(159.99, false)); Require(policy.CanAttempt(160, false));
        policy.RecordAttempt(160);
        policy.RecordReady(160);
        Require(!policy.CanAttempt(160, false));
        policy.RecordReady(169.99); Require(!policy.CanAttempt(169.99, false));
        policy.RecordUnavailable(); policy.RecordReady(170);
        Require(!policy.CanAttempt(179.99, false));
        policy.RecordReady(180);
        Require(policy.CanAttempt(180, false));
        Require(!policy.CanAttempt(180, true));
        // Inject only process-query results/errors: no process is started or
        // observed by this fixture, including the manual Retry path.
        Require(!ChildMayBeRunning(null));
        Require(ChildMayBeRunning(() => false));
        Require(!ChildMayBeRunning(() => true));
        Require(ChildMayBeRunning(() => throw new InvalidOperationException()));
        Require(ChildMayBeRunning(() => throw new System.ComponentModel.Win32Exception()));
        Require(ChildMayBeRunning(() => throw new ObjectDisposedException("fixture process")));
        bool uncertain = ChildMayBeRunning(() => throw new System.ComponentModel.Win32Exception());
        Require(!policy.CanAttempt(1000, uncertain) && !policy.CanAttempt(1000, uncertain, force: true));
    }
}
