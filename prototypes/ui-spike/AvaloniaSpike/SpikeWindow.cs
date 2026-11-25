using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Spike;

internal static class SpikeWindow
{
    public static Window Create()
    {
        var hz = 10;
        var seconds = 10d;

        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hz" when i + 1 < args.Length:
                    hz = int.Parse(args[++i]);
                    break;
                case "--seconds" when i + 1 < args.Length:
                    seconds = double.Parse(args[++i]);
                    break;
            }
        }

        var chart = new ChartControl();
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Background = Brushes.Black,
            Title = $"Avalonia spike {hz}Hz",
            Content = chart,
        };

        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000d / hz), DispatcherPriority.Render, (_, _) =>
        {
            chart.Advance();
            chart.InvalidateVisual();
        });

        window.Opened += (_, _) =>
        {
            timer.Start();
            var stop = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal, (_, _) =>
            {
                timer.Stop();
                Metrics.Dump("avalonia", hz, seconds);
                Environment.Exit(0);
            });
            stop.Start();
        };

        return window;
    }
}
