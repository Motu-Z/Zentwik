using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Spike;

internal sealed class ChartControl : FrameworkElement
{
    private const int SeriesCount = 8;
    private const int Points = 600;

    private readonly double[][] _series = new double[SeriesCount][];
    private readonly Pen[] _pens = new Pen[SeriesCount];
    private int _tick;

    public ChartControl()
    {
        var palette = new[]
        {
            Color.FromRgb(0x4C, 0x8D, 0xFF), Color.FromRgb(0x4C, 0xC3, 0x8A),
            Color.FromRgb(0xE2, 0xB3, 0x41), Color.FromRgb(0xE5, 0x67, 0x5C),
            Color.FromRgb(0xA9, 0x7B, 0xFF), Color.FromRgb(0x35, 0xC2, 0xB0),
            Color.FromRgb(0xF0, 0x8C, 0x3E), Color.FromRgb(0x9A, 0xA0, 0xA8),
        };

        for (var i = 0; i < SeriesCount; i++)
        {
            _series[i] = new double[Points];
            _pens[i] = new Pen(new SolidColorBrush(palette[i]), 1.5);
            _pens[i].Freeze();
        }
    }

    public void Advance()
    {
        _tick++;
        for (var s = 0; s < SeriesCount; s++)
        {
            var data = _series[s];
            Array.Copy(data, 1, data, 0, Points - 1);
            data[^1] = 50 + 40 * Math.Sin((_tick + s * 20) * 0.05) + Math.Sin(_tick * 0.7 + s) * 4;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();

        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var width = ActualWidth;
        var height = ActualHeight;
        var step = width / (Points - 1);

        for (var s = 0; s < SeriesCount; s++)
        {
            var data = _series[s];
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, ToY(data[0], height)), false, false);
                for (var i = 1; i < Points; i++)
                {
                    ctx.LineTo(new Point(i * step, ToY(data[i], height)), true, false);
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, _pens[s], geometry);
        }

        sw.Stop();
        Metrics.OnFrame(sw.Elapsed.TotalMilliseconds);
    }

    private static double ToY(double value, double height) =>
        height - Math.Clamp(value, 0, 100) / 100d * height;
}
