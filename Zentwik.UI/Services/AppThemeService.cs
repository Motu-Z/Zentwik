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

/// <summary>
/// 应用级主题引擎 —— 全局唯一状态源。
/// </summary>
/// <remarks>
/// 【为什么状态必须收敛到这里】
/// 之前主题模式 / 色彩模式 / 强调色各存一份：一份在 AppThemeService，
/// 一份在 SettingsViewModel 的 bool 字段里。
/// ViewModel 每次随页面重建都会把 bool 重置为初值，于是：
///   · 从别的页面切回「杂项」→ 按钮回到默认态，但服务里的真实状态没变
///     （页面还是浅色，按钮却显示「深色」）
///   · 用户手动选「深色」→ 服务里的 Mode 可能根本没被写进去
///     （RadioButton 的 IsChecked 回写有静默丢失的路径）
///     → 系统主题一变，应用又跟着系统走
/// 现在 Mode / AccentMode / AccentColor 只存在这里一份，
/// ViewModel 的单选状态改成对这里的**投影**（getter 读服务），
/// setter 才写回服务。按钮显示什么，永远等于服务里是什么。
///
/// 【为什么改色是「替换实例」而不是「改 .Color」】
/// 实测：ResourceDictionary 会对放入的 Freezable 自动冻结（省内存），
/// 冻结后设置 .Color 会抛「无法在冻结的画刷上设置属性」。
/// 而「用新画刷实例替换字典项」既能避开冻结，
/// 又能触发 DynamicResource 绑定立即刷新（实测：红→绿→黄全部生效）。
///
/// 【字典替换的实现】
/// Application.Resources.MergedDictionaries 按顺序查找，索引靠前的优先。
/// 我们把主题字典放在索引 0，切换时用 Replace 原地换掉这一项，
/// 其余字典（Accent / Controls）索引不变，优先级关系保持稳定。
/// </remarks>
public sealed class AppThemeService
{
    private static readonly Lazy<AppThemeService> _instance = new(() => new AppThemeService());
    public static AppThemeService Instance => _instance.Value;

    /// <summary>主题字典在 MergedDictionaries 中的固定槽位。</summary>
    private const int ThemeSlot = 0;

    /// <summary>
    /// 强调色画刷字典（独立实例，不与主题字典混在一起）。
    /// </summary>
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

    /// <summary>
    /// 淡强调色画刷 —— 主题色背景的浅色版本（半透明）。
    ///
    /// 【为什么需要单独一个画刷，而不是给 AccentBrush 加 Opacity】
    /// 强调色画刷是「实心底」，文字用 AccentForegroundBrush（按亮度取黑/白）。
    /// 但胶囊这类小面积底色如果也用实心强调色，文字与底色的对比度
    /// 完全取决于用户挑了什么颜色 —— 挑个亮黄就糊成一片。
    ///
    /// 淡强调色把主题色按低透明度叠在卡片底上，底色永远偏暗（深色主题）
    /// 或偏亮（浅色主题），于是文字可以统一用 TextPrimaryBrush
    /// （深色主题=白，浅色主题=黑），对比度稳定。
    /// </summary>
    public SolidColorBrush AccentSubtleBrush { get; private set; } =
        new(DefaultAccent) { Opacity = 0.22 };

    /// <summary>
    /// 应用图标（DrawingImage）—— 几何来自 Themes/BrandLogo.xaml，
    /// 颜色在这里组装，因此随强调色与明暗实时变化。
    /// </summary>
    /// <remarks>
    /// 【为什么必须由代码构建】
    /// ResourceDictionary 里的 DrawingImage 属于 Freezable，
    /// 加载时会被 WPF 冻结；冻结会让内部的 DynamicResource 表达式
    /// **当场求值固化**。
    /// 实测：图标在深浅两套主题下颜色完全相同，且等于画刷的初始值 ——
    /// 也就是图标从来没有跟随过强调色或明暗变化过。
    ///
    /// 改成在代码里组装 DrawingGroup（代码创建的对象不走 XAML 的冻结路径），
    /// 换色时整体重建实例并替换字典项，DynamicResource 才会重新解析。
    /// </remarks>
    public DrawingImage BrandLogoImage { get; private set; } = new();

    /// <summary>
    /// 图标调色板 —— 图标三层各自用什么颜色。
    /// </summary>
    /// <remarks>
    /// 图标几何共 3 层：第 1 层是主体，第 2/3 层是阴影。
    /// 不同方案下三层的取色规则不同，拆成独立结构便于对比与切换。
    /// </remarks>
    /// <param name="Body">主体层（第 1 层）颜色。</param>
    /// <param name="Shadow">阴影层（第 2/3 层）颜色。</param>
    public readonly record struct LogoPalette(Color Body, Color Shadow);

