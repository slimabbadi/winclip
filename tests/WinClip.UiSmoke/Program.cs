using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinClip;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var list = new ListBox { ItemsSource = Enumerable.Range(0, 10_000).Select(i => $"Clip {i}").ToArray() };
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var smooth = new SmoothScroll(list);
        var window = new Window { Content = list, Width = 400, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            Pump(250);
            var viewer = Find<ScrollViewer>(list)!;
            Require(viewer.ScrollableHeight > 0, "List has scrollable content");
            Wheel(list, -120);
            Pump(50);
            var intermediate = viewer.VerticalOffset;
            Pump(600);
            var settled = viewer.VerticalOffset;
            Require(settled > 0, "Wheel scroll moves content");
            if (SystemParameters.ClientAreaAnimation && SystemParameters.WheelScrollLines > 0)
                Require(intermediate > 0 && intermediate < settled, "Scroll animates through intermediate offsets");
            Wheel(list, -120);
            Wheel(list, 120);
            Pump(600);
            Require(Math.Abs(viewer.VerticalOffset - settled) < 2, "Rapid wheel reversal preserves accumulated target");
            Wheel(list, 12000);
            Pump(700);
            Require(viewer.VerticalOffset == 0, "Scrolling beyond top clamps to zero");
            Wheel(list, -120);
            Pump(50);
            smooth.Stop();
            Pump(50);
            var stopped = viewer.VerticalOffset;
            Pump(300);
            Require(viewer.VerticalOffset == stopped, "Stopping animation prevents later movement");
            var realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
            Require(realized < 100, $"Virtualization retained ({realized} of 10000 rows realized)");
            Console.WriteLine("PASS: smooth scrolling smoke checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            smooth.Stop();
            window.Close();
        }
    }

    private static void Wheel(ListBox list, int delta) => list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = UIElement.PreviewMouseWheelEvent });

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
