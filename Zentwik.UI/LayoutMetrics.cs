using System.Windows;

namespace Zentwik.UI;

/// <summary>
/// 全站布局契约。所有尺寸只在这里定义，XAML 通过 x:Static 引用。
/// </summary>
/// <remarks>
/// 2026-11-06 起全部改用用户提供的参考 HTML 的实测值：
/// 侧栏 72、顶栏 64、页面左右 32、面板圆角 6、行上下留白 18。
/// 旧的中性色卡片体系（页脚条 / SettingsCard / MaxWidth 1180）已整体废弃。
/// </remarks>
public static class LayoutMetrics
{
    /// <summary>左侧导航栏收缩态宽度（只有图标）。</summary>
    public const double SidebarWidth = 72;

    /// <summary>左侧导航栏展开态宽度（图标 + 文字）。</summary>
    public const double SidebarExpandedWidth = 220;

    /// <summary>导航项高度。</summary>
    public const double NavItemHeight = 48;

    /// <summary>导航图标左缘：图标 24 宽，中线落在 36，与 logo 中线对齐。</summary>
    public const double NavIconInset = 24;

    /// <summary>导航文字与图标的间距。</summary>
    public const double NavTextGap = 20;

    /// <summary>顶栏高度：标题 + 右侧动作，底边一条 1px 分割线。</summary>
    public const double TopBarHeight = 64;

    /// <summary>页面左右内边距，顶栏与内容共用，保证左边缘是一条线。</summary>
    public const double PageSidePadding = 32;

    /// <summary>面板圆角。</summary>
    public const double PanelRadius = 6;

    /// <summary>
    /// 窗口最小尺寸。
    /// 宽度按「侧栏展开」算：220 + 内容区 952，否则展开时进程表最右列会被挤出界。
    /// </summary>
    public const double WindowMinWidth = 1172;

    public const double WindowMinHeight = 700;

    /// <summary>
    /// 进程页右侧抽屉宽度。可收起到 0。
    /// 取 320 而非参考稿的 360：最小窗宽下 360 会把进程名列压到 150 以下。
    /// </summary>
    public const double ProcessDrawerWidth = 320;

    /// <summary>
    /// 日志页右侧控制中心宽度。可收起到 0。
    /// 与进程抽屉同宽，但日志区在最小窗宽下仍能装下「时间 + 级别 + 正文」三列。
    /// </summary>
    public const double LogStatsWidth = 340;

    /// <summary>
    /// 单列表单页（调校 / 电源）的内容最大宽度。
    /// </summary>
    /// <remarks>
    /// 这个值是**封顶**，不是固定宽度：内容块跟着可用宽度一起长大
    /// （两侧各留 32），只有超过这里才停下居中。
    /// 定死宽度在宽屏上会缩成一条，顶满又会把行标题和右侧控件拉得太远。
    /// </remarks>
    public const double FormContentWidth = 1320;

    /// <summary>通用页「设置列 + 关于栏」作为一个整体居中时的最大宽度。</summary>
    public const double SettingsGroupWidth = 1280;

    /// <summary>通用页设置列宽度（整组 1280 减去 关于栏 380 与 1px 分栏线）。</summary>
    public const double SettingsFormWidth = 899;

    /// <summary>通用页关于栏宽度。</summary>
    public const double AboutPaneWidth = 380;

    /// <summary>关于栏的列宽。GridLength 不能直接吃 x:Static 的 double，这里给出成型值。</summary>
    public static GridLength AboutPane => new(AboutPaneWidth);

    /// <summary>核心格宽度的参考值；实际布局由 Controls/CoreGridPanel 决定。</summary>
    public const double CoreCellWidth = 132;

    /// <summary>核心格高度的参考值。</summary>
    public const double CoreCellHeight = 78;

    /// <summary>核心格间距的参考值。</summary>
    public const double CoreCellGap = 8;

    /// <summary>调校页矩阵列数（16 核 4×4）。</summary>
    public const int CoreColumns = 4;

    /// <summary>
    /// 矩阵整体宽度 = 列数 ×（格宽 + 间距）。
    /// 必须限死：WrapPanel 会按可用宽度塞满，900 下会排成 6 列而不是 4 列。
    /// </summary>
    public const double CoreMatrixWidth = CoreColumns * (CoreCellWidth + CoreCellGap);

    /// <summary>竖向滚动条占位：表头与行都要减掉它，否则固定列整体左移。</summary>
    public const double ScrollBarGutter = 6;

    // ---- 进程表列宽：只有进程名列吃剩余宽度 ----

    public static GridLength TablePid => new(56);

    /// <summary>进程名 + 路径：唯一弹性列。</summary>
    public static GridLength TableName => new(1, GridUnitType.Star);

    public static GridLength TableCpu => new(72);

    /// <summary>绑定目标：下拉要装下「CCD0 物理核心」+ 箭头。</summary>
    public static GridLength TableMask => new(136);

    public static GridLength TablePriority => new(120);

    public const double TableNameMinWidth = 160;
}
