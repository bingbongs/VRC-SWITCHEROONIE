using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Switcheroonie.UI;

internal sealed class SpinStarfishView : Viewbox
{
    private readonly RotateTransform _rotation = new();
    private readonly Image _sprite;
    private static readonly Dictionary<int, BitmapSource> SpriteCache = new();
    private readonly Action<long> _tick;
    private readonly bool _preview;
    private bool _playing, _allowed = true, _performance;
    private long _start;
    internal int AppliedFrames { get; private set; }
    internal static readonly double[] Angles = Enumerable.Range(0, 36).Select(i => i * 10d).ToArray();
    internal SpinStarfishView(bool preview = false)
    {
        _preview = preview; _tick = Tick; Width = Height = 42; IsHitTestVisible = false;
        _sprite = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, RenderTransform = _rotation, RenderTransformOrigin = new Point(.5, .5) };
        RenderOptions.SetBitmapScalingMode(_sprite, BitmapScalingMode.NearestNeighbor);
        Child = _sprite; SetDpi(96);
        IsVisibleChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) => { SetDpi(VisualTreeHelper.GetDpi(this).PixelsPerInchX); UpdateRunning(); };
        Unloaded += (_, _) => Stop();
    }
    internal void SetDpi(double dpi)
    {
        int pixels = dpi <= 96 ? 64 : dpi <= 144 ? 96 : 128;
        if (!SpriteCache.TryGetValue(pixels, out var sprite))
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri("pack://application:,,,/Assets/mascot-starfish.png"); bitmap.DecodePixelWidth = pixels;
            bitmap.EndInit(); bitmap.Freeze(); sprite = bitmap; SpriteCache.Add(pixels, sprite);
        }
        _sprite.Source = sprite;
    }
    internal void SetPlaying(bool playing) { _playing = playing; UpdateRunning(); }
    internal void SetAnimationActive(bool allowed) { _allowed = allowed; UpdateRunning(); }
    private void UpdateRunning()
    {
        if (_preview || !_playing || !_allowed || !IsVisible) { Stop(); return; }
        if (_start == 0) _start = Stopwatch.GetTimestamp();
        AnimationClock.Subscribe(_tick);
    }
    private void Tick(long now)
    {
        if (!_performance && (!_playing || !_allowed || !IsVisible)) { Stop(); return; }
        _rotation.Angle = Angles[AnimationClock.Frame(_start, now, Angles.Length)];
        ++AppliedFrames;
    }
    private void Stop() { AnimationClock.Unsubscribe(_tick); _start = 0; _rotation.Angle = 0; }
    internal void SetFixtureFrame(int frame) { Stop(); _rotation.Angle = Angles[Math.Clamp(frame, 0, Angles.Length - 1)]; }
    internal void StartPerformanceFixture() { _performance = true; _start = Stopwatch.GetTimestamp(); AnimationClock.Subscribe(_tick); }
    internal void StopPerformanceFixture() { _performance = false; Stop(); }
}
