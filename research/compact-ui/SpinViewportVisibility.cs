using System.Windows;
using System.Windows.Controls;

namespace Switcheroonie.UI;

internal static class SpinViewportVisibility
{
    internal static bool Allows(bool ownerVisible, FrameworkElement icon, ScrollViewer viewport, bool? fixtureIconVisible = null)
    {
        // A logical ScrollViewer child is not necessarily a visual descendant:
        // this is normal before its template is built or while it is detached.
        if (!ownerVisible || !(fixtureIconVisible ?? icon.IsVisible) || !viewport.IsAncestorOf(icon) ||
            icon.RenderSize.Width <= 0 || icon.RenderSize.Height <= 0 ||
            !double.IsFinite(viewport.ViewportWidth) || !double.IsFinite(viewport.ViewportHeight) ||
            viewport.ViewportWidth <= 0 || viewport.ViewportHeight <= 0) return false;
        try
        {
            var bounds = icon.TransformToAncestor(viewport).TransformBounds(new Rect(icon.RenderSize));
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return false;
            bounds.Intersect(new Rect(0, 0, viewport.ViewportWidth, viewport.ViewportHeight));
            return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (InvalidOperationException)
        {
            // Reparenting/retemplating can invalidate a visual transform during
            // the lifecycle callback. Loaded/ScrollChanged/status will retry.
            return false;
        }
    }
}