    /// <summary>
    /// 当前图标调色板。
    /// </summary>
    public LogoPalette CurrentLogoPalette { get; private set; }

    /// <summary>
    /// 取某一层该用的颜色画刷。
    /// </summary>
    /// <remarks>
    /// 第 1 层 = 主体，第 2/3 层 = 阴影。
    /// </remarks>
    private Brush BrushForLayer(int layer) => layer == 1
        ? new SolidColorBrush(CurrentLogoPalette.Body)
        : new SolidColorBrush(CurrentLogoPalette.Shadow);

    /// <summary>
    /// 用当前调色板重建应用图标。
    /// </summary>
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

    /// <summary>
    /// 图标矢量内容（Drawing）—— 托盘渲染器直接拿它画 HICON。
    /// </summary>
    public Drawing BrandLogoDrawing { get; private set; } = new DrawingGroup();

    /// <summary>
    /// 按给定配色组装一份图标矢量内容。
    /// </summary>
    /// <remarks>
    /// 【为什么需要「按需组装」而不是只有一份固定 drawing】
    /// 应用内图标与托盘图标用的是**同一份几何、不同的一套颜色**：
    ///   · 应用内 → 主体 = 用户挑的强调色（品牌识别）
    ///   · 托盘   → 主体 = 中性灰（任务栏背景由系统决定，
    ///              用户挑的强调色在那里没有意义，
    ///              深色任务栏上需要亮色图标才看得见）
    /// 所以这里把「几何」和「颜色」拆开：几何取自资源字典（只读），
    /// 颜色由调用方给 —— 组装出的 Drawing 不进资源字典，
    /// 也就绕开了 ResourceDictionary 冻结 DynamicResource 的坑。
    /// </remarks>
    /// <param name="body">主体层（第 1 层）颜色。</param>
    /// <param name="shadow">阴影层（第 2/3 层）颜色。</param>
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

    /// <summary>
    /// 是否把外观改动写进用户配置。
    /// </summary>
    /// <remarks>
    /// 截图 / 自检会故意切主题与强调色，那些改动不该覆盖用户的选择，
    /// 所以由宿主决定开不开，默认关。
    /// </remarks>
    public bool PersistenceEnabled { get; set; }

    /// <summary>正在回灌上次保存的外观：期间的改动不再回写，避免自我覆盖。</summary>
    private bool _restoringAppearance;

    /// <summary>主题发生变化（明暗切换）时触发。</summary>
    public event EventHandler<bool>? ThemeChanged;

    /// <summary>
    /// 系统（任务栏）明暗发生变化 —— 与应用内主题无关。
    /// </summary>
    /// <remarks>
    /// 托盘图标订阅它：任务栏背景是系统决定的，
    /// 深色任务栏要用亮图标、浅色任务栏要用暗图标，
    /// 这个判断与应用内选了浅色还是深色无关。
    /// </remarks>
    public event EventHandler? SystemThemeChanged;

    /// <summary>强调色发生变化时触发。</summary>
    public event EventHandler<Color>? AccentChanged;

    /// <summary>
    /// 主题**模式**变化时触发（浅色 ↔ 深色 ↔ 系统）。
    /// 与 <see cref="ThemeChanged"/> 区分：后者是「解析后的明暗」，
    /// 前者是「用户选了什么」—— 分段控件的选中态要跟的是后者。
    /// </summary>
    public event EventHandler<AppTheme>? ThemeModeChanged;

    /// <summary>强调色**来源**变化时触发（系统 ↔ 个性）。</summary>
    public event EventHandler<AccentSource>? AccentModeChanged;

    private bool _initialized;

    /// <summary>
    /// 初始化：挂载资源字典。
    /// </summary>
    /// <remarks>
    /// 必须在任何使用 {StaticResource ...} 引用本工程样式的窗口
    /// 执行 InitializeComponent 之前调用 —— 否则样式还没合并进
    /// Application.Resources，XAML 解析会抛「找不到资源」。
    ///
    /// 幂等：重复调用不重复合并字典（--itest 与正常启动都走这里）。
    /// </remarks>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var resources = Application.Current.Resources;

