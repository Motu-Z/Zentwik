using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Zentwik.UI.Converters;

/// <summary>输入框的统一附加行为：占位提示、错误态、Esc 清空、聚焦全选</summary>
public static class InputAssist
{
    // 占位提示

    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.RegisterAttached(
            "Watermark", typeof(string), typeof(InputAssist),
            new PropertyMetadata(string.Empty, OnWatermarkChanged));

    public static string GetWatermark(DependencyObject obj) => (string)obj.GetValue(WatermarkProperty);

    public static void SetWatermark(DependencyObject obj, string value) => obj.SetValue(WatermarkProperty, value);

    /// <summary>模板内部状态：Text 是否有内容</summary>
    public static readonly DependencyProperty HasTextProperty =
        DependencyProperty.RegisterAttached(
            "HasText", typeof(bool), typeof(InputAssist),
            new PropertyMetadata(false));

    public static bool GetHasText(DependencyObject obj) => (bool)obj.GetValue(HasTextProperty);

    public static void SetHasText(DependencyObject obj, bool value) => obj.SetValue(HasTextProperty, value);

    private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.TextChanged -= OnTextChanged;
        box.TextChanged += OnTextChanged;
        SetHasText(box, !string.IsNullOrEmpty(box.Text));
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            SetHasText(box, !string.IsNullOrEmpty(box.Text));
    }

    // 错误态

    /// <summary>非空即错误态：输入框红边 + ToolTip 显示这段文案。</summary>
    public static readonly DependencyProperty ErrorTextProperty =
        DependencyProperty.RegisterAttached(
            "ErrorText", typeof(string), typeof(InputAssist),
            new PropertyMetadata(string.Empty, OnErrorTextChanged));

    public static string GetErrorText(DependencyObject obj) => (string)obj.GetValue(ErrorTextProperty);

    public static void SetErrorText(DependencyObject obj, string value) => obj.SetValue(ErrorTextProperty, value);

    /// <summary>模板用的布尔态，由 ErrorText 派生，避免模板里对字符串做「非空」判断。</summary>
    public static readonly DependencyProperty HasErrorProperty =
        DependencyProperty.RegisterAttached(
            "HasError", typeof(bool), typeof(InputAssist),
            new PropertyMetadata(false));

    public static bool GetHasError(DependencyObject obj) => (bool)obj.GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject obj, bool value) => obj.SetValue(HasErrorProperty, value);

    /// <summary>错误态接管 ToolTip 前的原值，错误清除后还回去。</summary>
    public static readonly DependencyProperty OriginalToolTipProperty =
        DependencyProperty.RegisterAttached(
            "OriginalToolTip", typeof(object), typeof(InputAssist),
            new PropertyMetadata(null));

    public static object? GetOriginalToolTip(DependencyObject obj) => obj.GetValue(OriginalToolTipProperty);

    public static void SetOriginalToolTip(DependencyObject obj, object? value) => obj.SetValue(OriginalToolTipProperty, value);

    private static void OnErrorTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        string text = e.NewValue as string ?? string.Empty;
        bool invalid = text.Length > 0;

        SetHasError(box, invalid);

        if (invalid)
        {
            // 只在第一次进入错误态时快照，重复报错不会把错误文案当成原 ToolTip。
            if (!GetHasErrorSnapshot(box))
            {
                SetOriginalToolTip(box, box.ToolTip);
                SetHasErrorSnapshot(box, true);
            }

            box.ToolTip = text;
        }
        else if (GetHasErrorSnapshot(box))
        {
            box.ToolTip = GetOriginalToolTip(box);
            SetHasErrorSnapshot(box, false);
        }
    }

    public static readonly DependencyProperty HasErrorSnapshotProperty =
        DependencyProperty.RegisterAttached(
            "HasErrorSnapshot", typeof(bool), typeof(InputAssist),
            new PropertyMetadata(false));

    public static bool GetHasErrorSnapshot(DependencyObject obj) => (bool)obj.GetValue(HasErrorSnapshotProperty);

    public static void SetHasErrorSnapshot(DependencyObject obj, bool value) => obj.SetValue(HasErrorSnapshotProperty, value);

    // 键盘操作

    /// <summary>Esc 清空文本；框本来就空时不吞事件，留给浮层处理关闭。</summary>
    public static readonly DependencyProperty ClearOnEscapeProperty =
        DependencyProperty.RegisterAttached(
            "ClearOnEscape", typeof(bool), typeof(InputAssist),
            new PropertyMetadata(false, OnClearOnEscapeChanged));

    public static bool GetClearOnEscape(DependencyObject obj) => (bool)obj.GetValue(ClearOnEscapeProperty);

    public static void SetClearOnEscape(DependencyObject obj, bool value) => obj.SetValue(ClearOnEscapeProperty, value);

    private static void OnClearOnEscapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.KeyDown -= OnEscapeKeyDown;

        if (e.NewValue is true)
            box.KeyDown += OnEscapeKeyDown;
    }

    private static void OnEscapeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox box || box.Text.Length == 0)
            return;

        box.Clear();
        e.Handled = true;
    }

    /// <summary>聚焦即全选：数值框与行内改名框用，避免用户先手动删旧值。</summary>
    public static readonly DependencyProperty SelectAllOnFocusProperty =
        DependencyProperty.RegisterAttached(
            "SelectAllOnFocus", typeof(bool), typeof(InputAssist),
            new PropertyMetadata(false, OnSelectAllOnFocusChanged));

    public static bool GetSelectAllOnFocus(DependencyObject obj) => (bool)obj.GetValue(SelectAllOnFocusProperty);

    public static void SetSelectAllOnFocus(DependencyObject obj, bool value) => obj.SetValue(SelectAllOnFocusProperty, value);

    private static void OnSelectAllOnFocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.GotKeyboardFocus -= OnSelectAllFocus;

        if (e.NewValue is true)
            box.GotKeyboardFocus += OnSelectAllFocus;
    }

    private static void OnSelectAllFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        // 延后一拍：WPF 在 GotKeyboardFocus 之后还会处理鼠标按下的选区，
        // 立刻 SelectAll 会被随后的鼠标事件覆盖掉。
        _ = box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(box.SelectAll));
    }
}
