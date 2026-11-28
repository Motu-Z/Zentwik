using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Input;
using Zentwik.Desktop.Services;
using Zentwik.Desktop.ViewModels;
using Zentwik.Localization;

namespace Zentwik.Desktop;

/// <summary>
/// 裸控件主窗口：一个 TabControl 九页，只用 Label / Button / DataGridView / ComboBox / TextBox / CheckBox / GroupBox / TableLayoutPanel / SplitContainer / FlowLayoutPanel。
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

    private readonly ShellPreferences _prefs = ShellPreferences.Load();
    private NotifyIcon? _tray;
    private bool _exitRequested;

    private readonly List<(Button Button, ICommand Command)> _commands = [];
    private readonly List<(Control Target, string Key)> _texts = [];
    private readonly List<(DataGridViewColumn Column, string Key)> _headers = [];

    /// <summary>重建列表行时置位，避免 SelectionChanged 把视图模型的选中项刷成 null。</summary>
    private bool _syncing;

    // 底栏：代理状态放状态条，顶部整块让给页签（原生工具都这么放）
    private readonly ToolStripStatusLabel _agentStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _dataDirStatus = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly StatusStrip _statusStrip = new();
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
    private readonly DataGridView _historyGrid = NewGrid(("时间", 40), ("CPU", 25), ("内存", 25), ("进程", 25), ("温度", 30), ("功耗", 30));
    private Button _refreshButton = null!;
    private Button _refreshHistoryButton = null!;
    private SplitContainer? _monitorSplit;

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
    private readonly Label _planKindCaption = Caption();
    private readonly Label _planIdCaption = Caption();
    private readonly Label _planNameCaption = Caption();
    private readonly Label _planSpecCaption = Caption();

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
    private readonly Label _sceneIdCaption = Caption();
    private readonly Label _sceneNameCaption = Caption();
    private readonly Label _scenePriorityCaption = Caption();
    private readonly Label _scenePlanCaption = Caption();
    private readonly Label _sceneTriggerCaption = Caption();
    private readonly Label _sceneTriggerArgCaption = Caption();

    // 调校与账本
    private readonly Label _tuningSummaryLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _intelSummaryLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly DataGridView _coresGrid = NewGrid(("核心", 40), ("Curve Optimizer", 60));
    private readonly DataGridView _ledgerGrid = NewGrid(("目标", 60), ("持有者", 40), ("状态", 40), ("取值", 120), ("失败原因", 80));
    private Button _refreshTuningButton = null!;
    private Button _releaseButton = null!;
    private SplitContainer? _tuningSplit;

    // 模块
    private readonly DataGridView _modulesGrid = NewGrid(("标识", 45), ("名称", 60), ("状态", 35), ("可选", 25), ("启用", 25), ("能力", 80), ("详情", 90));
    private Button _refreshModulesButton = null!;
    private Button _enableModuleButton = null!;
    private Button _disableModuleButton = null!;

    // 电源
    private readonly Label _powerLimitsSummaryLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _fastWattsCaption = Caption();
    private readonly Label _slowWattsCaption = Caption();
    private readonly Label _tdcAmpsCaption = Caption();
    private readonly Label _edcAmpsCaption = Caption();
    private readonly TextBox _fastWattsBox = new() { Width = 70 };
    private readonly TextBox _slowWattsBox = new() { Width = 70 };
    private readonly TextBox _tdcAmpsBox = new() { Width = 70 };
    private readonly TextBox _edcAmpsBox = new() { Width = 70 };
    private Button _readPowerLimitsButton = null!;
    private Button _applyPowerLimitsButton = null!;
    private readonly DataGridView _powerSchemesGrid = NewGrid(("名称", 70), ("GUID", 110), ("活动", 25), ("内置", 25), ("可见", 25));
    private readonly DataGridView _powerBackupsGrid = NewGrid(("备份 ID", 80), ("方案名称", 70), ("创建时间", 55), ("设置数", 30));
    private readonly Label _powerSchemeNewNameCaption = Caption();
    private readonly TextBox _powerSchemeNewNameBox = new() { Width = 180 };
    private Button _refreshPowerButton = null!;
    private Button _createPowerSchemeButton = null!;
    private Button _renamePowerSchemeButton = null!;
    private Button _backupPowerSchemeButton = null!;
    private Button _deletePowerSchemeButton = null!;
    private Button _restorePowerBackupButton = null!;
    private SplitContainer? _powerSplit;

    // 日志
    private readonly DataGridView _logGrid = NewGrid(("时间", 55), ("级别", 25), ("内容", 180));
    private Button _refreshLogButton = null!;

    // 诊断
    private readonly Label _doctorSummaryLabel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly DataGridView _doctorErrorsGrid = NewGrid(("时间", 55), ("级别", 25), ("内容", 180));
    private Button _refreshDoctorButton = null!;

    // 设置（语言 / 启动 / 后台 / 模块）
    private readonly ComboBox _languageCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true,
        Width = 160,
        Anchor = AnchorStyles.Left,
    };
    private readonly Label _languageCaption = Caption();
    private readonly CheckBox _autostartCheck = new() { AutoSize = true };
    private readonly CheckBox _closeToTrayCheck = new() { AutoSize = true };
    private readonly Label _startupHintLabel = new() { AutoSize = true, MaximumSize = new Size(760, 0) };

    // 进程（后端还没有 process.* 操作，这一页先摆骨架）
    private readonly DataGridView _processGrid = NewGrid(("PID", 30), ("进程", 80), ("优先级", 40), ("亲和性", 60), ("规则", 60));
    private readonly DataGridView _maskGrid = NewGrid(("规则", 60), ("进程名", 80), ("掩码", 60), ("说明", 80));
    private readonly Label _processNoteLabel = new() { AutoSize = true, MaximumSize = new Size(900, 0) };
    private Button _refreshProcessButton = null!;

    // 页签（7 页）
    private readonly TabPage _monitorPage = new();
    private readonly TabPage _tuningPage = new();
    private readonly TabPage _processPage = new();
    private readonly TabPage _powerPage = new();
    private readonly TabPage _scenesPage = new();
    private readonly TabPage _logPage = new();
    private readonly TabPage _settingsPage = new();

    // 分组框
    private readonly GroupBox _metricBox = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly GroupBox _capsBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _historyBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _coresBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _ledgerBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _tuningBox = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly GroupBox _planFormBox = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly GroupBox _sceneFormBox = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly GroupBox _powerLimitsBox = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly GroupBox _powerSchemesBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _powerBackupsBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _doctorSummaryBox = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly GroupBox _doctorErrorsBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _settingsBox = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly GroupBox _plansBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _modulesBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _doctorBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _processListBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _maskBox = new() { Dock = DockStyle.Fill };
    private readonly GroupBox _logBox = new() { Dock = DockStyle.Fill };
    private SplitContainer? _logSplit;
    private SplitContainer? _processSplit;
    private SplitContainer? _tuningSideSplit;

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
        BuildTuningPage();
        BuildProcessPage();
        BuildPowerPage();
        BuildScenesPage();
        BuildLogPage();
        BuildSettingsPage();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange([
            _monitorPage,
            _tuningPage,
            _processPage,
            _powerPage,
            _scenesPage,
            _logPage,
            _settingsPage,
        ]);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(4, 4, 4, 0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_noticePanel, 0, 0);
        root.Controls.Add(tabs, 0, 1);

        BuildStatusStrip();
        Controls.Add(root);
        Controls.Add(_statusStrip);

        ResumeLayout(performLayout: true);

        WireUp();
        ApplyTexts();
        RefreshCommandStates();
        ReloadAll();
    }

    // ---------------------------------------------------------------- 底栏

    private void BuildStatusStrip()
    {
        _statusStrip.Dock = DockStyle.Bottom;
        _statusStrip.SizingGrip = true;
        _statusStrip.ShowItemToolTips = true;
        _statusStrip.Items.Add(_agentStatus);
        _statusStrip.Items.Add(_dataDirStatus);

        // 右格是纯技术标识（服务名 + 管道名），不进本地化表。
        _dataDirStatus.Text = "ZentwikAgent · \\\\.\\pipe\\zentwik-agent";
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
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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

        var info = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 4 };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
        {
            info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        AddMetricRow(info, 0, _cpuCaption, _cpuValue);
        AddMetricRow(info, 1, _memCaption, _memValue);
        AddMetricRow(info, 2, _procCaption, _procValue);
        AddMetricRow(info, 3, _coreCaption, _coreValue);
        _metricBox.Controls.Add(info);

        _refreshButton = new Button { AutoSize = true, Margin = new Padding(0, 8, 6, 0) };
        _refreshHistoryButton = new Button { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

        _capsBox.Controls.Add(_capsGrid);
        _historyBox.Controls.Add(_historyGrid);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        _monitorSplit = split;
        split.Panel1.Controls.Add(_capsBox);
        split.Panel2.Controls.Add(_historyBox);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_refreshButton, _refreshHistoryButton]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_metricBox, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        _monitorPage.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 方案分组（挂在调校页下半）

    private void BuildPlansGroup()
    {
        _applyPlanButton = new Button { AutoSize = true };
        _revertPlanButton = new Button { AutoSize = true };
        _deletePlanButton = new Button { AutoSize = true };
        _togglePlanFormButton = new Button { AutoSize = true };
        _savePlanButton = new Button { AutoSize = true };
        _cancelPlanButton = new Button { AutoSize = true };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_applyPlanButton, _revertPlanButton, _deletePlanButton, _togglePlanFormButton]);

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, AutoSize = true };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(form, 0, _planKindCaption, _planKindCombo);
        AddRow(form, 1, _planIdCaption, _planIdBox);
        AddRow(form, 2, _planNameCaption, _planNameBox);
        AddRow(form, 3, _planSpecCaption, _planSpecBox);
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        _plansBox.Controls.Add(root);
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
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

    // ---------------------------------------------------------------- 调校与账本页

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

        // 32 线程的逐核表需要高度，账本和方案横向排不完 —— 左边逐核，右边上下分账本/方案。
        var side = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        _tuningSideSplit = side;
        side.Panel1.Controls.Add(_ledgerBox);
        side.Panel2.Controls.Add(_plansBox);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };
        _tuningSplit = split;
        split.Panel1.Controls.Add(_coresBox);
        split.Panel2.Controls.Add(side);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_tuningBox, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        _tuningPage.Controls.Add(root);

        BuildPlansGroup();
    }

    // ---------------------------------------------------------------- 模块分组（挂在设置页）

    private void BuildModulesGroup()
    {
        _refreshModulesButton = new Button { AutoSize = true };
        _enableModuleButton = new Button { AutoSize = true };
        _disableModuleButton = new Button { AutoSize = true };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_refreshModulesButton, _enableModuleButton, _disableModuleButton]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_modulesGrid, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        _modulesBox.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 电源页

    private void BuildPowerPage()
    {
        _powerPage.Padding = new Padding(8);

        _readPowerLimitsButton = new Button { AutoSize = true };
        _applyPowerLimitsButton = new Button { AutoSize = true };
        _refreshPowerButton = new Button { AutoSize = true };
        _createPowerSchemeButton = new Button { AutoSize = true };
        _renamePowerSchemeButton = new Button { AutoSize = true };
        _backupPowerSchemeButton = new Button { AutoSize = true };
        _deletePowerSchemeButton = new Button { AutoSize = true };
        _restorePowerBackupButton = new Button { AutoSize = true };

        var limitsInner = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        limitsInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        limitsInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        limitsInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        limitsInner.Controls.Add(_powerLimitsSummaryLabel, 0, 0);

        var limitsInputs = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        _fastWattsCaption.Margin = new Padding(0, 6, 4, 0);
        _slowWattsCaption.Margin = new Padding(10, 6, 4, 0);
        _tdcAmpsCaption.Margin = new Padding(10, 6, 4, 0);
        _edcAmpsCaption.Margin = new Padding(10, 6, 4, 0);
        limitsInputs.Controls.AddRange([
            _fastWattsCaption, _fastWattsBox,
            _slowWattsCaption, _slowWattsBox,
            _tdcAmpsCaption, _tdcAmpsBox,
            _edcAmpsCaption, _edcAmpsBox,
            _readPowerLimitsButton, _applyPowerLimitsButton,
        ]);
        limitsInner.Controls.Add(limitsInputs, 0, 1);
        _powerLimitsBox.Controls.Add(limitsInner);

        var schemesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        schemesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        schemesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        schemesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        schemesLayout.Controls.Add(_powerSchemesGrid, 0, 0);

        var schemeButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _powerSchemeNewNameCaption.Margin = new Padding(8, 6, 4, 0);
        schemeButtons.Controls.AddRange([
            _refreshPowerButton,
            _powerSchemeNewNameCaption,
            _powerSchemeNewNameBox,
            _createPowerSchemeButton,
            _renamePowerSchemeButton,
            _backupPowerSchemeButton,
            _deletePowerSchemeButton,
        ]);
        schemesLayout.Controls.Add(schemeButtons, 0, 1);
        _powerSchemesBox.Controls.Add(schemesLayout);

        var backupsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        backupsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        backupsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        backupsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        backupsLayout.Controls.Add(_powerBackupsGrid, 0, 0);

        var backupButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        backupButtons.Controls.Add(_restorePowerBackupButton);
        backupsLayout.Controls.Add(backupButtons, 0, 1);
        _powerBackupsBox.Controls.Add(backupsLayout);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        _powerSplit = split;
        split.Panel1.Controls.Add(_powerSchemesBox);
        split.Panel2.Controls.Add(_powerBackupsBox);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_powerLimitsBox, 0, 0);
        root.Controls.Add(split, 0, 1);
        _powerPage.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 日志页

    private void BuildLogPage()
    {
        _logPage.Padding = new Padding(8);

        _refreshLogButton = new Button { AutoSize = true };
        _refreshDoctorButton = new Button { AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_refreshLogButton, _refreshDoctorButton]);

        _logBox.Controls.Add(_logGrid);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        _logSplit = split;
        split.Panel1.Controls.Add(_logBox);
        split.Panel2.Controls.Add(_doctorBox);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(split, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        _logPage.Controls.Add(root);

        BuildDoctorGroup();
    }

    // ---------------------------------------------------------------- 诊断分组（挂在日志页下半）

    private void BuildDoctorGroup()
    {
        var summaryPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 1 };
        summaryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summaryPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryPanel.Controls.Add(_doctorSummaryLabel, 0, 0);
        _doctorSummaryBox.Controls.Add(summaryPanel);

        _doctorErrorsBox.Controls.Add(_doctorErrorsGrid);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_doctorSummaryBox, 0, 0);
        root.Controls.Add(_doctorErrorsBox, 0, 1);
        _doctorBox.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 进程页

    private void BuildProcessPage()
    {
        _processPage.Padding = new Padding(8);

        _refreshProcessButton = new Button { AutoSize = true, Enabled = false };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        buttons.Controls.Add(_refreshProcessButton);

        var notePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        notePanel.Controls.Add(_processNoteLabel);

        _processListBox.Controls.Add(_processGrid);
        _maskBox.Controls.Add(_maskGrid);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        _processSplit = split;
        split.Panel1.Controls.Add(_processListBox);
        split.Panel2.Controls.Add(_maskBox);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(notePanel, 0, 0);
        root.Controls.Add(split, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        _processPage.Controls.Add(root);
    }

    // ---------------------------------------------------------------- 设置页

    private void BuildSettingsPage()
    {
        _settingsPage.Padding = new Padding(8);

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 4 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(form, 0, _languageCaption, _languageCombo);
        AddRow(form, 1, new Label { AutoSize = true }, _autostartCheck);
        AddRow(form, 2, new Label { AutoSize = true }, _closeToTrayCheck);

        var hintPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        hintPanel.Controls.Add(_startupHintLabel);
        form.Controls.Add(hintPanel, 1, 3);

        _settingsBox.Controls.Add(form);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(_settingsBox, 0, 0);
        root.Controls.Add(_modulesBox, 0, 1);
        _settingsPage.Controls.Add(root);

        BuildModulesGroup();
    }

    // ---------------------------------------------------------------- 事件挂接

    private void WireUp()
    {
        BuildNoticePanel();

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Capabilities.CollectionChanged += (_, _) => ReloadCaps();
        _vm.TelemetryHistory.CollectionChanged += (_, _) => ReloadHistory();
        _vm.Plans.CollectionChanged += (_, _) => ReloadPlans();
        _vm.Scenes.CollectionChanged += (_, _) => ReloadScenes();
        _vm.CoreOffsets.CollectionChanged += (_, _) => ReloadCores();
        _vm.LedgerEntries.CollectionChanged += (_, _) => ReloadLedger();
        _vm.Modules.CollectionChanged += (_, _) => ReloadModules();
        _vm.PowerSchemes.CollectionChanged += (_, _) => ReloadPowerSchemes();
        _vm.PowerBackups.CollectionChanged += (_, _) => ReloadPowerBackups();
        _vm.Logs.CollectionChanged += (_, _) => ReloadLogs();
        _vm.DoctorErrors.CollectionChanged += (_, _) => ReloadDoctorErrors();

        _capsGrid.SelectionChanged += (_, _) => { };
        _historyGrid.SelectionChanged += (_, _) => { };
        _plansGrid.SelectionChanged += OnPlansSelected;
        _scenesGrid.SelectionChanged += OnScenesSelected;
        _ledgerGrid.SelectionChanged += OnLedgerSelected;
        _modulesGrid.SelectionChanged += OnModulesSelected;
        _powerSchemesGrid.SelectionChanged += OnPowerSchemesSelected;
        _powerBackupsGrid.SelectionChanged += OnPowerBackupsSelected;

        Bind(_refreshButton, _vm.RefreshCommand);
        Bind(_refreshHistoryButton, _vm.RefreshHistoryCommand);
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

        Bind(_refreshModulesButton, _vm.RefreshModulesCommand);
        BindWithConfirm(_enableModuleButton, _vm.EnableModuleCommand, () => string.Format(_catalog["modules.confirm_enable"], _vm.SelectedModule?.Name ?? string.Empty));
        BindWithConfirm(_disableModuleButton, _vm.DisableModuleCommand, () => string.Format(_catalog["modules.confirm_disable"], _vm.SelectedModule?.Name ?? string.Empty));

        Bind(_refreshPowerButton, _vm.RefreshPowerCommand);
        Bind(_readPowerLimitsButton, _vm.ReadPowerLimitsCommand);
        BindWithConfirm(_applyPowerLimitsButton, _vm.ApplyPowerLimitsCommand, () => _catalog["power.limits_confirm"]);
        Bind(_createPowerSchemeButton, _vm.CreatePowerSchemeCommand);
        Bind(_renamePowerSchemeButton, _vm.RenamePowerSchemeCommand);
        Bind(_backupPowerSchemeButton, _vm.BackupPowerSchemeCommand);
        BindWithConfirm(_deletePowerSchemeButton, _vm.DeletePowerSchemeCommand, () => string.Format(_catalog["power.scheme.confirm_delete"], _vm.SelectedPowerScheme?.Name ?? string.Empty));
        BindWithConfirm(_restorePowerBackupButton, _vm.RestorePowerBackupCommand, () => string.Format(_catalog["power.backup.confirm_restore"], _vm.SelectedPowerBackup?.Id ?? string.Empty));

        Bind(_refreshLogButton, _vm.RefreshLogCommand);
        Bind(_refreshDoctorButton, _vm.RefreshDoctorCommand);

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

        _fastWattsBox.TextChanged += (_, _) => { if (!_syncing) { _vm.FastWattsInput = _fastWattsBox.Text; } };
        _slowWattsBox.TextChanged += (_, _) => { if (!_syncing) { _vm.SlowWattsInput = _slowWattsBox.Text; } };
        _tdcAmpsBox.TextChanged += (_, _) => { if (!_syncing) { _vm.TdcAmpsInput = _tdcAmpsBox.Text; } };
        _edcAmpsBox.TextChanged += (_, _) => { if (!_syncing) { _vm.EdcAmpsInput = _edcAmpsBox.Text; } };
        _powerSchemeNewNameBox.TextChanged += (_, _) => { if (!_syncing) { _vm.PowerSchemeNewName = _powerSchemeNewNameBox.Text; } };

        _languageCombo.Format += (_, e) =>
        {
            if (e.ListItem is "zh-CN")
            {
                e.Value = _catalog["settings.language.zh_cn"];
            }
        };
        _languageCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _languageCombo.SelectedItem is string culture)
            {
                _vm.SelectedLanguage = culture;
            }
        };

        _catalog.CultureChanged += OnCultureChanged;

        _autostartCheck.CheckedChanged += (_, _) => OnAutostartToggled();
        _closeToTrayCheck.CheckedChanged += (_, _) =>
        {
            if (_syncing) { return; }
            _prefs.CloseToTray = _closeToTrayCheck.Checked;
            _prefs.Save();
            if (_prefs.CloseToTray) { EnsureTray(); }
        };

        // 属性文案先注册，ApplyTexts 才知道该翻哪个键。
        RegisterTexts();
    }

    private void RegisterTexts()
    {
        Register(_monitorPage, "nav.monitor");
        Register(_tuningPage, "nav.tuning");
        Register(_processPage, "nav.process");
        Register(_powerPage, "nav.power");
        Register(_scenesPage, "nav.scenes");
        Register(_logPage, "nav.log");
        Register(_settingsPage, "nav.settings");

        Register(_cpuCaption, "monitor.cpu");
        Register(_memCaption, "monitor.memory");
        Register(_procCaption, "monitor.processes");
        Register(_coreCaption, "monitor.cores");
        Register(_metricBox, "monitor.title");
        Register(_capsBox, "caps.title");
        Register(_historyBox, "monitor.history_title");
        Register(_refreshButton, "common.refresh");
        Register(_refreshHistoryButton, "monitor.history_refresh");
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
        Register(_plansBox, "plans.title");
        Register(_refreshTuningButton, "common.refresh");
        Register(_releaseButton, "tuning.release");

        Register(_processListBox, "process.title");
        Register(_maskBox, "process.mask_title");
        Register(_processNoteLabel, "process.backend_missing");
        Register(_refreshProcessButton, "common.refresh");

        Register(_refreshModulesButton, "common.refresh");
        Register(_enableModuleButton, "modules.enable");
        Register(_disableModuleButton, "modules.disable");

        Register(_powerLimitsBox, "power.limits");
        Register(_fastWattsCaption, "power.limits_fast");
        Register(_slowWattsCaption, "power.limits_slow");
        Register(_tdcAmpsCaption, "power.limits_tdc");
        Register(_edcAmpsCaption, "power.limits_edc");
        Register(_readPowerLimitsButton, "power.limits_read");
        Register(_applyPowerLimitsButton, "power.limits_apply");
        Register(_powerSchemesBox, "power.title");
        Register(_powerBackupsBox, "power.backups");
        Register(_powerSchemeNewNameCaption, "power.scheme.new_name");
        Register(_refreshPowerButton, "common.refresh");
        Register(_createPowerSchemeButton, "power.scheme.create");
        Register(_renamePowerSchemeButton, "power.scheme.rename");
        Register(_backupPowerSchemeButton, "power.scheme.backup");
        Register(_deletePowerSchemeButton, "power.scheme.delete");
        Register(_restorePowerBackupButton, "power.backup.restore");

        Register(_logBox, "log.title");
        Register(_refreshLogButton, "log.refresh");
        Register(_doctorBox, "doctor.title");
        Register(_doctorSummaryBox, "doctor.summary");
        Register(_doctorErrorsBox, "doctor.errors");
        Register(_refreshDoctorButton, "doctor.refresh");

        Register(_settingsBox, "settings.title");
        Register(_languageCaption, "settings.language");
        Register(_autostartCheck, "settings.autostart");
        Register(_closeToTrayCheck, "settings.close_to_tray");
        Register(_startupHintLabel, "settings.startup_hint");
        Register(_modulesBox, "modules.title");

        AddHeaders(_capsGrid, "caps.id", "caps.state", "caps.detail");
        AddHeaders(_historyGrid, "monitor.history.time", "monitor.history.cpu", "monitor.history.memory", "monitor.history.processes", "monitor.history.temp", "monitor.history.power");
        AddHeaders(_plansGrid, "plans.kind", "plans.name", "plans.caption", "plans.state");
        AddHeaders(_scenesGrid, "scenes.name", "scenes.priority", "scenes.target", "scenes.enabled", "scenes.active", "scenes.outcome");
        AddHeaders(_coresGrid, "tuning.core", "tuning.offset");
        AddHeaders(_ledgerGrid, "ledger.target", "ledger.owner", "ledger.status", "ledger.values", "ledger.failure");
        AddHeaders(_modulesGrid, "modules.id", "modules.name", "modules.state", "modules.optional", "modules.enabled", "modules.capabilities", "modules.detail");
        AddHeaders(_powerSchemesGrid, "power.scheme.name", "power.scheme.guid", "power.scheme.active", "power.scheme.builtin", "power.scheme.visible");
        AddHeaders(_powerBackupsGrid, "power.backup.id", "power.backup.name", "power.backup.created", "power.backup.count");
        AddHeaders(_logGrid, "log.time", "log.level", "log.message");
        AddHeaders(_doctorErrorsGrid, "doctor.time", "doctor.level", "doctor.message");
        AddHeaders(_processGrid, "process.pid", "process.name", "process.priority", "process.affinity", "process.rule");
        AddHeaders(_maskGrid, "process.mask_rule", "process.mask_process", "process.mask_value", "process.mask_note");
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
                _agentStatus.Text = _vm.StatusText;
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
            case nameof(MainViewModel.PowerLimitsSummary):
                _powerLimitsSummaryLabel.Text = _vm.PowerLimitsSummary;
                break;
            case nameof(MainViewModel.DoctorSummary):
                _doctorSummaryLabel.Text = _vm.DoctorSummary;
                break;
            case nameof(MainViewModel.PowerSchemeNewName):
                SyncPowerSchemeName();
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

    private void SyncPowerSchemeName()
    {
        _syncing = true;
        try
        {
            if (_powerSchemeNewNameBox.Text != _vm.PowerSchemeNewName)
            {
                _powerSchemeNewNameBox.Text = _vm.PowerSchemeNewName;
            }
        }
        finally
        {
            _syncing = false;
        }
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
        ReloadHistory();
        ReloadPlans();
        ReloadScenes();
        ReloadCores();
        ReloadLedger();
        ReloadModules();
        ReloadPowerSchemes();
        ReloadPowerBackups();
        ReloadLogs();
        ReloadDoctorErrors();
        SyncPlanForm();
        SyncSceneForm();
        SyncPowerSchemeName();
        UpdateNotice();
        ApplyTexts();

        _agentStatus.Text = _vm.StatusText;
        _cpuValue.Text = _vm.CpuText;
        _memValue.Text = _vm.MemoryText;
        _procValue.Text = _vm.ProcessText;
        _coreValue.Text = _vm.CoreText;
        _tuningSummaryLabel.Text = _vm.TuningSummary;
        _intelSummaryLabel.Text = _vm.IntelTuningSummary;
        _powerLimitsSummaryLabel.Text = _vm.PowerLimitsSummary;
        _doctorSummaryLabel.Text = _vm.DoctorSummary;
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

            _capsGrid.ClearSelection();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadHistory()
    {
        _syncing = true;
        try
        {
            _historyGrid.Rows.Clear();
            foreach (var row in _vm.TelemetryHistory)
            {
                _historyGrid.Rows.Add(row.Time, row.Cpu, row.Memory, row.Processes, row.Temperature, row.Power);
            }

            _historyGrid.ClearSelection();
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

            _coresGrid.ClearSelection();
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

    private void ReloadModules()
    {
        _syncing = true;
        try
        {
            var selectedId = _vm.SelectedModule?.Id;
            _modulesGrid.Rows.Clear();
            DataGridViewRow? match = null;
            foreach (var module in _vm.Modules)
            {
                var index = _modulesGrid.Rows.Add(
                    module.Id,
                    module.Name,
                    module.State,
                    module.Optional ? _catalog["common.yes"] : _catalog["common.no"],
                    module.Enabled ? _catalog["common.yes"] : _catalog["common.no"],
                    module.Capabilities,
                    module.Detail);
                _modulesGrid.Rows[index].Tag = module;
                if (module.Id == selectedId) { match = _modulesGrid.Rows[index]; }
            }

            if (match is not null && match.Cells.Count > 0)
            {
                _modulesGrid.CurrentCell = match.Cells[0];
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadPowerSchemes()
    {
        _syncing = true;
        try
        {
            var selectedGuid = _vm.SelectedPowerScheme?.Guid;
            _powerSchemesGrid.Rows.Clear();
            DataGridViewRow? match = null;
            foreach (var scheme in _vm.PowerSchemes)
            {
                var index = _powerSchemesGrid.Rows.Add(
                    scheme.Name,
                    scheme.Guid.ToString("D"),
                    scheme.Active ? "●" : string.Empty,
                    scheme.BuiltIn ? _catalog["common.yes"] : _catalog["common.no"],
                    scheme.Visible ? _catalog["common.yes"] : _catalog["common.no"]);
                _powerSchemesGrid.Rows[index].Tag = scheme;
                if (scheme.Guid == selectedGuid) { match = _powerSchemesGrid.Rows[index]; }
            }

            if (match is not null && match.Cells.Count > 0)
            {
                _powerSchemesGrid.CurrentCell = match.Cells[0];
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadPowerBackups()
    {
        _syncing = true;
        try
        {
            var selectedId = _vm.SelectedPowerBackup?.Id;
            _powerBackupsGrid.Rows.Clear();
            DataGridViewRow? match = null;
            foreach (var backup in _vm.PowerBackups)
            {
                var index = _powerBackupsGrid.Rows.Add(
                    backup.Id,
                    backup.Name,
                    backup.CreatedAt,
                    backup.SettingCount);
                _powerBackupsGrid.Rows[index].Tag = backup;
                if (backup.Id == selectedId) { match = _powerBackupsGrid.Rows[index]; }
            }

            if (match is not null && match.Cells.Count > 0)
            {
                _powerBackupsGrid.CurrentCell = match.Cells[0];
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadLogs()
    {
        _syncing = true;
        try
        {
            _logGrid.Rows.Clear();
            foreach (var entry in _vm.Logs)
            {
                _logGrid.Rows.Add(entry.Time, entry.Level, entry.Message);
            }

            _logGrid.ClearSelection();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ReloadDoctorErrors()
    {
        _syncing = true;
        try
        {
            _doctorErrorsGrid.Rows.Clear();
            foreach (var entry in _vm.DoctorErrors)
            {
                _doctorErrorsGrid.Rows.Add(entry.Time, entry.Level, entry.Message);
            }

            _doctorErrorsGrid.ClearSelection();
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

    private void OnModulesSelected(object? sender, EventArgs e)
    {
        if (!_syncing)
        {
            _vm.SelectedModule = _modulesGrid.CurrentRow?.Tag as ModuleRow;
        }
    }

    private void OnPowerSchemesSelected(object? sender, EventArgs e)
    {
        if (!_syncing)
        {
            _vm.SelectedPowerScheme = _powerSchemesGrid.CurrentRow?.Tag as PowerSchemeRow;
        }
    }

    private void OnPowerBackupsSelected(object? sender, EventArgs e)
    {
        if (!_syncing)
        {
            _vm.SelectedPowerBackup = _powerBackupsGrid.CurrentRow?.Tag as PowerBackupRow;
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

    private Button BindWithConfirm(Button button, ICommand command, Func<string> messageFactory)
    {
        button.Click += (_, _) =>
        {
            if (MessageBox.Show(
                this,
                messageFactory(),
                _catalog["common.confirm"],
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) == DialogResult.OK)
            {
                command.Execute(null);
            }
        };
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
            BackgroundColor = SystemColors.Window,
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
        SetSplitterDistance(_monitorSplit, 220);
        if (_tuningSplit is { Width: > 0 } tuningSplit)
        {
            SetSplitterDistance(tuningSplit, (int)(tuningSplit.Width * 0.44));
        }

        SetSplitterDistance(_tuningSideSplit, 300);
        SetSplitterDistance(_powerSplit, 240);
        SetSplitterDistance(_logSplit, 300);
        SetSplitterDistance(_processSplit, 260);

        SyncShellSettings();
        UpdateNotice();

        if (_autoConnect)
        {
            _ = _vm.InitializeAsync();
        }
    }

    // ---------------------------------------------------------------- 外壳行为（托盘 / 自启动）

    private void SyncShellSettings()
    {
        _syncing = true;
        try
        {
            _autostartCheck.Checked = StartupRegistration.IsEnabled();
            _closeToTrayCheck.Checked = _prefs.CloseToTray;
        }
        finally
        {
            _syncing = false;
        }

        if (_prefs.CloseToTray)
        {
            EnsureTray();
        }
    }

    private void OnAutostartToggled()
    {
        if (_syncing)
        {
            return;
        }

        var wanted = _autostartCheck.Checked;
        if (StartupRegistration.TrySetEnabled(wanted, out var error))
        {
            return;
        }

        MessageBox.Show(
            this,
            string.Format(_catalog["settings.autostart_failed"], error),
            _catalog["settings.title"],
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        _syncing = true;
        _autostartCheck.Checked = !wanted;
        _syncing = false;
    }

    private void EnsureTray()
    {
        if (_tray is not null)
        {
            _tray.Visible = true;
            return;
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(_catalog["shell.show"], null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_catalog["shell.exit"], null, (_, _) =>
        {
            _exitRequested = true;
            Close();
        });

        _tray = new NotifyIcon
        {
            Text = "Zentwik",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 只有用户点右上角关闭才收进托盘；注销/关机/程序自己退出照常结束。
        if (!_exitRequested
            && _prefs.CloseToTray
            && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            EnsureTray();
            return;
        }

        base.OnFormClosing(e);
    }

    private static void SetSplitterDistance(SplitContainer? split, int preferred)
    {
        if (split is null)
        {
            return;
        }

        // 竖直分隔条沿宽度分，横向分隔条沿高度分，夹取的上限不一样。
        var extent = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
        var target = Math.Min(preferred, Math.Max(split.Panel1MinSize, extent - split.Panel2MinSize - 8));
        if (target > split.Panel1MinSize)
        {
            split.SplitterDistance = target;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _catalog.CultureChanged -= OnCultureChanged;

            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }

        base.Dispose(disposing);
    }
}
