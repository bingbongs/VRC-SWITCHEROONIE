using Switcheroonie.Broker;

// Pure fake operations; never constructs WindowsGameInput or calls any native
// cursor, keyboard, window, process, registry or input APIs.
internal static class GameCursorCaptureTests
{
    internal static void Run(Action<bool, string> check)
    {
        var desktop = new CursorNative.Rect(-1920, 0, 2560, 1440);
        var client = new CursorNative.Rect(100, 200, 1100, 800);
        var center = new CursorNative.Rect(600, 500, 601, 501);
        check(GameCursorCapture.Target(client, false).Equals(center) && GameCursorCapture.Target(client, true).Equals(client),
            "FPS look uses a one-pixel center lock; pointer uses whole client");
        var scaled = new CursorNative.Rect(-1500, 100, -300, 900);
        check(GameCursorCapture.Target(scaled, false).Equals(new(-900, 500, -899, 501)),
            "Physical pixel center remains one pixel on negative-origin / resized DPI geometry");
        var fake = new Fake(desktop);
        var policy = new GameCursorCapture(fake, fake.Publish);
        policy.Update(true, false, client, desktop);
        check(policy.Owned && fake.Current.Equals(center) && fake.CenterCalls == 1 && fake.CenterX == 600 && fake.CenterY == 500,
            "Authenticated look acquisition centers once and owns the exact clip");
        check(fake.MetadataBeforeEveryNewClip && policy.PreviousRectangle.Equals(desktop) && policy.Generation == 1,
            "Recovery metadata precedes initial clip with original restoration rectangle");
        int writes = fake.ClipCalls;
        policy.Update(true, false, client, desktop);
        check(fake.ClipCalls == writes && fake.CenterCalls == 1 && fake.LastLease.Armed,
            "Stable center lock refreshes lease without repeated cursor warps");
        fake.Current = desktop;
        policy.Update(true, false, client, desktop);
        check(policy.Owned && fake.Current.Equals(center) && policy.PreviousRectangle.Equals(desktop) && policy.Generation == 2,
            "Unity unclipping is reasserted within the same authenticated active scope");
        fake.Current = client;
        policy.Update(true, false, client, desktop);
        check(policy.Owned && fake.Current.Equals(center) && policy.PreviousRectangle.Equals(desktop) && policy.Generation == 3,
            "Unity client clip replacement cannot let FPS look drift to window edges");
        int centers = fake.CenterCalls;
        fake.Events.Clear();
        policy.Update(true, true, client, desktop);
        check(policy.Owned && fake.Current.Equals(client) && fake.CenterCalls == centers,
            "Pointer transition permits full client movement without cursor recentering");
        check(fake.Events.IndexOf("restore") < fake.Events.IndexOf("new-lease") && fake.MetadataBeforeEveryNewClip,
            "Old clip is restored before publishing new shape, avoiding stranded old-clip crash gap");
        var resized = new CursorNative.Rect(-800, 150, 800, 1150);
        policy.Update(true, true, resized, desktop);
        check(fake.Current.Equals(resized) && policy.PreviousRectangle.Equals(desktop),
            "Resize replaces pointer clip and preserves original restoration baseline");
        policy.Update(true, false, resized, desktop);
        check(fake.Current.Equals(new(0, 650, 1, 651)) && fake.CenterX == 0 && fake.CenterY == 650,
            "Returning to look centers on latest client size and screen origin");
        policy.Update(false, false, resized, desktop);
        check(!policy.Owned && fake.Current.Equals(desktop) && !fake.LastLease.Armed,
            "Release restores original rectangle and disarms watchdog lease");

        fake = new Fake(desktop); policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop);
        var foreign = new CursorNative.Rect(-1500, 50, -1200, 300);
        fake.Current = foreign; writes = fake.ClipCalls;
        policy.Update(true, false, client, desktop);
        check(!policy.Owned && fake.Current.Equals(foreign) && fake.ClipCalls == writes && !fake.LastLease.Armed,
            "Unrelated outside-client clip is preserved rather than reasserted");
        policy.Update(true, false, client, desktop);
        check(!policy.Owned && fake.ClipCalls == writes,
            "A later polling tick cannot acquire through an outside-client foreign clip");
        policy.Release();
        check(fake.Current.Equals(foreign) && fake.ClipCalls == writes, "Release never restores over a different owner clip");
        fake = new Fake(desktop); policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop);
        var foreignInside = new CursorNative.Rect(250, 300, 400, 450);
        fake.Current = foreignInside; writes = fake.ClipCalls;
        policy.Update(true, false, client, desktop); policy.Update(true, true, client, desktop);
        check(!policy.Owned && fake.Current.Equals(foreignInside) && fake.ClipCalls == writes && !fake.LastLease.Armed,
            "A distinct inside-client foreign clip is preserved across present and later look/pointer updates");

        fake = new Fake(center); policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop); policy.Release();
        check(!policy.Owned && fake.ClipCalls == 0 && fake.CenterCalls == 0,
            "Matching pre-existing game lock is not claimed or restored as broker-owned");

        fake = new Fake(desktop) { Authority = false }; policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop);
        check(fake.ClipCalls == 0 && fake.CenterCalls == 0, "Missing focus/lease/watchdog authority prevents acquisition");
        fake = new Fake(desktop) { LoseAuthorityOnNewLease = true }; policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop);
        check(!policy.Owned && fake.Current.Equals(desktop) && fake.ClipCalls == 0 && !fake.LastLease.Armed,
            "Focus loss after metadata publication cancels the pending cursor mutation");
        fake = new Fake(desktop) { LoseAuthorityAfterNewClip = true }; policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop);
        check(!policy.Owned && fake.Current.Equals(desktop) && fake.CenterCalls == 0,
            "Focus loss immediately after clip acquisition restores without warping the new foreground");
        fake = new Fake(desktop) { FailNewClip = true }; policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop);
        check(!policy.Owned && fake.Current.Equals(desktop) && !fake.LastLease.Armed,
            "Native clip failure cannot report owned capture");

        fake = new Fake(desktop); policy = new(fake, fake.Publish);
        policy.Update(true, false, client, desktop); fake.FailRestore = true;
        policy.Release();
        check(policy.Owned && fake.LastLease.Armed && fake.Current.Equals(center),
            "Failed restoration retains owned recovery record rather than falsely disarming");
        long oldGeneration = policy.Generation;
        policy.Update(true, true, client, desktop);
        check(policy.Generation == oldGeneration && policy.OwnedRectangle.Equals(center) && fake.LastLease.Owned.Equals(center),
            "Failed old-clip restoration cannot publish a new-shape watchdog record");
        fake.FailRestore = false; fake.ReadFails = true;
        policy.Release();
        check(policy.Owned && fake.LastLease.Armed, "Unreadable clip retains recovery metadata for helper retry");
        fake.ReadFails = false; policy.Release();
        check(!policy.Owned && !fake.LastLease.Armed && fake.Current.Equals(desktop),
            "A later successful restoration clears retained recovery state");
        fake = new Fake(desktop); policy = new(fake, fake.Publish);
        policy.Update(true, false, new(0, 0, 100001, 400), desktop);
        check(fake.ClipCalls == 0 && fake.CenterCalls == 0, "Invalid or oversized client geometry cannot mutate cursor");
    }
    private readonly record struct Lease(bool Armed, CursorNative.Rect Owned, CursorNative.Rect Previous, long Generation);
    private sealed class Fake : GameCursorCapture.IOperations
    {
        private readonly CursorNative.Rect initial;
        internal CursorNative.Rect Current;
        internal Fake(CursorNative.Rect rectangle) { initial = rectangle; Current = rectangle; }
        internal bool Authority = true, ReadFails, FailNewClip, FailRestore, LoseAuthorityOnNewLease, LoseAuthorityAfterNewClip;
        internal bool MetadataBeforeEveryNewClip = true;
        internal int ClipCalls, CenterCalls, CenterX, CenterY;
        internal Lease LastLease;
        internal readonly List<string> Events = new();
        public bool AuthorityValid => Authority;
        public bool Read(out CursorNative.Rect rectangle) { rectangle = Current; return !ReadFails; }
        public bool Clip(CursorNative.Rect rectangle)
        {
            ++ClipCalls;
            bool restoration = rectangle.Equals(initial);
            Events.Add(restoration ? "restore" : "new-clip");
            if ((restoration && FailRestore) || (!restoration && FailNewClip)) return false;
            if (!restoration) MetadataBeforeEveryNewClip &= LastLease.Armed && LastLease.Owned.Equals(rectangle) && LastLease.Previous.Equals(initial);
            Current = rectangle;
            if (!restoration && LoseAuthorityAfterNewClip) Authority = false;
            return true;
        }
        public bool Center(int x, int y) { ++CenterCalls; CenterX = x; CenterY = y; return true; }
        internal void Publish(bool armed, CursorNative.Rect owned, CursorNative.Rect previous, long generation)
        {
            bool newShape = armed && (!LastLease.Armed || !LastLease.Owned.Equals(owned) || generation != LastLease.Generation);
            LastLease = new(armed, owned, previous, generation); Events.Add(newShape ? "new-lease" : armed ? "refresh" : "disarm");
            if (newShape && LoseAuthorityOnNewLease) Authority = false;
        }
    }
}
