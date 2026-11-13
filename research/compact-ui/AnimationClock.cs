using System.Diagnostics;
using System.Windows.Threading;

namespace Switcheroonie.UI;

// One timer for visible lightweight graphics; subscribers allocate only when
// joining/leaving. All drawing uses elapsed time rather than catch-up updates.
internal static class AnimationClock
{
    internal const int FramesPerSecond = 24;
    private static readonly List<Action<long>> Subscribers = new(4);
    private static readonly DispatcherTimer Timer = new(DispatcherPriority.Background)
    { Interval = TimeSpan.FromMilliseconds(Math.Ceiling(1000d / FramesPerSecond)) };
    private static long _lastTick;
    internal static bool IsRunning => Timer.IsEnabled;
    internal static int SubscriberCount => Subscribers.Count;
    static AnimationClock() => Timer.Tick += (_, _) =>
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastTick != 0 && now - _lastTick < Stopwatch.Frequency / FramesPerSecond) return;
        _lastTick = now;
        // Views only remove themselves while ticking. Reverse iteration keeps
        // the remaining callbacks stable without a per-frame copied array.
        for (int i = Subscribers.Count - 1; i >= 0; --i) Subscribers[i](now);
    };
    internal static void Subscribe(Action<long> callback)
    {
        if (!Subscribers.Contains(callback)) Subscribers.Add(callback);
        if (!Timer.IsEnabled) { _lastTick = 0; Timer.Start(); }
    }
    internal static void Unsubscribe(Action<long> callback)
    {
        Subscribers.Remove(callback);
        if (Subscribers.Count == 0) { Timer.Stop(); _lastTick = 0; }
    }
    internal static int Frame(long start, long now, int length) =>
        (int)((now - start) * (double)FramesPerSecond / Stopwatch.Frequency) % length;
}
