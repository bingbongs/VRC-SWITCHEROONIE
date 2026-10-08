using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Switcheroonie;
using Switcheroonie.Broker;

// Private mappings and fake clip operations only. These checks never instantiate
// WindowsGameInput or invoke ClipCursor, SetCursorPos, or a live keyboard hook.
internal static class CursorTests
{
    public static void Run(Action<bool, string> check)
    {
        string name = "Local\\VRC-SWITCHEROONIE-Cursor-" + Identity.Sid + "-" + Guid.NewGuid().ToString("N");
        check(CursorLeaseMap.ValidName(name) && !CursorLeaseMap.ValidName(name + "-extra") &&
            !CursorLeaseMap.ValidName("Local\\VRC-SWITCHEROONIE-Cursor-S-1-0-0-" + Guid.NewGuid().ToString("N")),
            "Cursor lease names require the current SID and one exact unique suffix");
        using var map = new CursorLeaseMap(name, true, Environment.ProcessId);
        using var reader = new CursorLeaseMap(name, false, Environment.ProcessId);
        using var fixture = MemoryMappedFile.OpenExisting(name);
        using var data = fixture.CreateViewAccessor();
        check(reader.WriterPid == Environment.ProcessId && reader.Read() is { Valid: true, Armed: false },
            "Private cursor lease starts disarmed with the exact writer identity");
        bool identityRejected = false, duplicateRejected = false;
        try { using var wrong = new CursorLeaseMap(name, false, Environment.ProcessId + 1); }
        catch (IOException) { identityRejected = true; }
        try { using var duplicate = new CursorLeaseMap(name, true, Environment.ProcessId); }
        catch (IOException) { duplicateRejected = true; }
        check(identityRejected && duplicateRejected, "Cursor map rejects the wrong parent PID and existing-name overwrite");

        var owned = new CursorNative.Rect(100, 200, 900, 800);
        var previous = new CursorNative.Rect(-1920, 0, 1920, 1080);
        map.Publish(true, 1234, 5678, owned, previous, generation: 7);
        CursorLease committed = reader.Read();
        check(committed.Valid && committed.Armed && committed.Window == 1234 && committed.GamePid == 5678 &&
            committed.Generation == 7 && committed.Owned.Equals(owned) && committed.Previous.Equals(previous),
            "Committed armed cursor lease preserves both exact rectangles and its acquisition generation");

        // Simulate a writer killed at each key point of the inactive-slot write.
        long selectedPublication = data.ReadInt64(0);
        long inactive = 256 + ((selectedPublication + 1) & 1) * 256;
        long inactiveSequence = data.ReadInt64(inactive) & ~1L;
        data.Write(inactive, inactiveSequence + 1);
        data.Write(inactive + 8, Stopwatch.GetTimestamp());
        data.Write(inactive + 16, 0L); // Partly written next disarmed record.
        check(reader.Read() == committed, "Interrupted inactive cursor publication keeps the prior armed recovery record readable");
        data.Write(inactive + 24, 9876L); data.Write(inactive + 104, 8L);
        check(reader.Read() == committed, "Mixed inactive-window and generation fields cannot replace the committed recovery record");
        data.Write(inactive, inactiveSequence + 2); // Complete slot, but no selector commit.
        check(reader.Read() == committed, "Completed unselected slot cannot prematurely disarm cursor recovery");
        map.Publish(false, 0, 0, owned, previous, generation: 7);
        check(reader.Read() is { Valid: true, Armed: false, Generation: 7 }, "Only an atomic publication commit selects the disarmed lease");

        map.Publish(true, 1234, 5678, owned, previous, generation: 9);
        committed = reader.Read();
        long selected = 256 + (data.ReadInt64(0) & 1) * 256;
        long selectedSequence = data.ReadInt64(selected);
        data.Write(selected, selectedSequence | 1L); // Invalid selected snapshot fallback case.
        check(!reader.Read().Valid && CursorRecovery.ShouldRecover(committed, committed, true, false, Stopwatch.GetTimestamp()),
            "A torn selected read is rejected while the helper's cached committed lease remains recoverable after parent death");
        data.Write(selected, selectedSequence);
        check(reader.Read() == committed, "Restoring the selected cursor seqlock preserves the committed lease");

        long now = Stopwatch.GetTimestamp();
        long ms = Math.Max(1, Stopwatch.Frequency / 1000);
        var fresh = committed with { Timestamp = now };
        var stale = committed with { Timestamp = now - ms * 250 };
        check(!CursorRecovery.ShouldRecover(fresh, fresh, false, false, now), "A fresh live focused cursor lease cannot trigger watchdog recovery");
        check(CursorRecovery.ShouldRecover(stale, stale, false, false, now), "A cursor lease older than 200 ms triggers watchdog recovery");
        check(CursorRecovery.ShouldRecover(fresh, fresh, true, false, now) && CursorRecovery.ShouldRecover(fresh, fresh, false, true, now),
            "Parent death and focus loss trigger recovery without waiting for lease expiry");
        check(CursorRecovery.ShouldRecover(fresh with { Timestamp = now + ms }, fresh, false, false, now) &&
            CursorRecovery.ShouldRecover(fresh with { Timestamp = 0 }, fresh, false, false, now),
            "Invalid or future cursor heartbeat timestamps fail safe");
        check(!CursorRecovery.ShouldRecover(stale with { Generation = stale.Generation + 1 }, stale, true, true, now) &&
            !CursorRecovery.ShouldRecover(stale with { Window = 9999 }, stale, true, true, now) &&
            !CursorRecovery.ShouldRecover(stale with { Previous = owned }, stale, true, true, now),
            "Recovery rejects a newer acquisition, changed game window, or mismatched restoration record");
        check(!CursorRecovery.ShouldRecover(stale with { Armed = false }, stale, true, true, now) &&
            !CursorRecovery.ShouldRecover(stale with { Valid = false }, stale, true, true, now),
            "Disarmed and invalid cursor leases cannot authorize restoration");

        map.Publish(true, 1234, 5678, owned, previous, shutdown: true, generation: 9);
        var shutdownOwned = reader.Read();
        check(shutdownOwned is { Valid: true, Armed: true, Shutdown: true, Generation: 9 } && shutdownOwned.Owned.Equals(owned) && shutdownOwned.Previous.Equals(previous),
            "Armed shutdown retains exact ownership identity and restoration snapshot");
        check(CursorRecovery.ShouldRecover(shutdownOwned, shutdownOwned, false, false, Stopwatch.GetTimestamp()),
            "Fresh focused armed shutdown requires restoration before helper exit");
        var failedThenRecovered = new FakeClip(owned) { RestoreSucceeds = false };
        check(!CursorNative.RestoreIfStillOwned(owned, previous, failedThenRecovered) && failedThenRecovered.Current.Equals(owned),
            "Failed local teardown does not falsely report a restored clip");
        failedThenRecovered.RestoreSucceeds = true;
        check(CursorRecovery.ShouldRecover(shutdownOwned, shutdownOwned, false, false, Stopwatch.GetTimestamp()) &&
            CursorNative.RestoreIfStillOwned(shutdownOwned.Owned, shutdownOwned.Previous, failedThenRecovered) && failedThenRecovered.Current.Equals(previous),
            "Shutdown helper can restore retained ownership after the local attempt failed");
        var foreignAtShutdown = new FakeClip(new(10, 20, 30, 40));
        check(CursorRecovery.ShouldRecover(shutdownOwned, shutdownOwned, false, false, Stopwatch.GetTimestamp()) &&
            !CursorNative.RestoreIfStillOwned(shutdownOwned.Owned, shutdownOwned.Previous, foreignAtShutdown) && foreignAtShutdown.RestoreCalls == 0,
            "Shutdown recovery still preserves a concurrent foreign clip");
        var helperRetry = new FakeClip(owned) { ReadSucceeds = false };
        check(CursorNative.RestoreOwnedResult(owned, previous, helperRetry) == CursorNative.RestoreResult.RetryFailure && helperRetry.RestoreCalls == 0,
            "A helper clip-read failure requests retry instead of confirming nonownership");
        helperRetry.ReadSucceeds = true; helperRetry.RestoreSucceeds = false;
        check(CursorNative.RestoreOwnedResult(owned, previous, helperRetry) == CursorNative.RestoreResult.RetryFailure && helperRetry.Current.Equals(owned),
            "A helper restore failure retains recovery authority rather than authorizing exit");
        helperRetry.RestoreSucceeds = true;
        check(CursorNative.RestoreOwnedResult(owned, previous, helperRetry) == CursorNative.RestoreResult.Restored && helperRetry.Current.Equals(previous),
            "A later successful helper retry restores the exact recorded original clip");
        helperRetry = new FakeClip(owned) { RestoreSucceeds = false };
        check(CursorNative.RestoreOwnedResult(owned, previous, helperRetry) == CursorNative.RestoreResult.RetryFailure,
            "First helper failure remains retryable during armed shutdown");
        helperRetry.Current = new(10, 20, 30, 40);
        int previousRestoreCalls = helperRetry.RestoreCalls;
        check(CursorNative.RestoreOwnedResult(owned, previous, helperRetry) == CursorNative.RestoreResult.NotOwned && helperRetry.RestoreCalls == previousRestoreCalls,
            "Foreign clip introduced between retries is preserved and safely ends recovery");
        map.Publish(true, 0, 5678, owned, previous, shutdown: true, generation: 9);
        check(!reader.Read().Valid, "Armed shutdown rejects a missing window instead of authorizing recovery");
        map.Publish(false, 0, 0, owned, previous, shutdown: true, generation: 9);
        check(reader.Read() is { Valid: true, Armed: false, Shutdown: true }, "Fully restored shutdown remains disarmed");
        selected = 256 + (data.ReadInt64(0) & 1) * 256;
        data.Write(selected + 16, -3L);
        check(!reader.Read().Valid, "Unknown private cursor mode cannot authorize recovery");
        map.Publish(true, 1234, 5678, owned, previous, generation: 9);

        check(!reader.HelperAlive, "An unstarted cursor helper does not authorize capture");
        map.PublishHelperHeartbeat();
        check(reader.HelperAlive, "A fresh private cursor-helper heartbeat authorizes capture readiness");
        data.Write(128, now - ms * 250);
        check(!reader.HelperAlive, "An expired cursor-helper heartbeat withdraws capture readiness");
        data.Write(128, Stopwatch.GetTimestamp() + ms * 1000);
        check(!reader.HelperAlive, "A future helper heartbeat does not authorize cursor capture");
        map.PublishRecovery(9);
        check(reader.RecoveryGeneration == 9 && reader.RecoveryTimestamp > 0,
            "Cursor recovery publishes its atomic acquisition generation");

        var clip = new FakeClip(owned);
        check(CursorNative.RestoreIfStillOwned(owned, previous, clip) && clip.RestoreCalls == 1 && clip.Current.Equals(previous),
            "Matching owned cursor clip restores exactly the previously recorded rectangle using fake operations");
        clip = new(new CursorNative.Rect(10, 20, 30, 40));
        check(!CursorNative.RestoreIfStillOwned(owned, previous, clip) && clip.RestoreCalls == 0,
            "A foreign different cursor clip is never changed by recovery");
        clip = new(owned) { ReadSucceeds = false };
        check(!CursorNative.RestoreIfStillOwned(owned, previous, clip) && clip.RestoreCalls == 0,
            "Unreadable cursor ownership fails without a restoration call");
        clip = new(owned);
        check(!CursorNative.RestoreIfStillOwned(owned, default, clip) && clip.ReadCalls == 0 && clip.RestoreCalls == 0,
            "Invalid restoration rectangles are rejected before querying or changing cursor state");
        clip = new(owned) { RestoreSucceeds = false };
        check(!CursorNative.RestoreIfStillOwned(owned, previous, clip) && clip.RestoreCalls == 1,
            "A failed fake cursor restoration is reported honestly");

        var presses = new KeyboardPressTracker();
        check(presses.Observe(9, false) is { First: true, Swallow: false }, "An unowned first menu-key press remains available for scope validation");
        presses.Claim(9);
        check(presses.Observe(9, false) is { First: false, Swallow: true },
            "An owned menu-key repeat stays consumed before modifier, typing, or focus policy checks");
        check(presses.Observe(9, true) is { Swallow: true } && presses.Observe(9, false) is { First: true, Swallow: false },
            "An owned menu-key release is consumed and the next new press starts unowned");
        check(presses.Observe(27, false) is { First: true, Swallow: false } && presses.Observe(27, true) is { Swallow: false },
            "An unowned Escape press and release both pass through");
    }

    sealed class FakeClip(CursorNative.Rect current) : CursorNative.IClipOperations
    {
        internal CursorNative.Rect Current = current;
        internal int ReadCalls, RestoreCalls;
        internal bool ReadSucceeds = true, RestoreSucceeds = true;
        public bool Read(out CursorNative.Rect rectangle) { ++ReadCalls; rectangle = Current; return ReadSucceeds; }
        public bool Restore(CursorNative.Rect rectangle) { ++RestoreCalls; if (RestoreSucceeds) Current = rectangle; return RestoreSucceeds; }
    }
}
