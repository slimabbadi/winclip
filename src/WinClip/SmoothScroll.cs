using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinClip;

/// <summary>Animates wheel input in pixels without disabling list virtualization.</summary>
internal sealed class SmoothScroll
{
    private readonly ListBox _list;
    private readonly Stopwatch _clock = new();
    private ScrollViewer? _viewer;
    private double _target;

    public SmoothScroll(ListBox list)
    {
        _list = list;
        list.PreviewMouseWheel += OnMouseWheel;
        list.IsVisibleChanged += (_, _) => Stop();
    }

    public void Stop()
    {
        CompositionTarget.Rendering -= OnRendering;
        _clock.Reset();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _viewer ??= FindViewer(_list);
        if (_viewer is null || e.Delta == 0)
            return;

        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0 ? _viewer.ViewportHeight : lines * 20.0;
        if (!_clock.IsRunning)
            _target = _viewer.VerticalOffset;
        _target = Math.Clamp(_target - e.Delta / 120.0 * distance, 0, _viewer.ScrollableHeight);
        e.Handled = true;

        if (!SystemParameters.ClientAreaAnimation)
        {
            Stop();
            _viewer.ScrollToVerticalOffset(_target);
        }
        else if (!_clock.IsRunning)
        {
            _clock.Start();
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        _target = Math.Clamp(_target, 0, _viewer!.ScrollableHeight);
        var remaining = _target - _viewer.VerticalOffset;
        if (Math.Abs(remaining) < 1)
        {
            _viewer.ScrollToVerticalOffset(_target);
            Stop();
            return;
        }
        _viewer.ScrollToVerticalOffset(_viewer.VerticalOffset + remaining * (1 - Math.Exp(-seconds * 22)));
    }

    private static ScrollViewer? FindViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer)
                return viewer;
            if (FindViewer(child) is { } nested)
                return nested;
        }
        return null;
    }
}
