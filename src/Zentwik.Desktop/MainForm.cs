using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Input;
using Zentwik.Desktop.ViewModels;
using Zentwik.Localization;

namespace Zentwik.Desktop;

/// <summary>
/// 裸控件主窗口：一个 TabControl 四页，只用 Label / Button / DataGridView / ComboBox / TextBox。
///
/// 约定（这一版刻意为之）：
/// - 不写自绘、不写布局引擎、不加动画、不做设计系统、不换肤；
/// - 颜色一律交给系统默认，只有「系统色」，没有主题、强调色、缩放；
/// - 所有交互都是「点按钮 → 调命令 → 命令跑完 → 属性变了 → 重填控件」。
///
/// 视图模型本身不引用任何控件类型，所以将来换成别的外壳时这一层可以直接扔掉。
/// </summary>
public sealed class MainForm : Form
{
    private readonly MainViewModel _vm;
    private readonly LocalizationCatalog _catalog;

    /// <summary>是否在窗口显示后自动连接代理。测试构造时传 false。</summary>
    private readonly bool _autoConnect;

    private readonly List<(Button Button, ICommand Command)> _commands = [];
    private readonly List<(Control Target, string Key)> _texts = [];
    private readonly List<(DataGridViewColumn Column, string Key)> _headers = [];

    /// <summary>重建列表行时置位，避免 SelectionChanged 把视图模型的选中项刷成 null。</summary>
    private bool _syncing;

    // 顶栏
    private readonly Label _statusLabel = new() { AutoSize = true, Padding = new Padding(0, 4, 16, 4) };
    private readonly Label _runtimeLabel = new() { AutoSize = true };
    private readonly Label _sceneLabel = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
    private readonly Panel _noticePanel = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _noticeLabel = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private Button _dismissButton = null!;

    // 监控
    private readonly Label _cpuValue = MetricValue();
    private readonly Label _memValue = MetricValue();
    private readonly Label _procValue = MetricValue();
    private readonly Label _coreValue = MetricValue();
    private readonly Label _cpuCaption = Caption();
    private readonly Label _memCaption = Caption();
    private readonly Label _procCaption = Caption();
    private readonly Label _coreCaption = Caption();
    private readonly DataGridView _capsGrid = NewGrid(("能力", 60), ("状态", 30), ("详情", 110));
    private Button _refreshButton = null!;

    // 方案
    private readonly DataGridView _plansGrid = NewGrid(("类型", 35), ("名称", 60), ("说明", 110), ("状态", 30));
    private Button _applyPlanButton = null!;
    private Button _revertPlanButton = null!;
    private Button _deletePlanButton = null!;
    private Button _togglePlanFormButton = null!;
    private readonly Panel _planForm = new() { Dock = DockStyle.Fill, Visible = false, AutoSize = true };
    private readonly ComboBox _planKindCombo = NewCombo();
    private readonly TextBox _planIdBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _planNameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _planSpecBox = new() { Dock = DockStyle.Fill };
    private Button _savePlanButton = null!;
    private Button _cancelPlanButton = null!;
    private Label _planKindCaption = Caption();
    private Label _planIdCaption = Caption();
    private Label _planNameCaption = Caption();
    private Label _planSpecCaption = Caption();

    // 场景
    private readonly DataGridView _scenesGrid = NewGrid(("名称", 60), ("优先级", 25), ("目标", 60), ("启用", 25), ("活动", 25), ("最近结果", 80));
    private Button _activateSceneButton = null!;
    private Button _deactivateSceneButton = null!;
    private Button _toggleSceneButton = null!;
    private Button _deleteSceneButton = null!;
    private Button _toggleSceneFormButton = null!;
    private readonly Panel _sceneForm = new() { Dock = DockStyle.Fill, Visible = false, AutoSize = true };
    private readonly TextBox _sceneIdBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _sceneNameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _scenePriorityBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _scenePlanCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _sceneTriggerCombo = NewCombo();
    private readonly TextBox _sceneTriggerArgBox = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _restoreOnExitCheck = new() { AutoSize = true };
    private Button _saveSceneButton = null!;
    private Button _cancelSceneButton = null!;
    private Label _sceneIdCaption = Caption();
    private Label _sceneNameCaption = Caption();
    private Label _scenePriorityCaption = Caption();
    private Label _scenePlanCaption = Caption();
    private Label _sceneTriggerCaption = Caption();
    private Label _sceneTriggerArgCaption = Caption();

