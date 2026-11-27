using System.Windows;
using System.Windows.Media;
using Zentwik.Desktop.ViewModels;

namespace Zentwik.Desktop;

public partial class MainWindow : Window
{
    private const int HistoryLength = 120;

    private readonly MainViewModel _viewModel;
    private double[] _samples = [];

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.HistoryChanged += OnHistoryChanged;
        Closed += (_, _) => viewModel.HistoryChanged -= OnHistoryChanged;
    }

    private void OnHistoryChanged(double[] samples)
    {
        _samples = samples;
        RenderChart();
    }

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => RenderChart();

    private void OnQuickNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var index) && index >= 0 && index < MainTabs.Items.Count)
        {
            MainTabs.SelectedIndex = index;
        }
    }

    // 最近值贴右：采样不足时不会从左侧拉出一条无意义的斜线。
    private void RenderChart()
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var points = new PointCollection(_samples.Length);
        var step = width / (HistoryLength - 1);
        var offset = width - (Math.Max(_samples.Length, 1) - 1) * step;

        for (var i = 0; i < _samples.Length; i++)
        {
            var value = Math.Clamp(_samples[i], 0d, 100d);
            points.Add(new Point(offset + i * step, height - height * value / 100d));
        }

        CpuLine.Points = points;
    }
}
