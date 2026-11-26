using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;
using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;
using Zentwik.Localization;

namespace Zentwik.Desktop.Tests;

public sealed class AccessibilityTests
{
    [Fact]
    public void Interactive_Controls_Expose_Automation_Names()
    {
        var missing = new List<string>();

        UiThread.Run(() =>
        {
            using var host = TestWindow.Create();
            var resRefresh = Application.Current?.Resources["String.CommonRefresh"];
            var resDismiss = Application.Current?.Resources["String.CommonDismiss"];

            foreach (var element in host.Descendants())
            {
                // 页签的容器 peer 读不到标题，Narrator 走的是 TabControlAutomationPeer 的子项。
                if (!IsInteractive(element) || element is TabItem)
                {
                    continue;
                }

                var peer = UIElementAutomationPeer.CreatePeerForElement(element)
                           ?? new FrameworkElementAutomationPeer(element);
                if (string.IsNullOrWhiteSpace(peer.GetName()))
                {
                    var parent = VisualTreeHelper.GetParent(element);
                    var templatedParent = element.TemplatedParent;
                    var autoName = AutomationProperties.GetName(element);
                    var content = (element as ContentControl)?.Content;
                    missing.Add(
                        $"{element.GetType().Name}({element.Name}) " +
                        $"Parent={parent?.GetType().Name} TemplatedParent={templatedParent?.GetType().Name} " +
                        $"AutoName='{autoName}' Content='{content}' Visible={element.IsVisible} Loaded={element.IsLoaded} " +
                        $"ResRefresh='{resRefresh}' ResDismiss='{resDismiss}'");
                }
            }

            var tabs = new TabControlAutomationPeer(host.Tabs).GetChildren();
            if (tabs.Count == 0)
            {
                missing.Add("TabControl(没有页签)");
            }

            foreach (var tab in tabs)
            {
                if (string.IsNullOrWhiteSpace(tab.GetName()))
                {
                    missing.Add("TabItem(无名称)");
                }
            }
        });

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Plans_List_Realizes_Only_Visible_Rows()
    {
        var realized = -1;
        var total = 2000;

        UiThread.Run(() =>
        {
            using var host = TestWindow.Create();
            host.ViewModel.Plans.Clear();
            for (var i = 0; i < total; i++)
            {
                host.ViewModel.Plans.Add(new PlanRow("CPU", $"方案 {i}", $"rev {i}"));
            }

            host.SelectTab(1);
            host.Window.UpdateLayout();

            realized = host.Descendants()
                .OfType<ListBox>()
                .Select(list => Enumerable.Range(0, list.Items.Count)
                    .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null))
                .DefaultIfEmpty(0)
                .Max();
        });

        Assert.InRange(realized, 1, total / 4);
    }

    private static bool IsInteractive(FrameworkElement element) =>
        element is ButtonBase or ComboBox or ListBox or TabItem;

    private sealed class TestWindow : IDisposable
    {
        private TestWindow(MainWindow window, MainViewModel viewModel)
        {
            Window = window;
            ViewModel = viewModel;
        }

        public MainWindow Window { get; }

        public MainViewModel ViewModel { get; }

        public static TestWindow Create()
        {
            var catalog = LocalizationCatalog.CreateDefault();
            var themes = new ThemeService();
            new LocalizationBridge(catalog).Apply();
            themes.Apply("dark", "blue");

            var viewModel = new MainViewModel(catalog, themes);
            var window = new MainWindow(viewModel);
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();

            return new TestWindow(window, viewModel);
        }

        public TabControl Tabs => Descendants().OfType<TabControl>().First();

        public void SelectTab(int index)
        {
            Tabs.SelectedIndex = index;
            Window.UpdateLayout();
        }

        public IEnumerable<FrameworkElement> Descendants()
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(Window);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current is FrameworkElement element)
                {
                    yield return element;
                }

                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(current, i));
                }
            }
        }

        public void Dispose()
        {
            ViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Window.Close();
        }
    }
}
