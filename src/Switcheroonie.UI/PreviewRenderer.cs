using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Switcheroonie.UI;

internal static class PreviewRenderer
{
    // Offscreen fixture: no Show, Loaded, broker, input capture, or hotkey registration.
    // Illustrative data demonstrates layout only and is labelled inside every image.
    internal static void Render(string path, double width, double height, double dpi, bool scrollBottom = false, bool advancedPad = false, bool offline = false)
    {
        var window = new MainWindow();
        window.SetPreviewStatus(offline);
        if (advancedPad) window.ExpandPreviewPad();
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.Margin = new Thickness(0);
        var canvas = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(14, 23, 28)),
            Padding = new Thickness(28), Child = content
        };
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        if (scrollBottom)
        {
            window.ScrollPreviewToBottom();
            canvas.UpdateLayout();
        }
        var scale = dpi / 96;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
