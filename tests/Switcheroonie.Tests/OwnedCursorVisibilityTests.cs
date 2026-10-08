using Switcheroonie.Broker;

// The fake window/cursor adapter exercises ownership and handoff ordering.
// These tests never create a window or call any live Windows cursor API.
internal static class OwnedCursorVisibilityTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Verify(bool condition, string name) => check(condition, "Owned cursor visibility: " + name);
        static (OwnedCursorVisibilityPolicy Policy, FakeOperations Operations) Create()
        {
            var operations = new FakeOperations();
            return (new(operations), operations);
        }

        var (policy, ops) = Create();
        Verify(policy.Hide() && ops.HideCalls == 1 && ops.Hidden, "an owned visible shape is hidden once");
        Verify(policy.Hide() && ops.HideCalls == 1, "an already-owned hidden shape needs no additional mutation");
        policy.Suspend();
        Verify(ops.RestoreCalls == 1 && !ops.Hidden && !ops.SurfaceVisible &&
            ops.Events.SequenceEqual(new[] { "hide-shape", "restore-arrow", "hide-surface" }),
            "pointer or authority loss hands off the shape before hiding the window without waiting for mouse movement");
        policy.Suspend();
        Verify(ops.RestoreCalls == 1, "repeated release cannot overwrite a later cursor shape");

        (policy, ops) = Create(); ops.OwnsPoint = false;
        Verify(!policy.Hide() && ops.HideCalls == 0, "a foreign window covering the lock point prevents timer hiding");
        policy.Suspend();
        Verify(ops.RestoreCalls == 0, "foreign cover cannot create a restoration claim");

        (policy, ops) = Create(); policy.Hide(); ops.OwnsPoint = false;
        policy.Suspend();
        Verify(ops.RestoreCalls == 0 && !ops.SurfaceVisible, "release under a foreign overlay hides only our surface");
        ops.OwnsPoint = true; policy.Suspend();
        Verify(ops.RestoreCalls == 0, "returning to the old point cannot revive a discarded foreign-covered claim");

        (policy, ops) = Create(); policy.Hide(); ops.Hidden = false;
        policy.Suspend();
        Verify(ops.RestoreCalls == 0 && !ops.Hidden, "a foreign non-null replacement survives release");
        policy.Dispose();
        Verify(ops.RestoreCalls == 0 && ops.DestroyCalls == 1, "disposal cannot replace the preserved foreign shape");

        (policy, ops) = Create(); ops.Hidden = true;
        Verify(policy.Hide() && ops.HideCalls == 0, "a pre-existing null shape is observed without taking ownership");
        policy.Suspend();
        Verify(ops.RestoreCalls == 0 && ops.Hidden, "release preserves a shape this policy never hid");

        (policy, ops) = Create(); policy.Hide();
        policy.Dispose();
        Verify(ops.RestoreCalls == 1 && ops.DestroyCalls == 1 &&
            ops.Events.SequenceEqual(new[] { "hide-shape", "restore-arrow", "hide-surface", "destroy-surface" }),
            "disposal performs an owned handoff before window destruction");
        int reads = ops.ReadCalls;
        Verify(!policy.Hide() && ops.ReadCalls == reads, "a queued refresh cannot retake ownership after disposal");
        policy.Suspend(); policy.Dispose();
        Verify(ops.DestroyCalls == 1 && ops.RestoreCalls == 1, "repeated disposal and release remain terminal");

        (policy, ops) = Create(); policy.Hide(); ops.ReadSucceeds = false;
        Verify(!policy.Hide() && ops.HideCalls == 1, "failed observation performs no speculative mutation");
        ops.ReadSucceeds = true; policy.Suspend();
        Verify(ops.RestoreCalls == 1, "a transient observation failure does not discard a still-owned handoff");

        (policy, ops) = Create(); ops.HideAuthority = false;
        Verify(!policy.Hide() && ops.HideCalls == 0, "authority lost between observation and mutation prevents hiding");
        policy.Suspend();
        Verify(ops.RestoreCalls == 0, "a refused hide never gains ownership");

        (policy, ops) = Create(); ops.OwnAtMutation = false;
        Verify(!policy.Hide() && ops.HideCalls == 0, "hit-test ownership is checked again at the hide boundary");

        (policy, ops) = Create(); policy.Hide(); ops.OwnAtMutation = false;
        policy.Suspend();
        Verify(ops.RestoreCalls == 0 && !ops.SurfaceVisible,
            "ownership loss between handoff observation and mutation preserves the new owner");

        (policy, ops) = Create(); policy.Hide(); ops.HiddenAtRestore = false;
        policy.Suspend();
        Verify(ops.RestoreCalls == 0, "a shape replacement at the handoff boundary is preserved");

        (policy, ops) = Create(); policy.Hide(); ops.RestoreSucceeds = false;
        policy.Dispose();
        Verify(ops.RestoreAttempts == 1 && ops.RestoreCalls == 0 && ops.DestroyCalls == 1,
            "failed safe handoff still disposes only our window without forcing a foreign shape");

        (policy, ops) = Create(); policy.Hide(); policy.Suspend();
        ops.SurfaceVisible = true; ops.Hidden = false;
        Verify(policy.Hide() && ops.HideCalls == 2, "a new authorized look interval can acquire its own visible shape");
        policy.Suspend();
        Verify(ops.RestoreCalls == 2, "each authorized interval receives exactly its own handoff");
    }

    sealed class FakeOperations : OwnedCursorVisibilityPolicy.IOperations
    {
        internal bool OwnsPoint = true, OwnAtMutation = true, Hidden, SurfaceVisible = true;
        internal bool ReadSucceeds = true, HideAuthority = true, HiddenAtRestore = true, RestoreSucceeds = true;
        internal int ReadCalls, HideCalls, RestoreCalls, RestoreAttempts, DestroyCalls;
        internal readonly List<string> Events = [];
        public bool Read(out OwnedCursorVisibilityPolicy.Observation observation)
        {
            ++ReadCalls; observation = new(OwnsPoint, Hidden); return ReadSucceeds;
        }
        public bool Hide()
        {
            if (!HideAuthority || !OwnsPoint || !OwnAtMutation || Hidden) return false;
            ++HideCalls; Hidden = true; Events.Add("hide-shape"); return true;
        }
        public bool RestoreArrow()
        {
            ++RestoreAttempts;
            if (!OwnsPoint || !OwnAtMutation || !Hidden || !HiddenAtRestore || !RestoreSucceeds) return false;
            ++RestoreCalls; Hidden = false; Events.Add("restore-arrow"); return true;
        }
        public void HideSurface() { SurfaceVisible = false; Events.Add("hide-surface"); }
        public void DestroySurface() { ++DestroyCalls; SurfaceVisible = false; Events.Add("destroy-surface"); }
    }
}
