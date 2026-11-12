using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Zentwik.UI.Services;
using Zentwik.UI.ViewModels;
using ScenarioTrigger = Zentwik.Core.Services.ScenarioTrigger;

namespace Zentwik.UI.Services;

/// <summary>集成自检：按版式契约逐条断言（运行：dotnet run -- --itest）。</summary>
/// <remarks>契约类断言令牌 / 尺寸 / 样式三者一致；行为类断言渲染、命中测试、命令语义、虚拟化。全部强类型调用，不用反射。</remarks>
public static class IntegrationTest
{
    /// <summary>参考稿的默认强调色。</summary>
    private static readonly Color DefaultAccent = Color.FromRgb(0x38, 0xBD, 0xF8);

    private static int _failures;

    private static StringBuilder? _sink;

    /// <summary>外壳上设的文本格式化模式（TextOptions 是继承属性，脱离窗口的页面读不到）。</summary>
    private static TextFormattingMode _shellFormattingMode = TextFormattingMode.Ideal;

    public static int Run() => Run(null);

    /// <summary>执行集成自检。<paramref name="output"/> 非空时把结果写入该缓冲区。</summary>
    public static int Run(StringBuilder? output)
    {
        _failures = 0;
        _sink = output;

        var theme = AppThemeService.Instance;

        // 进入自检前先把状态归位，避免上一条诊断参数留下的残留影响断言。
        theme.SetThemeMode(AppTheme.Dark);
        theme.SetAccentMode(AccentSource.Custom);
        theme.SetAccentColor(DefaultAccent);

        SectionShell();
        SectionTokensDark();
        SectionTokensLight();
        SectionTokenParity();
        SectionThemeSwitch(theme);
        SectionAccent(theme);
        SectionThemePersistence();
        SectionSystemAccent(theme);
        SectionStyleContract();
        SectionPages();
        SectionHitTest();
        SectionProcessDrawer();
        SectionCommands();
        SectionOverlays();
        SectionVirtualization();
        SectionPerformance();

        // 收尾：把全局状态恢复到默认，避免影响同一进程里后续的截图/性能探针。
        theme.SetThemeMode(AppTheme.Dark);
        theme.SetAccentMode(AccentSource.Custom);
        theme.SetAccentColor(DefaultAccent);

        return _failures;
    }

    // 0. 外壳

