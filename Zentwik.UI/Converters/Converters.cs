using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Zentwik.UI.Converters;

/// <summary>
/// bool → Visibility。true 时为 Visible。
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;

        // 参数可传 "invert" 反转语义
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

/// <summary>
/// bool → Visibility。true 时为 Collapsed（用于「面板展开时隐藏占位」的反向场景）。
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Collapsed;
    }
}

/// <summary>
/// bool → 不透明度。false 时压暗（用于「整页不可用」时把内容压到背景里），
/// true 时恢复 1.0。
/// </summary>
/// <remarks>
/// 压在遮罩下面的内容要「隐约可见」而不是消失：
/// 直接绑 Visibility 会让页面塌成一片空白，看不出这里本来有什么。
/// 遮罩本身另用带 alpha 的 OverlayScrimBrush 画，不用改这里。
/// </remarks>
public sealed class DimOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool available = value is not bool b || b;
        return available ? 1.0 : 0.28;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// 集合数量 → Visibility。数量为 0 时为 Visible（用于「空结果」提示），否则 Collapsed。
/// </summary>
/// <remarks>
/// 与 <see cref="BooleanToVisibilityConverter"/> 方向相反，
/// 单独写一个而不是加参数 —— 加了参数就要在每个用法处传参，
/// 很容易传反，而「传反了空提示永远不显示」是极难自查的 bug。
/// </remarks>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// 字符串 → Visibility。空串 / null 时为 Collapsed，非空时为 Visible。
/// </summary>
/// <remarks>
/// 用于「描述留空就不画占位」的场景 ——
/// Core 的方案模型里 Note 是「留空即不显示」，不是「显示一行空白」。
/// 界面若一律画出描述行，列表就会出现一排空的灰字。
/// </remarks>
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value is not string s || s.Length == 0;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// 字符串 → bool（按值相等），用于「按钮组绑定字符串枚举值」。
/// </summary>
/// <remarks>
/// 电源页的 Boost / ASPM 是 int，用 <see cref="IntEqualsBoolConverter"/>；
/// 进程页的优先级是字符串（「高」「高于标准」…），用这个。
///
/// 同一组按钮会自动取消其他项，取消时回写 false；
/// 若把 false 当成「值 = 空串」，刚点中的档位会被兄弟项的取消动作改掉。
/// </remarks>
public sealed class StringEqualsBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string s
            && parameter is string p
            && string.Equals(s, p, StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b || !b || parameter is not string p)
        {
            return Binding.DoNothing;
        }

        return p;
    }
}

/// <summary>
/// 排序键相等 → Visibility（用于表头的 ▼ 指示箭头）。
/// </summary>
/// <remarks>
/// 排序箭头只能出现在「当前排序列」上，而且要跟着升降序换方向。
/// 写成转换器后，表头只要声明「我的键是 xxx」，
/// 箭头显示与否由绑定自动决定 —— 加一列不用改任何逻辑。
///
/// 升 / 降序用同一个向下三角 + 旋转实现（见 XAML 里的 RotateTransform），
/// 方向信息在 SortDescending 里，不额外占一个键。
/// </remarks>
public sealed class SortKeyEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string key
            && parameter is string p
            && string.Equals(key, p, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

/// <summary>
/// int → bool（按值相等），用于「分段控件绑定枚举值」。
/// </summary>
/// <remarks>
/// 一组 RadioButton 的 <c>IsChecked</c> 是 bool，
/// 而 Boost / ASPM 这类设置在契约里是 int（0–4 / 0–2）。
/// <c>ConverterParameter</c> 写要匹配的下标，例如 <c>ConverterParameter=2</c>。
///
/// 同一 GroupName 内的 RadioButton 会**自动取消**其他项的选中，
/// 于是被取消的那一项会拿 false 回写。
/// 若把 false 当成「值 = 0」，刚点中「激进」就会被兄弟项的取消动作改回「关闭」——
/// 表现为「点不中第二档」。
/// </remarks>
public sealed class IntEqualsBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int current &&
            parameter is not null &&
            int.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted))
        {
            return current == wanted;
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool b || !b ||
            parameter is null ||
            !int.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted))
        {
            return Binding.DoNothing;
        }

        return wanted;
    }
}

/// <summary>
/// 可用宽度 → 受限宽度：<c>Min(可用宽度, Parameter)</c>。
/// </summary>
/// <remarks>
/// WPF 的 <c>MaxWidth</c> 只封顶、不负责对齐：子元素在 Stretch 下被压到 MaxWidth 后，
/// 剩余空间会被平均分到两侧 —— 表现就是「设了 MaxWidth 的卡片自己跑到中间」，
/// 左边缘与下方的列表对不齐。想要「左侧对齐、最多 900、窗口变窄时跟着缩」，
/// 只能把宽度算出来：绑到父容器的实际宽度，取一个上限。
///
/// Parameter 传上限（DIP）。可用宽度还没算出来（NaN / 0）时不插手布局。
/// </remarks>
public sealed class WidthClampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || available <= 0)
        {
            return DependencyProperty.UnsetValue;
        }

        if (parameter is null ||
            !double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double max) ||
            max <= 0)
        {
            return available;
        }

        return Math.Min(available, max);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
