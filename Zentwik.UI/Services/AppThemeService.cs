using System.Windows;
using System.Windows.Media;

namespace Zentwik.UI.Services;

/// <summary>应用主题模式。</summary>
public enum AppTheme
{
    Light,
    Dark,
    System,
}

/// <summary>强调色来源模式。</summary>
public enum AccentSource
{
    /// <summary>跟随系统强调色。</summary>
    System,

    /// <summary>用户通过彩虹滑块自定义。</summary>
    Custom,
}

/// <summary>应用级主题引擎 —— 全局唯一状态源；Mode / AccentMode / AccentColor 只存这一份。</summary>
public sealed class AppThemeService
{
    private static readonly Lazy<AppThemeService> _instance = new(() => new AppThemeService());
    public static AppThemeService Instance => _instance.Value;

    /// <summary>主题字典在 MergedDictionaries 中的固定槽位。</summary>
    private const int ThemeSlot = 0;

    /// <summary>强调色画刷字典（独立实例，不与主题字典混在一起）</summary>
    private readonly ResourceDictionary _accentDictionary = new();

    /// <summary>参考稿的科技蓝，全站默认强调色。</summary>
    private static readonly Color DefaultAccent = Color.FromRgb(0x38, 0xBD, 0xF8);

    /// <summary>当前强调色画刷（供 ViewModel / 代码直接使用）。</summary>
    public SolidColorBrush AccentBrush { get; private set; } = new(DefaultAccent);

    /// <summary>当前品牌色（twik）画刷。</summary>
    public SolidColorBrush BrandAccentBrush { get; private set; } = new(Color.FromRgb(0xC6, 0xDA, 0xFF));

    /// <summary>品牌色的中间调（应用图标第二层用）。</summary>
    public SolidColorBrush BrandAccentMidBrush { get; private set; } = new(Color.FromRgb(0x9A, 0xB4, 0xE0));

    /// <summary>品牌色的高光（应用图标第三层用）。</summary>
    public SolidColorBrush BrandAccentLightBrush { get; private set; } = new(Color.FromRgb(0xE2, 0xEC, 0xFF));

    /// <summary>压在强调色上的前景色画刷。</summary>
    public SolidColorBrush AccentForegroundBrush { get; private set; } = new(Color.FromRgb(0x06, 0x12, 0x1A));

    /// <summary>淡强调色画刷 —— 主题色背景的浅色版本（半透明）</summary>
    public SolidColorBrush AccentSubtleBrush { get; private set; } =
        new(DefaultAccent) { Opacity = 0.22 };

    /// <summary>应用图标（DrawingImage）：几何来自 BrandLogo.xaml，颜色在这里组装。</summary>
    public DrawingImage BrandLogoImage { get; private set; } = new();

    /// <summary>图标调色板 —— 图标三层各自用什么颜色</summary>
    public readonly record struct LogoPalette(Color Body, Color Shadow);

    /// <summary>当前图标调色板</summary>
    public LogoPalette CurrentLogoPalette { get; private set; }

    /// <summary>取某一层该用的颜色画刷</summary>
    private Brush BrushForLayer(int layer) => layer == 1
        ? new SolidColorBrush(CurrentLogoPalette.Body)
        : new SolidColorBrush(CurrentLogoPalette.Shadow);

    /// <summary>用当前调色板重建应用图标</summary>
    private void RebuildBrandLogo()
    {
        var resources = Application.Current.Resources;
        var group = new DrawingGroup();

        for (int i = 1; i <= 3; i++)
        {
            if (resources["BrandLogoPath" + i] is not PathGeometry geo) continue;
            group.Children.Add(new GeometryDrawing(BrushForLayer(i), null, geo));
        }

        // DrawingGroup 本身就是 Drawing，包围盒由内容自动计算
        // （正好等于三层合并后的 86,15 - 933,991）。
        BrandLogoImage = new DrawingImage(group);
        _accentDictionary["BrandLogoImage"] = BrandLogoImage;

        // 托盘用的是同一份矢量内容（Drawing 而非 DrawingImage，
        // 因为托盘渲染器要拿到可绘制的几何，而不是一张图片）
        BrandLogoDrawing = group;
    }