    private static void SectionShell()
    {
        Say("=== 0. 外壳契约（可折叠侧栏 + 64 顶栏）===");

        Check("侧栏收缩宽 = 72", LayoutMetrics.SidebarWidth == 72, $"{LayoutMetrics.SidebarWidth}");
        Check("侧栏展开宽 = 220", LayoutMetrics.SidebarExpandedWidth == 220,
              $"{LayoutMetrics.SidebarExpandedWidth}");
        Check("导航项高 = 48", LayoutMetrics.NavItemHeight == 48, $"{LayoutMetrics.NavItemHeight}");
        Check("顶栏高 = 64", LayoutMetrics.TopBarHeight == 64, $"{LayoutMetrics.TopBarHeight}");
        Check("页面左右内边距 = 32", LayoutMetrics.PageSidePadding == 32,
              $"{LayoutMetrics.PageSidePadding}");
        Check("面板圆角 = 6", LayoutMetrics.PanelRadius == 6, $"{LayoutMetrics.PanelRadius}");
        Check("窗口最小宽 = 展开侧栏 + 952 内容",
              Math.Abs(LayoutMetrics.WindowMinWidth - (LayoutMetrics.SidebarExpandedWidth + 952)) < 0.5,
              $"{LayoutMetrics.WindowMinWidth}");
        Check("窗口最小尺寸 = 1172×700",
              LayoutMetrics.WindowMinWidth == 1172 && LayoutMetrics.WindowMinHeight == 700,
              $"{LayoutMetrics.WindowMinWidth}×{LayoutMetrics.WindowMinHeight}");

        MainWindow? shell = null;

        try
        {
            shell = new MainWindow();
        }
        catch (Exception ex)
        {
            Check("外壳可以构造", false, ex.Message);
            return;
        }

        try
        {
            Check("窗口最小宽与常量一致", Math.Abs(shell.MinWidth - LayoutMetrics.WindowMinWidth) < 0.5,
                  $"{shell.MinWidth}");
            Check("窗口最小高与常量一致", Math.Abs(shell.MinHeight - LayoutMetrics.WindowMinHeight) < 0.5,
                  $"{shell.MinHeight}");

            // 顶栏即系统 caption：整条 64 都能拖动，所以按钮必须显式标可命中。
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(shell);
            Check("WindowChrome 拖动区高 = 64",
                  chrome is not null && Math.Abs(chrome.CaptionHeight - 64) < 0.5,
                  chrome is null ? "无 WindowChrome" : $"{chrome.CaptionHeight}");
            Check("不自绘系统标题栏按钮", chrome is not null && !chrome.UseAeroCaptionButtons);

            if (shell.Content is not FrameworkElement shellRoot)
            {
                Check("外壳内容可布局", false);
                return;
            }

            Render(shellRoot, 1240, 820);

            _shellFormattingMode = TextOptions.GetTextFormattingMode(shellRoot);

            var navHost = FindByName<StackPanel>(shellRoot, "NavHost");
            Check("导航栏存在", navHost is not null);

            if (navHost is not null)
            {
                Check("导航项 = 8 个", navHost.Children.Count == 8, $"{navHost.Children.Count}");

                var first = navHost.Children.OfType<RadioButton>().FirstOrDefault();
                Check("导航项 48 高、铺满侧栏宽",
                      first is not null
                      && Math.Abs(first.ActualHeight - 48) < 0.5
                      && first.ActualWidth >= LayoutMetrics.SidebarWidth - 1.5,
                      first is null ? "无导航项" : $"{first.ActualWidth}×{first.ActualHeight}");

                double navLeft = navHost.TranslatePoint(new Point(0, 0), shellRoot).X;
                Check("导航栏通栏（左缘 = 0，不再居中）",
                      Math.Abs(navLeft) < 0.5, $"{navLeft:F1}");
            }

            var contentHost = FindByName<ContentControl>(shellRoot, "ContentHost");
            Check("内容区存在", contentHost is not null);

            if (contentHost is not null)
            {
                double left = contentHost.TranslatePoint(new Point(0, 0), shellRoot).X;
                Check("内容区左缘 = 72（侧栏宽度）", Math.Abs(left - 72) < 0.5, $"{left:F1}");

                // 没有底部浮条：内容区必须一路铺到窗口底边。
                double bottom = contentHost.TranslatePoint(new Point(0, 0), shellRoot).Y
                                + contentHost.ActualHeight;
                Check("内容区铺到窗口底边（无底部浮条）",
                      Math.Abs(bottom - shellRoot.ActualHeight) < 0.5, $"底边 {bottom:F1}");
            }

            // 窗口三键：最小化 / 最大化 / 关闭，压在顶栏右侧。
            var winStyle = Application.Current.Resources["WindowButtonStyle"] as Style;
            var closeStyle = Application.Current.Resources["WindowCloseButtonStyle"] as Style;

            var winButtons = FindAll<Button>(shellRoot)
                .Where(b => (winStyle is not null && b.Style == winStyle)
                            || (closeStyle is not null && b.Style == closeStyle))
                .ToList();

            Check("窗口控制三键存在", winButtons.Count == 3, $"实际 {winButtons.Count}");

            if (winButtons.Count == 3)
            {
                Check("三键都在顶栏高度内",
                      winButtons.All(b => b.TranslatePoint(new Point(0, 0), shellRoot).Y
                                          + b.ActualHeight <= 64.5));

                Check("三键贴右缘",
                      winButtons.All(b => b.TranslatePoint(new Point(0, 0), shellRoot).X
                                          + b.ActualWidth <= shellRoot.ActualWidth - 2));
            }

            // 可折叠侧栏：72 ↔ 220
            var sidebar = FindByName<Border>(shellRoot, "Sidebar");
            var toggleBtn = FindByName<Button>(shellRoot, "SidebarToggle");
            var arrow = toggleBtn?.Content as System.Windows.Shapes.Path;
            var firstNav = navHost?.Children.OfType<RadioButton>().FirstOrDefault();

            TextBlock? navLabel = null;
            if (firstNav is not null)
            {
                firstNav.ApplyTemplate();
                navLabel = firstNav.Template.FindName("Label", firstNav) as TextBlock;
            }

            var shellState = ShellState.Instance;
            bool wasExpanded = shellState.IsExpanded;

            var brandLogo = FindByName<Image>(shellRoot, "BrandLogo");
            var wordmark = FindByName<StackPanel>(shellRoot, "BrandWordmark");
            var chip = firstNav?.Template.FindName("Chip", firstNav) as Border;
            var selBar = firstNav?.Template.FindName("Bar", firstNav) as Border;

            Check("侧栏 logo 用应用图标（BrandLogoImage），不是白底字母块",
                  brandLogo?.Source is DrawingImage
                  && ReferenceEquals(brandLogo.Source, Application.Current.Resources["BrandLogoImage"]),
                  brandLogo?.Source?.GetType().Name ?? "无 logo");

            try
            {
                Check("侧栏初始收缩 = 72",
                      sidebar is not null && Math.Abs(sidebar.ActualWidth - 72) < 0.5,
                      sidebar?.ActualWidth.ToString("F1") ?? "无侧栏");
                Check("收缩态品牌字标与 CPU 型号一起收起",
                      wordmark is not null && wordmark.Visibility == Visibility.Collapsed,
                      wordmark?.Visibility.ToString() ?? "无字标");
                Check("收缩态图标块 40×40（用户原本的图标观感）",
                      chip is not null
                      && Math.Abs(chip.ActualWidth - 40) < 0.5
                      && Math.Abs(chip.ActualHeight - 40) < 0.5,
                      chip is null ? "无图标块" : $"{chip.ActualWidth}×{chip.ActualHeight}");
                Check("收缩态不画贴边选中竖条（选中靠 40×40 圆角块）",
                      selBar is not null && selBar.ActualWidth < 0.5,
                      selBar?.ActualWidth.ToString("F1") ?? "无竖条");
                Check("收缩态导航文字不显示",
                      navLabel is not null && navLabel.Opacity < 0.01,
                      navLabel?.Opacity.ToString("F2") ?? "无文字");
                Check("收缩态靠悬浮提示补全名称",
                      firstNav is not null
                      && ToolTipService.GetIsEnabled(firstNav)
                      && Equals(firstNav.ToolTip, "调校"),
                      firstNav?.ToolTip?.ToString() ?? "无提示");

                // 点图标只切页，不替用户展开侧栏
                shell.NavigateTo("Process");
                shell.UpdateLayout();
                Check("程序化导航不撑开侧栏", shellState.IsCollapsed, $"{shellState.IsExpanded}");

                var processNav = navHost?.Children.OfType<RadioButton>()
                    .FirstOrDefault(r => (r.Tag as string) == "Process");
                processNav?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent));
                shell.UpdateLayout();
                Check("点导航项也不撑开侧栏", shellState.IsCollapsed, $"{shellState.IsExpanded}");

                // 箭头基准朝右（= 展开方向），展开后转 180° 朝左（= 收起方向）
                Check("折叠箭头基准角度 = 0（朝右）",
                      arrow?.RenderTransform is RotateTransform rtBase && Math.Abs(rtBase.Angle) < 0.01,
                      (arrow?.RenderTransform as RotateTransform)?.Angle.ToString("F0") ?? "无变换");
                Check("折叠箭头在 caption 区内可命中",
                      toggleBtn is not null
                      && System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(toggleBtn));

                shellState.IsExpanded = true;
                shell.SnapSidebar();
                Render(shellRoot, 1240, 820);

                Check("展开后侧栏 = 220",
                      sidebar is not null && Math.Abs(sidebar.ActualWidth - 220) < 0.5,
                      sidebar?.ActualWidth.ToString("F1") ?? "无侧栏");
                Check("展开后箭头翻转 180°（朝左）",
                      arrow?.RenderTransform is RotateTransform rtOpen && Math.Abs(rtOpen.Angle - 180) < 0.01,
                      (arrow?.RenderTransform as RotateTransform)?.Angle.ToString("F0") ?? "无变换");
                Check("展开后导航文字可见",
                      navLabel is not null && navLabel.Opacity > 0.99,
                      navLabel?.Opacity.ToString("F2") ?? "无文字");
                Check("展开后品牌字标与 CPU 型号显形",
                      wordmark is not null && wordmark.Visibility == Visibility.Visible,
                      wordmark?.Visibility.ToString() ?? "无字标");
                Check("展开后 CPU 型号就在侧栏里",
                      wordmark is not null
                      && FindAll<TextBlock>(wordmark).Any(t => t.Text == CpuInfo.Name),
                      CpuInfo.Name);
                Check("展开后选中项左侧竖条出现",
                      selBar is not null && Math.Abs(selBar.ActualWidth - 3) < 0.5,
                      selBar?.ActualWidth.ToString("F1") ?? "无竖条");
                Check("展开后不再重复弹提示",
                      firstNav is not null && !ToolTipService.GetIsEnabled(firstNav),
                      $"{firstNav is not null && ToolTipService.GetIsEnabled(firstNav)}");

                if (contentHost is not null)
                {
                    double left = contentHost.TranslatePoint(new Point(0, 0), shellRoot).X;
                    Check("展开后内容区左缘 = 220", Math.Abs(left - 220) < 0.5, $"{left:F1}");
                }

                shellState.IsExpanded = false;
                shell.SnapSidebar();
                Render(shellRoot, 1240, 820);

                Check("收起后回到 72",
                      sidebar is not null && Math.Abs(sidebar.ActualWidth - 72) < 0.5,
                      sidebar?.ActualWidth.ToString("F1") ?? "无侧栏");
                Check("收起后导航文字重新隐藏",
                      navLabel is not null && navLabel.Opacity < 0.01,
                      navLabel?.Opacity.ToString("F2") ?? "无文字");

                // 箭头按钮真的能收起（走的是 Click 处理器）
                shellState.IsExpanded = true;
                toggleBtn?.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check("点箭头即收起", shellState.IsCollapsed, $"{shellState.IsExpanded}");
            }
            finally
            {
                shellState.IsExpanded = wasExpanded;
                shell.SnapSidebar();
            }
        }
        finally
        {
            try
            {
                shell.Close();
            }
            catch
            {
                // 从未 Show 过的窗口关闭时可能抛，无副作用
            }
        }
    }

    // 1. 深色令牌

    private static void SectionTokensDark()
    {
        Say("\n=== 1. 深色令牌（取值照抄参考 HTML）===");

        var res = Application.Current.Resources;

        AssertBrush(res, "WindowBgBrush", "FF09090B");
        AssertBrush(res, "SidebarBgBrush", "FF111114", "侧栏 = 面板色（参考 HTML 的 --panel）");
        AssertBrush(res, "PanelBrush", "FF111114");
        AssertBrush(res, "PanelHoverBrush", "FF18181C");
        AssertBrush(res, "LineBrush", "FF27272A");
        AssertBrush(res, "LineFaintBrush", "08FFFFFF", "行分隔线 = rgba(255,255,255,0.03)");
        AssertBrush(res, "TextPrimaryBrush", "FFF4F4F5");
        AssertBrush(res, "TextSecondaryBrush", "FF71717A");
        AssertBrush(res, "TextTertiaryBrush", "FF52525B");
        AssertBrush(res, "ControlBgBrush", "FF111114");
        AssertBrush(res, "ControlBorderBrush", "FF27272A");
        AssertBrush(res, "ControlBorderHoverBrush", "FF52525B");
        AssertBrush(res, "DangerBrush", "FFF87171");
        AssertBrush(res, "DangerSubtleBrush", "1AF87171");
        AssertBrush(res, "DangerBorderBrush", "4DF87171", "危险区外框 = rgba(248,113,113,0.3)");
        AssertBrush(res, "RowDashBrush", "0DFFFFFF", "单列页行间虚线 = rgba(255,255,255,0.05)");
        AssertBrush(res, "SuccessBrush", "FF4ADE80");
        AssertBrush(res, "WarningBrush", "FFFBBF24");
        AssertBrush(res, "OverlayScrimBrush", "CC000000");
        AssertBrush(res, "ToolTipBgBrush", "FF27272A", "悬浮提示底");
        AssertBrush(res, "ToolTipBorderBrush", "FF3F3F46", "悬浮提示描边");
        AssertBrush(res, "LogDebugBrush", "FFA78BFA");
        AssertBrush(res, "LogInfoBrush", "FF60A5FA");
        AssertBrush(res, "LogWarnBrush", "FFFBBF24");
        AssertBrush(res, "LogErrorBrush", "FFF87171");
        AssertBrush(res, "LogFatalBrush", "FFEF4444");
        AssertBrush(res, "LogWarnTextBrush", "FFFDE68A");
        AssertBrush(res, "LogErrorTextBrush", "FFFCA5A5");

        Check("等宽字体族已定义", res["AppMonoFontFamily"] is FontFamily,
              (res["AppMonoFontFamily"] as FontFamily)?.Source ?? "<无>");
    }

    // 2. 浅色令牌

    private static void SectionTokensLight()
    {
        Say("\n=== 2. 浅色令牌（同语言反相）===");

        var light = LoadDictionary("Themes/Tokens.Light.xaml");

        AssertDicBrush(light, "WindowBgBrush", "FFFFFFFF");
        AssertDicBrush(light, "PanelBrush", "FFF8F8F9");
        AssertDicBrush(light, "LineBrush", "FFE4E4E7");
        AssertDicBrush(light, "LineFaintBrush", "08000000", "浅色下是 3% 黑");
        AssertDicBrush(light, "TextPrimaryBrush", "FF18181B");
        AssertDicBrush(light, "TextSecondaryBrush", "FF71717A");
        AssertDicBrush(light, "TextTertiaryBrush", "FFA1A1AA");
        AssertDicBrush(light, "DangerBrush", "FFDC2626");
        AssertDicBrush(light, "SuccessBrush", "FF16A34A");
        AssertDicBrush(light, "OverlayScrimBrush", "B3FFFFFF");
        AssertDicBrush(light, "SidebarBgBrush", "FFF8F8F9");
        AssertDicBrush(light, "ToolTipBgBrush", "FFFFFFFF");
        AssertDicBrush(light, "ToolTipBorderBrush", "FFD4D4D8");
    }

    // 3. 两套字典的键集合

    private static void SectionTokenParity()
    {
        Say("\n=== 3. 明暗字典键集合必须逐条对齐 ===");

        var dark = LoadDictionary("Themes/Tokens.Dark.xaml");
        var light = LoadDictionary("Themes/Tokens.Light.xaml");

        var darkKeys = dark.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToList();
        var lightKeys = light.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToList();

        Check("键数量一致", darkKeys.Count == lightKeys.Count,
              $"深 {darkKeys.Count} / 浅 {lightKeys.Count}");

        var missing = darkKeys.Except(lightKeys).ToList();
        var extra = lightKeys.Except(darkKeys).ToList();

        Check("浅色没有漏键", missing.Count == 0, string.Join(", ", missing));
        Check("浅色没有多余键", extra.Count == 0, string.Join(", ", extra));

        // 旧体系的键必须彻底消失 —— 留着会让人以为还有第二个主题体系。
        var legacy = new[]
        {
            "CardBgBrush", "BorderColorBrush", "TextMetaBrush", "CardShadowEffect",
            "PageFadeBrush", "ToolTipShadowEffect", "AccentColorBrush",
        };

        var res = Application.Current.Resources;
        var alive = legacy.Where(k => res[k] is not null).ToList();
        Check("旧样式键已彻底移除", alive.Count == 0, string.Join(", ", alive));
    }

    // 4. 明暗热切换

    private static void SectionThemeSwitch(AppThemeService theme)
    {
        Say("\n=== 4. 明暗热切换 ===");

        var res = Application.Current.Resources;
        var events = new List<bool>();

        EventHandler<bool> handler = (_, isDark) => events.Add(isDark);
        theme.ThemeChanged += handler;

        try
        {
            Check("初始 IsDark = true", theme.IsDark);

            theme.SetThemeMode(AppTheme.Light);
            Check("切浅色后 IsDark = false", !theme.IsDark);
            Check("切浅色后 WindowBg = #FFFFFF", BrushColor(res, "WindowBgBrush") == "FFFFFFFF",
                  BrushColor(res, "WindowBgBrush"));
            Check("切浅色后 Panel = #F8F8F9", BrushColor(res, "PanelBrush") == "FFF8F8F9",
                  BrushColor(res, "PanelBrush"));
            Check("ThemeChanged 只发一次且值为 false", events.Count == 1 && !events[0],
                  $"次数 {events.Count}");

            theme.SetThemeMode(AppTheme.Dark);
            Check("切回深色后 WindowBg = #09090B", BrushColor(res, "WindowBgBrush") == "FF09090B",
                  BrushColor(res, "WindowBgBrush"));
            Check("切回深色又发一次", events.Count == 2 && events[1],
                  $"次数 {events.Count}");

            // 同模式重复设置不应重复发事件（否则整棵树会被无谓重刷）
            theme.SetThemeMode(AppTheme.Dark);
            Check("同模式重复设置不发事件", events.Count == 2, $"次数 {events.Count}");
        }
        finally
        {
            theme.ThemeChanged -= handler;
            theme.SetThemeMode(AppTheme.Dark);
        }
    }

    // 5. 强调色

    /// <summary>外观偏好的落盘往返：主题模式、强调色来源、自定义色都得能存能读</summary>
    private static void SectionThemePersistence()
    {
        Say("\n=== 5b. 外观偏好落盘 ===");

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"zentwik-theme-{Guid.NewGuid():N}.json");

        ThemePreferenceStore.PathOverride = path;

        try
        {
            ThemePreferenceStore.Save(new ThemePreference
            {
                Mode = AppTheme.Light,
                AccentMode = AccentSource.System,
                AccentColor = "#112233",
            });

            bool loaded = ThemePreferenceStore.TryLoad(out ThemePreference back);

            Check("外观：主题 / 色彩 / 强调色三项都能存能读",
                  loaded
                  && back.Mode == AppTheme.Light
                  && back.AccentMode == AccentSource.System
                  && string.Equals(back.AccentColor, "#112233", StringComparison.OrdinalIgnoreCase),
                  loaded ? $"{back.Mode}/{back.AccentMode}/{back.AccentColor}" : "读失败");

            // 改色就走 PersistAppearance（PersistenceEnabled 打开时才写）。
            AppThemeService service = AppThemeService.Instance;
            service.PersistenceEnabled = true;

            try
            {
                service.SetAccentColor(Color.FromRgb(0x11, 0x22, 0x33));

                bool persisted = ThemePreferenceStore.TryLoad(out ThemePreference fromService);

                Check("外观：改强调色即落盘",
                      persisted
                      && string.Equals(fromService.AccentColor, "#112233", StringComparison.OrdinalIgnoreCase),
                      fromService.AccentColor);
            }
            finally
            {
                service.PersistenceEnabled = false;
                service.SetAccentColor(DefaultAccent);
            }

            System.IO.File.WriteAllText(path, "{ 坏掉的 json");

            Check("外观：配置坏掉时回落默认外观且不抛",
                  !ThemePreferenceStore.TryLoad(out ThemePreference fallback)
                  && fallback.Mode == AppTheme.Dark,
                  fallback.Mode.ToString());
        }
        finally
        {
            ThemePreferenceStore.PathOverride = null;

            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception)
            {
                // 临时文件删不掉不影响断言。
            }
        }
    }

    private static void SectionAccent(AppThemeService theme)
    {
        Say("\n=== 5. 强调色注入（替换画刷实例，规避冻结）===");

        var res = Application.Current.Resources;

        theme.SetAccentColor(Color.FromRgb(0xFF, 0x00, 0x00));
        Check("强调色 = #FF0000", BrushColor(res, "AccentBrush") == "FFFF0000",
              BrushColor(res, "AccentBrush"));

        theme.SetAccentColor(Color.FromRgb(0x00, 0xFF, 0x00));
        Check("二次改色 = #00FF00（换实例生效）", BrushColor(res, "AccentBrush") == "FF00FF00",
              BrushColor(res, "AccentBrush"));

        // 淡强调色的不透明度跟明暗走：深色 0.26 / 浅色 0.16
        var subtleDark = res["AccentSubtleBrush"] as SolidColorBrush;
        Check("深色淡强调色 opacity = 0.26",
              subtleDark is not null && Math.Abs(subtleDark.Opacity - 0.26) < 0.001,
              subtleDark?.Opacity.ToString("F2") ?? "<无>");

        theme.SetThemeMode(AppTheme.Light);
        var subtleLight = res["AccentSubtleBrush"] as SolidColorBrush;
        Check("浅色淡强调色 opacity = 0.16",
              subtleLight is not null && Math.Abs(subtleLight.Opacity - 0.16) < 0.001,
              subtleLight?.Opacity.ToString("F2") ?? "<无>");
        theme.SetThemeMode(AppTheme.Dark);

        // 品牌色必须等于算法结果 —— 它是派生值，不能有第二套口径
        theme.SetAccentColor(DefaultAccent);
        var expectedBrand = ColorMath.DeriveBrandColor(DefaultAccent, isDark: true);
        Check("品牌色 = 算法派生值（深色）",
              BrushColor(res, "BrandAccentBrush") == Argb(expectedBrand),
              $"{BrushColor(res, "BrandAccentBrush")} vs {Argb(expectedBrand)}");

        theme.SetAccentMode(AccentSource.Custom);
        theme.SetAccentColor(DefaultAccent);
        Check("回到默认蓝 = #38BDF8", BrushColor(res, "AccentBrush") == "FF38BDF8",
              BrushColor(res, "AccentBrush"));
    }

    // 6. 系统色

    private static void SectionSystemAccent(AppThemeService theme)
    {
        Say("\n=== 6. 系统色读取（注册表）===");

        var res = Application.Current.Resources;
        Color sysAccent = SystemTheme.GetSystemAccentColor();

        Say($"    系统浅色={SystemTheme.IsSystemLightTheme()}  系统强调色={sysAccent}");

        Check("系统强调色不是兜底值（注册表可读）", sysAccent != DefaultAccent, sysAccent.ToString());

        Color? palette = ReadPaletteMainAccent();
        if (palette is Color fromPalette)
        {
            // DWM 与 AccentPalette 两条路径必须指向同一个颜色，否则说明读到别处去了
            Check("系统强调色与 AccentPalette 主色一致",
                  sysAccent.R == fromPalette.R
                  && sysAccent.G == fromPalette.G
                  && sysAccent.B == fromPalette.B,
                  $"{sysAccent} vs {fromPalette}");
        }
        else
        {
            Say("    （本机没有 AccentPalette，跳过交叉校验）");
        }

        theme.SetAccentMode(AccentSource.System);
        Check("切到系统色彩后强调色 = 注册表值",
              BrushColor(res, "AccentBrush") == Argb(sysAccent),
              $"{BrushColor(res, "AccentBrush")} vs {Argb(sysAccent)}");

        theme.SetAccentMode(AccentSource.Custom);
        theme.SetAccentColor(DefaultAccent);
    }

    // 7. 样式实际值 vs 布局常量

    private static void SectionStyleContract()
    {
        Say("\n=== 7. 样式实际值必须与布局常量一致 ===");

        var res = Application.Current.Resources;

        var topBar = res["TopBarStyle"] as Style;
        Check("TopBarStyle.Height = 64",
              topBar is not null && Equals(SetterValue(topBar, FrameworkElement.HeightProperty), 64.0),
              $"{SetterValue(topBar, FrameworkElement.HeightProperty)}");

        var thickness = SetterValue(topBar, Border.BorderThicknessProperty) as Thickness?;
        Check("TopBarStyle 只有底边 1px 线",
              thickness is Thickness t && t.Left == 0 && t.Top == 0 && t.Right == 0 && t.Bottom == 1,
              thickness?.ToString() ?? "<无>");

        var panel = res["PanelStyle"] as Style;
        var radius = SetterValue(panel, Border.CornerRadiusProperty) as CornerRadius?;
        Check("PanelStyle 圆角 = 6", radius is CornerRadius r && Math.Abs(r.TopLeft - 6) < 0.01,
              radius?.ToString() ?? "<无>");

        var row = res["RowStyle"] as Style;
        Check("RowStyle 行高下限 = 60",
              row is not null && Equals(SetterValue(row, FrameworkElement.MinHeightProperty), 60.0),
              $"{SetterValue(row, FrameworkElement.MinHeightProperty)}");

        var rowLine = SetterValue(row, Border.BorderThicknessProperty) as Thickness?;
        Check("RowStyle 底线 1px",
              rowLine is Thickness rl && rl.Bottom == 1 && rl.Top == 0,
              rowLine?.ToString() ?? "<无>");

        var action = res["ActionButtonStyle"] as Style;
        Check("ActionButtonStyle 高 = 32",
              action is not null && Equals(SetterValue(action, FrameworkElement.HeightProperty), 32.0),
              $"{SetterValue(action, FrameworkElement.HeightProperty)}");

        var nav = res["NavRailItemStyle"] as Style;
        Check("NavRailItemStyle 高 = 48、不锁宽（通栏）",
              Equals(SetterValue(nav, FrameworkElement.HeightProperty), 48.0)
              && SetterValue(nav, FrameworkElement.WidthProperty) is null,
              $"{SetterValue(nav, FrameworkElement.WidthProperty)}×{SetterValue(nav, FrameworkElement.HeightProperty)}");

        var toggle = res["ToggleSwitchStyle"] as Style;
        Check("开关 = 44×24",
              Equals(SetterValue(toggle, FrameworkElement.WidthProperty), 44.0)
              && Equals(SetterValue(toggle, FrameworkElement.HeightProperty), 24.0),
              $"{SetterValue(toggle, FrameworkElement.WidthProperty)}×{SetterValue(toggle, FrameworkElement.HeightProperty)}");

        var title = res["PageTitleStyle"] as Style;
        Check("PageTitleStyle 字号 = 20",
              title is not null && Equals(SetterValue(title, TextBlock.FontSizeProperty), 20.0),
              $"{SetterValue(title, TextBlock.FontSizeProperty)}");

        var mono = res["MonoLabelStyle"] as Style;
        Check("MonoLabelStyle 字号 = 11（等宽小标签）",
              mono is not null && Equals(SetterValue(mono, TextBlock.FontSizeProperty), 11.0),
              $"{SetterValue(mono, TextBlock.FontSizeProperty)}");

        var desc = res["RowDescStyle"] as Style;
        Check("RowDescStyle 字号 = 12",
              desc is not null && Equals(SetterValue(desc, TextBlock.FontSizeProperty), 12.0),
              $"{SetterValue(desc, TextBlock.FontSizeProperty)}");
    }

    // 8. 八页渲染

    private static readonly (string Name, Func<FrameworkElement> Factory)[] Pages =
    {
        ("调校", () => new Views.TuningView()),
        ("进程", () => new Views.ProcessView()),
        ("电源", () => new Views.PowerView()),
        ("场景", () => new Views.ScenariosView()),
        ("日志", () => new Views.LogsView()),
        ("通用", () => new Views.SettingsView()),
        ("行为", () => new Views.BehaviorView()),
        ("高级", () => new Views.AdvancedView()),
    };

    private static void SectionPages()
    {
        Say("\n=== 8. 八页渲染（顶栏 / 左缘 / 无浮条 / 不越界 / 无阴影）===");

        var res = Application.Current.Resources;
        var topBarStyle = res["TopBarStyle"] as Style;
        var titleStyle = res["PageTitleStyle"] as Style;

        foreach (var (name, factory) in Pages)
        {
            FrameworkElement page;

            try
            {
                page = factory();
                Render(page, 1168, 820);
            }
            catch (Exception ex)
            {
                Check($"{name}：可以构造并布局", false, ex.GetType().Name + " " + ex.Message);
                continue;
            }

            Check($"{name}：可以构造并布局", true);

            var bar = topBarStyle is null ? null : FindByStyle<Border>(page, topBarStyle);
            Check($"{name}：有顶栏", bar is not null);

            if (bar is null)
            {
                continue;
            }

            Check($"{name}：顶栏高 = 64", Math.Abs(bar.ActualHeight - 64) < 0.5,
                  $"{bar.ActualHeight:F1}");

            Check($"{name}：顶栏贴顶",
                  Math.Abs(bar.TranslatePoint(new Point(0, 0), page).Y) < 0.5);

            var titleBlock = titleStyle is null ? null : FindByStyle<TextBlock>(page, titleStyle);

            if (titleBlock is not null)
            {
                double tx = titleBlock.TranslatePoint(new Point(0, 0), page).X;
                Check($"{name}：页标题左缘 = 32（与卡片同一条线）", Math.Abs(tx - 32) < 1, $"{tx:F1}");
            }

            // 顶栏所在网格必须只有「64 + 剩余」两行 —— 有第三行就是浮条回来了
            if (VisualTreeHelper.GetParent(bar) is Grid host)
            {
                bool twoRows = host.RowDefinitions.Count == 2
                               && Math.Abs(host.RowDefinitions[0].Height.Value - 64) < 0.5
                               && host.RowDefinitions[1].Height.IsStar;

                Check($"{name}：顶栏下没有第二条页脚行", twoRows,
                      $"{host.RowDefinitions.Count} 行");

                var body = host.Children.OfType<FrameworkElement>()
                    .FirstOrDefault(c => Grid.GetRow(c) == 1);

                if (body is not null)
                {
                    double bottom = body.TranslatePoint(new Point(0, 0), page).Y + body.ActualHeight;
                    double expected = page.ActualHeight - body.Margin.Bottom;

                    Check($"{name}：内容铺到页底（无底部浮条）",
                          Math.Abs(bottom - expected) < 0.5,
                          $"底边 {bottom:F1} / 期望 {expected:F1}");
                }
            }

            // 最小窗口宽度下的最坏情况：内容区 = 1172 - 220（侧栏展开）= 952
            Render(page, 952, 700);

            var overflow = LayoutAudit.FindOverflow(page);
            Check($"{name}：952 宽（侧栏展开时的最小内容区）下没有控件越界",
                  overflow.Count == 0, string.Join(" | ", overflow));

            var effects = ResidentEffects(page);
            Check($"{name}：常驻元素不挂 Effect", effects.Count == 0, string.Join(", ", effects));
        }

        // 调校页：CPU 型号已挪进侧栏，右侧状态区只留 SMU
        {
            var tuning = new Views.TuningView();
            Render(tuning, 1168, 820);

            var texts = FindAll<TextBlock>(tuning).Select(t => t.Text).ToList();

            Check("调校：顶栏不再重复 CPU 型号",
                  !texts.Any(t => t.Contains("Ryzen", StringComparison.OrdinalIgnoreCase)),
                  string.Join(" | ", texts.Where(t => t.Contains("Ryzen"))));
            Check("调校：段头不再挂区间标签（用户要求删掉）",
                  !texts.Any(t => t.Contains("-50 ~ +50")), string.Join(" | ", texts.Where(t => t.Contains("~"))));
            Check("调校：状态区不再列「逻辑核心」", !texts.Contains("逻辑核心"));
            Check("调校：状态区不再列「电压偏移范围」", !texts.Contains("电压偏移范围"));
            Check("调校：不再单独列出 SMU 状态行（改为整页不可用）", !texts.Contains("SMU 状态"));

            // 8 列自然排布：16 核正好两行，最后一行不满也不强求
            var grid = FindAll<UniformGrid>(tuning).FirstOrDefault();
            Check("调校：核心格 8 列排布", grid?.Columns == 8, $"列数 {(grid is null ? "无 UniformGrid" : grid.Columns)}");

            // SMU 不可用：整页遮罩 + 内容压暗，回读 / 应用同时禁用
            var available = new Views.TuningView();
            Render(available, 1168, 820);
            var scrim = FindAll<Border>(available)
                .FirstOrDefault(b => ReferenceEquals(b.Background, res["OverlayScrimBrush"]));
            Check("调校：SMU 可用时不出遮罩",
                  scrim is null || scrim.Visibility != Visibility.Visible);

            var offline = new Views.TuningView();
            if (offline.DataContext is TuningViewModel offlineVm)
            {
                offlineVm.SmuAvailable = false;
            }

            Render(offline, 1168, 820);
            var offlineScrim = FindAll<Border>(offline)
                .FirstOrDefault(b => ReferenceEquals(b.Background, res["OverlayScrimBrush"]));
            Check("调校：SMU 不可用时整页遮罩可见",
                  offlineScrim is { Visibility: Visibility.Visible });

            var offlineTexts = FindAll<TextBlock>(offline).Select(t => t.Text).ToList();
            Check("调校：遮罩上写明原因",
                  offlineTexts.Contains("SMU 接口不可用"), string.Join(" | ", offlineTexts));
            Check("调校：不可用时应用按钮禁用",
                  FindAll<Button>(offline).Any(b => b.IsEnabled == false));
        }

        // 单栏页（行为 / 高级）：内容居中，不贴左
        foreach (var (name, factory) in new (string, Func<FrameworkElement>)[]
                 {
                     ("行为", () => new Views.BehaviorView()),
                     ("高级", () => new Views.AdvancedView()),
                 })
        {
            var page = factory();
            Render(page, 1168, 820);

            var scrollerStyle = res["PageScrollStyle"] as Style;
            var scroller = FindAll<ScrollViewer>(page)
                .FirstOrDefault(s => scrollerStyle is not null && s.Style == scrollerStyle);
            var content = scroller?.Content as FrameworkElement;

            if (content is null)
            {
                Check($"{name}：内容块可定位", false);
                continue;
            }

            double left = content.TranslatePoint(new Point(0, 0), page).X;
            double right = page.ActualWidth - left - content.ActualWidth;

            // 滚动容器右侧预留 6px 滚动条槽，不算「留白不均」
            Check($"{name}：内容居中（左右留白一致）",
                  Math.Abs(left - right) <= 7,
                  $"左 {left:F1} / 右 {right:F1}（含 6px 滚动条槽）");
        }
    }

    // 9. 命中测试

    // 9b. 进程页右侧抽屉

    private static void SectionProcessDrawer()
    {
        Say("\n=== 9b. 进程页抽屉（可收纳 / 双页签 / 顶栏入口）===");

        var view = new Views.ProcessView();
        Render(view, 1168, 820);

        if (view.DataContext is not ProcessViewModel vm)
        {
            Check("抽屉：持有视图模型", false);
            return;
        }

        Check("抽屉：持有视图模型", true);
        Check("抽屉：默认展开", vm.IsDrawerOpen);
        Check("抽屉：默认停在「已保存规则」页签", vm.IsRulesTab);

        var host = FindByName<Border>(view, "DrawerHost");
        Check("抽屉：容器在视觉树里", host is not null);

        if (host is not null)
        {
            Check("抽屉：展开宽度 = 320",
                  Math.Abs(host.ActualWidth - LayoutMetrics.ProcessDrawerWidth) < 1,
                  $"{host.ActualWidth:F1}");
        }

        // 视觉树里的元素即使 Visibility=Collapsed 也找得到，所以要判 Visibility 而不是 null
        FrameworkElement? entry = FindByAutomationName(view, "展开规则与掩码");
        Check("抽屉：展开时顶栏不出现入口",
              entry is null || entry.Visibility != Visibility.Visible);

        vm.IsDrawerOpen = false;
        view.SnapDrawer(false);
        view.UpdateLayout();

        if (host is not null)
        {
            Check("抽屉：收起后宽度归零", host.ActualWidth < 1, $"{host.ActualWidth:F1}");
        }

        entry = FindByAutomationName(view, "展开规则与掩码");
        Check("抽屉：收起后顶栏出现「规则与掩码」入口",
              entry is not null && entry.Visibility == Visibility.Visible);

        vm.IsDrawerOpen = true;
        view.SnapDrawer(true);
        view.UpdateLayout();

        vm.IsMasksTab = true;
        view.UpdateLayout();

        Check("抽屉：切到掩码页签后规则页让位", !vm.IsRulesTab && vm.IsMasksTab);
        Check("抽屉：掩码条目非空", vm.DrawerMasks.Count > 0, vm.DrawerMasks.Count.ToString());

        bool topologyOk = true;
        foreach (MaskPreviewItem item in vm.DrawerMasks)
        {
            if (string.IsNullOrEmpty(item.Topology))
            {
                topologyOk = false;
            }
        }

        Check("抽屉：每个掩码条目都带核心拓扑", topologyOk);
    }

    private static void SectionHitTest()
    {
        Say("\n=== 9. 顶栏动作按钮可命中（WindowChrome 内最容易点不到）===");

        var cases = new (string Page, Func<FrameworkElement> Factory, string[] Names)[]
        {
            ("调校", () => new Views.TuningView(), new[] { "回读", "应用参数" }),
            ("进程", () => new Views.ProcessView(), new[] { "清空所有规则", "解除全部绑核" }),
            ("电源", () => new Views.PowerView(), new[] { "回读", "应用电源策略" }),
            ("场景", () => new Views.ScenariosView(), new[] { "启用自动切换", "新建规则" }),
            ("日志", () => new Views.LogsView(), new[] { "控制中心", "打开日志目录", "清空日志" }),
            ("通用", () => new Views.SettingsView(), new[] { "打开项目源码", "检查更新" }),
            ("行为", () => new Views.BehaviorView(), new[] { "不启动", "启动后台服务与界面" }),
            ("高级", () => new Views.AdvancedView(), new[] { "启动异常自动拦截", "运行自检并导出" }),
        };

        foreach (var (page, factory, names) in cases)
        {
            FrameworkElement root;

            try
            {
                root = factory();
                Render(root, 1168, 820);
            }
            catch (Exception ex)
            {
                Check($"{page}：可以构造", false, ex.Message);
                continue;
            }

            foreach (string automationName in names)
            {
                CheckHitTestableIn(root, $"{page}/{automationName}", automationName);
            }
        }
    }

    // 10. 命令语义

    private static void SectionCommands()
    {
        Say("\n=== 10. 命令语义 ===");

        // 调校：改一格 → 脏；放弃 → 干净
        var tuning = new TuningViewModel();
        Check("调校：初始不脏", !tuning.IsDirty);
        Check("调校：核心数 = 16", tuning.CpuCoreCount == 16, $"{tuning.CpuCoreCount}");
        Check("调校：核心项数 = 16", tuning.CoreMargins.Count == 16, $"{tuning.CoreMargins.Count}");

        int before = tuning.CoreMargins[0].Margin;
        tuning.CoreMargins[0].IncrementCommand.Execute(null);
        Check("调校：加一格后 IsDirty = true", tuning.IsDirty,
              $"值 {before}→{tuning.CoreMargins[0].Margin}");
        Check("调校：加一格后数值 +1", tuning.CoreMargins[0].Margin == before + 1,
              $"{before}→{tuning.CoreMargins[0].Margin}");

        tuning.CoreMargins[0].Margin = before;
        Check("调校：改回原值不算脏", !tuning.IsDirty, $"值回到 {before}");

        tuning.CoreMargins[0].IncrementCommand.Execute(null);

        tuning.CoreMargins[0].IncrementCommand.Execute(null);
        Check("调校：脏值下面板拿到「保存至」", PlanHub.Tuning.HasUnsavedChanges);
        Check("调校：脏值时「保存至」可点（不要求 SMU 可写）",
              PlanHub.Tuning.CanSavePending?.Invoke() == true);

        tuning.DiscardCommand.Execute(null);
        Check("调校：放弃后 IsDirty = false", !tuning.IsDirty);
        Check("调校：放弃后数值还原", tuning.CoreMargins[0].Margin == before,
              $"{tuning.CoreMargins[0].Margin}");

        // FMax 走步进，不许越界
        int fmax = tuning.FMaxMHz;
        tuning.StepFMaxUpCommand.Execute(null);
        Check("调校：FMax 步进 = +10",
              tuning.FMaxMHz == Math.Min(tuning.FMaxMaximum, fmax + tuning.FMaxStepMHz),
              $"{fmax}→{tuning.FMaxMHz}");
        tuning.FMaxMHz = tuning.FMaxMaximum;
        tuning.StepFMaxUpCommand.Execute(null);
        Check("调校：FMax 上边界夹住", tuning.FMaxMHz == tuning.FMaxMaximum, $"{tuning.FMaxMHz}");
        tuning.DiscardCommand.Execute(null);

        // 读不到值的核：不能折成 0
        // 0 是「不偏移」，是用户会以为生效了的正常值。读失败必须是另一个样子。
        var blind = new CoreMarginItem(3, 0, -50, 50, 1);
        blind.MarkUnavailable();
        Check("调校：读不到的核显示占位符", blind.EditText == "—", blind.EditText);
        Check("调校：读不到的核不可编辑", blind.IsReadOnly && blind.IsUnavailable);

        // 下发时还原成 int.MinValue，让 Worker 跳过这一核而不是写 0
        Check("调校：读不到的核下发值是 int.MinValue",
              blind.PayloadValue() == int.MinValue, $"{blind.PayloadValue()}");

        blind.EditText = "-20";
        Check("调校：读不到的核拒绝输入",
              blind.PayloadValue() == int.MinValue && blind.EditText == "—", blind.EditText);

        blind.Restore(-25);
        Check("调校：回读成功后恢复数值",
              !blind.IsUnavailable && blind.Margin == -25 && blind.EditText == "-25",
              $"{blind.IsUnavailable} / {blind.Margin} / {blind.EditText}");

        var genuineZero = new CoreMarginItem(0, 0, -50, 50, 1);
        Check("调校：真正的 0 偏移不算不可用",
              !genuineZero.IsUnavailable && genuineZero.EditText == "0", genuineZero.EditText);

        // 调校值跟着当前方案走
        PlanItem? activeProfile = PlanHub.Tuning.Current;

        if (activeProfile is not null)
        {
            int[] savedMargins = activeProfile.CoreMargins;
            uint savedFMax = activeProfile.FMaxMHz;

            activeProfile.CoreMargins = new[] { -1, -2, -3, -4 };
            activeProfile.FMaxMHz = 5000;

            var fromProfile = new TuningViewModel();
            Check("调校：当前方案有值时按方案取值",
                  fromProfile.CoreMargins.Count == 4 && fromProfile.CoreMargins[0].Margin == -1,
                  $"{fromProfile.CoreMargins.Count} 核 / 首核 {fromProfile.CoreMargins[0].Margin}");
            Check("调校：方案里的 FMax 也生效", fromProfile.FMaxMHz == 5000, $"{fromProfile.FMaxMHz}");

            // 白纸方案（新建出来的那种）：没有调校值，不会把上一份的残留带进来
            activeProfile.CoreMargins = Array.Empty<int>();
            activeProfile.FMaxMHz = 0;
            Check("调校：白纸方案不算有调校值", activeProfile.CoreMargins.Length == 0 && activeProfile.FMaxMHz == 0);

            activeProfile.CoreMargins = savedMargins;
            activeProfile.FMaxMHz = savedFMax;

            // 顶栏切方案 → 本页换一组值
            var switched = new PlanItem("t-switch", "切换验证", false)
            {
                CoreMargins = new[] { -3, -4, -5 },
                FMaxMHz = 5050,
            };

            activeProfile.CoreMargins = new[] { -1, -2 };
            activeProfile.FMaxMHz = 4800;

            var beforeSwitch = new TuningViewModel();
            Check("调校：切方案前是原方案的值",
                  beforeSwitch.CoreMargins.Count == 2 && beforeSwitch.FMaxMHz == 4800,
                  $"{beforeSwitch.CoreMargins.Count} 核 / {beforeSwitch.FMaxMHz}");

            PlanHub.Tuning.Items.Add(switched);
            PlanHub.Tuning.SelectCommand.Execute(switched);

            Check("调校：切方案后跟着换值",
                  beforeSwitch.CoreMargins.Count == 3 && beforeSwitch.FMaxMHz == 5050,
                  $"{beforeSwitch.CoreMargins.Count} 核 / {beforeSwitch.FMaxMHz}");

            PlanHub.Tuning.Items.Remove(switched);
            PlanHub.Tuning.SelectCommand.Execute(activeProfile);
            Check("调校：切回来恢复原方案的值",
                  beforeSwitch.CoreMargins.Count == 2 && beforeSwitch.FMaxMHz == 4800,
                  $"{beforeSwitch.CoreMargins.Count} 核 / {beforeSwitch.FMaxMHz}");

            activeProfile.CoreMargins = savedMargins;
            activeProfile.FMaxMHz = savedFMax;
        }

        // 电源：改 EPP → 脏；恢复默认
        var power = new PowerViewModel();
        Check("电源：初始不脏", !power.IsDirty);

        int epp = power.Epp;
        power.Epp = Math.Min(100, epp + 5);
        Check("电源：改 EPP 后 IsDirty = true", power.IsDirty, $"{epp}→{power.Epp}");

        power.RestoreDefaultsCommand.Execute(null);
        int defaultEpp = power.IsAcMode ? 50 : 80;
        Check("电源：恢复默认把草稿改成默认值（AC 50 / DC 80）",
              power.Epp == defaultEpp, $"{power.Epp} / 期望 {defaultEpp}");

        power.DiscardCommand.Execute(null);
        Check("电源：放弃后 EPP 回到已应用值", power.Epp == epp, $"{power.Epp}");
        Check("电源：放弃后不脏", !power.IsDirty);

        Check("电源：方案列表非空", power.Schemes.Count > 0, $"{power.Schemes.Count}");

        // 电源方案：新建 / 改名 / 删除的界面侧状态
        Check("电源：列表末尾是「＋ 新建方案」",
              power.Schemes[power.Schemes.Count - 1].IsCreateNew,
              $"{power.Schemes[power.Schemes.Count - 1].Name}");

        Check("电源：新建弹层有 4 个内置模板（节能 / 平衡 / 高性能 / 卓越性能）",
              power.Presets.Count == 4,
              string.Join(" / ", power.Presets.Select(p => p.Name)));

        // 模板名是通用词，本机往往已有同名方案；直接沿用会被后台的重名校验挡回来，
        // 用户看到的是「点了新建没反应」。
        Check("电源：重名时自动加序号",
              power.BuildUniqueSchemeName("平衡") == "平衡 2",
              power.BuildUniqueSchemeName("平衡"));

        Check("电源：不重名时沿用原名",
              power.BuildUniqueSchemeName("我的方案") == "我的方案",
              power.BuildUniqueSchemeName("我的方案"));

        Check("电源：内置方案被标记为不可删除",
              power.Schemes.Where(s => !s.IsCreateNew).All(s => s.IsBuiltIn),
              string.Join(" / ", power.Schemes.Where(s => !s.IsCreateNew).Select(s => s.Name)));

        power.OpenCreateMenuCommand.Execute(null);
        Check("电源：打开新建弹层", power.IsCreateMenuOpen);

        power.CloseCreateMenuCommand.Execute(null);
        Check("电源：关闭新建弹层", !power.IsCreateMenuOpen);

        var renamable = power.Schemes.FirstOrDefault(s => !s.IsCreateNew);
        if (renamable is not null)
        {
            power.BeginRenameCommand.Execute(renamable);
            Check("电源：进入改名态并填入原名",
                  renamable.IsRenaming && renamable.EditName == renamable.Name,
                  renamable.EditName);

            renamable.EditName = "改过的名字";
            power.CancelRenameCommand.Execute(renamable);
            Check("电源：取消改名丢弃草稿",
                  !renamable.IsRenaming && renamable.EditName == renamable.Name,
                  renamable.EditName);
        }

        // 电源：AC / DC 是两套独立的值
        int acEpp = power.Epp;
        power.IsAcMode = false;
        power.Epp = (acEpp + 10) % 101;
        int dcEpp = power.Epp;
        power.IsAcMode = true;

        Check("电源：切回交流电后值不受电池那一套影响", power.Epp == acEpp, $"{power.Epp} / 期望 {acEpp}");
        Check("电源：电池那一套确实被改了", dcEpp != acEpp, $"DC {dcEpp} / AC {acEpp}");
        Check("电源：最大处理器状态下限 = 50", power.MaxProcessorStateMinimum == 50, $"{power.MaxProcessorStateMinimum}");

        power.DiscardCommand.Execute(null);
        Check("电源：放弃后两套都回到已应用值", !power.IsDirty, $"{power.IsDirty}");

        // 进程：筛选 / 选择 / 解绑
        var proc = new ProcessViewModel();
        int all = proc.FilteredProcesses.Count;
        Check("进程：有占位进程", all > 0, $"{all}");

        proc.ShowSystemProcesses = false;
        int noSystem = proc.FilteredProcesses.Count;
        proc.ShowSystemProcesses = true;
        Check("进程：显示系统进程时条目只增不减",
              proc.FilteredProcesses.Count >= noSystem,
              $"关 {noSystem} / 开 {proc.FilteredProcesses.Count}");

        proc.SelectAllCommand.Execute(null);
        Check("进程：全选后选中数 = 可见条目数",
              proc.SelectedCount == proc.FilteredProcesses.Count, $"{proc.SelectedCount}");

        // 再点一次全选 = 取消全选（命令本身是「反选」语义）
        proc.SelectAllCommand.Execute(null);
        Check("进程：再点全选即取消全选", proc.SelectedCount == 0, $"{proc.SelectedCount}");

        // 解除全部绑核属于高危操作 → 先弹确认，取消后不得有副作用
        int boundBefore = proc.RunningProcesses.Count(p => p.TargetType != "none");
        proc.ReleaseAllCommand.Execute(null);

        if (boundBefore > 0)
        {
            Check("进程：解除全部先弹确认", ConfirmService.Instance.IsOpen);
            ConfirmService.Instance.CancelCommand.Execute(null);
            Check("进程：取消解除后已绑核数不变",
                  proc.RunningProcesses.Count(p => p.TargetType != "none") == boundBefore,
                  $"{boundBefore}");
        }
        else
        {
            Say("    （占位进程里没有已绑核，跳过解除确认）");
        }

        // 进程：下拉口径与「改进程即落成规则」
        Check("进程：优先级下拉 5 档且为中文",
              proc.PriorityOptions.Count == 5
              && proc.PriorityOptions[0] == "高"
              && proc.PriorityOptions[4] == "低",
              string.Join("/", proc.PriorityOptions));

        // 核心组也在掩码表里：下拉不再有独立的一套 CCD 选项。
        Check("进程：掩码下拉 = 掩码项 + 不调度",
              proc.MaskOptions.Any(m => m.TargetType == "mask" && m.MaskId is not null)
              && proc.MaskOptions.Any(m => m.TargetType == "none"),
              $"{proc.MaskOptions.Count} 项");

        // 挑一个还没有规则的进程，改它的绑核 —— 应当自动落成一条同名规则。
        var fresh = proc.RunningProcesses.FirstOrDefault(
            p => !p.IsSystem
                 && proc.SavedRules.All(r => !string.Equals(r.ProcessName, p.ProcessName, StringComparison.OrdinalIgnoreCase)));

        if (fresh is not null)
        {
            var mask = proc.MaskOptions.First(m => m.TargetType == "mask");
            fresh.MaskKey = mask.Key;

            var made = proc.SavedRules.FirstOrDefault(
                r => string.Equals(r.ProcessName, fresh.ProcessName, StringComparison.OrdinalIgnoreCase));

            Check("进程：改进程绑核后自动生成同名规则", made is not null, fresh.ProcessName);
            Check("进程：生成的规则带着新的掩码", made?.MaskLabel == mask.Label, made?.MaskLabel ?? "-");

            // key 没变、内部值被改脏时，重设同一个 key 必须能修回来（否则界面上选了掩码，保存仍报「掩码不存在」）。
            fresh.TargetValue = string.Empty;
            fresh.MaskKey = mask.Key;
            Check("进程：重设同一掩码能修回内部值",
                  fresh.TargetValue == mask.TargetValue, fresh.TargetValue);
        }
        else
        {
            Say("    （占位进程都已有规则，跳过自动生成断言）");
        }

        // 规则刷新后主界面行必须跟着规则走：右侧已显示 CCD0、主界面还挂旧掩码的错位不能再出现。
        {
            MaskOption maskOpt = proc.MaskOptions.First(m => m.TargetType == "mask");

            ScheduleRuleItem probeRule = new("r-sync", "probe-sync.exe", "probe", "旧掩码", "标准", enabled: true);
            probeRule.ApplyMask(maskOpt.Label, "mask", maskOpt.TargetValue);
            proc.SavedRules.Add(probeRule);

            RunningProcessItem probeProc = new(65001, "probe-sync.exe", "probe", 0, "标准");
            probeProc.ApplyMask("旧掩码", "mask", "4294901760");
            proc.RunningProcesses.Add(probeProc);

            proc.SyncFromRules();

            Check("进程：规则刷新后主界面行同步到规则值",
                  probeProc.MaskLabel == maskOpt.Label && probeProc.TargetValue == maskOpt.TargetValue,
                  $"{probeProc.MaskLabel} / 期望 {maskOpt.Label}");

            proc.SavedRules.Remove(probeRule);
            proc.RunningProcesses.Remove(probeProc);
        }

        // 掩码表刷新走逐位替换，不能 Clear —— 清空会让下拉把已选项回写成 null。
        {
            MaskOption first = proc.MaskOptions.First(m => m.TargetType == "mask");

            Zentwik.Core.Ipc.MaskListPayload payload = new();
            payload.Masks.Add(new Zentwik.Core.Ipc.MaskEntryPayload
            {
                Id = first.MaskId ?? "m1",
                Name = first.Label,
                Mask = ulong.Parse(first.TargetValue, System.Globalization.CultureInfo.InvariantCulture)
            });

            proc.ApplyMasks(payload);

            Check("进程：掩码表刷新后同名项仍是同一实例",
                  proc.MaskOptions.Count == 2
                  && ReferenceEquals(proc.MaskOptions.First(m => m.TargetType == "mask"), first),
                  $"{proc.MaskOptions.Count} 项");
        }

        // 场景：一条列表，每条形如 IF → 三个动作
        var scenes = new ScenariosViewModel();
        ScenariosViewModel.SuppressWorkerInit = true;
        _ = scenes.InitializeAsync();

        Check("场景：规则收成一条列表（不再分两组）", scenes.Rules.Count > 0, $"{scenes.Rules.Count}");
        Check("场景：总控默认开", scenes.SceneAutoSwitch);
        Check("场景：列表按优先级升序",
              scenes.Rules.Select(r => r.Priority).SequenceEqual(scenes.Rules.Select(r => r.Priority).OrderBy(p => p)),
              string.Join(",", scenes.Rules.Select(r => r.Priority)));

        // 一条规则只配一块是常态：没配的那块必须显示「保持原样」，而不是空着。
        Check("场景：存在只动一块的规则",
              scenes.Rules.Any(r => (r.HasProfile ? 1 : 0) + (r.HasMask ? 1 : 0) + (r.HasPowerScheme ? 1 : 0) == 1));
        Check("场景：没配的动作显示保持原样",
              scenes.Rules.Where(r => !r.HasProfile).All(r => r.ProfileText == "保持原样"));

        // 场景：单栏版式 + 流程卡
        {
            var view = new Views.ScenariosView();
            Render(view, 1168, 820);

            var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();

            Check("场景：页面标题为「场景自动化」", texts.Contains("场景自动化"), string.Join("/", texts.Take(6)));
            Check("场景：总控只出现一次",
                  texts.Count(t => t == "自动化总控") == 1, $"{texts.Count(t => t == "自动化总控")}");
            Check("场景：段标题为「已保存的规则」", texts.Contains("已保存的规则"));
            Check("场景：规则卡有 IF 标签", texts.Any(t => t.StartsWith("IF ")));
            Check("场景：规则卡三个动作块齐全且文案统一",
                  new[] { "调校方案", "调度方案", "电源方案" }.All(texts.Contains));
            Check("场景：空动作显示「保持原样」", texts.Any(t => t == "保持原样"));

            Check("场景：顶栏有新建规则主按钮",
                  FindByAutomationName(view, "新建规则") is not null);
            Check("场景：左侧方案库已删除",
                  FindByAutomationName(view, "切换到该方案") is null
                  && FindByAutomationName(view, "重命名此方案") is null);
            Check("场景：每张卡都带拖拽手柄",
                  FindByAutomationName(view, "拖动调整优先级") is not null);

            // 总控关掉后整组置灰，但规则本身不能丢。
            // 开关必须在构造视图之前置好：DataContext 里的 VM 是 InitializeComponent 建的。
            ScenariosViewModel.StartWithAutoSwitchOff = true;

            try
            {
                var dimmed = new Views.ScenariosView();
                Render(dimmed, 1168, 820);

                Check("场景：总控关掉时规则列表置灰",
                      FindAll<StackPanel>(dimmed).Any(p => p.Opacity < 0.5));
                Check("场景：总控关掉时规则还在",
                      FindAll<TextBlock>(dimmed).Any(t => t.Text.StartsWith("IF ")));
            }
            finally
            {
                ScenariosViewModel.StartWithAutoSwitchOff = false;
            }

            // 表单：一句人话「当 [大类] [状态] [进程]」
            ScenariosViewModel.StartWithEditorOpen = true;

            try
            {
                var editor = new Views.ScenariosView();
                Render(editor, 1168, 900);

                var editorTexts = FindAll<TextBlock>(editor).Select(t => t.Text).ToList();

                Check("场景：表单把条件写成「当 …」", editorTexts.Contains("当"), string.Join("/", editorTexts.Take(8)));
                Check("场景：表单有 IF / THEN 两个段标",
                      editorTexts.Contains("IF / 触发条件")
                      && editorTexts.Contains("THEN / 执行动作"));
                Check("场景：三个动作块按模块命名",
                      new[] { "调校方案", "调度方案", "电源方案" }.All(editorTexts.Contains));
                Check("场景：触发条上有两级下拉",
                      FindAll<ComboBox>(editor).Count(c => c.Tag is null) >= 2,
                      $"{FindAll<ComboBox>(editor).Count(c => c.Tag is null)}");
                Check("场景：进程搜索框在场",
                      FindByAutomationName(editor, "触发进程名") is not null);

                // 三个动作默认都是「保持原样」：下拉必须画虚线框，而不是普通的实线空框。
                Check("场景：空动作下拉画虚线框",
                      FindAll<Rectangle>(editor).Count(r => r.Visibility == Visibility.Visible
                                                            && r.StrokeDashArray.Count > 0) >= 3,
                      $"{FindAll<Rectangle>(editor).Count(r => r.Visibility == Visibility.Visible && r.StrokeDashArray.Count > 0)}");
            }
            finally
            {
                ScenariosViewModel.StartWithEditorOpen = false;
            }
        }

        // 三份方案列表各自独立、文案按类别
        {
            Check("方案：三份列表按类别出文案",
                  PlanHub.Tuning.Label == "调校方案"
                  && PlanHub.Scheduling.Label == "调度方案"
                  && PlanHub.Power.Label == "电源方案",
                  $"{PlanHub.Tuning.Label}/{PlanHub.Scheduling.Label}/{PlanHub.Power.Label}");

            Check("方案：三份列表互不共用集合",
                  !ReferenceEquals(PlanHub.Tuning.Items, PlanHub.Scheduling.Items)
                  && !ReferenceEquals(PlanHub.Scheduling.Items, PlanHub.Power.Items));

            Check("方案：类别文案不再出现「自定义方案」",
                  !PlanHub.Tuning.Label.Contains("自定义方案")
                  && !PlanHub.Scheduling.Label.Contains("自定义方案")
                  && !PlanHub.Power.Label.Contains("自定义方案"));
        }

        // 默认名带类别：三页都叫「新方案 1」时分不清刚建的是哪一类
        {
            PlanHub.Tuning.BeginCreateCommand.Execute(null);
            Check("方案：调校页默认名带类别",
                  PlanHub.Tuning.NewName.StartsWith("新调校", StringComparison.Ordinal),
                  PlanHub.Tuning.NewName);
            PlanHub.Tuning.CancelCreateCommand.Execute(null);

            PlanHub.Scheduling.BeginCreateCommand.Execute(null);
            Check("方案：进程页默认名带类别",
                  PlanHub.Scheduling.NewName.StartsWith("新调度", StringComparison.Ordinal),
                  PlanHub.Scheduling.NewName);
            PlanHub.Scheduling.CancelCreateCommand.Execute(null);

            PlanHub.Power.BeginCreateCommand.Execute(null);
            Check("方案：电源页默认名带类别",
                  PlanHub.Power.NewName.StartsWith("新电源", StringComparison.Ordinal),
                  PlanHub.Power.NewName);
            PlanHub.Power.CancelCreateCommand.Execute(null);
        }

        // 方案库已从场景页移走：改名 / 删除仍要能跑
        {
            var session = PlanHub.Tuning;
            var card = session.Items.First();
            string original = card.Name;

            session.StartRenameCommand.Execute(card);
            Check("方案：点铅笔进入改名态", card.IsRenaming);

            card.RenameText = "改名验证";
            session.CommitRenameCommand.Execute(card);
            Check("方案：提交后名字生效", card.Name == "改名验证", card.Name);
            Check("方案：提交后退出改名态", !card.IsRenaming);

            card.Name = original;

            // 改名与新建同一道重名校验：必须在本地挡住，输入框原样留着，
            // 不能等 Worker 拒绝后让名字先跳一下再跳回来。
            var dup = new PlanItem("p-selftest-dup", "撞名目标", false);
            session.Items.Add(dup);
            session.StartRenameCommand.Execute(card);
            card.RenameText = "撞名目标";
            session.CommitRenameCommand.Execute(card);
            Check("方案：重名改名被就地挡住", card.IsRenaming, card.Name);
            Check("方案：重名没把名字写坏", card.Name == original, card.Name);
            card.IsRenaming = false;
            session.Items.Remove(dup);

            // 删除用临时方案，不动真实那几份
            var temp = new PlanItem("p-selftest", "临时方案", false);
            session.Items.Add(temp);
            int beforeCount = session.Items.Count;
            session.ConfirmDeleteCommand.Execute(temp);
            Check("方案：删除真的把方案移出集合",
                  session.Items.Count == beforeCount - 1 && !session.Items.Contains(temp));
        }

        // 方案面板：行内摘要 / 当前行 / 未保存拦截 / 底部应用按钮
        {
            var session = PlanHub.Tuning;
            PlanItem first = session.Items[0];

            Check("方案：调校摘要带核数、负压区间与 FMax",
                  first.Summary.Contains("核", StringComparison.Ordinal)
                  && first.Summary.Contains("MHz", StringComparison.Ordinal),
                  first.Summary);

            Check("方案：调度摘要写规则条数",
                  PlanHub.Scheduling.Items[0].Summary.Contains("规则", StringComparison.Ordinal),
                  PlanHub.Scheduling.Items[0].Summary);

            Check("方案：电源摘要写系统方案名",
                  PlanHub.Power.Items[0].Summary.Length > 0,
                  PlanHub.Power.Items[0].Summary);

            session.SetUnsaved(false);
            session.SelectCommand.Execute(first);

            Check("方案：选中行打上 IsCurrent",
                  first.IsCurrent && ReferenceEquals(session.Current, first));

            Check("方案：底部应用按钮带上方案名",
                  session.ApplyLabel == $"应用「{first.Name}」", session.ApplyLabel);

            Check("方案：生效方案的应用按钮不可点", !session.CanApplyCurrent,
                  $"active={first.IsActive}");

            session.SetUnsaved(true);
            Check("方案：脏值时底部按钮改成保存",
                  session.ApplyLabel == $"保存至「{first.Name}」", session.ApplyLabel);
            session.SetUnsaved(false);
            Check("方案：存完恢复成应用",
                  session.ApplyLabel == $"应用「{first.Name}」", session.ApplyLabel);

            string oldName = first.Name;
            first.Name = oldName + "-改";
            Check("方案：改名后顶栏标题跟新名",
                  session.CurrentName == oldName + "-改", session.CurrentName);
            Check("方案：改名后底部按钮跟新名",
                  session.ApplyLabel == $"应用「{oldName}-改」", session.ApplyLabel);
            first.Name = oldName;

            // 有未保存改动时切别的方案：必须先弹确认。
            var other = new PlanItem("p-unsaved-guard", "未保存守卫", false)
            {
                Summary = "16 核 · CO 0 · 4000 MHz"
            };
            session.Items.Add(other);

            session.SetUnsaved(true);
            session.SelectCommand.Execute(other);
            Check("方案：未保存时切方案要先确认",
                  ReferenceEquals(session.Current, first) && ConfirmService.Instance.IsOpen,
                  session.Current?.Name);

            Check("方案：未保存时顶栏有标记", session.HasUnsavedChanges);

            ConfirmService.Instance.CancelCommand.Execute(null);
            Check("方案：取消后仍留在原方案",
                  ReferenceEquals(session.Current, first) && !ConfirmService.Instance.IsOpen,
                  session.Current?.Name);

            session.SelectCommand.Execute(other);
            ConfirmService.Instance.ConfirmCommand.Execute(null);
            Check("方案：确认丢弃后完成切换",
                  ReferenceEquals(session.Current, other) && !ConfirmService.Instance.IsOpen,
                  session.Current?.Name);

            session.SetUnsaved(false);
            session.SelectCommand.Execute(first);
            Check("方案：清掉未保存后直接切",
                  ReferenceEquals(session.Current, first), session.Current?.Name);

            session.Items.Remove(other);
        }

        // 界面状态：重启后恢复「上次选中的方案」
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"zw-uistate-{Guid.NewGuid():N}.json");

            UiStateStore.PathOverride = path;

            try
            {
                Check("状态：没写过时没有记录", UiStateStore.SelectedPlanId("Tuning") is null);

                UiStateStore.SetSelectedPlanId("Tuning", "plan-a");
                Check("状态：写入后能读回", UiStateStore.SelectedPlanId("Tuning") == "plan-a",
                      UiStateStore.SelectedPlanId("Tuning"));

                UiStateStore.SetSelectedPlanId("Power", "plan-b");
                Check("状态：三类互不干扰",
                      UiStateStore.SelectedPlanId("Tuning") == "plan-a"
                      && UiStateStore.SelectedPlanId("Power") == "plan-b");
            }
            finally
            {
                UiStateStore.PathOverride = null;

                try { System.IO.File.Delete(path); } catch { /* 清理失败无所谓 */ }
            }
        }

        // 场景：候选改名后，已列出的规则卡显示名跟着变
        {
            var linked = new ScenariosViewModel();
            ScenarioRuleItem rule = linked.Rules.First(r => r.ProfileId.Length > 0);
            ScenarioActionOption option = linked.EditorProfiles.First(o => o.Id == rule.ProfileId);

            Check("场景：规则卡方案名来自动作候选",
                  rule.ProfileName == option.Name, rule.ProfileName);

            linked.EditorProfiles[linked.EditorProfiles.IndexOf(option)] =
                option with { Name = "改名后的方案" };
            linked.RefreshActionOptionsAsync().GetAwaiter().GetResult();

            Check("场景：候选改名后规则卡跟着变",
                  rule.ProfileName == "改名后的方案", rule.ProfileName);
            Check("场景：重解析只改显示名不重建规则",
                  rule.ProfileId == option.Id && linked.Rules.Contains(rule));
        }

        // 场景：进程名校验与优先级上下
        {
            var vm = new ScenariosViewModel();
            vm.NewRuleCommand.Execute(null);
            vm.EditorProfileId = vm.EditorProfiles.Last().Id;
            vm.EditorProcessName = "1";
            vm.SaveEditorCommand.Execute(null);

            Check("场景：非法进程名被拦下并给出内联错误",
                  vm.HasProcessNameError && vm.IsEditorOpen,
                  vm.ProcessNameError);

            vm.EditorProcessName = "cs2.exe";
            vm.SaveEditorCommand.Execute(null);

            Check("场景：合法进程名可以提交", !vm.IsEditorOpen && !vm.HasProcessNameError);

            var order = new ScenariosViewModel();
            var first = order.Rules[0];
            var second = order.Rules[1];
            order.MoveRuleUpCommand.Execute(second);

            Check("场景：上移命令真的换序并重新编号",
                  ReferenceEquals(order.Rules[0], second) && second.Priority < first.Priority,
                  $"{second.Priority}/{first.Priority}");
        }

        // 场景：进程候选的收起判定
        // Popup 用 StaysOpen=True，收起全靠这条判断；判错就会「点输入框闪一下就没了」。
        {
            Check("场景：点输入框不收起进程候选",
                  !Views.ScenariosView.ShouldDismissProcessPicker(true, false, true));

            Check("场景：点候选行不收起",
                  !Views.ScenariosView.ShouldDismissProcessPicker(true, true, false));

            Check("场景：点页面别处收起候选",
                  Views.ScenariosView.ShouldDismissProcessPicker(true, false, false));

            Check("场景：候选没开时不受点击影响",
                  !Views.ScenariosView.ShouldDismissProcessPicker(false, false, false));
        }

        // 方案面板：点输入框不能当成「点了空白」。
        {
            Check("方案面板：新建输入框不算空白区",
                  Views.ProfileOverlay.IsInteractiveChild(new System.Windows.Controls.TextBox()));

            Check("方案面板：按钮不算空白区",
                  Views.ProfileOverlay.IsInteractiveChild(new System.Windows.Controls.Button()));

            Check("方案面板：面板背景仍算空白区",
                  !Views.ProfileOverlay.IsInteractiveChild(new System.Windows.Controls.Border()));
        }

        // 场景：新建 / 三动作校验 / 拖拽排序 / 停用
        {
            var vm = new ScenariosViewModel();
            _ = vm.InitializeAsync();

            Check("场景：动作候选首项是「保持原样」",
                  vm.EditorProfiles.FirstOrDefault()?.Id == string.Empty
                  && vm.EditorMasks.FirstOrDefault()?.Id == string.Empty);

            int beforeRules = vm.Rules.Count;
            vm.NewRuleCommand.Execute(null);
            Check("场景：点「新建规则」打开表单", vm.IsEditorOpen);
            Check("场景：表单默认「进程 / 启动时」",
                  vm.IsProcessKind && vm.EditorTrigger == ScenarioTrigger.ProcessRunning);

            // 两级下拉：大类决定联动谁，状态决定是哪个瞬间。
            Check("场景：进程状态有启动与退出两档",
                  vm.TriggerStates.Select(s => s.Label).SequenceEqual(new[] { "启动时", "退出时" }),
                  string.Join("/", vm.TriggerStates.Select(s => s.Label)));
            Check("场景：进程候选已就绪", vm.ProcessSuggestions.Count > 0, $"{vm.ProcessSuggestions.Count}");

            vm.EditorProcessName = "cs";
            Check("场景：进程候选按输入过滤",
                  vm.ProcessSuggestions.Count == 1 && vm.ProcessSuggestions[0].Name == "cs2.exe",
                  string.Join("/", vm.ProcessSuggestions.Select(p => p.Name)));

            vm.OpenProcessPickerCommand.Execute(null);
            Check("场景：打开进程下拉后处于展开态", vm.IsProcessPickerOpen);

            vm.PickProcessCommand.Execute(new ScenarioProcessOption("chrome.exe", 2050));
            Check("场景：选中候选后写回进程名并收起下拉",
                  vm.EditorProcessName == "chrome.exe" && !vm.IsProcessPickerOpen,
                  $"{vm.EditorProcessName} / {vm.IsProcessPickerOpen}");

            vm.SelectedKind = vm.TriggerKinds[1];
            Check("场景：切到系统电源后状态换成电源两档",
                  vm.TriggerStates.Select(s => s.Label).SequenceEqual(new[] { "拔下时", "接通时" })
                  && vm.EditorTrigger == ScenarioTrigger.OnBattery,
                  string.Join("/", vm.TriggerStates.Select(s => s.Label)));
            Check("场景：系统电源下不显示进程搜索框", !vm.IsProcessKind);

            vm.SelectedKind = vm.TriggerKinds[0];
            Check("场景：切回进程后状态落回「启动时」",
                  vm.EditorTrigger == ScenarioTrigger.ProcessRunning);

            vm.SelectedState = vm.TriggerStates[1];
            Check("场景：选「退出时」后编辑器触发源是进程退出",
                  vm.EditorTrigger == ScenarioTrigger.ProcessExited);

            // 调度方案是「整套用户规则」，与进程是否还活着无关，退出时同样可切。
            int beforeExitSchedule = vm.Rules.Count;
            vm.EditorProcessName = "exit-schedule.exe";
            vm.EditorMaskId = vm.EditorMasks.First(m => m.Id.Length > 0).Id;
            vm.SaveEditorCommand.Execute(null);

            Check("场景：进程退出时也能保存调度方案",
                  vm.Rules.Count == beforeExitSchedule + 1 && !vm.IsEditorOpen,
                  $"{vm.Rules.Count}");

            if (vm.Rules.Count > beforeExitSchedule)
            {
                var exitSchedule = vm.Rules.Last();
                Check("场景：退出规则带上调度动作",
                      exitSchedule.Trigger == ScenarioTrigger.ProcessExited && exitSchedule.HasMask);
                vm.DeleteRuleCommand.Execute(exitSchedule);
            }

            // 回到「启动时」重新开一张空表单，继续后面的用例。
            vm.NewRuleCommand.Execute(null);
            vm.SelectedKind = vm.TriggerKinds[0];
            vm.SelectedState = vm.TriggerStates[0];
            vm.EditorMaskId = string.Empty;

            // 进程名为空时必须拦下：存一条没有参数的「进程运行」规则，
            // 它永远匹配不到任何进程，用户看不出是配置问题。
            vm.EditorProcessName = string.Empty;
            vm.SaveEditorCommand.Execute(null);
            Check("场景：进程名为空时拒绝保存并保持表单打开",
                  vm.IsEditorOpen && vm.Rules.Count == beforeRules);

            // 三块全留空 = 命中后什么也不做，后台同样会拒，界面先拦。
            vm.EditorProcessName = "selftest.exe";
            vm.SaveEditorCommand.Execute(null);
            Check("场景：三动作全空时拒绝保存",
                  vm.IsEditorOpen && vm.Rules.Count == beforeRules);

            vm.EditorMaskId = vm.EditorMasks.First(m => m.Id.Length > 0).Id;
            vm.SaveEditorCommand.Execute(null);
            Check("场景：保存后新增一条规则",
                  vm.Rules.Count == beforeRules + 1 && !vm.IsEditorOpen,
                  $"{vm.Rules.Count}");

            var added = vm.Rules.Last();
            Check("场景：新规则排在列表末尾（优先级最大）",
                  vm.Rules.All(r => r.Priority <= added.Priority));
            Check("场景：新规则只带调度动作",
                  added.HasMask && !added.HasProfile && !added.HasPowerScheme);

            if (vm.Rules.Count >= 2)
            {
                vm.MoveRuleTo(added, 0);
                Check("场景：拖到首位后顺序真的换了", ReferenceEquals(vm.Rules[0], added));
                Check("场景：重排后优先级整段重新编号",
                      vm.Rules.Select(r => r.Priority).SequenceEqual(
                          Enumerable.Range(1, vm.Rules.Count).Select(i => i * 10)),
                      string.Join(",", vm.Rules.Select(r => r.Priority)));
            }

            bool enabled = added.Enabled;
            vm.ToggleRuleCommand.Execute(added);
            Check("场景：开关改的是规则自身", added.Enabled != enabled);

            int afterAdd = vm.Rules.Count;
            vm.DeleteRuleCommand.Execute(added);
            Check("场景：删除把规则移出列表",
                  vm.Rules.Count == afterAdd - 1 && !vm.Rules.Contains(added));

            // 退出时：真能存成一条 ProcessExited 规则
            int beforeExit = vm.Rules.Count;
            vm.NewRuleCommand.Execute(null);
            vm.SelectedKind = vm.TriggerKinds[0];
            vm.SelectedState = vm.TriggerStates[1];
            vm.EditorProcessName = "exit-test.exe";
            vm.EditorProfileId = vm.EditorProfiles.First(p => p.Id.Length > 0).Id;
            vm.SaveEditorCommand.Execute(null);

            Check("场景：退出规则保存成功",
                  vm.Rules.Count == beforeExit + 1 && !vm.IsEditorOpen, $"{vm.Rules.Count}");

            var exitRule = vm.Rules.Last();
            Check("场景：退出规则带 ProcessExited 与调校动作",
                  exitRule.Trigger == ScenarioTrigger.ProcessExited
                  && exitRule.HasProfile && !exitRule.HasMask);
            Check("场景：退出规则在列表里写「IF 进程退出」",
                  exitRule.TriggerLabel == "IF 进程退出" && exitRule.TriggerValue == "exit-test.exe",
                  $"{exitRule.TriggerLabel} / {exitRule.TriggerValue}");

            vm.DeleteRuleCommand.Execute(exitRule);

            // 三个动作下拉用 SelectedValue 做 TwoWay 绑定：候选集合 Clear 再重建时。
            vm.EditorProfileId = null!;
            vm.EditorMaskId = null!;
            vm.EditorPowerSchemeId = null!;
            vm.EditorProcessName = null!;

            bool nullSafe;
            try
            {
                nullSafe = vm.EditorProfileId.Length == 0
                           && vm.EditorMaskId.Length == 0
                           && vm.EditorPowerSchemeId.Length == 0
                           && vm.EditorProcessName.Length == 0
                           && !vm.HasEditorProfile
                           && !vm.HasEditorMask
                           && !vm.HasEditorPowerScheme
                           && vm.ShowProcessPlaceholder;
            }
            catch (Exception)
            {
                nullSafe = false;
            }

            Check("场景：候选重建回写 null 后动作 Id 归一为空串且不抛异常", nullSafe);

            // 切页回来候选会 Clear 重建，ComboBox 丢掉选中项后值不变就不再通知，
            // 表现是下拉空着、点开又看得到选项。重建后必须强制重绑一次。
            var notified = new List<string>();
            vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);
            vm.RebindActionSelection();

            Check("场景：重绑会通知三个动作 Id",
                  notified.Contains(nameof(vm.EditorProfileId))
                  && notified.Contains(nameof(vm.EditorMaskId))
                  && notified.Contains(nameof(vm.EditorPowerSchemeId)),
                  string.Join("/", notified));
        }

        // 输入框统一行为：模板水印 / 焦点线 / 错误态
        {
            var fieldStyle = Application.Current.Resources["TextFieldStyle"] as Style;
            Check("输入框：带框样式存在", fieldStyle is not null);

            if (fieldStyle is not null)
            {
                var box = new TextBox
                {
                    Style = fieldStyle,
                    Width = 240,
                    Height = 36,
                    ToolTip = "原始提示",
                };

                Zentwik.UI.Converters.InputAssist.SetWatermark(box, "测试水印");
                box.Measure(new Size(240, 36));
                box.Arrange(new Rect(0, 0, 240, 36));
                box.ApplyTemplate();

                var watermark = box.Template?.FindName("Watermark", box) as FrameworkElement;
                var focusLine = box.Template?.FindName("FocusLine", box) as FrameworkElement;
                var errorMark = box.Template?.FindName("ErrorMark", box) as FrameworkElement;

                Check("输入框：模板内水印 / 焦点线 / 错误标记齐全",
                      watermark is not null && focusLine is not null && errorMark is not null);

                if (watermark is not null)
                {
                    Check("输入框：空文本且未聚焦时显示水印",
                          watermark.Visibility == Visibility.Visible,
                          watermark.Visibility.ToString());

                    // 用户截图里的现象：IME 合成时文字已显示、页面级水印还在。
                    // 现在水印由模板按 HasText 判断，Text 一变就收起。
                    box.Text = "cs2";
                    Check("输入框：有文本时水印立即隐藏",
                          Zentwik.UI.Converters.InputAssist.GetHasText(box)
                          && watermark.Visibility != Visibility.Visible,
                          watermark.Visibility.ToString());

                    box.Text = string.Empty;
                    Check("输入框：清空后水印恢复",
                          watermark.Visibility == Visibility.Visible,
                          watermark.Visibility.ToString());
                }

                Zentwik.UI.Converters.InputAssist.SetErrorText(box, "名称重复");
                Check("输入框：错误态置红边标记并接管 ToolTip",
                      Zentwik.UI.Converters.InputAssist.GetHasError(box)
                      && Equals(box.ToolTip, "名称重复")
                      && errorMark is not null && errorMark.Visibility == Visibility.Visible
                      && focusLine is not null && focusLine.Visibility == Visibility.Visible,
                      $"{box.ToolTip}");

                Zentwik.UI.Converters.InputAssist.SetErrorText(box, string.Empty);
                Check("输入框：错误清除后还原原 ToolTip 与常态",
                      !Zentwik.UI.Converters.InputAssist.GetHasError(box)
                      && Equals(box.ToolTip, "原始提示")
                      && errorMark is not null && errorMark.Visibility != Visibility.Visible,
                      $"{box.ToolTip}");
            }
        }

        // 全站段头口径：主内容区一律「带底线 + 等宽灰标题」
        foreach (var (name, factory, expected) in new (string, Func<FrameworkElement>, int)[]
                 {
                     ("调校", () => new Views.TuningView(), 2),
                     ("电源", () => new Views.PowerView(), 2),
                     ("通用", () => new Views.SettingsView(), 1),
                     ("行为", () => new Views.BehaviorView(), 3),
                     ("高级", () => new Views.AdvancedView(), 4),
                 })
        {
            var page = factory();
            Render(page, 1168, 820);
            var headerStyle = Application.Current.Resources["SectionHeaderStyle"] as Style;

            int count = FindAll<Border>(page)
                .Count(b => headerStyle is not null && b.Style == headerStyle);

            Check($"{name}：{expected} 处段头全部带底线", count == expected, $"实际 {count}");
        }

        // 日志：单一级别筛选 + 统计仪表
        var logs = new LogsViewModel();
        Check("日志：有占位日志", logs.Entries.Count > 0, $"{logs.Entries.Count}");
        Check("日志：默认显示全部级别",
              logs.FilteredEntries.Count == logs.Entries.Count,
              $"{logs.FilteredEntries.Count}/{logs.Entries.Count}");
        Check("日志：统计与条目一致",
              logs.InfoCount == logs.Entries.Count(e => e.Level == "INFO")
              && logs.WarnCount == logs.Entries.Count(e => e.Level == "WARN")
              && logs.ErrorCount == logs.Entries.Count(e => e.Level is "ERROR" or "FATAL"),
              $"信息 {logs.InfoCount} / 警告 {logs.WarnCount} / 错误 {logs.ErrorCount}");

        logs.IsFilterInfo = true;
        Check("日志：只看信息只剩 INFO",
              logs.FilteredEntries.Count > 0 && logs.FilteredEntries.All(e => e.Level == "INFO"),
              $"{logs.FilteredEntries.Count}");

        logs.IsFilterWarn = true;
        Check("日志：只看警告只剩 WARN",
              logs.FilteredEntries.Count > 0 && logs.FilteredEntries.All(e => e.Level == "WARN"),
              $"{logs.FilteredEntries.Count}");

        logs.IsFilterError = true;
        Check("日志：只看错误只剩 ERROR / FATAL",
              logs.FilteredEntries.Count > 0
              && logs.FilteredEntries.All(e => e.Level is "ERROR" or "FATAL"),
              $"{logs.FilteredEntries.Count}");

        logs.IsFilterAll = true;
        Check("日志：切回 ALL 恢复全量",
              logs.FilteredEntries.Count == logs.Entries.Count, $"{logs.FilteredEntries.Count}");

        logs.SearchText = "cs2";
        Check("日志：搜索只留命中项",
              logs.FilteredEntries.Count > 0
              && logs.FilteredEntries.All(e => e.Message.Contains("cs2", StringComparison.OrdinalIgnoreCase)),
              $"{logs.FilteredEntries.Count}");
        logs.SearchText = string.Empty;

        Check("日志：最近异常取最后一条 ERROR",
              logs.LatestIssue is not null && logs.LatestIssue.Level == "ERROR",
              logs.LatestIssueLevel);

        // 日志：后台行文本的解析（协议：yyyy-MM-ddTHH:mm:ss.fff|级别|消息）
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            var parsed = LogsViewModel.Parse(
                $"{today}T23:10:11.123|INFO|启动完成\n"
                + $"{today}T23:10:12.000|WARN|路径 C:\\a|b\n"
                + "坏行没有分隔符\n"
                + $"{today}T23:10:13.500|ERROR|第一行\\n第二行");

            Check("日志：解析行文本，坏行被跳过", parsed.Count == 3, $"{parsed.Count}");
            Check("日志：当天条目只留时分秒", parsed[0].Time == "23:10:11.123", parsed[0].Time);
            Check("日志：消息里的竖线不被切断", parsed[1].Message == "路径 C:\\a|b", parsed[1].Message);
            Check("日志：字面量 \\n 还原成换行",
                  parsed[2].Message.Contains('\n'), parsed[2].Message.Replace("\n", "<LF>"));

            Check("日志：日志目录与 Worker 同源（Roaming\\Zentwik\\Logs）",
                  LogsViewModel.LogDirectory.EndsWith(
                      System.IO.Path.Combine("Zentwik", "Logs"), StringComparison.OrdinalIgnoreCase),
                  LogsViewModel.LogDirectory);

            string diagnostics = logs.BuildDiagnostics();
            Check("日志：诊断信息含环境 / 版本 / 后台状态 / 日志尾部",
                  diagnostics.Contains("Zentwik 诊断信息", StringComparison.Ordinal)
                  && diagnostics.Contains("应用版本", StringComparison.Ordinal)
                  && diagnostics.Contains("后台服务", StringComparison.Ordinal)
                  && diagnostics.Contains("最近日志", StringComparison.Ordinal),
                  $"{diagnostics.Length} 字符");
        }

        Check("日志：控制中心默认展开", logs.IsStatsOpen);
        Check("日志：路径缩成 %AppData% 前缀",
              logs.LogFolderDisplay.StartsWith("%AppData%", StringComparison.Ordinal),
              logs.LogFolderDisplay);
        Check("日志：日志目录体积有值",
              logs.LogFolderSizeText.Length > 0,
              logs.LogFolderSizeText);

        bool statsNotified = false;
        bool statsOpenAfter = true;
        logs.StatsStateChanged += open => { statsNotified = true; statsOpenAfter = open; };
        logs.IsStatsOpen = false;
        Check("日志：收起控制中心会通知视图", statsNotified && !statsOpenAfter);
        logs.IsStatsOpen = true;

        logs.ClearCommand.Execute(null);
        Check("日志：清空后无条目、异常卡收起",
              logs.FilteredEntries.Count == 0 && !logs.HasIssue);

        // 行为：四档启动方式 / 三档关闭行为，互斥且直写设置
        var settings = AppSettings.Instance;
        StartupBehavior originalStartup = settings.Startup;
        MainWindowCloseBehavior originalClose = settings.CloseBehavior;
        bool originalAutoApply = settings.AutoApplyTuning;

        try
        {
            settings.Startup = StartupBehavior.ServiceWindow;
            var behavior = new BehaviorViewModel();

            Check("行为：四档里只有当前档为真（后台+界面）",
                  behavior.StartupServiceWindow && !behavior.StartupNone
                  && !behavior.StartupServiceOnly && !behavior.StartupServiceTray);

            behavior.StartupNone = true;
            Check("行为：选「不启动」写入 None",
                  settings.Startup == StartupBehavior.None, settings.Startup.ToString());
            Check("行为：写回后选中态跟着位移",
                  behavior.StartupNone && !behavior.StartupServiceWindow);

            behavior.StartupServiceTray = true;
            Check("行为：选「后台+托盘」写回 ServiceTray",
                  settings.Startup == StartupBehavior.ServiceTray, settings.Startup.ToString());

            // 行内说明必须跟着档位走，四档共用一句话就失去意义。
            string hintTray = behavior.StartupHint;
            behavior.StartupNone = true;
            string hintNone = behavior.StartupHint;
            Check("行为：启动说明随档位变化且不为空",
                  hintTray.Length > 0 && hintNone.Length > 0 && hintTray != hintNone,
                  $"{hintTray} / {hintNone}");

            behavior.CloseWindowOnly = true;
            Check("行为：关闭行为写回 CloseWindowOnly",
                  settings.CloseBehavior == MainWindowCloseBehavior.CloseWindowOnly,
                  settings.CloseBehavior.ToString());
            Check("行为：关闭行为三档互斥",
                  !behavior.CloseMinimizeToTray && !behavior.CloseExitAll);

            string hintWindowOnly = behavior.CloseHint;
            behavior.CloseExitAll = true;
            string hintExitAll = behavior.CloseHint;
            behavior.CloseMinimizeToTray = true;
            string hintHide = behavior.CloseHint;
            Check("行为：关闭说明三档各不相同",
                  hintWindowOnly != hintExitAll
                  && hintExitAll != hintHide
                  && hintHide != hintWindowOnly,
                  hintWindowOnly);

            Check("行为：完全退出时关闭窗口并连带关后台",
                  MainWindow.ResolveCloseAction(true, MainWindowCloseBehavior.ExitAll)
                  == MainWindow.WindowCloseAction.ExitAll);
            Check("行为：收进托盘时隐藏窗口",
                  MainWindow.ResolveCloseAction(true, MainWindowCloseBehavior.MinimizeToTray)
                  == MainWindow.WindowCloseAction.Hide);
            Check("行为：仅留服务时只退 UI、保住后台",
                  MainWindow.ResolveCloseAction(true, MainWindowCloseBehavior.CloseWindowOnly)
                  == MainWindow.WindowCloseAction.ExitUiKeepWorker);
            Check("行为：托盘不可用时收进托盘退化为完全退出",
                  MainWindow.ResolveCloseAction(false, MainWindowCloseBehavior.MinimizeToTray)
                  == MainWindow.WindowCloseAction.ExitAll);
            Check("行为：仅留服务不需要托盘",
                  MainWindow.ResolveCloseAction(false, MainWindowCloseBehavior.CloseWindowOnly)
                  == MainWindow.WindowCloseAction.ExitUiKeepWorker);

            behavior.AutoApplyTuning = false;
            Check("行为：关闭启动时自动应用会写回设置",
                  !settings.AutoApplyTuning && !behavior.AutoApplyTuning);
        }
        finally
        {
            settings.Startup = originalStartup;
            settings.CloseBehavior = originalClose;
            settings.AutoApplyTuning = originalAutoApply;
        }

        // 设置远程同步 + 提示分类
        bool watchBefore = settings.WatchProcesses;
        bool bubbleBefore = settings.ErrorBubble;
        int toastBefore = settings.ToastDurationSeconds;

        try
        {
            settings.ApplyRemote(new Zentwik.Core.Ipc.UiSettingsPayload
            {
                CloseBehavior = "WorkerOnly",
                WatchProcessBinding = false,
                NotifyOnError = false,
                ToastSeconds = 9,
            });

            Check("设置：远程值映射到关闭行为",
                  settings.CloseBehavior == MainWindowCloseBehavior.CloseWindowOnly,
                  settings.CloseBehavior.ToString());
            Check("设置：远程值映射到后台开关",
                  !settings.WatchProcesses && !settings.ErrorBubble);
            Check("设置：提示时长随远程值更新", settings.ToastDurationSeconds == 9);

            settings.ErrorBubble = true;
            ToastService.Instance.ShowError("测试错误");
            Check("提示：开着错误气泡时错误可见", ToastService.Instance.IsVisible);
            Check("提示：错误走警示图标", ToastService.Instance.IsError);

            settings.ErrorBubble = false;
            ToastService.Instance.Hide();
            ToastService.Instance.ShowError("测试错误");
            Check("提示：关掉错误气泡后错误不弹", !ToastService.Instance.IsVisible);

            ToastService.Instance.Show("结果反馈");
            Check("提示：关掉错误气泡不影响普通提示", ToastService.Instance.IsVisible);
            Check("提示：普通提示回到对勾图标", !ToastService.Instance.IsError);
        }
        finally
        {
            settings.CloseBehavior = originalClose;
            settings.WatchProcesses = watchBefore;
            settings.ErrorBubble = bubbleBefore;
            settings.ToastDurationSeconds = toastBefore;
            ToastService.Instance.Hide();
        }

        var advanced = new AdvancedViewModel();
        Check("高级：三个诊断动作可执行",
              advanced.RestoreSafeDefaultsCommand.CanExecute(null)
              && advanced.RunSelfTestCommand.CanExecute(null)
              && advanced.ExportLogsCommand.CanExecute(null));

        // 通用：HEX 输入（解析发生在提交时：LostFocus / Enter）
        var misc = new SettingsViewModel();
        misc.HexInput = "#FF0000";
        misc.CommitHexInput();
        Check("通用：合法 HEX 提交后生效",
              BrushColor(Application.Current.Resources, "AccentBrush") == "FFFF0000",
              BrushColor(Application.Current.Resources, "AccentBrush"));

        misc.HexInput = "不是颜色";
        misc.CommitHexInput();
        Check("通用：非法输入被拒并回退到上一个合法值",
              misc.HexInput == "#FF0000", misc.HexInput);

        AppThemeService.Instance.SetAccentColor(DefaultAccent);
    }

    // 11. 浮层

    private static void SectionOverlays()
    {
        Say("\n=== 11. 浮层（掩码管理 / 方案 / 确认 / Toast）===");

        // 掩码管理器：拓扑与快捷选择。
        var proc = new ProcessViewModel();
        proc.OpenMaskManager();

        Check("掩码：打开后管理器状态为真", proc.IsMaskDialogOpen);
        Check("掩码：抽屉停在掩码页签", proc.IsMasksTab && proc.IsDrawerOpen);
        Check("掩码：页签持有自己的 VM", proc.MaskManager is not null);

        // 规则只写 schedules.json、方案快照不动：底部按钮得能切到「保存至」
        Check("进程页：接了「保存至」钩子",
              PlanHub.Scheduling.CanSavePending is not null && PlanHub.Scheduling.SavePending is not null);

        var mask = proc.MaskManager;

        if (mask is null)
        {
            return;
        }

        Check("掩码：CCD = 2 组", mask.CcdGroups.Count == 2, $"{mask.CcdGroups.Count}");
        Check("掩码：每组 8 物理核 × 2 线程",
              mask.CcdGroups.All(g => g.Cores.Count == 8
                                      && g.Cores.All(c => c.Threads.Count == 2)));

        // 左栏必须铺出后台存的掩码，否则用户会以为存的东西丢了
        Check("掩码：已保存列表非空", mask.SavedMasks.Count > 0, $"{mask.SavedMasks.Count}");
        Check("掩码：列表每项都有后台 Id",
              mask.SavedMasks.All(m => !string.IsNullOrEmpty(m.Id)),
              string.Join(", ", mask.SavedMasks.Select(m => m.Id)));

        Check("掩码：核编号跨 CCD 连续（CCD1 从 Core 8 起）",
              mask.CcdGroups[1].Cores[0].DisplayName == "Core 8",
              mask.CcdGroups[1].Cores[0].DisplayName);

        // 单点勾选：计数必须当场跟，不能等快捷按钮
        mask.ClearSelectionCommand.Execute(null);
        mask.CcdGroups[0].Cores[0].Threads[0].IsSelected = true;
        Check("掩码：单点勾选即时刷新计数", mask.SelectedCoreCount == 1, $"{mask.SelectedCoreCount}");
        mask.CcdGroups[0].Cores[0].Threads[0].IsSelected = false;
        Check("掩码：取消勾选即时回落", mask.SelectedCoreCount == 0, $"{mask.SelectedCoreCount}");

        mask.SelectAllCommand.Execute(null);
        Check("掩码：全选 32", mask.SelectedCoreCount == 32, $"{mask.SelectedCoreCount}");

        mask.SelectPhysicalOnlyCommand.Execute(null);
        Check("掩码：仅物理核心 16", mask.SelectedCoreCount == 16, $"{mask.SelectedCoreCount}");

        mask.InvertSelectionCommand.Execute(null);
        Check("掩码：反向 = 16", mask.SelectedCoreCount == 16, $"{mask.SelectedCoreCount}");

        mask.ClearSelectionCommand.Execute(null);
        Check("掩码：清空 = 0", mask.SelectedCoreCount == 0, $"{mask.SelectedCoreCount}");
        Check("掩码：计数文案随之更新", mask.SelectedCoreCountText == "已选 0 个逻辑处理器",
              mask.SelectedCoreCountText);

        // 列表 → 编辑器 → 返回
        SavedMaskItem? firstMask = mask.SavedMasks.FirstOrDefault();

        if (firstMask is not null)
        {
            mask.OpenEditorCommand.Execute(firstMask);
            Check("掩码：点卡片进编辑器", mask.IsEditing && !mask.IsListVisible);
            Check("掩码：编辑器标题为「编辑掩码」", mask.EditorTitle == "编辑掩码", mask.EditorTitle);
            Check("掩码：编辑器载入该掩码的勾选", mask.SelectedCoreCount > 0, $"{mask.SelectedCoreCount}");
        }

        mask.NewMaskCommand.Execute(null);
        Check("掩码：新建后进编辑器且标题为「新建掩码」",
              mask.IsEditing && mask.IsNewMask && mask.EditorTitle == "新建掩码",
              mask.EditorTitle);
        Check("掩码：新建后网格清空", mask.SelectedCoreCount == 0, $"{mask.SelectedCoreCount}");

        mask.CancelCommand.Execute(null);
        Check("掩码：取消后回到列表视图", !mask.IsEditing && mask.IsListVisible);

        // 视图自己渲染一遍：DataContext 若没接上宿主 VM，整块会静默空白。
        var maskPane = new Views.MaskManagerOverlay { Owner = proc, DataContext = proc };
        Render(maskPane, 280, 560);
        Check("掩码：抽屉视图拿到进程页 VM",
              maskPane.DataContext is ProcessViewModel,
              maskPane.DataContext?.GetType().Name ?? "null");
        Check("掩码：列表视图铺出掩码卡片",
              FindAll<Border>(maskPane).Count > 3,
              $"{FindAll<Border>(maskPane).Count} 个 Border");

        if (firstMask is not null)
            mask.OpenEditorCommand.Execute(firstMask);

        Render(maskPane, 280, 560);
        Check("掩码：编辑器铺出拓扑格子",
              FindAll<Border>(maskPane).Count > 20,
              $"{FindAll<Border>(maskPane).Count} 个 Border");

        // 拓扑列表必须真的占高度：绑定路径写错时 ItemsControl 会静默变成 0 高，
        // 截图里就是「核心拓扑勾选」标签下面一片空白。
        ItemsControl? topologyList = FindAll<ItemsControl>(maskPane)
            .FirstOrDefault(c => c.ItemsSource is IEnumerable<CcdGroup>);

        Check("掩码：拓扑列表有实际高度",
              topologyList is not null && topologyList.ActualHeight > 100,
              topologyList is null ? "找不到拓扑列表" : $"{topologyList.ActualHeight:0} px");

        // 方案浮层
        var profiles = PlanHub.Tuning;
        Check("方案：方案库非空", profiles.Items.Count >= 1, $"{profiles.Items.Count}");
        Check("方案：有当前方案", profiles.HasCurrent, profiles.CurrentName);

        profiles.OpenOverlayCommand.Execute(null);
        Check("方案：打开浮层", profiles.IsOverlayOpen);
        profiles.CloseOverlayCommand.Execute(null);
        Check("方案：关闭浮层", !profiles.IsOverlayOpen);

        if (profiles.Items.Count >= 2)
        {
            var target = profiles.Items.First(p => !p.IsActive);
            profiles.SelectCommand.Execute(target);
            Check("方案：切换后当前方案跟着变", ReferenceEquals(profiles.Current, target),
                  profiles.CurrentName);
        }

        // 确认层：取消必须无副作用
        var confirm = ConfirmService.Instance;
        var advanced = new AdvancedViewModel();

        bool smartBefore = AppSettings.Instance.SmartAntiBrick;
        advanced.ClearAllCommand.Execute(null);

        Check("确认层：危险操作先弹确认", confirm.IsOpen, confirm.Title);
        Check("确认层：列出后果", confirm.Consequences.Count > 0, $"{confirm.Consequences.Count} 条");

        confirm.CancelCommand.Execute(null);
        Check("确认层：取消后关闭", !confirm.IsOpen);
        Check("确认层：取消不产生副作用",
              AppSettings.Instance.SmartAntiBrick == smartBefore);

        // Toast
        var toast = ToastService.Instance;
        toast.Show("自检占位");
        Check("Toast：显示后可见且带文案", toast.IsVisible && toast.Message.Length > 0);
        toast.Hide();
        Check("Toast：隐藏后不可见", !toast.IsVisible);
    }

    // 12. 虚拟化

    private static void SectionVirtualization()
    {
        Say("\n=== 12. 长列表必须虚拟化 ===");
        CheckProcessListVirtualization();
        CheckLogListVirtualization();
    }

    // 13. 性能铁律

    private static void SectionPerformance()
    {
        Say("\n=== 13. 性能铁律 ===");

        var res = Application.Current.Resources;
        var shadow = res["OverlayShadowEffect"] as DropShadowEffect;

        Check("浮层阴影存在（仅给临时浮层用）", shadow is not null);
        Check("浮层阴影走 Performance 渲染",
              shadow is not null && shadow.RenderingBias == RenderingBias.Performance,
              shadow?.RenderingBias.ToString() ?? "<无>");

        Check("文本格式化模式 = Display（本机 140% 缩放下 Ideal 会持续占 CPU）",
              _shellFormattingMode == TextFormattingMode.Display, _shellFormattingMode.ToString());
    }

    // 断言工具

    private static void CheckProcessListVirtualization()
    {
        int previous = ProcessViewModel.BulkSeedCount;
        ProcessViewModel.BulkSeedCount = 600;

        try
        {
            var view = new Views.ProcessView();
            Render(view, 1200, 760);

            var vm = (ProcessViewModel)view.DataContext;
            Check("压测：进程列表装得下 600 条", vm.FilteredProcesses.Count >= 600,
                  $"实际 {vm.FilteredProcesses.Count}");

            var list = FindByAutomationName(view, "运行中进程列表") as ListBox;
            Check("进程列表是可滚动列表容器", list is not null);

            int containers = list is null ? -1 : CountDescendantsOf<ListBoxItem>(list);
            Check("600 条数据只实例化可见行（容器数远小于 600）",
                  containers > 0 && containers < 80, $"实际容器数 {containers}");

            int combos = list is null ? -1 : CountDescendantsOf<ComboBox>(list);
            Check("行内下拉也随虚拟化回收（ComboBox 数 < 160）",
                  combos >= 0 && combos < 160, $"实际 ComboBox 数 {combos}");
        }
        finally
        {
            ProcessViewModel.BulkSeedCount = previous;
        }
    }

    private static void CheckLogListVirtualization()
    {
        var view = new Views.LogsView();
        Render(view, 1200, 760);

        var vm = (LogsViewModel)view.DataContext;

        for (int i = 0; i < 800; i++)
        {
            vm.FilteredEntries.Add(new LogEntryItem(
                $"10:{i / 60:D2}:{i % 60:D2}.000", "INFO", $"批量日志 {i}"));
        }

        view.UpdateLayout();

        Check("压测：日志列表装得下 800 条", vm.FilteredEntries.Count >= 800,
              $"实际 {vm.FilteredEntries.Count}");

        var list = FindByAutomationName(view, "日志列表") as ListBox;
        Check("日志列表是可滚动列表容器", list is not null);

        int containers = list is null ? -1 : CountDescendantsOf<ListBoxItem>(list);
        Check("800 条日志只实例化可见行（容器数远小于 800）",
              containers > 0 && containers < 60, $"实际容器数 {containers}");
    }

    /// <summary>断言应用资源里某个键是预期的 ARGB 画刷。</summary>
    private static void AssertBrush(ResourceDictionary res, string key, string expected, string? note = null)
    {
        string actual = BrushColor(res, key);
        Check($"{key} = #{expected}{(note is null ? "" : $"（{note}）")}",
              actual == expected, actual);
    }

    /// <summary>断言独立加载的字典里某个键是预期的 ARGB 画刷。</summary>
    private static void AssertDicBrush(ResourceDictionary dict, string key, string expected, string? note = null)
    {
        string actual = dict[key] is SolidColorBrush b
            ? $"{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}"
            : "<无>";

        Check($"浅色 {key} = #{expected}{(note is null ? "" : $"（{note}）")}",
              actual == expected, actual);
    }

    private static ResourceDictionary LoadDictionary(string relativePath) =>
        new() { Source = new Uri(relativePath, UriKind.Relative) };

    private static string BrushColor(ResourceDictionary resources, string key)
    {
        return resources[key] is SolidColorBrush b
            ? $"{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}"
            : "<无>";
    }

    private static string Argb(Color c) => $"FF{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>离屏排一遍：Measure → Arrange → UpdateLayout。</summary>
    private static void Render(FrameworkElement root, double width, double height)
    {
        root.Width = width;
        root.Height = height;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    /// <summary>取样式（含 BasedOn 链）里某个属性的设定值。</summary>
    private static object? SetterValue(Style? style, DependencyProperty property)
    {
        while (style is not null)
        {
            foreach (Setter setter in style.Setters.OfType<Setter>())
            {
                if (setter.Property == property)
                {
                    return setter.Value;
                }
            }

            style = style.BasedOn;
        }

        return null;
    }

    /// <summary>找出页面里所有挂了 Effect 的常驻元素</summary>
    private static List<string> ResidentEffects(DependencyObject root)
    {
        var found = new List<string>();
        Walk(root);
        return found;

        void Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);

                if (child is FrameworkElement fe)
                {
                    string typeName = fe.GetType().Name;

                    // 浮层与 Toast 是临时元素，整棵子树跳过
                    if (typeName.Contains("Overlay", StringComparison.Ordinal)
                        || typeName.Contains("Toast", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (fe.Effect is not null)
                    {
                        found.Add(typeName);
                    }
                }

                Walk(child);
            }
        }
    }

    /// <summary>在视觉树里找第一个指定样式的元素。</summary>
    private static T? FindByStyle<T>(DependencyObject root, Style style) where T : FrameworkElement
    {
        if (root is T typed && typed.Style == style)
        {
            return typed;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var found = FindByStyle<T>(VisualTreeHelper.GetChild(root, i), style);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>按 x:Name 查找元素。</summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T typed && typed.Name == name)
        {
            return typed;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var found = FindByName<T>(VisualTreeHelper.GetChild(root, i), name);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var result = new List<T>();
        Collect(root);
        return result;

        void Collect(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);

                if (child is T match)
                {
                    result.Add(match);
                }

                Collect(child);
            }
        }
    }

    /// <summary>断言某个按钮在真实布局下能被点中</summary>
    private static void CheckHitTestableIn(FrameworkElement root, string label, string automationName)
    {
        var button = FindByAutomationName(root, automationName);

        if (button is null)
        {
            Check($"{label}：能在视觉树里找到", false);
            return;
        }

        Check($"{label}：能在视觉树里找到", true);

        if (button.ActualWidth <= 0 || button.ActualHeight <= 0)
        {
            Check($"{label}：有可命中几何", false, $"{button.ActualWidth}×{button.ActualHeight}");
            return;
        }

        var origin = button.TranslatePoint(new Point(0, 0), root);
        var point = new Point(origin.X + button.ActualWidth / 2,
                              origin.Y + button.ActualHeight / 2);

        var hit = VisualTreeHelper.HitTest(root, point);

        bool hitSelf = hit?.VisualHit is not null
            && (hit.VisualHit == button || FindAncestor<ButtonBase>(hit.VisualHit) == button);

        Check($"{label}：中心点命中到按钮本身或其内部", hitSelf,
              hit?.VisualHit is null
                  ? "null"
                  : $"{hit.VisualHit.GetType().Name}{(hit.VisualHit is FrameworkElement hfe ? $"「{SafeText(hfe)}」" : "")}"
                    + " | 链: " + HitChain(hit?.VisualHit));
    }

    /// <summary>把命中链打印出来（诊断用）。</summary>
    private static string HitChain(DependencyObject? node)
    {
        var parts = new List<string>();

        while (node is not null && parts.Count < 6)
        {
            string name = node is FrameworkElement fe
                ? fe.GetType().Name
                : node.GetType().Name;

            string hitFlag = node is UIElement ue && !ue.IsHitTestVisible ? "(不可命中)" : "";

            parts.Add(name + hitFlag);

            node = VisualTreeHelper.GetParent(node);
        }

        return string.Join(" < ", parts);
    }

    /// <summary>取元素上显示的文字（没有就返回空串）。</summary>
    private static string SafeText(FrameworkElement element)
    {
        try
        {
            return element is TextBlock tb ? tb.Text : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>按 AutomationProperties.Name 查找元素</summary>
    private static FrameworkElement? FindByAutomationName(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe &&
            AutomationProperties.GetName(fe) == name)
        {
            return fe;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var found = FindByAutomationName(VisualTreeHelper.GetChild(root, i), name);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>向上找最近的指定类型祖先。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            // 非 FrameworkElement 的视觉节点只能走视觉树；
            // LogicalTreeHelper.GetParent 对它们会抛异常。
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private static int CountDescendantsOf<T>(DependencyObject root) where T : DependencyObject
    {
        int total = 0;
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T)
            {
                total++;
            }

            total += CountDescendantsOf<T>(child);
        }

        return total;
    }

    private static void Say(string line)
    {
        Console.WriteLine(line);
        _sink?.AppendLine(line);
    }

    /// <summary>读 Explorer\Accent\AccentPalette 主色组（索引 3，RGBA）与 DWM 交叉校验。</summary>
    private static Color? ReadPaletteMainAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");

            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 16)
            {
                const int MainAccentIndex = 3;
                int o = MainAccentIndex * 4;
                return Color.FromRgb(palette[o], palette[o + 1], palette[o + 2]);
            }
        }
        catch
        {
            // 读不到就跳过交叉校验
        }

        return null;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;

        string note = detail ?? string.Empty;
        string line = $"  {(ok ? "PASS" : "FAIL")}  {name}{(note.Length > 0 ? "  -> " + note : "")}";

        Console.WriteLine(line);
        _sink?.AppendLine(line);
    }
}
