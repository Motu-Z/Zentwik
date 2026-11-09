using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zentwik.Core.Services;
using Zentwik.UI.Services;
using Zentwik.UI.ViewModels;

namespace Zentwik.UI;

/// <summary>
/// 离屏截图工具。
/// </summary>
/// <remarks>
/// 用 WPF 自己的 RenderTargetBitmap 渲染，而不是抓屏幕：
/// 抓屏会受窗口遮挡、桌面缩放、多显示器影响，结果不可复现；
/// RenderTargetBitmap 渲染的是控件树，结果只取决于布局与资源。
///
/// RenderTargetBitmap 渲染 Window 会得到空白 —— Window 不是普通的
/// 布局容器，它的背景由系统合成。所以这里取 window.Content 作为渲染目标，
/// 并把 Window 自身的 Background 垫在下面，保证截图包含窗口底色。
/// </remarks>
internal static class ScreenshotHelper
{
    public static void Capture(Window window, string outputPath)
    {
        // 等待布局完成，否则可能截到还没排版的内容
        window.UpdateLayout();

        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            width = (int)window.Width;
            height = (int)window.Height;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        // 垫一层窗口底色（Window 的 Background 不在 Content 树里）
        var canvas = new DrawingVisual();
        using (var dc = canvas.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Transparent, null,
                            new Rect(0, 0, width, height));
        }
        bitmap.Render(canvas);