    /// <summary>图标矢量内容（Drawing）—— 托盘渲染器直接拿它画 HICON</summary>
    public Drawing BrandLogoDrawing { get; private set; } = new DrawingGroup();

    /// <summary>按给定配色组装图标矢量；几何只读，颜色由调用方给，不进资源字典。</summary>
    public Drawing BuildLogoDrawing(Color body, Color shadow)
    {
        var resources = Application.Current.Resources;
        var group = new DrawingGroup();

        for (int i = 1; i <= 3; i++)
        {
            if (resources["BrandLogoPath" + i] is not PathGeometry geo) continue;

            var brush = new SolidColorBrush(i == 1 ? body : shadow);
            group.Children.Add(new GeometryDrawing(brush, null, geo));
        }

        return group;
    }

    private AppThemeService() { }

    /// <summary>当前主题模式（用户选择：浅色 / 深色 / 系统）。</summary>
    public AppTheme Mode { get; private set; } = AppTheme.Dark;

    /// <summary>当前实际生效的主题（System 模式下已解析为 Light / Dark）。</summary>
    public bool IsDark { get; private set; } = true;

    /// <summary>当前强调色来源。</summary>
    public AccentSource AccentMode { get; private set; } = AccentSource.Custom;

    /// <summary>当前强调色。默认值取自参考稿的科技蓝 #38BDF8。</summary>
    public Color AccentColor { get; private set; } = DefaultAccent;

    /// <summary>是否把外观改动写进用户配置</summary>
    public bool PersistenceEnabled { get; set; }

    /// <summary>正在回灌上次保存的外观：期间的改动不再回写，避免自我覆盖。</summary>
    private bool _restoringAppearance;

    /// <summary>主题发生变化（明暗切换）时触发。</summary>
    public event EventHandler<bool>? ThemeChanged;

    /// <summary>系统（任务栏）明暗发生变化 —— 与应用内主题无关</summary>
    public event EventHandler? SystemThemeChanged;

    /// <summary>强调色发生变化时触发。</summary>
    public event EventHandler<Color>? AccentChanged;

    /// <summary>主题**模式**变化时触发（浅色 ↔ 深色 ↔ 系统）</summary>
    public event EventHandler<AppTheme>? ThemeModeChanged;

    /// <summary>强调色**来源**变化时触发（系统 ↔ 个性）。</summary>
    public event EventHandler<AccentSource>? AccentModeChanged;

    private bool _initialized;

    // 绝对 pack URI：相对 URI 按入口程序集解析，非 Zentwik.UI 入口（如离屏探针）无法复现。
    private static Uri ThemeUri(string relative) =>
        new("pack://application:,,,/Zentwik.UI;component/" + relative, UriKind.Absolute);

    /// <summary>初始化：挂载资源字典</summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var resources = Application.Current.Resources;

