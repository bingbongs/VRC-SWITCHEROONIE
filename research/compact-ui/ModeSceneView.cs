using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Switcheroonie.UI;

internal readonly record struct SceneFrame(double Progress, double Scale, double Sweep);
internal static class SceneTracks
{
    internal static readonly SceneFrame[] Switch = Enumerable.Range(0, 36).Select(i =>
    {
        double t = i / 35d, eased = t * t * (3 - 2 * t);
        return new SceneFrame(eased, 1 + .055 * Math.Sin(t * Math.PI), Math.Sin(t * Math.PI));
    }).ToArray();
}

// Cached outlined glyph geometry and simple vectors, with no raster generation,
// effects or animated bitmap decoding.
internal sealed class ModeSceneView : Viewbox
{
    private readonly Grid _vr, _desktop;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _vrMove = new(), _desktopMove = new();
    private readonly Rectangle _sweep;
    private readonly Action<long> _tick;
    private readonly bool _preview;
    private string? _mode;
    private long _started;
    private bool _allowed = true, _performance;
    internal int AppliedFrames { get; private set; }
    private static readonly Brush Gold = FrozenBrush(248, 195, 80);
    private static readonly Brush Ink = FrozenBrush(16, 17, 19);
    private static readonly Brush Outline = FrozenBrush(255, 239, 178);
    private static readonly Brush GoldFace = MakeGoldFace();
    private static readonly Geometry VrGlyph = Glyph("VR");
    private static readonly Geometry DesktopGlyph = Glyph("DESKTOP");

    internal ModeSceneView(bool preview)
    {
        _preview = preview; _tick = Tick; Stretch = Stretch.Uniform;
        IsHitTestVisible = false; MaxHeight = 46;
        var scene = new Grid { Width = 390, Height = 54, ClipToBounds = true };
        _vr = MakeMode(VrGlyph, 140); _desktop = MakeMode(DesktopGlyph, 314);
        _vr.RenderTransform = _vrMove; _desktop.RenderTransform = _desktopMove;
        scene.Children.Add(_vr); scene.Children.Add(_desktop);
        _sweep = new Rectangle { Width = 3, Height = 46, Fill = Outline, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center, RenderTransform = new TranslateTransform() };
        scene.Children.Add(_sweep);
        scene.RenderTransformOrigin = new Point(.5, .5); scene.RenderTransform = _scale;
        Child = scene;
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
        Unloaded += (_, _) => Stop();
        Settle();
    }
    private static Grid MakeMode(Geometry glyph, double width)
    {
        var view = new Grid { Width = 390, Height = 54 };
        var word = new Grid { Width = width, Height = 43, Margin = new Thickness(0, 0, 0, 3) };
        word.Children.Add(new Path { Data = glyph, Stretch = Stretch.Fill, Fill = Ink, Stroke = Ink, StrokeThickness = 6, StrokeLineJoin = PenLineJoin.Round, RenderTransform = new TranslateTransform(1, 2) });
        word.Children.Add(new Path { Data = glyph, Stretch = Stretch.Fill, Fill = GoldFace, Stroke = Outline, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round });
        word.Children.Add(new Path { Data = glyph, Stretch = Stretch.Fill, Fill = GoldFace, Stroke = Ink, StrokeThickness = .7, StrokeLineJoin = PenLineJoin.Round });
        view.Children.Add(word);
        var glint = FrozenGeometry("M0,-7 L2,-2 L7,0 L2,2 L0,7 L-2,2 L-7,0 L-2,-2 Z");
        foreach (double x in new[] { 195 - width / 2 - 12, 195 + width / 2 + 12 })
            view.Children.Add(new Path { Data = glint, Fill = Outline, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, RenderTransform = new TranslateTransform(x, x < 195 ? 13 : 38) });
        return view;
    }
    internal void SetMode(string? mode, bool animate)
    {
        if (mode is not ("Physical" or "Desktop")) mode = null;
        if (_mode == mode) return;
        string? old = _mode; Stop(); _mode = mode;
        if (animate && !_preview && _allowed && IsVisible && old is not null && mode is not null)
        { _started = Stopwatch.GetTimestamp(); Apply(SceneTracks.Switch[0]); AnimationClock.Subscribe(_tick); }
        else Settle();
    }
    internal void SetAnimationActive(bool allowed) { _allowed = allowed; if (!allowed) Stop(); }
    private void Tick(long now)
    {
        int frame = (int)((now - _started) * (double)AnimationClock.FramesPerSecond / Stopwatch.Frequency);
        if (_performance) { Apply(SceneTracks.Switch[frame % SceneTracks.Switch.Length]); return; }
        if (!_allowed || !IsVisible || frame >= SceneTracks.Switch.Length) { Stop(); return; }
        Apply(SceneTracks.Switch[frame]);
    }
    private void Apply(SceneFrame frame)
    {
        ++AppliedFrames;
        bool toDesktop = _mode == "Desktop";
        _vr.Opacity = toDesktop ? 1 - frame.Progress : frame.Progress;
        _desktop.Opacity = toDesktop ? frame.Progress : 1 - frame.Progress;
        _vrMove.X = toDesktop ? -frame.Progress * 24 : (1 - frame.Progress) * -24;
        _desktopMove.X = toDesktop ? (1 - frame.Progress) * 24 : frame.Progress * 24;
        _scale.ScaleX = _scale.ScaleY = frame.Scale;
        _sweep.Opacity = frame.Sweep * .65;
        ((TranslateTransform)_sweep.RenderTransform).X = -190 + frame.Progress * 380;
    }
    private void Stop() { AnimationClock.Unsubscribe(_tick); Settle(); }
    private void Settle()
    {
        _vr.Opacity = _mode == "Physical" ? 1 : _mode is null ? .15 : 0;
        _desktop.Opacity = _mode == "Desktop" ? 1 : 0;
        _vrMove.X = _desktopMove.X = 0;
        _scale.ScaleX = _scale.ScaleY = 1; _sweep.Opacity = 0;
    }
    internal void SetFixtureTransition(bool toDesktop, int frame)
    { _mode = toDesktop ? "Desktop" : "Physical"; Apply(SceneTracks.Switch[Math.Clamp(frame, 0, SceneTracks.Switch.Length - 1)]); }
    internal void StartPerformanceFixture() { _mode = "Desktop"; _performance = true; _started = Stopwatch.GetTimestamp(); AnimationClock.Subscribe(_tick); }
    internal void StopPerformanceFixture() { _performance = false; Stop(); }
    private static Geometry FrozenGeometry(string value) { var geometry = Geometry.Parse(value); geometry.Freeze(); return geometry; }
    private static Brush FrozenBrush(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private static Geometry Glyph(string text)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Black"), FontStyles.Italic, FontWeights.Black, FontStretches.Normal), 64, Gold, 1);
        var geometry = formatted.BuildGeometry(new Point()); geometry.Freeze(); return geometry;
    }
    private static Brush MakeGoldFace()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(.5, 0), EndPoint = new Point(.5, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 238, 155), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 183, 39), .38));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 248, 211), .49));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(247, 205, 74), .53));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(171, 87, 13), .76));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 216, 95), 1));
        brush.Freeze(); return brush;
    }
}
