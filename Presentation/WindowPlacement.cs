using System.Windows;
using UpscaleHelper.Infrastructure;

namespace UpscaleHelper.Presentation;

/// <summary>Remembers a window's size, position and maximized state between launches.</summary>
internal sealed class WindowPlacement(string directory)
{
    private readonly LocalJsonFile<Bounds> file = new(directory, "window.json");
    private WindowState lastVisibleState;

    public void Restore(Window window)
    {
        lastVisibleState = window.WindowState;
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized) lastVisibleState = window.WindowState;
        };

        if (file.Read() is not { } saved || !ValidSize(saved.Width, saved.Height)) return;

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        double width = Fit(saved.Width, window.MinWidth, window.MaxWidth, screen.Width);
        // Fall back to the default position if the saved title bar is outside the desktop.
        bool restorePosition = double.IsFinite(saved.Left) && double.IsFinite(saved.Top) &&
            HasReachableTitleBar(screen, saved.Left, saved.Top, width);
        window.Width = restorePosition ? width : Fit(saved.Width, window.MinWidth, window.MaxWidth, SystemParameters.WorkArea.Width);
        window.Height = Fit(saved.Height, window.MinHeight, window.MaxHeight,
            restorePosition ? screen.Height : SystemParameters.WorkArea.Height);

        if (restorePosition)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = saved.Left;
            window.Top = saved.Top;
        }
        if (saved.Maximized) window.WindowState = lastVisibleState = WindowState.Maximized;
    }

    public void Save(Window window)
    {
        Rect bounds = window.RestoreBounds;
        if (bounds.IsEmpty || !ValidSize(bounds.Width, bounds.Height) ||
            !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)) return;

        WindowState state = window.WindowState == WindowState.Minimized ? lastVisibleState : window.WindowState;
        file.Write(new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, state == WindowState.Maximized));
    }

    private static bool ValidSize(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;

    private static double Fit(double size, double minimum, double maximum, double available) =>
        Math.Clamp(size, minimum, Math.Max(minimum, Math.Min(maximum, available)));

    private static bool HasReachableTitleBar(Rect screen, double left, double top, double width)
    {
        var visible = Rect.Intersect(screen, new Rect(left, top, width, 32));
        return !visible.IsEmpty && visible.Width >= Math.Min(120, width) && visible.Height >= 16;
    }

    private sealed record Bounds(double Left, double Top, double Width, double Height, bool Maximized);
}