    // 调校
    private readonly Label _tuningSummaryLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _intelSummaryLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly DataGridView _coresGrid = NewGrid(("核心", 40), ("Curve Optimizer", 60));
    private readonly DataGridView _ledgerGrid = NewGrid(("目标", 60), ("持有者", 40), ("状态", 40), ("取值", 120), ("失败原因", 80));
    private Button _refreshTuningButton = null!;
    private Button _releaseButton = null!;
    private SplitContainer? _tuningSplit;

    // 语言（顶栏右侧，唯一保留的界面选项）
    private readonly ComboBox _languageCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 120,
        Anchor = AnchorStyles.Left,
    };
    private readonly Label _languageCaption = Caption();

    // 页签
    private readonly TabPage _monitorPage = new();
    private readonly TabPage _plansPage = new();
    private readonly TabPage _scenesPage = new();
    private readonly TabPage _tuningPage = new();

    // 分组框
    private readonly GroupBox _metricBox = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly GroupBox _capsBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _coresBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _ledgerBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _tuningBox = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly GroupBox _planFormBox = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly GroupBox _sceneFormBox = new() { Dock = DockStyle.Top, AutoSize = true };

    public MainForm(MainViewModel viewModel, LocalizationCatalog catalog, bool autoConnect = true)
    {
        _vm = viewModel;
        _catalog = catalog;
        _autoConnect = autoConnect;

        SuspendLayout();

        Text = "Zentwik";
        // 不设自定义字体，用系统默认；缩放只按 DPI 走，避免按字体度量再算一遍把整窗缩小。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 580);
        ClientSize = new Size(1120, 760);

        BuildMonitorPage();
        BuildPlansPage();
        BuildScenesPage();
        BuildTuningPage();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange([_monitorPage, _plansPage, _scenesPage, _tuningPage]);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var statusBar = BuildStatusBar();
        root.Controls.Add(statusBar, 0, 0);
        root.Controls.Add(_noticePanel, 0, 1);
        root.Controls.Add(tabs, 0, 2);
        Controls.Add(root);

        ResumeLayout(performLayout: true);

        WireUp();
        ApplyTexts();
        RefreshCommandStates();
        ReloadAll();
    }

    // ---------------------------------------------------------------- 顶栏

    private Control BuildStatusBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(2, 2, 2, 6),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var labels = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        labels.Controls.Add(_statusLabel);
        labels.Controls.Add(_sceneLabel);

        var language = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _languageCaption.Margin = new Padding(0, 6, 6, 0);
        _languageCombo.Margin = new Padding(0, 2, 0, 0);
        language.Controls.Add(_languageCaption);
        language.Controls.Add(_languageCombo);

        bar.Controls.Add(labels, 0, 0);
        bar.Controls.Add(language, 1, 0);
        return bar;
    }

    private void BuildNoticePanel()
    {
        _noticePanel.Dock = DockStyle.Fill;
        _noticePanel.AutoSize = true;
        _noticePanel.Padding = new Padding(8, 6, 8, 6);
        _noticePanel.BorderStyle = BorderStyle.FixedSingle;

        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _noticeLabel.Dock = DockStyle.Fill;
        _noticeLabel.AutoSize = true;
        _noticeLabel.TextAlign = ContentAlignment.MiddleLeft;

        _dismissButton = new Button { AutoSize = true, FlatStyle = FlatStyle.System };
        _dismissButton.Click += (_, _) => _vm.DismissNoticeCommand.Execute(null);

        row.Controls.Add(_noticeLabel, 0, 0);
        row.Controls.Add(_dismissButton, 1, 0);
        _noticePanel.Controls.Add(row);
    }

    // ---------------------------------------------------------------- 监控页

    private void BuildMonitorPage()
    {
        _monitorPage.Padding = new Padding(8);

        var info = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 5 };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
        {
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _runtimeLabel.Margin = new Padding(0, 2, 0, 10);
        info.Controls.Add(_runtimeLabel, 0, 0);
        info.SetColumnSpan(_runtimeLabel, 2);
        AddMetricRow(info, 1, _cpuCaption, _cpuValue);
        AddMetricRow(info, 2, _memCaption, _memValue);
        AddMetricRow(info, 3, _procCaption, _procValue);
        AddMetricRow(info, 4, _coreCaption, _coreValue);
        _metricBox.Controls.Add(info);

        _refreshButton = new Button { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

        _capsBox.Controls.Add(_capsGrid);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.Add(_refreshButton);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_metricBox, 0, 0);
        root.Controls.Add(_capsBox, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        _monitorPage.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 方案页

    private void BuildPlansPage()
    {
        _plansPage.Padding = new Padding(8);

        _applyPlanButton = new Button { AutoSize = true };
        _revertPlanButton = new Button { AutoSize = true };
        _deletePlanButton = new Button { AutoSize = true };
        _togglePlanFormButton = new Button { AutoSize = true };
        _savePlanButton = new Button { AutoSize = true };
        _cancelPlanButton = new Button { AutoSize = true };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_applyPlanButton, _revertPlanButton, _deletePlanButton, _togglePlanFormButton]);

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, AutoSize = true };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(form, 0, _planKindCaption, _planKindCombo);
        AddRow(form, 1, _planIdCaption, _planIdBox);
        AddRow(form, 2, _planNameCaption, _planNameBox);
        AddRow(form, 3, _planSpecCaption, _planSpecBox);
        var formButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        formButtons.Controls.AddRange([_savePlanButton, _cancelPlanButton]);
        form.Controls.Add(formButtons, 1, 4);
        _planFormBox.Controls.Add(form);
        _planForm.Controls.Add(_planFormBox);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_plansGrid, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        root.Controls.Add(_planForm, 0, 2);
        _plansPage.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 场景页

    private void BuildScenesPage()
    {
        _scenesPage.Padding = new Padding(8);

        _activateSceneButton = new Button { AutoSize = true };
        _deactivateSceneButton = new Button { AutoSize = true };
        _toggleSceneButton = new Button { AutoSize = true };
        _deleteSceneButton = new Button { AutoSize = true };
        _toggleSceneFormButton = new Button { AutoSize = true };
        _saveSceneButton = new Button { AutoSize = true };
        _cancelSceneButton = new Button { AutoSize = true };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_activateSceneButton, _deactivateSceneButton, _toggleSceneButton, _deleteSceneButton, _toggleSceneFormButton]);

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, AutoSize = true };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(form, 0, _sceneIdCaption, _sceneIdBox);
        AddRow(form, 1, _sceneNameCaption, _sceneNameBox);
        AddRow(form, 2, _scenePriorityCaption, _scenePriorityBox);
        AddRow(form, 3, _scenePlanCaption, _scenePlanCombo);
        AddRow(form, 4, _sceneTriggerCaption, _sceneTriggerCombo);
        AddRow(form, 5, _sceneTriggerArgCaption, _sceneTriggerArgBox);
        form.Controls.Add(_restoreOnExitCheck, 1, 6);
        var formButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        formButtons.Controls.AddRange([_saveSceneButton, _cancelSceneButton]);
        form.Controls.Add(formButtons, 1, 7);
        _sceneFormBox.Controls.Add(form);
        _sceneForm.Controls.Add(_sceneFormBox);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_scenesGrid, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        root.Controls.Add(_sceneForm, 0, 2);
        _scenesPage.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 调校页

    private void BuildTuningPage()
    {
        _tuningPage.Padding = new Padding(8);

        _refreshTuningButton = new Button { AutoSize = true };
        _releaseButton = new Button { AutoSize = true };

        var summaries = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        summaries.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summaries.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaries.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaries.Controls.Add(_tuningSummaryLabel, 0, 0);
        summaries.Controls.Add(_intelSummaryLabel, 0, 1);
        _tuningBox.Controls.Add(summaries);

        _coresBox.Controls.Add(_coresGrid);
        _ledgerBox.Controls.Add(_ledgerGrid);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_refreshTuningButton, _releaseButton]);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        _tuningSplit = split;
        split.Panel1.Controls.Add(_coresBox);
        split.Panel2.Controls.Add(_ledgerBox);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_tuningBox, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        _tuningPage.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 事件挂接

    private void WireUp()
    {
        BuildNoticePanel();

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Capabilities.CollectionChanged += (_, _) => ReloadCaps();
        _vm.Plans.CollectionChanged += (_, _) => ReloadPlans();
        _vm.Scenes.CollectionChanged += (_, _) => ReloadScenes();
        _vm.CoreOffsets.CollectionChanged += (_, _) => ReloadCores();
        _vm.LedgerEntries.CollectionChanged += (_, _) => ReloadLedger();

        _capsGrid.SelectionChanged += (_, _) => { };
        _plansGrid.SelectionChanged += OnPlansSelected;
        _scenesGrid.SelectionChanged += OnScenesSelected;
        _ledgerGrid.SelectionChanged += OnLedgerSelected;

        Bind(_refreshButton, _vm.RefreshCommand);
        Bind(_applyPlanButton, _vm.ApplyPlanCommand);
        Bind(_revertPlanButton, _vm.RevertPlanCommand);
        Bind(_deletePlanButton, _vm.DeletePlanCommand);
        Bind(_togglePlanFormButton, _vm.ToggleCreatePlanCommand);
        Bind(_savePlanButton, _vm.CreatePlanCommand);
        Bind(_activateSceneButton, _vm.ActivateSceneCommand);
        Bind(_deactivateSceneButton, _vm.DeactivateSceneCommand);
        Bind(_toggleSceneButton, _vm.ToggleSceneEnabledCommand);
        Bind(_deleteSceneButton, _vm.DeleteSceneCommand);
        Bind(_toggleSceneFormButton, _vm.ToggleCreateSceneCommand);
        Bind(_saveSceneButton, _vm.CreateSceneCommand);
        Bind(_refreshTuningButton, _vm.RefreshTuningCommand);
        Bind(_releaseButton, _vm.ReleaseTakeoverCommand);

        _cancelPlanButton.Click += (_, _) => _vm.IsCreatePlanOpen = false;
        _cancelSceneButton.Click += (_, _) => _vm.IsCreateSceneOpen = false;

        _planKindCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) { return; }
            if (_planKindCombo.SelectedIndex >= 0)
            {
                _vm.NewPlanKind = _vm.PlanKinds[_planKindCombo.SelectedIndex];
            }
        };
        _planIdBox.TextChanged += (_, _) => { if (!_syncing) { _vm.NewPlanId = _planIdBox.Text; } };
        _planNameBox.TextChanged += (_, _) => { if (!_syncing) { _vm.NewPlanName = _planNameBox.Text; } };
        _planSpecBox.TextChanged += (_, _) => { if (!_syncing) { _vm.NewPlanSpec = _planSpecBox.Text; } };

        _sceneIdBox.TextChanged += (_, _) => { if (!_syncing) { _vm.NewSceneId = _sceneIdBox.Text; } };
        _sceneNameBox.TextChanged += (_, _) => { if (!_syncing) { _vm.NewSceneName = _sceneNameBox.Text; } };
        _scenePriorityBox.TextChanged += (_, _) => { if (!_syncing) { _vm.NewScenePriority = _scenePriorityBox.Text; } };
        _scenePlanCombo.TextChanged += (_, _) => { if (!_syncing) { _vm.NewScenePlanId = _scenePlanCombo.Text; } };
        _sceneTriggerArgBox.TextChanged += (_, _) => { if (!_syncing) { _vm.NewSceneTriggerArg = _sceneTriggerArgBox.Text; } };
        _restoreOnExitCheck.CheckedChanged += (_, _) => { if (!_syncing) { _vm.NewSceneRestoreOnExit = _restoreOnExitCheck.Checked; } };
        _sceneTriggerCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _sceneTriggerCombo.SelectedIndex < 0) { return; }
            _vm.NewSceneTriggerKind = _vm.TriggerKinds[_sceneTriggerCombo.SelectedIndex];
        };

        _languageCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _languageCombo.SelectedItem is string culture)
            {
                _vm.SelectedLanguage = culture;
            }
        };

        _catalog.CultureChanged += OnCultureChanged;

        // 属性文案先注册，ApplyTexts 才知道该翻哪个键。
        RegisterTexts();
    }

    private void RegisterTexts()
    {
        Register(_monitorPage, "nav.monitor");
        Register(_plansPage, "nav.plans");
        Register(_scenesPage, "nav.scenes");
        Register(_tuningPage, "nav.tuning");

        Register(_cpuCaption, "monitor.cpu");
        Register(_memCaption, "monitor.memory");
        Register(_procCaption, "monitor.processes");
        Register(_coreCaption, "monitor.cores");
        Register(_metricBox, "monitor.title");
        Register(_capsBox, "caps.title");
        Register(_refreshButton, "common.refresh");
        Register(_dismissButton, "common.dismiss");

        Register(_applyPlanButton, "plans.apply");
        Register(_revertPlanButton, "plans.revert");
        Register(_deletePlanButton, "plans.delete");
        Register(_togglePlanFormButton, "plans.create_toggle");
        Register(_savePlanButton, "plans.save");
        Register(_cancelPlanButton, "common.dismiss");
        Register(_planIdCaption, "plans.id");
        Register(_planNameCaption, "plans.name");
        Register(_planKindCaption, "plans.kind");
        Register(_planSpecCaption, "plans.spec");
        Register(_planFormBox, "plans.create");

        Register(_activateSceneButton, "scenes.activate");
        Register(_deactivateSceneButton, "scenes.deactivate");
        Register(_toggleSceneButton, "scenes.toggle");
        Register(_deleteSceneButton, "scenes.delete");
        Register(_toggleSceneFormButton, "scenes.create_toggle");
        Register(_saveSceneButton, "scenes.save");
        Register(_cancelSceneButton, "common.dismiss");
        Register(_sceneIdCaption, "scenes.id");
        Register(_sceneNameCaption, "scenes.name");
        Register(_scenePriorityCaption, "scenes.priority");
        Register(_scenePlanCaption, "scenes.plan_id");
        Register(_sceneTriggerCaption, "scenes.trigger_kind");
        Register(_sceneTriggerArgCaption, "scenes.trigger_arg");
        Register(_restoreOnExitCheck, "scenes.restore_on_exit");
        Register(_sceneFormBox, "scenes.title");

        Register(_tuningBox, "tuning.channel");
        Register(_coresBox, "tuning.cores_title");
        Register(_ledgerBox, "tuning.ledger_title");
        Register(_refreshTuningButton, "common.refresh");
        Register(_releaseButton, "tuning.release");

        Register(_languageCaption, "settings.language");

        AddHeaders(_capsGrid, "caps.id", "caps.state", "caps.detail");
        AddHeaders(_plansGrid, "plans.kind", "plans.name", "plans.caption", "plans.state");
        AddHeaders(_scenesGrid, "scenes.name", "scenes.priority", "scenes.target", "scenes.enabled", "scenes.active", "scenes.outcome");
        AddHeaders(_coresGrid, "tuning.core", "tuning.offset");
        AddHeaders(_ledgerGrid, "ledger.target", "ledger.owner", "ledger.status", "ledger.values", "ledger.failure");
    }

    private void AddHeaders(DataGridView grid, params string[] keys)
    {
        for (var i = 0; i < keys.Length && i < grid.Columns.Count; i++)
        {
            _headers.Add((grid.Columns[i], keys[i]));
        }
    }

    private void Register(Control target, string key) => _texts.Add((target, key));

    private void ApplyTexts()
    {
        foreach (var (target, key) in _texts)
        {
            target.Text = _catalog[key];
        }

        foreach (var (column, key) in _headers)
        {
            column.HeaderText = _catalog[key];
        }

        _syncing = true;
        try
        {
            RebuildPlanKinds();
            RebuildTriggerKinds();
            RebuildLanguageCombo();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RebuildPlanKinds()
    {
        var current = _vm.NewPlanKind;
        _planKindCombo.Items.Clear();
        foreach (var kind in _vm.PlanKinds)
        {
            _planKindCombo.Items.Add(_catalog["plans.kind." + kind.ToLowerInvariant()]);
        }

        var index = _vm.PlanKinds.ToList().IndexOf(current);
        _planKindCombo.SelectedIndex = index < 0 ? 0 : index;
    }

    private void RebuildTriggerKinds()
    {
        var current = _vm.NewSceneTriggerKind;
        _sceneTriggerCombo.Items.Clear();
        _sceneTriggerCombo.Items.AddRange([.. _vm.TriggerKinds]);
        var index = _vm.TriggerKinds.ToList().IndexOf(current);
        _sceneTriggerCombo.SelectedIndex = index < 0 ? 0 : index;
    }

    private void RebuildLanguageCombo()
    {
        _languageCombo.Items.Clear();
        _languageCombo.Items.AddRange([.. _vm.Languages]);
        _languageCombo.SelectedItem = _vm.SelectedLanguage;
    }

    private void OnCultureChanged()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ApplyTexts));
            return;
        }

        ApplyTexts();
    }

    // ---------------------------------------------------------------- 属性变化

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnViewModelChanged(sender, e)));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.StatusText):
                _statusLabel.Text = _vm.StatusText;
                break;
            case nameof(MainViewModel.RuntimeSummary):
                _runtimeLabel.Text = _vm.RuntimeSummary;
                break;
            case nameof(MainViewModel.ActiveSceneText):
                _sceneLabel.Text = _vm.ActiveSceneText;
                break;
            case nameof(MainViewModel.CpuText):
                _cpuValue.Text = _vm.CpuText;
                break;
            case nameof(MainViewModel.MemoryText):
                _memValue.Text = _vm.MemoryText;
                break;
            case nameof(MainViewModel.ProcessText):
                _procValue.Text = _vm.ProcessText;
                break;
            case nameof(MainViewModel.CoreText):
                _coreValue.Text = _vm.CoreText;
                break;
            case nameof(MainViewModel.TuningSummary):
                _tuningSummaryLabel.Text = _vm.TuningSummary;
                break;
            case nameof(MainViewModel.IntelTuningSummary):
                _intelSummaryLabel.Text = _vm.IntelTuningSummary;
                break;
            case nameof(MainViewModel.NoticeText):
            case nameof(MainViewModel.NoticeIsError):
            case nameof(MainViewModel.HasNotice):
                UpdateNotice();
                break;
            case nameof(MainViewModel.IsCreatePlanOpen):
                _planForm.Visible = _vm.IsCreatePlanOpen;
                break;
            case nameof(MainViewModel.IsCreateSceneOpen):
                _sceneForm.Visible = _vm.IsCreateSceneOpen;
                break;
            case nameof(MainViewModel.NewPlanKind):
            case nameof(MainViewModel.NewPlanId):
            case nameof(MainViewModel.NewPlanName):
            case nameof(MainViewModel.NewPlanSpec):
                SyncPlanForm();
                break;
            case nameof(MainViewModel.NewSceneId):
            case nameof(MainViewModel.NewSceneName):
            case nameof(MainViewModel.NewScenePriority):
            case nameof(MainViewModel.NewScenePlanId):
            case nameof(MainViewModel.NewSceneTriggerKind):
            case nameof(MainViewModel.NewSceneTriggerArg):
            case nameof(MainViewModel.NewSceneRestoreOnExit):
                SyncSceneForm();
                break;
        }

        RefreshCommandStates();
    }

    private void UpdateNotice()
    {
        _noticePanel.Visible = _vm.HasNotice;
        var text = _vm.NoticeText ?? string.Empty;
        _noticeLabel.Text = _vm.NoticeIsError ? _catalog["common.error_prefix"] + text : text;
    }

    private void SyncPlanForm()
    {
        _syncing = true;
        try
        {
            if (_planIdBox.Text != _vm.NewPlanId) { _planIdBox.Text = _vm.NewPlanId; }
            if (_planNameBox.Text != _vm.NewPlanName) { _planNameBox.Text = _vm.NewPlanName; }
            if (_planSpecBox.Text != _vm.NewPlanSpec) { _planSpecBox.Text = _vm.NewPlanSpec; }
            var index = _vm.PlanKinds.ToList().IndexOf(_vm.NewPlanKind);
            if (index >= 0 && _planKindCombo.SelectedIndex != index) { _planKindCombo.SelectedIndex = index; }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncSceneForm()
    {
        _syncing = true;
        try
        {
            if (_sceneIdBox.Text != _vm.NewSceneId) { _sceneIdBox.Text = _vm.NewSceneId; }
            if (_sceneNameBox.Text != _vm.NewSceneName) { _sceneNameBox.Text = _vm.NewSceneName; }
            if (_scenePriorityBox.Text != _vm.NewScenePriority) { _scenePriorityBox.Text = _vm.NewScenePriority; }
            if (_scenePlanCombo.Text != _vm.NewScenePlanId) { _scenePlanCombo.Text = _vm.NewScenePlanId; }
            if (_sceneTriggerArgBox.Text != _vm.NewSceneTriggerArg) { _sceneTriggerArgBox.Text = _vm.NewSceneTriggerArg; }
            if (_restoreOnExitCheck.Checked != _vm.NewSceneRestoreOnExit) { _restoreOnExitCheck.Checked = _vm.NewSceneRestoreOnExit; }
            var index = _vm.TriggerKinds.ToList().IndexOf(_vm.NewSceneTriggerKind);
            if (index >= 0 && _sceneTriggerCombo.SelectedIndex != index) { _sceneTriggerCombo.SelectedIndex = index; }
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------------------------------------------------------- 列表重填

    private void ReloadAll()
    {
        ReloadCaps();
        ReloadPlans();
        ReloadScenes();
        ReloadCores();
        ReloadLedger();
        SyncPlanForm();
        SyncSceneForm();
        UpdateNotice();
        ApplyTexts();

        _statusLabel.Text = _vm.StatusText;
        _runtimeLabel.Text = _vm.RuntimeSummary;
        _sceneLabel.Text = _vm.ActiveSceneText;
        _cpuValue.Text = _vm.CpuText;
        _memValue.Text = _vm.MemoryText;
        _procValue.Text = _vm.ProcessText;
        _coreValue.Text = _vm.CoreText;
        _tuningSummaryLabel.Text = _vm.TuningSummary;
        _intelSummaryLabel.Text = _vm.IntelTuningSummary;
        _planForm.Visible = _vm.IsCreatePlanOpen;
        _sceneForm.Visible = _vm.IsCreateSceneOpen;
    }

    private void ReloadCaps()
    {
        _syncing = true;
        try
        {
            _capsGrid.Rows.Clear();
            foreach (var row in _vm.Capabilities)
            {
                _capsGrid.Rows.Add(row.Id, row.State, row.Detail);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadPlans()
    {
        _syncing = true;
        try
        {
            var selectedId = _vm.SelectedPlan?.Id;
            _plansGrid.Rows.Clear();
            DataGridViewRow? match = null;
            foreach (var plan in _vm.Plans)
            {
                var index = _plansGrid.Rows.Add(
                    _catalog["plans.kind." + plan.Kind.ToLowerInvariant()],
                    plan.Name,
                    plan.Caption,
                    plan.State);
                _plansGrid.Rows[index].Tag = plan;
                if (plan.Id == selectedId) { match = _plansGrid.Rows[index]; }
            }

            if (match is not null && match.Cells.Count > 0)
            {
                _plansGrid.CurrentCell = match.Cells[0];
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadScenes()
    {
        _syncing = true;
        try
        {
            var selectedId = _vm.SelectedScene?.Id;
            _scenesGrid.Rows.Clear();
            DataGridViewRow? match = null;
            foreach (var scene in _vm.Scenes)
            {
                var index = _scenesGrid.Rows.Add(
                    scene.Name,
                    scene.Priority,
                    scene.Targets,
                    scene.Enabled ? _catalog["common.yes"] : _catalog["common.no"],
                    scene.Active ? "●" : string.Empty,
                    scene.LastOutcome);
                _scenesGrid.Rows[index].Tag = scene;
                if (scene.Id == selectedId) { match = _scenesGrid.Rows[index]; }
            }

            if (match is not null && match.Cells.Count > 0)
            {
                _scenesGrid.CurrentCell = match.Cells[0];
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadCores()
    {
        _syncing = true;
        try
        {
            _coresGrid.Rows.Clear();
            foreach (var core in _vm.CoreOffsets)
            {
                _coresGrid.Rows.Add(core.Label, core.ValueText);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadLedger()
    {
        _syncing = true;
        try
        {
            var selectedTarget = _vm.SelectedLedger?.Target;
            _ledgerGrid.Rows.Clear();
            DataGridViewRow? match = null;
            foreach (var entry in _vm.LedgerEntries)
            {
                var index = _ledgerGrid.Rows.Add(
                    entry.Target,
                    entry.Owner,
                    entry.Status,
                    entry.ValuesSummary,
                    entry.Failure);
                _ledgerGrid.Rows[index].Tag = entry;
                if (entry.Target == selectedTarget) { match = _ledgerGrid.Rows[index]; }
            }

            if (match is not null && match.Cells.Count > 0)
            {
                _ledgerGrid.CurrentCell = match.Cells[0];
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnPlansSelected(object? sender, EventArgs e)
    {
        if (!_syncing)
        {
            _vm.SelectedPlan = _plansGrid.CurrentRow?.Tag as PlanRow;
        }
    }

    private void OnScenesSelected(object? sender, EventArgs e)
    {
        if (!_syncing)
        {
            _vm.SelectedScene = _scenesGrid.CurrentRow?.Tag as SceneRow;
        }
    }

    private void OnLedgerSelected(object? sender, EventArgs e)
    {
        if (!_syncing)
        {
            _vm.SelectedLedger = _ledgerGrid.CurrentRow?.Tag as LedgerRow;
        }
    }

    // ---------------------------------------------------------------- 辅助

    private Button Bind(Button button, ICommand command)
    {
        button.Click += (_, _) => command.Execute(null);
        command.CanExecuteChanged += (_, _) =>
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(RefreshCommandStates));
            }
        };
        _commands.Add((button, command));
        return button;
    }

    private void RefreshCommandStates()
    {
        foreach (var (button, command) in _commands)
        {
            button.Enabled = command.CanExecute(null);
        }
    }

    private static void AddRow(TableLayoutPanel table, int row, Control caption, Control input)
    {
        table.RowCount = Math.Max(table.RowCount, row + 1);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        caption.Margin = new Padding(0, 8, 12, 0);
        input.Margin = new Padding(0, 4, 0, 0);
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(input, 1, row);
    }

    private static void AddMetricRow(TableLayoutPanel table, int row, Label caption, Label value)
    {
        caption.Margin = new Padding(0, 4, 10, 4);
        value.Margin = new Padding(0, 4, 0, 4);
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(value, 1, row);
    }

    private static Label MetricValue() => new() { AutoSize = true, Text = "—" };

    private static Label Caption() => new() { AutoSize = true };

    private static ComboBox NewCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

    private static DataGridView NewGrid(params (string Header, int Fill)[] columns)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BorderStyle = BorderStyle.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        };

        foreach (var (header, fill) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                FillWeight = fill,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
        }

        return grid;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // SplitterDistance 在控件还没拿到真实高度时设会越界，必须等窗口显示后再设。
        if (_tuningSplit is { } split)
        {
            var target = Math.Min(180, Math.Max(split.Panel1MinSize, split.Height - split.Panel2MinSize - 8));
            if (target > split.Panel1MinSize)
            {
                split.SplitterDistance = target;
            }
        }

        if (_autoConnect)
        {
            _ = _vm.InitializeAsync();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _catalog.CultureChanged -= OnCultureChanged;
        }

        base.Dispose(disposing);
    }
}
