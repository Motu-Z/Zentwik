using System.Drawing;
using System.Windows.Forms;
using Xunit;
using Zentwik.Desktop.ViewModels;
using Zentwik.Localization;

namespace Zentwik.Desktop.Tests;

/// <summary>
/// 裸控件外壳的冒烟测试：能在 STA 线程里建出窗口、四个页签齐全、交互控件都有可读文案。
///
/// 这一版 UI 没有设计系统可测，能测的就是「结构没塌」和「点了有东西」。
/// </summary>
public sealed class MainFormSmokeTests
{
    [Fact]
    public void Form_Builds_Four_Tabs_With_Labelled_Controls()
    {
        var failure = RunOnStaThread(() =>
        {
            var catalog = LocalizationCatalog.CreateDefault();
            var viewModel = new MainViewModel(catalog);
            using var form = new MainForm(viewModel, catalog, autoConnect: false);

            var tabs = Descendants<TabControl>(form).Single();
            Assert.Equal(4, tabs.TabPages.Count);

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
    public void Form_Has_No_Theme_Or_Accent_Surface()
    {
        var failure = RunOnStaThread(() =>
        {
            var catalog = LocalizationCatalog.CreateDefault();
            var viewModel = new MainViewModel(catalog);
            using var form = new MainForm(viewModel, catalog, autoConnect: false);

            // 五页里的「设置」页已删；主题 / 强调色 / 缩放三个下拉不再存在。
            var tabs = Descendants<TabControl>(form).Single();
            Assert.DoesNotContain(tabs.TabPages.Cast<TabPage>(), page => page.Text == "设置");

            var combos = Descendants<ComboBox>(form).ToList();
            var language = combos.Single(combo => combo.Items.Contains("zh-CN"));
            Assert.Equal(3, language.Items.Count);

            // 窗口用系统默认色，不设自定义底色。
            Assert.Equal(SystemColors.Control, form.BackColor);
        });

        Assert.Null(failure);
    }

    [Fact]
    public void Culture_Change_Retranslates_Tab_Headers()
    {
        var failure = RunOnStaThread(() =>
        {
            var catalog = LocalizationCatalog.CreateDefault();
            var viewModel = new MainViewModel(catalog);
            using var form = new MainForm(viewModel, catalog, autoConnect: false);

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
