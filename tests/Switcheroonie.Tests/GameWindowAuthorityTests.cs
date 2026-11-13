using Switcheroonie.Broker;

// Fake process identity authority only: no WindowsGameInput, windows, hooks,
// cursor changes, process inspection or other live APIs are constructed here.
internal static class GameWindowAuthorityTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Verify(bool condition, string name) => check(condition, "Game window authority: " + name);
        var source = new Fake(); var cache = new GameWindowAuthorityCache(source, 1000);
        Verify(cache.Validate(100, 10, 1000) && source.Acquires == 1 && source.AliveReads == 1,
            "first scope pins exact window/PID/process identity");
        bool valid = true;
        for (long time = 1001; time < 1500; ++time) valid &= cache.Validate(100, 10, time);
        Verify(valid && source.Acquires == 1 && source.Refreshes == 0 && source.AliveReads == 500,
            "499 input frames retain identity with live checks and no window enumeration");
        Verify(cache.Validate(100, 10, 1500) && source.Refreshes == 1,
            "full main-window identity is refreshed at the bounded 500ms deadline");
        Verify(cache.Validate(100, 10, 1999) && source.Refreshes == 1,
            "refresh does not turn the next frame back into an expensive process lookup");
        source.RefreshSucceeds = false;
        Verify(!cache.Validate(100, 10, 2000), "failed refresh rejects the old identity immediately");
        source.RefreshSucceeds = true;
        Verify(cache.Validate(100, 10, 2001) && source.Acquires == 2,
            "recovery acquires a fresh authenticated identity instead of reviving failed cache");
        source.AliveSucceeds = false;
        Verify(!cache.Validate(100, 10, 2002), "an exited or uncertain process is rejected before refresh deadline");
        source.AliveSucceeds = true; source.Identity = 200;
        Verify(cache.Validate(100, 10, 2003) && source.Acquires == 3 && source.LastIdentity == 200,
            "same-PID restart requires a newly pinned process identity");
        Verify(cache.Validate(101, 10, 2004) && source.Acquires == 4 && source.LastWindow == 101,
            "replacement window cannot inherit the previous window authority");
        Verify(cache.Validate(101, 11, 2005) && source.Acquires == 5 && source.LastPid == 11,
            "changed PID cannot inherit even an identical numeric window handle");
        Verify(!cache.Validate(101, 11, 2004), "backwards clock invalidates authority rather than extending its freshness");
        source.AcquireSucceeds = false;
        Verify(!cache.Validate(102, 12, 2006), "wrong process/session/main-window authentication cannot populate cache");
        source.AcquireSucceeds = true; source.Identity = 0;
        Verify(!cache.Validate(102, 12, 2007), "missing process-start identity is never accepted");
        source.Identity = 201;
        Verify(!cache.Validate(0, 12, 2008) && !cache.Validate(102, 0, 2009) && !cache.Validate(102, 12, 0),
            "empty scope or clock cannot authorize a capture");
        Verify(cache.Validate(102, 12, 2010), "valid scope can be authenticated after invalid candidates");
        int acquired = source.Acquires; cache.Reset();
        Verify(cache.Validate(102, 12, 2011) && source.Acquires == acquired + 1,
            "focus/eligibility reset disposes the old lease and forces fresh authentication");
    }
    sealed class Fake : GameWindowAuthorityCache.IOperations
    {
        internal bool AcquireSucceeds = true, AliveSucceeds = true, RefreshSucceeds = true;
        internal int Acquires, AliveReads, Refreshes;
        internal long Identity = 100, LastIdentity, LastWindow;
        internal uint LastPid;
        public bool Acquire(long window, uint pid, out long identity)
        { ++Acquires; identity = Identity; LastWindow = window; LastPid = pid; LastIdentity = identity; return AcquireSucceeds; }
        public bool Alive(uint pid, long identity) { ++AliveReads; return AliveSucceeds && pid == LastPid && identity == LastIdentity; }
        public bool Refresh(long window, uint pid, long identity) { ++Refreshes; return RefreshSucceeds && window == LastWindow && pid == LastPid && identity == LastIdentity; }
        public void Release() { }
    }
}
