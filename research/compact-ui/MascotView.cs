using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Switcheroonie.UI;

internal readonly record struct MascotFrame(int Sprite, double X, double Y, double ScaleX, double ScaleY, double Angle, string? Effect = null);

internal static class MascotTracks
{
    internal const int FramesPerSecond = 24;
    internal static readonly MascotFrame[] Idle = Enumerable.Range(0, 28).Select(i =>
    {
        double bounce = (1 - Math.Cos(i * Math.Tau / 28)) / 2;
        return new MascotFrame(0, 0, -3 * bounce, 1, 1 + .035 * bounce, 0);
    }).ToArray();
    internal static readonly IReadOnlyDictionary<Celebration, MascotFrame[]> Celebrations =
        Enum.GetValues<Celebration>().ToDictionary(x => x, Build);
    private static MascotFrame[] Build(Celebration kind) => Enumerable.Range(0, 42).Select<int, MascotFrame>(i =>
    {
        double t = i / (double)FramesPerSecond;
        return kind switch
        {
            Celebration.RunBonk => t < .5
                ? new(1, t * 48, -Math.Abs(Math.Sin(t * Math.Tau * 5)) * 4, 1 + .06 * Math.Sin(t * Math.Tau * 5), 1 - .07 * Math.Sin(t * Math.Tau * 5), Math.Sin(t * Math.Tau * 5) * 5)
                : t < .85 ? new(2, 24, 0, .84 + .1 * (t - .5) / .35, 1.12 - .1 * (t - .5) / .35, -18 * (1 - (t - .5) / .35), "✦")
                : new(4, 24 * Math.Max(0, 1 - (t - .85) / .5), -Math.Abs(Math.Sin((t - .85) * Math.Tau * 2)) * 2, 1, 1, 0),
            Celebration.FlipThumb => t < .95
                ? new(3, 0, -Math.Sin(Math.Min(1, t / .95) * Math.PI) * 20, 1, 1, t / .95 * 360)
                : new(4, 0, -Math.Abs(Math.Sin((t - .95) * Math.Tau * 2)) * 3, 1, 1, 0, t < 1.35 ? "★" : null),
            Celebration.SelfMunch => t < .65
                ? new(5, 0, 0, 1 + .035 * Math.Sin(t * Math.Tau * 5), 1 - .14 * Math.Abs(Math.Sin(t * Math.Tau * 5)), 0, "nom")
                : t < 1.05 ? new(5, 0, 0, Math.Max(.08, 1 - (t - .65) / .4), Math.Max(.08, 1 - (t - .65) / .4), (t - .65) / .4 * 35)
                : new(4, 0, 0, Math.Min(1, .08 + (t - 1.05) / .35), Math.Min(1, .08 + (t - 1.05) / .35), 35 * Math.Max(0, 1 - (t - 1.05) / .35), t < 1.4 ? "✦" : null),
            Celebration.RocketSpin => t < .95
                ? new(3, Math.Sin(t * Math.Tau) * 4, -Math.Sin(Math.Min(1, t / .95) * Math.PI) * 20, 1 - Math.Sin(t / .95 * Math.PI) * .25, 1, -t / .95 * 360, "✧")
                : new(4, 0, -Math.Abs(Math.Sin((t - .95) * Math.Tau * 3)) * 3, 1, 1, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }).ToArray();
}

internal sealed class MascotView : Grid
{
    private readonly Image _sprite;
    private readonly TextBlock _effect = new() { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.Gold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly RotateTransform _rotate = new();
    private readonly TranslateTransform _move = new();
    private static readonly Dictionary<int, BitmapSource[]> SpriteCache = new();
    private BitmapSource[] _cells = null!;
    private readonly Action<long> _tick;
    private readonly CelebrationBag _bag = new();
    private readonly bool _preview;
    private MascotFrame[] _track = MascotTracks.Idle;
    private int _frame, _lastSprite = -1;
    private long _trackStarted;
    private bool _celebrating;
    private bool _allowed = true;
    private string? _lastEffect;
    internal int AppliedFrames { get; private set; }
    internal Celebration? LastCelebration { get; private set; }

    internal MascotView(bool preview)
    {
        _preview = preview; _tick = Tick;
        Width = Height = 72; IsHitTestVisible = false;
        _sprite = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(.5, .72) };
        RenderOptions.SetBitmapScalingMode(_sprite, BitmapScalingMode.NearestNeighbor);
        var transforms = new TransformGroup(); transforms.Children.Add(_scale); transforms.Children.Add(_rotate); transforms.Children.Add(_move);
        _sprite.RenderTransform = transforms;
        Children.Add(_sprite); Children.Add(_effect);
        SetDpi(96);
        IsVisibleChanged += (_, _) => UpdateRunning();
        Unloaded += (_, _) => Suspend();
        Loaded += (_, _) => UpdateRunning();
    }
    internal void SetDpi(double dpi)
    {
        int pixels = dpi <= 96 ? 64 : dpi <= 144 ? 96 : dpi <= 192 ? 128 : 192;
        if (!SpriteCache.TryGetValue(pixels, out var cells))
        {
            var atlas = new BitmapImage(); atlas.BeginInit(); atlas.CacheOption = BitmapCacheOption.OnLoad;
            atlas.UriSource = new Uri("pack://application:,,,/Assets/mascot-atlas.clean.png"); atlas.DecodePixelWidth = pixels * 3; atlas.EndInit(); atlas.Freeze();
            cells = Enumerable.Range(0, 6).Select(i =>
            {
                var cell = new CroppedBitmap(atlas, new Int32Rect(i % 3 * pixels, i / 3 * pixels, pixels, pixels)); cell.Freeze(); return (BitmapSource)cell;
            }).ToArray();
            SpriteCache.Add(pixels, cells);
        }
        _cells = cells; _lastSprite = -1; Apply(_track[Math.Min(_frame, _track.Length - 1)]);
    }
    internal void SetAnimationActive(bool allowed) { _allowed = allowed; UpdateRunning(); }
    private void UpdateRunning()
    {
        if (_preview || !_allowed || !IsVisible) { Suspend(); return; }
        _track = MascotTracks.Idle; _frame = 0; _trackStarted = Stopwatch.GetTimestamp(); _celebrating = false; Apply(_track[0]); AnimationClock.Subscribe(_tick);
    }
    private void Suspend()
    {
        AnimationClock.Unsubscribe(_tick); _track = MascotTracks.Idle; _frame = 0; _trackStarted = 0; _celebrating = false; Apply(_track[0]);
    }
    internal void Celebrate()
    {
        if (_preview || !_allowed || !IsVisible) return;
        LastCelebration = _bag.Next(); _track = MascotTracks.Celebrations[LastCelebration.Value]; _frame = 0; _trackStarted = Stopwatch.GetTimestamp(); _celebrating = true;
        Apply(_track[0]); AnimationClock.Subscribe(_tick);
    }
    private void Tick(long now)
    {
        // The shared clock caps updates; missed display frames are skipped.
        int position = (int)((now - _trackStarted) * (double)MascotTracks.FramesPerSecond / Stopwatch.Frequency);
        if (_celebrating && position >= _track.Length)
        {
            _track = MascotTracks.Idle; _trackStarted = now; _celebrating = false; position = 0;
        }
        _frame = position % _track.Length;
        Apply(_track[_frame]); AppliedFrames++;
    }
    private void Apply(MascotFrame pose)
    {
        if (_lastSprite != pose.Sprite) { _sprite.Source = _cells[pose.Sprite]; _lastSprite = pose.Sprite; }
        _move.X = pose.X; _move.Y = pose.Y; _scale.ScaleX = pose.ScaleX; _scale.ScaleY = pose.ScaleY; _rotate.Angle = pose.Angle;
        if (_lastEffect != pose.Effect)
        {
            _lastEffect = pose.Effect; _effect.Text = pose.Effect ?? "";
            _effect.Visibility = pose.Effect is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    internal void SetFixtureFrame(Celebration? celebration, int index)
    {
        var track = celebration is null ? MascotTracks.Idle : MascotTracks.Celebrations[celebration.Value];
        Apply(track[Math.Clamp(index, 0, track.Length - 1)]);
    }
    // Performance fixture uses the same property/timer path on a detached,
    // measured visual. It never creates an HWND or a foreground window.
    internal void StartPerformanceFixture() { _track = MascotTracks.Idle; _frame = 0; _trackStarted = Stopwatch.GetTimestamp(); _celebrating = false; AnimationClock.Subscribe(_tick); }
    internal void StopPerformanceFixture() => Suspend();
}
