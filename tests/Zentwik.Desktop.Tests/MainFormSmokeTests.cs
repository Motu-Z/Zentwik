using System.Drawing;
using System.Windows.Forms;
using Xunit;
using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;
using Zentwik.Localization;

namespace Zentwik.Desktop.Tests;

/// <summary>
/// 裸控件外壳的冒烟测试：能在 STA 线程里建出窗口、五个页签齐全、交互控件都有可读文案。
///
/// 这一版 UI 没有设计系统可测，能测的就是「结构没塌」和「点了有东西」。
/// </summary>
public sealed class MainFormSmokeTests
{
    [Fact]
    public void Form_Builds_Five_Tabs_With_Labelled_Controls()
    {
        var failure = RunOnStaThread(() =>
        {
            var catalog = LocalizationCatalog.CreateDefault();
            var themes = new ThemeService();
            var viewModel = new MainViewModel(catalog, themes);
            using var form = new MainForm(viewModel, catalog, themes, autoConnect: false);

            var tabs = Descendants<TabControl>(form).Single();
            Assert.Equal(5, tabs.TabPages.Count);

            var pages = tabs.TabPages.Cast<TabPage>().ToList();
            Assert.All(pages, page => Assert.False(string.IsNullOrWhiteSpace(page.Text)));

            var buttons = Descendants<Button>(form).ToList();
            Assert.NotEmpty(buttons);
            Assert.All(buttons, button => Assert.False(string.IsNullOrWhiteSpace(button.Text)));

            var grids = Descendants<DataGridView>(form).ToList();
            Assert.Equal(5, grids.Count);
            Assert.All(grids, grid => Assert.True(grid.Columns.Count > 0));
        });

        Assert.Null(failure);
    }

    [Fact]
    public void Culture_Change_Retranslates_Tab_Headers()
    {
        var failure = RunOnStaThread(() =>
        {
            var catalog = LocalizationCatalog.CreateDefault();
            var themes = new ThemeService();
            var viewModel = new MainViewModel(catalog, themes);
            using var form = new MainForm(viewModel, catalog, themes, autoConnect: false);

            var tabs = Descendants<TabControl>(form).Single();

            catalog.CurrentCulture = "en-US";
            Application.DoEvents();
            var english = tabs.TabPages[0].Text;

            catalog.CurrentCulture = "zh-CN";
            Application.DoEvents();
            var chinese = tabs.TabPages[0].Text;

            Assert.NotEqual(english, chinese);
            Assert.False(string.IsNullOrWhiteSpace(chinese));
        });

        Assert.Null(failure);
    }

    [Fact]
    public void Theme_Service_Clamps_Unknown_Values()
    {
        var themes = new ThemeService();
        themes.Apply("neon", "chartreuse");
        Assert.Equal("dark", themes.Theme);
        Assert.Equal("blue", themes.Accent);

        themes.Apply("light", "teal");
        Assert.Equal("light", themes.Theme);
        Assert.Equal("teal", themes.Accent);
        Assert.False(themes.IsDark);
        Assert.Equal(SystemColors.Window, themes.WindowBack);
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static Exception? RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure;
    }
}