        // 再渲染窗口内容
        if (window.Content is UIElement content)
        {
            bitmap.Render(content);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    /// <summary>
    /// 离屏截图 + 叠加一个 ToolTip 预览。
    /// </summary>
    /// <remarks>
    /// ToolTip 真实运行时挂在 Popup 里 —— Popup 是独立的顶层窗口，
    /// 不在视觉树中，RenderTargetBitmap 根本拍不到它。
    /// 所以这里手工实例化一个 ToolTip，Measure/Arrange 让它套用模板，
    /// 再单独渲染并贴到截图的左下角，用来校验全局 ToolTip 样式的实际观感。
    /// </remarks>
    public static void CaptureWithTooltip(Window window, string outputPath)
    {
        window.UpdateLayout();

        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            width = (int)window.Width;
            height = (int)window.Height;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        var canvas = new DrawingVisual();
        using (var dc = canvas.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Transparent, null,
                            new Rect(0, 0, width, height));
        }
        bitmap.Render(canvas);

        if (window.Content is UIElement content)
        {
            bitmap.Render(content);
        }

        // ---- ToolTip 预览 ----
        // 给一个有限宽度：Measure(∞) 时 ToolTip 的 DesiredSize.Width 可能是 0，
        // 文字会被压成一条竖条。
        var tip = new ToolTip { Content = "在浏览器中打开项目仓库" };
        tip.Measure(new Size(320, double.PositiveInfinity));
        tip.Arrange(new Rect(0, 0, 320, tip.DesiredSize.Height));
        tip.UpdateLayout();

        int tipW = (int)Math.Ceiling(tip.ActualWidth);
        int tipH = (int)Math.Ceiling(tip.ActualHeight);

        if (tipW > 0 && tipH > 0)
        {
            var tipBitmap = new RenderTargetBitmap(tipW, tipH, 96, 96, PixelFormats.Pbgra32);
            tipBitmap.Render(tip);

            // 单独存一份，方便排查「到底渲染出来没有」
            var tipEncoder = new PngBitmapEncoder();
            tipEncoder.Frames.Add(BitmapFrame.Create(tipBitmap));
            string tipPath = Path.ChangeExtension(outputPath, null) + ".tooltip.png";
            using (var ts = File.Create(tipPath))
            {
                tipEncoder.Save(ts);
            }

            var tipVisual = new DrawingVisual();
            using (var dc = tipVisual.RenderOpen())
            {
                dc.DrawImage(tipBitmap,
                    new Rect(24, height - tipH - 24, tipW, tipH));
            }
            bitmap.Render(tipVisual);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    /// <summary>
    /// 离屏渲染托盘图标预览。
    /// </summary>
    /// <remarks>
    /// 两行：深色任务栏底 + 亮色图标 / 浅色任务栏底 + 暗色图标。
    /// 每行放 16 / 22 / 32 三个**物理像素**尺寸 ——
    /// 16 对应 100% 缩放，22 对应 140%（本机），32 对应 200%。
    /// 矢量渲染，三个尺寸都是 1:1 落点，不该出现任何缩放模糊。
    /// </remarks>
    public static void CaptureTrayIcon(string outputPath)
    {
        var theme = AppThemeService.Instance;
        int[] sizes = { 16, 22, 32 };

        const int pad = 16;
        const int cell = 56;
        const int rowH = 56;

        int width = pad * 2 + cell * sizes.Length;
        int height = pad * 2 + rowH * 2;

        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
        };

        for (int row = 0; row < 2; row++)
        {
            bool darkTaskbar = row == 0;
            var palette = LogoPaletteFactory.BuildForTray(darkTaskbar);

            // 模拟任务栏条
            var bar = new Border
            {
                Background = new SolidColorBrush(darkTaskbar
                    ? Color.FromRgb(0x20, 0x20, 0x20)
                    : Color.FromRgb(0xEC, 0xEC, 0xEC)),
                Width = width - pad * 2,
                Height = 40,
            };
            Canvas.SetLeft(bar, pad);
            Canvas.SetTop(bar, pad + row * rowH);
            canvas.Children.Add(bar);

            var drawing = theme.BuildLogoDrawing(palette.Body, palette.Shadow);

            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                var image = new Image
                {
                    Source = TrayIconRenderer.RenderDrawing(drawing, size),
                    Width = size,
                    Height = size,
                };
                Canvas.SetLeft(image, pad + 24 + i * cell);
                Canvas.SetTop(image, pad + row * rowH + (40 - size) / 2);
                canvas.Children.Add(image);
            }
        }

        canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        canvas.Arrange(new Rect(canvas.DesiredSize));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(canvas.ActualWidth),
            (int)Math.Ceiling(canvas.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string path = Path.ChangeExtension(outputPath, null) + ".trayicon.png";

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 离屏截图 + 叠加一个「方案面板」预览。
    /// </summary>
    /// <remarks>
    /// 方案面板真实运行时挂在 Popup 里。Popup 是独立的顶层窗口，
    /// 不在视觉树中，RenderTargetBitmap 拍不到（与 ToolTip 同一个坑）。
    /// 这里手工实例化一份、套上卡片外框单独渲染，再贴到页头方案条下方，
    /// 用来校验面板的实际排版。
    /// </remarks>
    public static void CaptureWithPopover(Window window, string outputPath)
    {
        window.UpdateLayout();

        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            width = (int)window.Width;
            height = (int)window.Height;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        var canvas = new DrawingVisual();
        using (var dc = canvas.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Transparent, null,
                            new Rect(0, 0, width, height));
        }
        bitmap.Render(canvas);

        if (window.Content is UIElement content)
        {
            bitmap.Render(content);
        }

        // ---- 方案面板预览 ----
        // 用一份临时的调校会话摆出「生效中 / 未保存 / 普通」三种行态，
        // 不写盘、不打 IPC —— 只为了拍得出面板的真实排版。
        var demo = new PlanScope(PlanKind.Tuning);

        demo.Items.Add(new PlanItem("demo-active", "默认调校", true)
        {
            Summary = "16 核 · CO -18~-33 · 4750 MHz"
        });
        demo.Items.Add(new PlanItem("demo-editing", "新调校 2", false)
        {
            Summary = "16 核 · CO -18~-24 · 4500 MHz"
        });
        demo.Items.Add(new PlanItem("demo-idle", "静音长续航", false)
        {
            Summary = "16 核 · CO -40 · 3200 MHz"
        });

        // 第四行停在改名态：这块最容易漏掉确定按钮，截图要能自证。
        demo.Items.Add(new PlanItem("demo-renaming", "新调校 3", false)
        {
            Summary = "16 核 · CO -10 · 4200 MHz",
            RenameText = "新调校 3",
            IsRenaming = true
        });

        demo.SelectCommand.Execute(demo.Items[1]);
        demo.SetUnsaved(true);

        var panel = new Border
        {
            Width = 300,
            Background = (Brush)Application.Current.FindResource("PanelBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 8),
            // 面板是独立可视树，继承不到外层 DataContext —— 不给就只拍出空壳。
            Child = new Views.ProfileOverlay { DataContext = demo },
        };

        panel.Measure(new Size(300, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 300, panel.DesiredSize.Height));
        panel.UpdateLayout();

        int panelW = (int)Math.Ceiling(panel.ActualWidth);
        int panelH = (int)Math.Ceiling(panel.ActualHeight);

        if (panelW > 0 && panelH > 0)
        {
            var panelBitmap = new RenderTargetBitmap(panelW, panelH, 96, 96, PixelFormats.Pbgra32);
            panelBitmap.Render(panel);

            var panelEncoder = new PngBitmapEncoder();
            panelEncoder.Frames.Add(BitmapFrame.Create(panelBitmap));

            string panelPath = Path.ChangeExtension(outputPath, null) + ".popover.png";
            using (var ps = File.Create(panelPath))
            {
                panelEncoder.Save(ps);
            }

            var overlayVisual = new DrawingVisual();
            using (var dc = overlayVisual.RenderOpen())
            {
                dc.DrawImage(panelBitmap, new Rect(296, 64, panelW, panelH));
            }
            bitmap.Render(overlayVisual);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
