using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Spike;

internal sealed class ChartControl : Control
{
    private const int SeriesCount = 8;
    private const int PointCount = 600;

    private readonly double[][] _series = new double[SeriesCount][];
    private readonly IBrush[] _brushes = new IBrush[SeriesCount];
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
            _series[i] = new double[PointCount];
            _brushes[i] = new SolidColorBrush(palette[i]);
            _pens[i] = new Pen(_brushes[i], 1.5);
        }
    }

    public void Advance()
    {
        _tick++;
        for (var s = 0; s < SeriesCount; s++)
        {
            var data = _series[s];
            Array.Copy(data, 1, data, 0, PointCount - 1);
            data[^1] = 50 + 40 * Math.Sin((_tick + s * 20) * 0.05) + Math.Sin(_tick * 0.7 + s) * 4;
        }
    }

    public override void Render(DrawingContext context)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        var bounds = Bounds;
        context.FillRectangle(Brushes.Black, new Rect(0, 0, bounds.Width, bounds.Height));

        var step = bounds.Width / (PointCount - 1);
        for (var s = 0; s < SeriesCount; s++)
        {
            var data = _series[s];
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, ToY(data[0], bounds.Height)), false);
                for (var i = 1; i < PointCount; i++)
                {
                    ctx.LineTo(new Point(i * step, ToY(data[i], bounds.Height)));
                }

                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, _pens[s], geometry);
        }

        started.Stop();
        Metrics.OnFrame(started.Elapsed.TotalMilliseconds);
        base.Render(context);
    }

    private static double ToY(double value, double height) =>
        height - Math.Clamp(value, 0, 100) / 100d * height;
}
