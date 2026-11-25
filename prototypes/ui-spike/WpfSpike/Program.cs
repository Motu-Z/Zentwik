using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Spike;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var hz = 10;
        var seconds = 10d;

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

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var chart = new ChartControl();
        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Content = new Grid { Children = { chart } },
            Background = System.Windows.Media.Brushes.Black,
            Title = $"WPF spike {hz}Hz",
        };

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000d / hz),
        };

        timer.Tick += (_, _) =>
        {
            chart.Advance();
            chart.InvalidateVisual();
        };

        window.Loaded += (_, _) =>
        {
            timer.Start();
            var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            stop.Tick += (_, _) =>
            {
                stop.Stop();
                timer.Stop();
                Metrics.Dump("wpf", hz, seconds);
                app.Shutdown();
            };
            stop.Start();
        };

        app.Run(window);
    }
}