        // 主题字典放索引 0（最高优先级），Accent / Controls 依次在后。
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/Tokens.Dark.xaml", UriKind.Relative)
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/Accent.xaml", UriKind.Relative)
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Themes/Controls.xaml", UriKind.Relative)
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
            Source = new Uri("Themes/BrandLogo.xaml", UriKind.Relative)
        });

        IsDark = true;

        // 品牌色必须在这里算一次。
        // 【为什么】SetThemeMode 里如果「模式没变」会直接 return，
        // 而 Mode 初值就是 Dark —— 于是「--theme=dark」这条路径上
        // RefreshBrandAccent 永远不会被调用，品牌色停留在构造函数的初始值
        // （浅蓝），图标与侧栏的 twik 文字都不是真正派生自强调色的颜色。
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

    /// <summary>
    /// 把系统主题监听挂到指定窗口上。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Initialize"/> 分开：监听需要窗口句柄，
    /// 而字典合并必须早于窗口的 InitializeComponent。
    ///
    /// 必须在窗口的 SourceInitialized 之后调用 —— 构造函数阶段
    /// WindowInteropHelper.Handle 仍是 0，拿不到 HwndSource。
    /// </remarks>
    public void AttachWindow(Window hostWindow)
    {
        // 系统主题变化监听：注册表改动后 Windows 会广播 WM_SETTINGCHANGE，
        // 由窗口钩子转发到这里。
        SystemEventsBridge.RegisterThemeChanged(hostWindow, RefreshFromSystem);
    }

    /// <summary>
    /// 重新读取系统状态并应用 —— 由 WM_SETTINGCHANGE 触发。
    /// </summary>
    /// <remarks>
    /// 【明暗与强调色必须各自独立判断】
    /// 之前写成 if / else-if，结果「主题=系统 且 色彩=系统」时
    /// 系统强调色永远不会被刷新 —— 用户改系统强调色，应用毫无反应。
    /// 现在两件事各判各的，互不排斥。
    ///
    /// 【为什么手动选过深色就不再跟随】
    /// 只有 Mode == System 才应用系统明暗。用户显式选了深色/浅色，
    /// Mode 就是 Dark/Light，此时系统怎么改都不动 —— 这正是期望行为。
    /// </remarks>
    public void RefreshFromSystem()
    {
        // 系统（任务栏）明暗可能变了 —— 托盘图标配色依赖它。
        // 【为什么不挂在 ThemeChanged 上】
        // ThemeChanged 表示「应用内明暗」，只有 Mode == System 时才会变。
        // 但任务栏的明暗是系统决定的，与应用内选浅色/深色无关 ——
        // 用户把应用设成深色、系统任务栏是浅色，托盘仍要用深色图标。
        // 所以这里无条件通知，由订阅方自己比较颜色决定要不要重绘。
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

    /// <summary>
    /// 切换主题模式。
    /// </summary>
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

    /// <summary>
    /// 应用已解析出的具体主题（Light / Dark）。
    /// </summary>
    private void ApplyResolvedTheme(AppTheme resolved)
    {
        bool newIsDark = resolved == AppTheme.Dark;
        if (newIsDark == IsDark) return;

        IsDark = newIsDark;

        var resources = Application.Current.Resources;
        resources.MergedDictionaries[ThemeSlot] = new ResourceDictionary
        {
            Source = new Uri(newIsDark ? "Themes/Tokens.Dark.xaml" : "Themes/Tokens.Light.xaml",
                             UriKind.Relative)
        };

        // 明暗切换会改变品牌色的 L 档（深色加亮 / 浅色压暗）→ 重算一次
        RefreshBrandAccent();

        // 淡强调色的不透明度也跟明暗相关 → 同样要重建
        RefreshAccentSubtle();

        ThemeChanged?.Invoke(this, IsDark);
    }

    /// <summary>
    /// 切换强调色来源。
    /// </summary>
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

    /// <summary>
    /// 设置强调色并原地改写 AccentBrush / BrandAccentBrush。
    /// </summary>
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

    /// <summary>
    /// 重建淡强调色画刷。
    ///
    /// 深色主题下用更高的不透明度（0.26），否则叠在近黑的卡片上
    /// 会淡到几乎看不见；浅色主题下用更低的（0.16），
    /// 免得在白底上糊成一片。
    /// </summary>
    private void RefreshAccentSubtle()
    {
        AccentSubtleBrush = new SolidColorBrush(AccentColor)
        {
            Opacity = IsDark ? 0.26 : 0.16,
        };
        _accentDictionary["AccentSubtleBrush"] = AccentSubtleBrush;
    }

    /// <summary>
    /// 重算品牌色（twik）并写入资源字典。
    /// </summary>
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

    /// <summary>
    /// 图标所在表面的颜色（窗口底色）—— 用于计算图标与背景的对比度。
    /// </summary>
    private Color LogoSurfaceColor => IsDark
        ? Color.FromRgb(0x09, 0x09, 0x0B)
        : Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 重算图标调色板并重建图标。
    /// </summary>
    public void RefreshLogoPalette()
    {
        CurrentLogoPalette = LogoPaletteFactory.Build(AccentColor, IsDark, LogoSurfaceColor);
        RebuildBrandLogo();
    }

    /// <summary>
    /// 按相对亮度判断颜色是「浅」还是「深」。
    /// </summary>
    private static bool IsLight(Color c)
    {
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.6;
    }
}
