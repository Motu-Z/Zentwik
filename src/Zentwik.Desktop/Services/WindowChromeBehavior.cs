using System.Windows;
using System.Windows.Controls.Primitives;

namespace Zentwik.Desktop.Services;

public enum WindowChromeAction
{
    None,
    Minimize,
    MaximizeRestore,
    Close,
}

public static class WindowChromeBehavior
{
    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.RegisterAttached(
            "Action",
            typeof(WindowChromeAction),
            typeof(WindowChromeBehavior),
            new PropertyMetadata(WindowChromeAction.None, OnActionChanged));

    public static WindowChromeAction GetAction(DependencyObject obj) =>
        (WindowChromeAction)obj.GetValue(ActionProperty);

    public static void SetAction(DependencyObject obj, WindowChromeAction value) =>
        obj.SetValue(ActionProperty, value);

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnButtonClick;
        if (e.NewValue is WindowChromeAction action && action != WindowChromeAction.None)
        {
            button.Click += OnButtonClick;
        }
    }

    private static void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject dep)
        {
            return;
        }

        var window = Window.GetWindow(dep);
        if (window is null)
        {
            return;
        }

        switch (GetAction(dep))
        {
            case WindowChromeAction.Minimize:
                SystemCommands.MinimizeWindow(window);
                break;
            case WindowChromeAction.MaximizeRestore:
                if (window.WindowState == WindowState.Maximized)
                {
                    SystemCommands.RestoreWindow(window);
                }
                else
                {
                    SystemCommands.MaximizeWindow(window);
                }
                break;
            case WindowChromeAction.Close:
                SystemCommands.CloseWindow(window);
                break;
        }
    }
}
