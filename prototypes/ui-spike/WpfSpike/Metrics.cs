using System.Diagnostics;
using System.IO;

namespace Spike;

internal static class Metrics
{
    private static readonly List<double> RenderMs = [];
    private static readonly List<double> FrameIntervals = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastFrameAt;
    private static double _firstFrameAt;
    private static int _frames;

    public static void OnFrame(double elapsedMs)
    {
        RenderMs.Add(elapsedMs);
        var now = Clock.Elapsed.TotalMilliseconds;
        if (_firstFrameAt == 0)
        {
            _firstFrameAt = now;
        }
        else
        {
            FrameIntervals.Add(now - _lastFrameAt);
        }

        _lastFrameAt = now;
        _frames++;
    }

    public static void Dump(string label, int hz, double runSeconds)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();

        var startup = _firstFrameAt;
        var cpuSeconds = process.TotalProcessorTime.TotalSeconds;
        var rss = process.WorkingSet64 / (1024d * 1024d);

        var sorted = RenderMs.Order().ToArray();
        var intervals = FrameIntervals.Order().ToArray();

        var lines = new List<string>
        {
            $"mode={label}",
            $"targetHz={hz}",
            $"runSeconds={runSeconds:0.#}",
            $"frames={_frames}",
            $"effectiveHz={_frames / runSeconds:0.0}",
            $"renderMs_p50={Pct(sorted, 0.50):0.000}",
            $"renderMs_p95={Pct(sorted, 0.95):0.000}",
            $"renderMs_max={sorted[^1]:0.000}",
            $"intervalMs_p50={(intervals.Length > 0 ? Pct(intervals, 0.50) : 0):0.00}",
            $"intervalMs_p95={(intervals.Length > 0 ? Pct(intervals, 0.95) : 0):0.00}",
            $"cpuSeconds={cpuSeconds:0.00}",
            $"cpuPercentOfOneCore={cpuSeconds / runSeconds * 100:0.0}",
            $"rssMb={rss:0.0}",
            $"startupToFirstFrameMs={startup:0}",
        };

        var path = Path.Combine(AppContext.BaseDirectory, $"result-{label}-{hz}.txt");
        File.WriteAllLines(path, lines);
        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }
    }

    private static double Pct(double[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
}
