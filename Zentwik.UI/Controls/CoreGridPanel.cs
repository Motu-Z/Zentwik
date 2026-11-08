using System;
using System.Windows;
using System.Windows.Controls;

namespace Zentwik.UI.Controls;

/// <summary>
/// 固定格子尺寸的自适应网格：格子宽高恒定、绝不拉伸，
/// 列数按子元素数量与可用宽度推导，整组水平居中。
/// </summary>
public class CoreGridPanel : Panel
{
    public static readonly DependencyProperty CellWidthProperty =
        DependencyProperty.Register(
            nameof(CellWidth),
            typeof(double),
            typeof(CoreGridPanel),
            new FrameworkPropertyMetadata(132d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CellHeightProperty =
        DependencyProperty.Register(
            nameof(CellHeight),
            typeof(double),
            typeof(CoreGridPanel),
            new FrameworkPropertyMetadata(68d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(
            nameof(Gap),
            typeof(double),
            typeof(CoreGridPanel),
            new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CenterContentProperty =
        DependencyProperty.Register(
            nameof(CenterContent),
            typeof(bool),
            typeof(CoreGridPanel),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    public double CellHeight
    {
        get => (double)GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public bool CenterContent
    {
        get => (bool)GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = InternalChildren.Count;

        if (count == 0)
        {
            return default;
        }

        double cellWidth = CellWidth;
        double cellHeight = CellHeight;
        double gap = Gap;

        int columns = ResolveColumns(count, FitColumns(availableSize.Width, cellWidth, gap, count));
        int rows = (count + columns - 1) / columns;

        var constraint = new Size(cellWidth, cellHeight);

        for (int i = 0; i < count; i++)
        {
            InternalChildren[i].Measure(constraint);
        }

        return new Size(
            columns * cellWidth + (columns - 1) * gap,
            rows * cellHeight + (rows - 1) * gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = InternalChildren.Count;

        if (count == 0)
        {
            return finalSize;
        }

        double cellWidth = CellWidth;
        double cellHeight = CellHeight;
        double gap = Gap;

        int columns = ResolveColumns(count, FitColumns(finalSize.Width, cellWidth, gap, count));

        double totalWidth = columns * cellWidth + (columns - 1) * gap;
        double offsetX = CenterContent ? Math.Max(0, (finalSize.Width - totalWidth) / 2) : 0;

        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int column = i % columns;

            InternalChildren[i].Arrange(new Rect(
                offsetX + column * (cellWidth + gap),
                row * (cellHeight + gap),
                cellWidth,
                cellHeight));
        }

        return finalSize;
    }

    /// <summary>可用宽度最多能容纳几列。</summary>
    private static int FitColumns(double available, double cellWidth, double gap, int count)
    {
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
        {
            return Math.Min(count, 4);
        }

        int fit = (int)Math.Floor((available + gap) / (cellWidth + gap));

        return Math.Min(Math.Max(fit, 1), count);
    }

    /// <summary>
    /// 行数越少越优先，但必须是能整除的列数，且不能瘦长。
    /// 找不到整除解时返回上限列数，末行自然靠左。
    /// </summary>
    private static int ResolveColumns(int count, int maxColumns)
    {
        int minRows = count >= 4 ? 2 : 1;

        for (int rows = minRows; rows <= count; rows++)
        {
            if (count % rows != 0)
            {
                continue;
            }

            int columns = count / rows;

            if (columns > maxColumns || columns < rows * 0.75)
            {
                continue;
            }

            return columns;
        }

        return maxColumns;
    }
}
