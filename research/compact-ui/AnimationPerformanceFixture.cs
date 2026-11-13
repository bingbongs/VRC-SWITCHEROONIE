using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Switcheroonie.UI;

internal static class AnimationPerformanceFixture
{
    internal static async Task RunAsync(string path)
    {
        var mascot = new MascotView(preview: true);
        var scene = new ModeSceneView(preview: true);
        var spin = new SpinStarfishView(preview: true);
        mascot.Measure(new Size(72, 72)); mascot.Arrange(new Rect(0, 0, 72, 72)); mascot.UpdateLayout();
        scene.Measure(new Size(390, 46)); scene.Arrange(new Rect(0, 0, 390, 46)); scene.UpdateLayout();
        spin.Measure(new Size(42, 42)); spin.Arrange(new Rect(0, 0, 42, 42)); spin.UpdateLayout();
        // Warm up JIT/cache outside measured phases. This is a detached visual,
        // so results describe animation work, not foreground compositor load.
        mascot.StartPerformanceFixture(); scene.StartPerformanceFixture(); spin.StartPerformanceFixture();
        await Task.Delay(1000); mascot.StopPerformanceFixture(); scene.StopPerformanceFixture(); spin.StopPerformanceFixture();
        var visible = await MeasureAsync(mascot, scene, spin, "visible-animation-path", true);
        var hidden = await MeasureAsync(mascot, scene, spin, "hidden-animation-path", false);
        if (hidden.Frames != 0 || hidden.SceneFrames != 0 || hidden.SpinFrames != 0 || visible.Frames / visible.Seconds > 24.1 || visible.Frames == 0 ||
            visible.SceneFrames != visible.Frames || visible.SpinFrames != visible.Frames || AnimationClock.IsRunning || AnimationClock.SubscriberCount != 0)
            throw new InvalidOperationException("Animation suspension or 24 Hz cap failed");
        var report = new
        {
            schemaVersion = 2,
            category = "Detached WPF animation workload; no HWND, broker, input or registry calls",
            limitation = "Measures mascot, looping switch vectors and starfish property updates together after warmup. Actual visible compositor/GPU rendering and concurrent VRChat performance are not measured.",
            framesPerSecondCap = 24,
            decodedPixelsPerCellAt100Percent = 64,
            hiddenClockStopped = !AnimationClock.IsRunning,
            hiddenSubscribers = AnimationClock.SubscriberCount,
            phases = new[] { visible, hidden }
        };
        File.WriteAllText(Path.GetFullPath(path), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed record Phase(string Name, double Seconds, int Frames, int SceneFrames, int SpinFrames, double CpuMilliseconds, long ManagedAllocatedBytes, long WorkingSetBytes);
    private static async Task<Phase> MeasureAsync(MascotView mascot, ModeSceneView scene, SpinStarfishView spin, string name, bool active)
    {
        using var process = Process.GetCurrentProcess();
        if (active) { mascot.StartPerformanceFixture(); scene.StartPerformanceFixture(); spin.StartPerformanceFixture(); }
        else { mascot.StopPerformanceFixture(); scene.StopPerformanceFixture(); spin.StopPerformanceFixture(); }
        int frames = mascot.AppliedFrames, sceneFrames = scene.AppliedFrames, spinFrames = spin.AppliedFrames;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var cpu = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        await Task.Delay(6000);
        double seconds = watch.Elapsed.TotalSeconds;
        var usedCpu = process.TotalProcessorTime - cpu;
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        int delta = mascot.AppliedFrames - frames;
        int sceneDelta = scene.AppliedFrames - sceneFrames, spinDelta = spin.AppliedFrames - spinFrames;
        mascot.StopPerformanceFixture(); scene.StopPerformanceFixture(); spin.StopPerformanceFixture(); process.Refresh();
        return new Phase(name, seconds, delta, sceneDelta, spinDelta, usedCpu.TotalMilliseconds, bytes, process.WorkingSet64);
    }
}
