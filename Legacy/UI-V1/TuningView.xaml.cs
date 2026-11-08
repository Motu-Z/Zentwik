using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;

// UI 工程同时开了 UseWindowsForms，同名类型必须显式消歧义（CS0104）。
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Zentwik.UI.Views
{
    /// <summary>
    /// 调校页：CO 逐核 / FMax / Windows 电源策略。
    ///
    /// 数据路径与旧版一致：UI 写 tuning.json → 发 apply-tuning → Worker 写 SMU。
    /// 电源策略走批次十一契约：query-power-policy 读快照，apply-power-policy 增量写入
    /// （本机不支持的项发 null = 不改，避免把「没提」误当成「改成 0」）。
    /// </summary>
    public partial class TuningView : UserControl
    {
        /// <summary>与 SmuReader 的取值边界一致（事实卡 §1.5）。</summary>
        private const int CoMin = -50;
        private const int CoMax = 50;
        private const uint FMaxMin = 4450;
        private const uint FMaxMax = 5650;
        private const int FMaxStep = 10;

        private readonly TuningStore _store = App.Tuning;
        private readonly AppPaths _paths = new();
        private readonly List<TextBox> _coBoxes = new();

        private List<ProfileSnapshot> _profiles = new();
        private ProfileSnapshot? _renameTarget;

        /// <summary>最近一次电源策略快照。null 表示尚未读到（Worker 未就绪等）。</summary>
        private PowerPolicySnapshot? _policy;

        private bool _busy;
        private bool _isDirty;

        // XAML 里给 TextBox 设初始 Text 会在 InitializeComponent 期间就触发 TextChanged，
        // 此时 SaveBar 等后声明控件还没生成 —— 没有这个守卫，构造函数直接 NRE 崩启动。
        private bool _initialized;

        // 回填 / 步进按钮会改 Text 并触发 TextChanged，置位防重入，否则一次赋值产生两次 dirty。
        private bool _suppressCoEvents;
        private bool _suppressProfileEvents;
        private bool _suppressPolicyEvents;

        // 铅笔点击（PreviewMouseDown）先于 ComboBoxItem 的选中逻辑；
        // 不先置位的话，点铅笔会被当成「切换方案」发一次 IPC。
        private bool _renaming;

        private static readonly string[] BoostItems = { "关闭", "开启", "激进", "高效开启", "高效激进" };
        private static readonly string[] AspmItems = { "关闭", "中等", "最大节能" };
        private static readonly string[] EppItems =
            Enumerable.Range(0, 101).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        private static readonly string[] MaxStateItems =
            Enumerable.Range(50, 51).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

        public TuningView()
        {
            InitializeComponent();
            _initialized = true;
            LoadFromStore();
            LoadProfiles();
            _ = RefreshSmuStatusAsync();
            _ = LoadPowerPolicyAsync();
        }

        // ============ 数据装载 ============

        /// <summary>
        /// 从 tuning.json 读回当前值并铺到输入框。
        /// public：方案页或本页切换方案后都要调它，否则界面还停在旧方案的值上。
        /// </summary>
        public void LoadFromStore()
        {
            try
            {
                _store.Load();
            }
            catch (Exception ex)
            {
                ShowToast($"读取 tuning.json 失败：{ex.Message}", "ErrorRed");
                return;
            }

            TuningProfile profile = _store.Profile;
            int[] margins = profile.CurveOptimizer ?? Array.Empty<int>();

            BuildCoreInputs(margins.Length > 0 ? margins.Length : 16, margins);

            _suppressCoEvents = true;
            FMaxBox.Text = profile.FMaxMHz.ToString(CultureInfo.InvariantCulture);
            _suppressCoEvents = false;

            // 回填会触发 TextChanged，标记必须在这之后清。
            ClearDirty();
        }

        private void BuildCoreInputs(int count, int[]? values)
        {
            if (count < 1)
                count = 1;

            // 上限只是防手滑（输入 100000 会卡死界面），不是业务约束。
            if (count > 128)
                count = 128;

            CoGrid.Children.Clear();
            _coBoxes.Clear();

            for (int i = 0; i < count; i++)
            {
                int initial = values != null && i < values.Length ? values[i] : 0;

                var box = new TextBox
                {
                    Style = (Style)FindResource("NumberInput"),
                    Text = initial.ToString(CultureInfo.InvariantCulture),
                    ToolTip = $"核心 {i}"
                };
                box.TextChanged += OnCoTextChanged;
                _coBoxes.Add(box);

                var minus = new Button { Style = (Style)FindResource("StepButton"), Content = "−" };
                minus.Click += (_, _) => StepCo(box, -1);
                var plus = new Button { Style = (Style)FindResource("StepButton"), Content = "+" };
                plus.Click += (_, _) => StepCo(box, 1);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(minus, 0);
                Grid.SetColumn(box, 1);
                Grid.SetColumn(plus, 2);
                grid.Children.Add(minus);
                grid.Children.Add(box);
                grid.Children.Add(plus);

                var cell = new Border
                {
                    Style = (Style)FindResource("NumberBoxBorder"),
                    Child = grid,
                    Margin = new Thickness(0, 0, 12, 12)
                };
                CoGrid.Children.Add(cell);
            }
        }

        // ============ CO / FMax 输入 ============

        private void OnCoTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressCoEvents || sender is not TextBox box)
                return;

            string sanitized = SanitizeSigned(box.Text);
            int? clamped = null;
            if (int.TryParse(sanitized, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && Math.Clamp(value, CoMin, CoMax) is int bounded && bounded != value)
            {
                clamped = bounded;
            }

            _suppressCoEvents = true;
            if (clamped is int fixedValue)
                box.Text = fixedValue.ToString(CultureInfo.InvariantCulture);
            else if (sanitized != box.Text)
                box.Text = sanitized;
            _suppressCoEvents = false;

            MarkDirty();
        }

        /// <summary>只放行「负号开头 + 数字」；中间的非法字符直接剔除，越界值钳回 [-50, 50]。</summary>
        private static string SanitizeSigned(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool negative = false;

            foreach (char c in text)
            {
                if (c == '-' && sb.Length == 0 && !negative)
                    negative = true;
                else if (char.IsAsciiDigit(c) && sb.Length < 3)
                    sb.Append(c);
            }

            return negative ? "-" + sb : sb.ToString();
        }

        private void StepCo(TextBox box, int delta)
        {
            _ = int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int current);
            int next = Math.Clamp(current + delta, CoMin, CoMax);

            _suppressCoEvents = true;
            box.Text = next.ToString(CultureInfo.InvariantCulture);
            _suppressCoEvents = false;

            MarkDirty();
        }

        private void OnFMaxTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressCoEvents)
                return;

            string digits = new string(FMaxBox.Text.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length > 5)
                digits = digits[..5];

            _suppressCoEvents = true;
            if (digits != FMaxBox.Text)
                FMaxBox.Text = digits;
            _suppressCoEvents = false;

            MarkDirty();
        }

        private void OnFMaxMinusClick(object sender, RoutedEventArgs e) => StepFMax(-FMaxStep);

        private void OnFMaxPlusClick(object sender, RoutedEventArgs e) => StepFMax(FMaxStep);

        private void StepFMax(int delta)
        {
            _ = uint.TryParse(FMaxBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint current);
            long next = current + delta;

            // 0 是「不写 FMax」的哨兵值，向下穿过下限时落回 0 而不是钳在 4450。
            if (next <= 0)
                next = 0;
            else if (next < FMaxMin)
                next = FMaxMin;
            else if (next > FMaxMax)
                next = FMaxMax;

            _suppressCoEvents = true;
            FMaxBox.Text = next.ToString(CultureInfo.InvariantCulture);
            _suppressCoEvents = false;

            MarkDirty();
        }

        // ============ dirty / 浮动保存条 ============

        private void MarkDirty()
        {
            if (!_initialized || _isDirty)
                return;

            _isDirty = true;
            SaveBar.Visibility = Visibility.Visible;
        }

        private void ClearDirty()
        {
            if (!_initialized)
                return;

            _isDirty = false;
            SaveBar.Visibility = Visibility.Collapsed;
        }

        private void OnDiscardClick(object sender, RoutedEventArgs e)
        {
            // 放弃 = 回到盘上与硬件快照的已知状态，电源策略一起还原。
            LoadFromStore();
            if (_policy is not null)
                FillPolicyControls(_policy);
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            if (ReadMargins(out string marginError) is not int[] margins)
            {
                ShowToast(marginError, "ErrorRed");
                return;
            }

            if (!TryReadFMax(out uint fMax, out string fmaxError))
            {
                ShowToast(fmaxError, "ErrorRed");
                return;
            }

            try
            {
                // 只写 tuning.json：不写 SMU、不发 IPC（应用走「应用」按钮）。
                _store.Save(new TuningProfile
                {
                    CurveOptimizer = margins,
                    FMaxMHz = fMax,
                    FMaxBaselineMHz = _store.Profile.FMaxBaselineMHz
                });

                ClearDirty();
                ShowToast("参数已保存（应用后写入硬件）", "SuccessGreen");
            }
            catch (Exception ex)
            {
                ShowToast($"保存失败：{ex.GetType().Name}: {ex.Message}", "ErrorRed");
            }
        }

        // ============ 应用 ============

        private async void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            if (ReadMargins(out string marginError) is not int[] margins)
            {
                ShowToast(marginError, "ErrorRed");
                return;
            }

            if (!TryReadFMax(out uint fMax, out string fmaxError))
            {
                ShowToast(fmaxError, "ErrorRed");
                return;
            }

            _busy = true;
            ApplyButton.IsEnabled = false;

            try
            {
                _store.Save(new TuningProfile
                {
                    CurveOptimizer = margins,
                    FMaxMHz = fMax,
                    FMaxBaselineMHz = _store.Profile.FMaxBaselineMHz
                });

                var problems = new List<string>();
                bool tuningWriteFailed = false;

                // —— 1. SMU 调校 ——
                try
                {
                    await using WorkerPipeClient client = new();
                    await client.ConnectAsync(TimeSpan.FromSeconds(2));

                    PipeResponse response = await client.SendAsync(
                        new PipeRequest
                        {
                            Version = WorkerPipeContract.ProtocolVersion,
                            Command = WorkerPipeContract.CommandApplyTuning,
                            Payload = null
                        },
                        TimeSpan.FromSeconds(75));

                    // Skipped 与 failed 都是 Success=false，但只有 failed 才需要复查 SMU 状态。
                    if (!response.Success)
                    {
                        tuningWriteFailed = response.Payload == "failed";
                        problems.Add($"调校：{response.Message}");
                    }
                }
                catch (Exception ex)
                {
                    tuningWriteFailed = true;
                    problems.Add($"调校：{ex.Message}");
                }

                // —— 2. 电源策略（增量：本机不支持的项发 null）——
                ApplyPowerPolicyPayload? policyPayload = BuildPolicyPayload();
                if (policyPayload is not null && !policyPayload.IsEmpty)
                {
                    try
                    {
                        await using WorkerPipeClient client = new();
                        await client.ConnectAsync(TimeSpan.FromSeconds(2));

                        PipeResponse response = await client.SendAsync(
                            new PipeRequest
                            {
                                Version = WorkerPipeContract.ProtocolVersion,
                                Command = WorkerPipeContract.CommandApplyPowerPolicy,
                                Payload = JsonSerializer.Serialize(policyPayload, IpcJsonContext.Default.ApplyPowerPolicyPayload)
                            },
                            TimeSpan.FromSeconds(10));

                        if (!response.Success)
                            problems.Add($"电源策略：{response.Message}");

                        // 成功与部分失败都会回写入后的快照（⑤⑥），拿它刷新界面，
                        // 否则「提示失败、控件还停在旧值」，下一次应用会把已生效的项重写一遍。
                        if (response.Payload is not null
                            && JsonSerializer.Deserialize(response.Payload, IpcJsonContext.Default.PowerPolicySnapshot) is PowerPolicySnapshot snapshot)
                        {
                            FillPolicyControls(snapshot);
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"电源策略：{ex.Message}");
                    }
                }

                ClearDirty();

                if (problems.Count == 0)
                    ShowToast("硬件参数与电源策略已成功下发", "SuccessGreen");
                else
                    ShowToast(string.Join("；", problems), "ErrorRed");

                if (tuningWriteFailed)
                    _ = RefreshSmuStatusAsync();
            }
            finally
            {
                _busy = false;
                ApplyButton.IsEnabled = true;
            }
        }

        /// <summary>收集电源策略的当前界面值。本机不支持的项保持 null（= 不改）。</summary>
        private ApplyPowerPolicyPayload? BuildPolicyPayload()
        {
            if (_policy is null)
                return null;

            var payload = new ApplyPowerPolicyPayload();

            if (_policy.EppSupported && EppBox.SelectedIndex >= 0)
                payload.Epp = EppBox.SelectedIndex;

            if (_policy.BoostModeSupported && BoostBox.SelectedIndex >= 0)
                payload.BoostMode = BoostBox.SelectedIndex;

            // MaxStateItems 从 50 起连续编号，索引即偏移。
            if (_policy.MaxProcessorStateSupported && MaxStateBox.SelectedIndex >= 0)
                payload.MaxProcessorState = MaxStateBox.SelectedIndex + 50;

            if (_policy.AspmSupported && AspmBox.SelectedIndex >= 0)
                payload.Aspm = AspmBox.SelectedIndex;

            if (_policy.IdleDisableSupported)
                payload.IdleDisable = CStateToggle.IsChecked == true;

            return payload;
        }

        // ============ 电源策略读取 / 渲染 ============

        private async System.Threading.Tasks.Task LoadPowerPolicyAsync()
        {
            try
            {
                await using WorkerPipeClient client = new();
                await client.ConnectAsync(TimeSpan.FromSeconds(2));

                PipeResponse response = await client.SendAsync(
                    new PipeRequest
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Command = WorkerPipeContract.CommandQueryPowerPolicy,
                        Payload = null
                    },
                    TimeSpan.FromSeconds(5));

                // 「读不到活动方案」也是 Success=true，快照本身就是降级信息，不是查询失败。
                PowerPolicySnapshot? snapshot = response.Success
                    ? JsonSerializer.Deserialize(response.Payload ?? string.Empty, IpcJsonContext.Default.PowerPolicySnapshot)
                    : PowerPolicySnapshot.Unavailable(response.Message);

                FillPolicyControls(snapshot ?? PowerPolicySnapshot.Unavailable("无法获取当前电源方案"));
            }
            catch (Exception)
            {
                // Worker 不在 / 管道超时：按降级快照渲染，各行显示「本机不支持」，
                // 原因放 ToolTip —— 比把不可写状态画成可写控件更诚实。
                FillPolicyControls(PowerPolicySnapshot.Unavailable("Worker 未就绪，无法读取电源策略"));
            }
        }

        private void FillPolicyControls(PowerPolicySnapshot snapshot)
        {
            _policy = snapshot;
            PolicySchemeText.Text = $"电源方案：{snapshot.ActiveSchemeName}";

            _suppressPolicyEvents = true;

            FillRow(EppBox, EppUnsupportedText, snapshot.EppSupported, snapshot.EppError,
                EppItems, Math.Clamp(snapshot.Epp ?? 0, 0, EppItems.Length - 1));
            FillRow(BoostBox, BoostUnsupportedText, snapshot.BoostModeSupported, snapshot.BoostModeError,
                BoostItems, Math.Clamp(snapshot.BoostMode ?? 0, 0, BoostItems.Length - 1));
            FillRow(MaxStateBox, MaxStateUnsupportedText, snapshot.MaxProcessorStateSupported, snapshot.MaxProcessorStateError,
                MaxStateItems, Math.Clamp((snapshot.MaxProcessorState ?? 50) - 50, 0, MaxStateItems.Length - 1));
            FillRow(AspmBox, AspmUnsupportedText, snapshot.AspmSupported, snapshot.AspmError,
                AspmItems, Math.Clamp(snapshot.Aspm ?? 0, 0, AspmItems.Length - 1));

            CStateToggle.IsChecked = snapshot.IdleDisable == true;
            CStateToggle.Visibility = snapshot.IdleDisableSupported ? Visibility.Visible : Visibility.Collapsed;
            CStateUnsupportedText.Visibility = snapshot.IdleDisableSupported ? Visibility.Collapsed : Visibility.Visible;
            CStateUnsupportedText.ToolTip = string.IsNullOrEmpty(snapshot.IdleDisableError) ? null : snapshot.IdleDisableError;

            _suppressPolicyEvents = false;
        }

        private static void FillRow(ComboBox box, TextBlock unsupported, bool supported, string? error, string[] items, int selectedIndex)
        {
            if (supported)
            {
                box.ItemsSource = items;
                box.Visibility = Visibility.Visible;
                unsupported.Visibility = Visibility.Collapsed;
                box.SelectedIndex = selectedIndex;
            }
            else
            {
                box.ItemsSource = null;
                box.Visibility = Visibility.Collapsed;
                unsupported.Visibility = Visibility.Visible;
                unsupported.ToolTip = string.IsNullOrEmpty(error) ? null : error;
            }
        }

        private void OnPolicyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_suppressPolicyEvents)
                MarkDirty();
        }

        private void OnPolicyToggleChanged(object sender, RoutedEventArgs e)
        {
            if (!_suppressPolicyEvents)
                MarkDirty();
        }

        // ============ 刷新（读硬件） ============

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            _busy = true;
            try
            {
                await using WorkerPipeClient client = new();
                await client.ConnectAsync(TimeSpan.FromSeconds(2));

                PipeResponse response = await client.SendAsync(
                    new PipeRequest
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Command = WorkerPipeContract.CommandQuerySmuSnapshot,
                        Payload = null
                    },
                    TimeSpan.FromSeconds(5));

                if (!response.Success)
                {
                    ShowToast($"刷新失败 — {response.Message}", "ErrorRed");
                    return;
                }

                TuningSnapshotPayload? snapshot = JsonSerializer.Deserialize(
                    response.Payload ?? string.Empty,
                    IpcJsonContext.Default.TuningSnapshotPayload);

                if (snapshot is null)
                {
                    ShowToast("刷新失败 — 响应无法解析", "ErrorRed");
                    return;
                }

                ApplySnapshot(snapshot);
                ShowToast("已从硬件读取当前状态", "SuccessGreen");
            }
            catch (Exception ex)
            {
                ShowToast($"刷新失败：{ex.GetType().Name}: {ex.Message}", "ErrorRed");
            }
            finally
            {
                _busy = false;
            }

            // 电源策略与 SMU 相互独立：SMU 刷新失败也要继续刷新策略。
            await LoadPowerPolicyAsync();
        }

        private void ApplySnapshot(TuningSnapshotPayload snapshot)
        {
            int[] margins = snapshot.CoreMargins ?? Array.Empty<int>();
            int[] display = new int[margins.Length];
            bool anyFailed = false;

            for (int i = 0; i < margins.Length; i++)
            {
                // int.MinValue 是「该核读不到」，不是「偏移 0」。
                if (margins[i] == int.MinValue)
                {
                    anyFailed = true;
                    display[i] = 0;
                }
                else
                {
                    display[i] = margins[i];
                }
            }

            BuildCoreInputs(display.Length > 0 ? display.Length : 16, display);

            _suppressCoEvents = true;
            FMaxBox.Text = snapshot.FMaxMHz.ToString(CultureInfo.InvariantCulture);
            _suppressCoEvents = false;

            ClearDirty();

            if (anyFailed)
                ShowToast("部分核读取失败，已按 0 显示", "WarnAmber");
        }

        // ============ SMU 状态副标题 ============

        /// <summary>页面切到调校时由主窗口调用，刷新一次 SMU 状态。</summary>
        public async void RefreshSmuStatus() => await RefreshSmuStatusAsync();

        private async System.Threading.Tasks.Task RefreshSmuStatusAsync()
        {
            try
            {
                await using WorkerPipeClient client = new();
                await client.ConnectAsync(TimeSpan.FromSeconds(2));

                PipeResponse response = await client.SendAsync(
                    new PipeRequest
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Command = WorkerPipeContract.CommandQuerySmuStatus,
                        Payload = null
                    },
                    TimeSpan.FromSeconds(2));

                SmuStatusPayload? status = JsonSerializer.Deserialize(
                    response.Payload ?? string.Empty,
                    IpcJsonContext.Default.SmuStatusPayload);

                CpuSubText.Text = status is { Available: true }
                    ? "16-Core Processor · SMU 状态：已联通"
                    : $"16-Core Processor · SMU 状态：不可用（{status?.Error ?? "未提供原因"}）";
            }
            catch (Exception)
            {
                // 连不上 / 超时 / JSON 异常一律归为「无法检测」，不推断成「不可用」。
                CpuSubText.Text = "16-Core Processor · SMU 状态：无法检测";
            }
        }

        // ============ 方案下拉：切换 / 重命名 ============

        /// <summary>读 profiles.json 填下拉。每次现读，不缓存 —— 切换或重命名后盘上就变了。</summary>
        public void LoadProfiles()
        {
            string path = _paths.File("profiles.json");
            ProfileFile? file = null;

            try
            {
                if (File.Exists(path))
                    file = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.ProfileFile);

                _profiles = file?.Profiles ?? new List<ProfileSnapshot>();
            }
            catch (Exception ex)
            {
                _profiles = new List<ProfileSnapshot>();
                ShowToast($"读取 profiles.json 失败：{ex.Message}", "ErrorRed");
            }

            _suppressProfileEvents = true;
            ProfileComboBox.ItemsSource = _profiles;
            ProfileComboBox.SelectedValue = file?.ActiveId;

            // ActiveId 缺失时退回第一项。
            if (ProfileComboBox.SelectedIndex < 0 && _profiles.Count > 0)
                ProfileComboBox.SelectedIndex = 0;

            _suppressProfileEvents = false;
        }

        private async void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileEvents || _renaming || _busy)
                return;

            if (ProfileComboBox.SelectedValue is not string id || id.Length == 0)
                return;

            string name = (ProfileComboBox.SelectedItem as ProfileSnapshot)?.Name ?? id;
            _busy = true;
            ApplyButton.IsEnabled = false;

            try
            {
                await using WorkerPipeClient client = new();
                await client.ConnectAsync(TimeSpan.FromSeconds(2));

                PipeResponse response = await client.SendAsync(
                    new PipeRequest
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Command = WorkerPipeContract.CommandApplyTuning,
                        Payload = id
                    },
                    TimeSpan.FromSeconds(75));

                if (response.Success)
                {
                    ShowToast($"已切换方案：{name}", "SuccessGreen");
                    LoadFromStore();
                }
                else
                {
                    ShowToast($"切换失败 — {response.Message}", "ErrorRed");
                    LoadProfiles();
                }
            }
            catch (Exception ex)
            {
                ShowToast($"切换失败：{ex.GetType().Name}: {ex.Message}", "ErrorRed");
                LoadProfiles();
            }
            finally
            {
                _busy = false;
                ApplyButton.IsEnabled = true;
            }
        }

        private void OnRenameItemPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Preview 隧道先于 ComboBoxItem 的选中逻辑：先置位再让选中发生，
            // 切换 IPC 就不会把「点铅笔」当成「用户要切方案」。
            _renaming = true;
        }

        private void OnRenameItemClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is not FrameworkElement { DataContext: ProfileSnapshot snapshot })
            {
                _renaming = false;
                return;
            }

            _renameTarget = snapshot;
            RenameBox.Text = snapshot.Name;
            RenamePopup.PlacementTarget = ProfileComboBox;
            RenamePopup.IsOpen = true;
            RenameBox.Focus();
            RenameBox.SelectAll();
        }

        private void OnRenamePopupClosed(object? sender, EventArgs e)
        {
            _renaming = false;
            _renameTarget = null;

            // 点铅笔会让 ComboBox 视觉选中那一项（可能不是生效方案）；
            // 重读盘把选中项拉回 ActiveId，保证界面与真实状态一致。
            LoadProfiles();
        }

        private async void OnRenameConfirmClick(object sender, RoutedEventArgs e) => await RenameCurrentAsync();

        private void OnRenameCancelClick(object sender, RoutedEventArgs e) => RenamePopup.IsOpen = false;

        private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = RenameCurrentAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RenamePopup.IsOpen = false;
            }
        }

        private async System.Threading.Tasks.Task RenameCurrentAsync()
        {
            if (_renameTarget is null || _busy)
                return;

            string newName = RenameBox.Text.Trim();
            if (newName.Length == 0)
            {
                ShowToast("新名称不能为空", "WarnAmber");
                return;
            }

            foreach (ProfileSnapshot item in _profiles)
            {
                if (!string.Equals(item.Id, _renameTarget.Id, StringComparison.Ordinal)
                    && string.Equals(item.Name, newName, StringComparison.Ordinal))
                {
                    ShowToast($"已存在同名方案：{newName}", "WarnAmber");
                    return;
                }
            }

            _busy = true;
            try
            {
                await using WorkerPipeClient client = new();
                await client.ConnectAsync(TimeSpan.FromSeconds(2));

                PipeResponse response = await client.SendAsync(
                    new PipeRequest
                    {
                        Version = WorkerPipeContract.ProtocolVersion,
                        Command = WorkerPipeContract.CommandRenameProfile,
                        Payload = JsonSerializer.Serialize(
                            new RenameProfilePayload { Id = _renameTarget.Id, NewName = newName },
                            IpcJsonContext.Default.RenameProfilePayload)
                    },
                    TimeSpan.FromSeconds(5));

                if (response.Success)
                {
                    ShowToast("方案已重命名", "SuccessGreen");
                    RenamePopup.IsOpen = false;
                }
                else
                {
                    ShowToast($"重命名失败 — {response.Message}", "ErrorRed");
                }
            }
            catch (Exception ex)
            {
                ShowToast($"重命名失败：{ex.GetType().Name}: {ex.Message}", "ErrorRed");
            }
            finally
            {
                _busy = false;
            }
        }

        // ============ Toast ============

        /// <summary>轻提示，最多同时 3 条，3 秒自动消失。brushKey 是语义色 token，禁止传强调色以外的动态值。</summary>
        private void ShowToast(string message, string brushKey)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => ShowToast(message, brushKey));
                return;
            }

            var bar = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(6, 0, 0, 6),
                Background = (Brush)FindResource(brushKey)
            };

            var text = new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Left);
            dock.Children.Add(bar);
            dock.Children.Add(text);

            var toast = new Border
            {
                Background = (Brush)FindResource("PopupBg"),
                BorderBrush = (Brush)FindResource("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 16, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = dock,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 2,
                    Opacity = 0.4
                }
            };

            ToastHost.Children.Insert(0, toast);
            while (ToastHost.Children.Count > 3)
                ToastHost.Children.RemoveAt(ToastHost.Children.Count - 1);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                ToastHost.Children.Remove(toast);
                timer.Stop();
            };
            timer.Start();
        }

        // ============ 取值校验 ============

        private int[]? ReadMargins(out string error)
        {
            error = string.Empty;
            int[] values = new int[_coBoxes.Count];

            for (int i = 0; i < _coBoxes.Count; i++)
            {
                string text = _coBoxes[i].Text.Trim();
                if (text.Length == 0)
                {
                    values[i] = 0;
                    continue;
                }

                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    error = $"核 {i} 的值「{text}」不是整数。";
                    return null;
                }

                if (value < CoMin || value > CoMax)
                {
                    error = $"核 {i} 的值 {value} 超出范围 [{CoMin}, {CoMax}]。";
                    return null;
                }

                values[i] = value;
            }

            return values;
        }

        private bool TryReadFMax(out uint fMax, out string error)
        {
            error = string.Empty;
            fMax = 0;

            if (!uint.TryParse(FMaxBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fMax))
            {
                error = "FMax 必须是非负整数（0 表示不写 FMax）。";
                return false;
            }

            if (fMax != 0 && (fMax < FMaxMin || fMax > FMaxMax))
            {
                error = $"FMax 需在 {FMaxMin}–{FMaxMax} MHz 之间，或填 0 表示不写。";
                return false;
            }

            return true;
        }
    }
}
