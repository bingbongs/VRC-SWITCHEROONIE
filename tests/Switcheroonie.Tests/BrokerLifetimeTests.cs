using Switcheroonie.Broker;

internal static class BrokerLifetimeTests
{
    internal static void Run(Action<bool, string> check)
    {
        const string broker = @"C:\Fixture\Portable\Switcheroonie.Broker.exe";
        const string ui = @"C:\Fixture\Portable\Switcheroonie.UI.exe";
        var parent = new BrokerParentIdentity(42, 7, 1234, ui, "fixture-user");
        bool Parse(string[] args, int? expected) => BrokerLifetimePolicy.TryParse(args, 99, out var actual) && actual == expected;
        check(Parse([], null), "Direct broker startup retains independent lifetime");
        check(Parse(["--osc-watchdog"], null), "Independent OSC helper startup has no UI-parent authority");
        check(Parse(["--ui-parent=42"], 42), "Canonical UI-parent argument is accepted");
        foreach (string argument in new[] { "--ui-parent", "--ui-parent=", "--ui-parent=0", "--ui-parent=-1",
            "--ui-parent=00042", "--ui-parent=+42", "--ui-parent=42 ", "--ui-parent=2147483648",
            "--ui-parent=99", "--UI-PARENT=42", "--ui-parent-extra=42" })
            check(!BrokerLifetimePolicy.TryParse([argument], 99, out _), "Malformed or ambiguous parent authority refused: " + argument);
        foreach (string[] args in new[] { new[] { "--ui-parent=42", "--ui-parent=43" },
            new[] { "--ui-parent=42", "--osc-watchdog" }, new[] { "--ui-parent=42", "--cursor-watchdog=x" },
            new[] { "--ui-parent=42", "--parent=17" } })
            check(!BrokerLifetimePolicy.TryParse(args, 99, out _), "Duplicate/helper lifetime authority is refused");
        bool Admit(BrokerParentIdentity value, string image = broker, Func<string,bool>? ordinary = null,
            Func<string,string,bool>? sameFile = null, int creator = 42, long started = 2000) =>
            BrokerLifetimePolicy.Admitted(value,42,creator,99,7,started,"fixture-user",image,ordinary ?? (_=>true),sameFile ?? ((_,_)=>true));
        check(Admit(parent), "Exact sibling UI, session, user and immutable start identity admitted");
        foreach (var invalid in new[] { parent with { Pid = 43 }, parent with { Pid = 99 }, parent with { Session = 8 },
            parent with { Started = 0 }, parent with { User = "foreign-user" }, parent with { Image = broker },
            parent with { Image = @"C:\Foreign\Switcheroonie.UI.exe" }, parent with { Image = @"C:\Fixture\Portable\..\Portable\Switcheroonie.UI.exe" } })
            check(!Admit(invalid), "Foreign, reused, self, helper or ambiguous parent identity refused");
        check(!Admit(parent, @"C:\Fixture\Portable\dotnet.exe"), "UI-owned production lifetime refuses an arbitrary host image");
        check(!Admit(parent, ordinary: _=>false), "Immediate package root/image reparse entries refused");
        check(!Admit(parent, sameFile: (_,_)=>false), "Case alias cannot substitute a different physical image file");
        check(!Admit(parent, ordinary: _=>throw new IOException("unavailable")), "Inaccessible image authority fails closed");
        check(!Admit(parent, creator: 43), "Same-folder UI cannot replace the actual creating process");
        check(!Admit(parent with { Started = 3000 }), "Parent replacement created after broker birth is refused before capture");
        check(!Admit(parent, started: 0), "Unknown broker creation identity refuses parent lifetime");
        using var stop = new CancellationTokenSource();
        BrokerLifetimePolicy.Poll(parent,parent,BrokerParentState.Alive,stop);
        check(!stop.IsCancellationRequested, "Live UI remains authoritative while hidden/minimized/closed to tray");
        BrokerLifetimePolicy.Poll(parent,parent with { Started = 5678 },BrokerParentState.Alive,stop);
        check(stop.IsCancellationRequested, "Same PID with changed start identity cancels only broker lifetime");
        foreach (var state in new[] { BrokerParentState.Exited, BrokerParentState.Unavailable })
        {
            using var cancelled = new CancellationTokenSource(); bool released = false;
            using var callback = cancelled.Token.Register(()=>released=true);
            BrokerLifetimePolicy.Poll(parent,null,state,cancelled);
            check(cancelled.IsCancellationRequested && released, "Parent exit/unavailable produces cooperative self cancellation and cleanup signal");
        }
        var group = new BrokerClientLifetime(); var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        check(group.TryRun(()=>pending.Task), "Client accepted before broker shutdown is tracked");
        var drain = group.DrainAsync(); bool lateRan = false;
        check(!drain.IsCompleted && !group.TryRun(()=>{lateRan=true;return Task.CompletedTask;}) && !lateRan,
            "Shutdown stops accepting client execution and waits for the existing request");
        pending.SetResult(); drain.GetAwaiter().GetResult();
        check(drain.IsCompletedSuccessfully, "Existing client drains before engine cleanup can run");
        using var faultStop = new CancellationTokenSource();
        var faultClients = new BrokerClientLifetime(); var pendingFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        faultClients.TryRun(()=>pendingFault.Task);
        var failedProducer = BrokerServiceLifetime.RunProducerAsync(()=>Task.FromException(new IOException("fixture producer failure")),faultStop);
        check(faultStop.IsCancellationRequested, "Unexpected producer fault always cancels pipe acceptance");
        bool cleaned = false;
        var stopped = BrokerServiceLifetime.StopAsync(failedProducer,faultClients,faultStop,()=>cleaned=true);
        check(!stopped.IsCompleted && !cleaned, "Faulted producer still drains pending client before helper/engine teardown");
        pendingFault.SetResult();
        try { stopped.GetAwaiter().GetResult(); check(false,"Producer failure must remain observable"); }
        catch (IOException) { check(cleaned && stopped.IsFaulted,"Faulted shutdown preserves failure after guaranteed cleanup"); }
    }
}
