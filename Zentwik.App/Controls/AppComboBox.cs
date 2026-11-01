using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Zentwik.App.Controls;

/// <summary>
/// 全局下拉框。存在的唯一理由是「宽度对齐」这一件模板做不到的事。
///
/// 【为什么不直接给 ComboBox 套一个重模板就完事】
/// 下拉弹层的宽度需要等于控件宽度，否则短文案的下拉框展开后是一个只有 60px 的窄条，
/// 长文案（如「Above Normal」）被截断。系统模板用 ElementName 绑定解决这件事，
/// 但资源字典里的 ControlTemplate 没有 x:Class 命名域，{x:Bind} 不可用；
/// 退回 {Binding ElementName=...} 就是运行期反射绑定，违反「零反射」。
///
/// 因此模板只负责外观，宽度同步放在这里：OnApplyTemplate 拿到 Popup 之后
/// 把 Popup.Child 的 MinWidth 对齐控件实际宽度，并在 SizeChanged 时更新。
/// </summary>
public sealed partial class AppComboBox : ComboBox
{
    /// <summary>
    /// 关闭态显示文本。
    ///
    /// 【为什么必须自己维护这条通道】
    /// 模板里的 ContentPresenter 不能直接绑 <c>SelectionBoxItem</c>：
    /// ComboBox 基类会在运行时往同一个 ContentPresenter 的 <c>Content</c> 上写值，
    /// 模板的 TemplateBinding 会被基类那一次写入顶掉（写成 null），
    /// 结果就是「下拉框展开能看到条目，合上却一片空白」。
    /// 这里另开一条纯文本通道（DP），模板用 TextBlock 显示，与基类互不打架。
    /// </summary>
    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(
            nameof(DisplayText),
            typeof(string),
            typeof(AppComboBox),
            new PropertyMetadata(string.Empty));

    private Popup? _popup;

    public AppComboBox()
    {
        // 不设这一行，样式解析会继续用 ComboBox 的默认样式键（即系统白底模板），
        // 本类自己的隐式样式永远不会命中。
        DefaultStyleKey = typeof(AppComboBox);
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        // WinUI 3 的 ComboBox/Selector 没有可重写的 OnSelectionChanged 虚方法，
        // 只能订阅事件来同步关闭态文本。
        SelectionChanged += OnSelectionChanged;
    }

    /// <summary>当前选中项的纯文本，关闭态直接显示它。</summary>
    public string DisplayText => (string)GetValue(DisplayTextProperty);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _popup = GetTemplateChild("Popup") as Popup;
        SyncPopupWidth();
        UpdateDisplayText();
    }

    /// <summary>选中项变化时同步关闭态文本。</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDisplayText();

    private void UpdateDisplayText()
    {
        object? item = SelectedItem;

        // SelectedItem 在某些时序下还没同步过来，退回按索引取。
        if (item is null && SelectedIndex >= 0 && SelectedIndex < Items.Count)
            item = Items[SelectedIndex];

        SetValue(DisplayTextProperty, item?.ToString() ?? string.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncPopupWidth();
        UpdateDisplayText();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => SyncPopupWidth();

    private void SyncPopupWidth()
    {
        if (_popup?.Child is not FrameworkElement child)
            return;

        double width = ActualWidth;
        if (width <= 0)
            return;

        // 用 MinWidth 而不是 Width：展开后弹层仍可因超长文案被撑宽，
        // 但绝不会窄于下拉框本身（对齐感来自「不窄于」，不是「必须相等」）。
        child.MinWidth = width;
    }
}
