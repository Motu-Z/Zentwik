using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 界面设置（主题 / 强调色），持久化到 %APPDATA%\Zentwik\ui.json
    ///
    /// 托盘图标的有无由 <see cref="AutoStartMode"/> 与 <see cref="CloseBehavior"/> 共同决定
    /// （见 <see cref="TrayPolicy"/>）。这两个值有三个写入点：行为页的两个下拉、
    /// 主窗口侧边栏的自启复选框、以及 <c>App.ChangeAutoStartAsync</c> 的 IPC 成功路径。
    /// 让「谁改了」逐个去通知托盘，漏一处就是「设置改了、图标没跟着变」；
    /// 改成属性变更即广播，托盘只需要订阅一次。
    ///
    /// 刻意只在这两个属性上抛：其余字段（主题、材质、动效）改的是界面自身的呈现，
    /// 由各自的绑定就地刷新，与进程生命周期无关，广播出去只会多出无意义的回调。
    /// </summary>
    public sealed class UiSettings : INotifyPropertyChanged
    {
        /// <summary>出厂强调色。与 ThemeService 的默认色、通用页色板的兜底色同一值。</summary>
        public const string DefaultAccentHex = "#38BDF8";

        /// <summary>
        /// 属性变更通知。序列化时被忽略（事件不参与 JSON），AOT 源生成不受影响。
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        // 两个会广播变更的属性的后备字段。初值与属性默认值必须一致：
        // 枚举的 0 恰好是 None / Exit，但那是巧合，不写出来就等于靠枚举顺序成立。
        private AutoStartMode _autoStartMode = AutoStartMode.None;

        private CloseBehavior _closeBehavior = CloseBehavior.Exit;
        [JsonConverter(typeof(JsonStringEnumConverter<AppThemeMode>))]
        public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

        /// <summary>
        /// 强调色是否跟随 Windows 系统强调色。
        /// **默认 true**：首次安装、或配置文件里没有这个键时，一律跟随系统。
        /// </summary>
        public bool FollowSystemAccent { get; set; } = true;

        /// <summary>自定义强调色（十六进制，如 #38BDF8）。仅 FollowSystemAccent=false 时生效。</summary>
        public string CustomAccentHex { get; set; } = "#38BDF8";

        /// <summary>
        /// 窗口材质：Mica / Acrylic / Solid。
        ///
        /// 刻意存字符串而不是强类型枚举：ui.json 走源生成序列化（AppJsonContext），
        /// 新增枚举会扩大序列化契约面，而这三个值的解析失败成本极低（回落 Mica）。
        /// 解析与合法性收敛都在 ThemeController.ParseBackdrop 里做。
        /// </summary>
        public string Backdrop { get; set; } = "Mica";

        /// <summary>窗口动效：Full / Reduced / Off。解析见 ThemeController.ParseMotion。</summary>
        public string Motion { get; set; } = "Full";

        // 启动与后台

        /// <summary>
        /// 开机自启的形态（是否已注册计划任务 ZentwikAutoStart，以及拉起哪些进程）。
        /// 真实状态以计划任务是否存在为准，本字段仅作缓存与界面回显。
        ///
        /// 枚举的默认值是 0。这里 0 恰好是 <see cref="AutoStartMode.None"/>，
        /// 但如果将来有人往枚举里插一个值而不改数字，默认值就会悄悄变成「开机自启」。
        /// 显式赋值是让「默认不自启」这条产品决定钉在代码里，而不是靠枚举顺序碰巧成立。
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter<AutoStartMode>))]
        public AutoStartMode AutoStartMode
        {
            get => _autoStartMode;
            set
            {
                // 只在真的变了的时候广播：App 的保存失败回滚路径会把同一个值再赋一遍，
                // 无脑广播会让托盘在那一次回滚里被销毁又重建。
                if (_autoStartMode == value)
                    return;

                _autoStartMode = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 点主窗口 X 时的行为。
        ///
        /// 旧实现是 <c>App.CurrentCloseBehavior</c> —— 一个编译期写死的静态属性，
        /// 用户改不了，手工验收也只能改源码重编。行为设置是用户设置，必须能选、能存。
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter<CloseBehavior>))]
        public CloseBehavior CloseBehavior
        {
            get => _closeBehavior;
            set
            {
                if (_closeBehavior == value)
                    return;

                _closeBehavior = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 启动时自动应用调校：静默模式（-silent）下是否把 tuning.json 的 CO / FMax 写回 SMU。
        /// </summary>
        public bool ApplyTuningOnStartup { get; set; } = true;

        /// <summary>
        /// 后台常驻调度：为 true 时按 schedules.json 的规则给新进程自动调度。
        ///
        /// 双进程架构下，后台进程（Worker）由谁拉起、要不要常驻，由
        /// <see cref="AutoStartMode"/> 与 <see cref="CloseBehavior"/> 决定 ——
        /// 前者管开机时拉什么，后者管关窗口时留什么。本项只表达
        /// 「后台要不要干调度这件事」，与「后台存不存在」是两回事。
        ///
        /// 旧版这里还有一个「后台服务总闸」与一组「轻量模式」字段（延迟秒数等），
        /// 三者都已删除：它们表达的是单进程时代「点 X 要不要真的退出 / 多久销毁界面」
        /// 的语义，双进程下同一个问题已经有了更精确的答案，留着只会让几处互相打架。
        /// 删除清单见 docs/Core-API-契约-批次二冻结.txt 的「批次八追加」。
        /// </summary>
        public bool WatchProcessBinding { get; set; } = true;

        /// <summary>
        /// 出错时用系统气泡提示（非阻塞）。
        ///
        /// 默认 true：失败的写入 / 驱动异常必须让用户看见。
        /// 但调 SMU 时偶发的一次读失败并不严重，用户如果觉得弹得烦可以关掉，
        /// 错误仍然完整写进日志页，不会被丢弃。
        /// </summary>
        public bool NotifyOnError { get; set; } = true;

        /// <summary>提示（Toast）停留秒数，1–30。界面读它决定气泡多久消失。</summary>
        public int ToastSeconds { get; set; } = 3;

        /// <summary>
        /// 启动异常自动拦截：连续多次「写入调校后没走到完成」时，跳过下次开机自动调校。
        ///
        /// 数据一分不删，只是这一次不往 SMU 写。关掉它等于放弃这层兜底 ——
        /// 一套真让机器起不来的参数会每次开机都重演。
        /// </summary>
        public bool SmartAntiBrick { get; set; } = true;

        /// <summary>
        /// 智能休眠感知：从睡眠 / 休眠恢复后重新下发当前调校参数。
        ///
        /// 进程本身在睡眠里只是被冻结，唤醒后照常活着；丢的是 SMU 里的
        /// CO 偏移、FMax 这些寄存器值 —— 主板在 S3 / 休眠时会把它们清回默认。
        /// 所以这里要做的不是「重启进程」，是「醒来后再写一遍」。
        /// </summary>
        public bool SmartHibernateAware { get; set; } = true;

        /// <summary>
        /// 广播属性变更。只由本类的 setter 调用。
        /// <c>[CallerMemberName]</c> 让调用点不必写字面量，属性改名时不会漏。
        /// </summary>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class UiSettingsStore : IUiSettingsStore
    {
        private readonly IAppPaths _paths;

        public UiSettingsStore(IAppPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
        }

        private string FilePath => _paths.File("ui.json");

        public UiSettings Settings { get; private set; } = new();

        public void Load()
        {
            try
            {
                Settings = ReadFromDisk() ?? new UiSettings();
            }
            catch
            {
                Settings = new UiSettings();
            }
        }

        /// <summary>
        /// 读盘。**不做任何旧格式兼容**：本工程已裁定不背历史包袱，
        /// 旧键（AutoStart / showTrayIcon / backendService / accentVersion …）
        /// 读进来会被静默忽略，下次 <see cref="Save"/> 自然消失。
        /// </summary>
        private UiSettings? ReadFromDisk()
        {
            if (!File.Exists(FilePath))
                return null;

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.UiSettings) ?? new UiSettings();
        }

        /// <summary>
        /// 恢复出厂：整份设置换回默认实例并落盘。
        ///
        /// 整体替换而不是逐字段清零：将来新增字段自动被带上默认值，
        /// 不会出现「重置后仍保留上一台机器的某一项」。
        /// </summary>
        public void ResetToFactory()
        {
            Settings = new UiSettings();
            Save();
        }

        public void Save()
        {
            // 写盘失败必须让调用方知道：吞掉会让 IPC 回 Success=true，
            // 界面显示已保存、重启后设置却回退。
            JsonFile.Write(FilePath, Settings, AppJsonContext.Default.UiSettings);
        }
    }
}
