namespace Switcheroonie.Broker;

// State/decision policy tested using fake native operations. The platform adapter
// supplies authenticated foreground scope and watchdog liveness on every call.
internal sealed class GameCursorCapture
{
    internal interface IOperations
    {
        bool AuthorityValid { get; }
        bool Read(out CursorNative.Rect rectangle);
        bool Clip(CursorNative.Rect rectangle);
        bool Center(int x, int y);
    }
    private readonly IOperations operations;
    private readonly Action<bool, CursorNative.Rect, CursorNative.Rect, long> publish;
    internal bool Owned { get; private set; }
    internal CursorNative.Rect OwnedRectangle { get; private set; }
    internal CursorNative.Rect PreviousRectangle { get; private set; }
    internal long Generation { get; private set; }

    internal GameCursorCapture(IOperations operations, Action<bool, CursorNative.Rect, CursorNative.Rect, long> publish)
    { this.operations = operations; this.publish = publish; }

    internal static CursorNative.Rect Target(CursorNative.Rect client, bool pointer)
    {
        if (pointer) return client;
        int x = client.Left + (client.Right - client.Left) / 2;
        int y = client.Top + (client.Bottom - client.Top) / 2;
        return new(x, y, x + 1, y + 1);
    }
    internal void Update(bool allowed, bool pointer, CursorNative.Rect client, CursorNative.Rect desktop)
    {
        if (!allowed || !CursorNative.ValidRect(client) || !CursorNative.ValidRect(desktop) || !operations.AuthorityValid)
        { Release(); return; }
        if (!operations.Read(out var current) || !CursorNative.ValidRect(current)) { Release(); return; }
        var target = Target(client, pointer);
        bool oldOwnedMatch = Owned && current.Equals(OwnedRectangle);
        // Only known shapes may be reasserted: unclipped desktop, exact full
        // client, or our exact old clip during resize. Containment alone cannot
        // identify ownership, so a different inner rectangle is also preserved.
        if (!current.Equals(desktop) && !current.Equals(client) && !oldOwnedMatch)
        { Owned = false; Publish(false); return; }
        if (Owned && oldOwnedMatch && target.Equals(OwnedRectangle)) { Publish(true); return; }
        if (!Owned && current.Equals(target)) { Publish(false); return; }

        var previous = Owned ? PreviousRectangle : current;
        if (Owned && oldOwnedMatch)
        {
            // Remove the old owned clip before publishing a new shape. If the
            // broker dies between these steps no obsolete clip can be stranded
            // behind recovery metadata for a different rectangle.
            if (!operations.Clip(previous)) { Publish(true); return; }
        }
        Owned = false; Publish(false);
        if (!operations.AuthorityValid) return;
        OwnedRectangle = target; PreviousRectangle = previous; ++Generation;
        Publish(true); // Recovery record must precede the only new clip mutation.
        if (!operations.AuthorityValid || !operations.Clip(target)) { Publish(false); return; }
        Owned = true;
        if (!operations.AuthorityValid) { Release(); return; }
        // Pointer mode preserves the current pointer position for clicking and
        // dragging. Look mode uses raw relative deltas with a single-pixel lock.
        if (!pointer && !operations.Center(target.Left, target.Top)) { Release(); return; }
        if (!operations.AuthorityValid) { Release(); return; }
        Publish(true);
    }
    internal void Publish(bool armed) => publish(armed, OwnedRectangle, PreviousRectangle, Generation);
    internal void Release()
    {
        if (Owned)
        {
            if (!operations.Read(out var current)) { Publish(true); return; }
            if (current.Equals(OwnedRectangle) && !operations.Clip(PreviousRectangle)) { Publish(true); return; }
        }
        Owned = false; Publish(false);
    }
}
