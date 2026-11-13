using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Switcheroonie.UI;

internal static class SpinViewportLifecycleTests
{
    internal static void Run(Action<bool, string> check)
    {
        var settings = new SettingsWindow(preview: true);
        var scroll = (ScrollViewer)settings.FindName("MainScroll");
        var icon = (FrameworkElement)settings.FindName("SpinIconHost");
        var iconParent = (Grid)icon.Parent;
        bool oldPathThrows = false;
        try { icon.TransformToAncestor(scroll); }
        catch (InvalidOperationException) { oldPathThrows = true; }
        check(oldPathThrows, "Settings pre-template visual state reproduces the original ancestor exception");
        check(!settings.SpinVisibilityFixture(true), "Settings visibility before template attachment safely disables animation");

        var content = (FrameworkElement)settings.Content; settings.Content = null;
        var root = new Border { Child = content };
        void Layout() { root.Measure(new Size(500, 580)); root.Arrange(new Rect(0, 0, 500, 580)); root.UpdateLayout(); }
        Layout();
        check(scroll.IsAncestorOf(icon), "Offscreen settings layout establishes the real ScrollViewer visual ancestry");
        check(!settings.SpinVisibilityFixture(true), "An icon below the visible viewport stays suspended");
        scroll.ScrollToEnd(); Layout();
        check(settings.SpinVisibilityFixture(true), "Shown-owner state enables only the icon inside the laid-out viewport");
        check(!settings.SpinVisibilityFixture(false), "Hidden-owner state stops icon animation");
        check(!settings.SpinVisibilityFixture(true, minimized: true), "Minimized-owner state stops icon animation");
        check(settings.SpinVisibilityFixture(true), "Reopened-owner state resumes the visible icon safely");

        iconParent.Children.Remove(icon);
        check(!scroll.IsAncestorOf(icon) && !settings.SpinVisibilityFixture(true), "Detached visual suspends animation without an ancestor transform");
        iconParent.Children.Add(icon); Layout(); scroll.ScrollToEnd(); Layout();
        check(scroll.IsAncestorOf(icon) && settings.SpinVisibilityFixture(true), "Reattached and laid-out icon resumes without a stale transform");
        icon.Visibility = Visibility.Collapsed;
        check(!settings.SpinVisibilityFixture(true), "Collapsed icon remains suspended despite retained layout size");
        icon.Visibility = Visibility.Visible; Layout(); scroll.ScrollToEnd(); Layout();
        check(settings.SpinVisibilityFixture(true), "Restored icon is re-evaluated from current viewport geometry");
        check(AnimationClock.SubscriberCount == 0 && !AnimationClock.IsRunning,
            "Preview lifecycle fixtures show no window and leave no animation timer subscribers");
    }
}