        // 主题字典放索引 0（最高优先级），Accent / Controls 依次在后。
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = ThemeUri("Themes/Tokens.Dark.xaml")
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = ThemeUri("Themes/Accent.xaml")
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = ThemeUri("Themes/Controls.xaml")
        });

        // 强调色画刷单独成典，靠替换实例刷新（原因见类 remarks）。
        _accentDictionary["AccentBrush"] = AccentBrush;
        _accentDictionary["AccentForegroundBrush"] = AccentForegroundBrush;
        _accentDictionary["AccentSubtleBrush"] = AccentSubtleBrush;
        _accentDictionary["BrandAccentBrush"] = BrandAccentBrush;
        _accentDictionary["BrandAccentMidBrush"] = BrandAccentMidBrush;
        _accentDictionary["BrandAccentLightBrush"] = BrandAccentLightBrush;
        resources.MergedDictionaries.Add(_accentDictionary);

        // 图标几何资源（Themes/BrandLogo.xaml 只提供 PathGeometry，
        // 颜色由 AppThemeService 组装成 DrawingImage）
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = ThemeUri("Themes/BrandLogo.xaml")
        });

        IsDark = true;

        // 品牌色必须在这里算一次。
        RefreshBrandAccent();

        // 上次选的外观。必须在字典挂好之后才应用 —— 切主题要换索引 0 的字典。
        if (PersistenceEnabled)
            RestoreAppearance();
    }

    /// <summary>回灌上次保存的主题模式与强调色。</summary>
    private void RestoreAppearance()
    {
        if (!ThemePreferenceStore.TryLoad(out ThemePreference saved))
            return;

        _restoringAppearance = true;

        try
        {
            SetThemeMode(saved.Mode);
            SetAccentMode(saved.AccentMode);

            if (saved.AccentMode == AccentSource.Custom &&
                ColorMath.TryParseHex(saved.AccentColor, out Color color))
            {
                SetAccentColor(color);
            }
        }
        finally
        {
            _restoringAppearance = false;
        }
    }

    /// <summary>把当前外观写回用户配置。诊断模式与回灌期间不写。</summary>
    private void PersistAppearance()
    {
        if (!PersistenceEnabled || _restoringAppearance || !_initialized)
            return;

        ThemePreferenceStore.Save(new ThemePreference
        {
            Mode = Mode,
            AccentMode = AccentMode,
            AccentColor = ColorMath.ToHex(AccentColor),
        });
    }

    /// <summary>把系统主题监听挂到指定窗口上</summary>
    public void AttachWindow(Window hostWindow)
    {
        // 系统主题变化监听：注册表改动后 Windows 会广播 WM_SETTINGCHANGE，
        // 由窗口钩子转发到这里。
        SystemEventsBridge.RegisterThemeChanged(hostWindow, RefreshFromSystem);
    }

    /// <summary>重新读取系统状态并应用 —— 由 WM_SETTINGCHANGE 触发</summary>
    public void RefreshFromSystem()
    {
        // 系统（任务栏）明暗可能变了 —— 托盘图标配色依赖它。
        SystemThemeChanged?.Invoke(this, EventArgs.Empty);

        if (Mode == AppTheme.System)
        {
            ApplyResolvedTheme(SystemTheme.IsSystemLightTheme() ? AppTheme.Light : AppTheme.Dark);
        }

        if (AccentMode == AccentSource.System)
        {
            SetAccentColor(SystemTheme.GetSystemAccentColor());
        }
    }

    /// <summary>切换主题模式</summary>
    public void SetThemeMode(AppTheme mode)
    {
        if (Mode == mode)
        {
            // 模式没变也要重新解析一次：
            // 模式是 System 时，系统明暗可能已经变了。
            if (mode == AppTheme.System)
            {
                ApplyResolvedTheme(SystemTheme.IsSystemLightTheme() ? AppTheme.Light : AppTheme.Dark);
            }
            return;
        }

        Mode = mode;

        ApplyResolvedTheme(mode == AppTheme.System
            ? (SystemTheme.IsSystemLightTheme() ? AppTheme.Light : AppTheme.Dark)
            : mode);

        ThemeModeChanged?.Invoke(this, mode);
        PersistAppearance();
    }

    /// <summary>应用已解析出的具体主题（Light / Dark）</summary>
    private void ApplyResolvedTheme(AppTheme resolved)
    {
        bool newIsDark = resolved == AppTheme.Dark;
        if (newIsDark == IsDark) return;

        IsDark = newIsDark;

        var resources = Application.Current.Resources;
        resources.MergedDictionaries[ThemeSlot] = new ResourceDictionary
        {
            Source = ThemeUri(newIsDark ? "Themes/Tokens.Dark.xaml" : "Themes/Tokens.Light.xaml")
        };

        // 明暗切换会改变品牌色的 L 档（深色加亮 / 浅色压暗）→ 重算一次
        RefreshBrandAccent();

        // 淡强调色的不透明度也跟明暗相关 → 同样要重建
        RefreshAccentSubtle();

        ThemeChanged?.Invoke(this, IsDark);
    }

    /// <summary>切换强调色来源</summary>
    public void SetAccentMode(AccentSource source)
    {
        if (AccentMode == source)
        {
            // 模式没变也同步一次颜色：切到「系统」后系统色可能已变。
            if (source == AccentSource.System)
            {
                SetAccentColor(SystemTheme.GetSystemAccentColor());
            }
            return;
        }

        AccentMode = source;

        if (source == AccentSource.System)
        {
            SetAccentColor(SystemTheme.GetSystemAccentColor());
        }

        AccentModeChanged?.Invoke(this, source);
        PersistAppearance();
    }

    /// <summary>设置强调色并原地改写 AccentBrush / BrandAccentBrush</summary>
    public void SetAccentColor(Color color)
    {
        if (AccentColor == color && _accentDictionary.Contains("AccentBrush"))
        {
            // 同色不重刷：避免每次系统事件都重建画刷导致无谓的重绘。
            return;
        }

        AccentColor = color;

        // 替换画刷实例（而非改 .Color）—— 原因见 _accentDictionary 的说明
        AccentBrush = new SolidColorBrush(color);
        _accentDictionary["AccentBrush"] = AccentBrush;

        // 浅色强调色上用深色字，深色强调色上用浅色字（按亮度判断）
        AccentForegroundBrush = new SolidColorBrush(
            IsLight(color) ? Color.FromRgb(0x06, 0x12, 0x1A) : Colors.White);
        _accentDictionary["AccentForegroundBrush"] = AccentForegroundBrush;

        RefreshAccentSubtle();

        RefreshBrandAccent();
        AccentChanged?.Invoke(this, color);

        PersistAppearance();
    }

    /// <summary>重建淡强调色画刷</summary>
    private void RefreshAccentSubtle()
    {
        AccentSubtleBrush = new SolidColorBrush(AccentColor)
        {
            Opacity = IsDark ? 0.26 : 0.16,
        };
        _accentDictionary["AccentSubtleBrush"] = AccentSubtleBrush;
    }

    /// <summary>重算品牌色（twik）并写入资源字典</summary>
    public void RefreshBrandAccent()
    {
        var brand = ColorMath.DeriveBrandColor(AccentColor, IsDark);
        var (main, mid, light) = ColorMath.DeriveBrandPalette(brand);

        BrandAccentBrush = new SolidColorBrush(main);
        _accentDictionary["BrandAccentBrush"] = BrandAccentBrush;

        BrandAccentMidBrush = new SolidColorBrush(mid);
        _accentDictionary["BrandAccentMidBrush"] = BrandAccentMidBrush;

        BrandAccentLightBrush = new SolidColorBrush(light);
        _accentDictionary["BrandAccentLightBrush"] = BrandAccentLightBrush;

        // 图标配色依赖强调色 + 当前表面色 → 必须一起重算
        RefreshLogoPalette();
    }

    /// <summary>图标所在表面的颜色（窗口底色）—— 用于计算图标与背景的对比度</summary>
    private Color LogoSurfaceColor => IsDark
        ? Color.FromRgb(0x09, 0x09, 0x0B)
        : Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>重算图标调色板并重建图标</summary>
    public void RefreshLogoPalette()
    {
        CurrentLogoPalette = LogoPaletteFactory.Build(AccentColor, IsDark, LogoSurfaceColor);
        RebuildBrandLogo();
    }

    /// <summary>按相对亮度判断颜色是「浅」还是「深」</summary>
    private static bool IsLight(Color c)
    {
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.6;
    }
}
