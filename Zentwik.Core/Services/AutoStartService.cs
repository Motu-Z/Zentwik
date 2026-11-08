using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Zentwik.Core.Helpers;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 开机静默自启：通过 Windows 计划任务实现「登录后延时 15 秒、最高权限、无窗口启动」。
    ///
    /// 为什么用 schtasks.exe 而不是 COM（TaskScheduler / ITaskService）：
    ///   1. 零依赖：schtasks 是 Windows 内置命令行，不需要注册 COM 互操作程序集，
    ///      也不受 .NET 与 Windows 版本兼容性影响。
    ///   2. 需要的能力它都覆盖：登录触发器、延时、RunLevel=Highest。
    ///   3. 便于排错：命令行输出可直接进日志，用户也能自己在任务计划程序里核对。
    ///
    /// 为什么用 /XML 而不是 /Create /SC ONLOGON /RL HIGHEST：
    ///   · /XML 能把触发器、主体、设置一次写全，不受 schtasks 参数缺省值影响；
    ///   · 路径带空格时 XML 的 &lt;Command&gt; 是独立元素，不用跟 /TR 的引号规则搏斗。
    ///
    /// 【主体写法是这套方案唯一容易搞错、且错了就完全不触发的地方】
    ///   必须是 UserId + LogonType=InteractiveToken + RunLevel=HighestAvailable：
    ///     · UserId 用当前用户的 SID —— 登录触发器和主体都指向同一个人，
    ///       计划任务服务才能把「该用户登录」这个事件落到这个任务上；
    ///     · InteractiveToken 表示直接用登录会话的令牌，不存密码、不需要 /RP；
    ///     · HighestAvailable 让它以管理员令牌启动，且**不经过 UAC**。
    ///
    ///   绝对不要写成 &lt;GroupId&gt;S-1-5-32-544&lt;/GroupId&gt;（Administrators 组）+ 不指定
    ///   UserId 的 LogonTrigger。组主体代表「一组人」，登录事件无法映射到具体主体，
    ///   表现就是这个任务创建成功、状态正常、但每次开机都不触发，且没有任何报错。
    /// </summary>
    public partial class AutoStartService
    {
        private readonly ILogService _log;

        public AutoStartService(ILogService log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
        }

        /// <summary>计划任务名称（固定，便于用户识别）。</summary>
        public const string TaskName = "ZentwikAutoStart";

        /// <summary>登录后延时秒数。</summary>
        private const int DelaySeconds = 15;

        /// <summary>
        /// 登录自启的启动参数：拉起 Worker 后由 Worker 再把 UI 叫起来。
        ///
        /// 【为什么不是 -silent】
        /// 双进程架构下任务只负责拉起 Worker（常驻 + 写 SMU），「要不要显示界面」是
        /// Worker 启动后按配置决定的事，不再是启动参数能表达的语义。旧常量
        /// <c>SilentArgument = "-silent"</c> 在 Worker 侧没有任何解析点（`Program.Main`
        /// 只把 args 交给 `Host.CreateApplicationBuilder`），属于死代码。
        /// 批次五实测 `Zentwik.Worker.exe -silent` **不会崩**（进程存活、管道 ping 通、
        /// `startup_crash.log` 无新增），所以删除理由是「语义无效」而不是「会崩」。
        ///
        /// 【为什么是 --open-ui 而不是 IPC 的 open-ui】
        /// 这里是**命令行开关**，不是管道命令。管道命令 `WorkerPipeContract.CommandOpenUi`
        /// 的值是 `"open-ui"`（无横线），两者不可混用：一个是进程启动参数，一个是协议字段。
        /// </summary>
        public const string OpenUiArgument = "--open-ui";

        /// <summary>
        /// 登录自启的启动参数：只拉起 UI 的托盘，不开主窗口。
        ///
        /// 【为什么托盘也走命令行而不是 IPC】
        /// 与 <see cref="OpenUiArgument"/> 同理：这是**进程启动参数**，决定 UI 进程
        /// 一起来是建窗口还是只建托盘。托盘图标由 UI 进程持有（事实卡 §7.2：
        /// Worker 不持有任何托盘），所以 Worker 只能把「别开窗口」这件事作为参数传下去。
        ///
        /// 【与 --open-ui 互斥】
        /// 两者同时出现时以 <see cref="OpenUiArgument"/> 为准（见 Worker 的参数解析），
        /// 因为「打开窗口」是更明确、用户感知更强的意图。
        /// </summary>
        public const string TrayArgument = "--tray";

        /// <summary>
        /// 把形态翻译成 Worker 的命令行参数。纯函数，便于单元测试逐个钉住四种形态。
        ///
        /// 【None 与 WorkerOnly 都返回空串，但含义不同】
        /// None 根本不该创建计划任务（<see cref="Enable(AutoStartMode)"/> 会直接拒绝）；
        /// WorkerOnly 则**要**创建任务，只是不带任何参数 —— 计划任务只拉 Worker，
        /// Worker 起来后因为没收到开关而不去拉 UI。两者在命令行层面无法区分，
        /// 区分点在调用方：一个不该走到这里，一个走到这里是正确结果。
        /// </summary>
        public static string BuildArguments(AutoStartMode mode) => mode switch
        {
            AutoStartMode.WorkerWithTray => TrayArgument,
            AutoStartMode.WorkerWithUI => OpenUiArgument,
            _ => string.Empty
        };

        /// <summary>形态的中文说明。日志与计划任务描述共用一份，避免两处文案漂移。</summary>
        public static string DescribeMode(AutoStartMode mode) => mode switch
        {
            AutoStartMode.None => "不自启",
            AutoStartMode.WorkerOnly => "只启动后台服务（不显示界面、不建托盘）",
            AutoStartMode.WorkerWithTray => "启动后台服务 + 托盘图标",
            AutoStartMode.WorkerWithUI => "启动后台服务 + 主界面",
            _ => "未知形态"
        };


        /// <summary>
        /// 读取 schtasks 输出用的编码。
        ///
        /// schtasks 是控制台程序，往管道写的是**系统 OEM 代码页**（中文系统 936），
        /// 用 UTF-8 去读会得到一屏乱码，后面按「状态 / 上次结果」这些标签做的匹配全部落空，
        /// 诊断日志等于白写。这里按 GetOEMCP 取真实代码页。
        /// 注意：.NET Core 起 GBK 等传统代码页必须显式注册 CodePagesEncodingProvider，
        /// 该类库随 Microsoft.NETCore.App 一起发布，不需要额外引用包。
        /// </summary>
        private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

        private static Encoding ResolveConsoleEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                uint codePage = GetOEMCP();
                if (codePage != 0 && codePage != 65001)
                    return Encoding.GetEncoding((int)codePage);
            }
            catch
            {
                // 取不到就退回 UTF-8：乱码总比抛异常好
            }

            return Encoding.UTF8;
        }

        [LibraryImport("kernel32.dll")]
        private static partial uint GetOEMCP();

        /// <summary>计划任务当前是否已注册。</summary>
        public bool IsEnabled()
        {
            try
            {
                (int exitCode, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
                return exitCode == 0;
            }
            catch (Exception ex)
            {
                _log.Warn($"查询计划任务失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 创建（或覆盖）计划任务。默认形态是「拉起 Worker 后打开主界面」，
        /// 与旧版 <c>bool AutoStart = true</c> 的行为一致，保留这个无参重载是为了
        /// 让「只是想开机看到界面」的调用方不必写枚举。
        /// </summary>
        /// <returns>true = 创建成功且已回查确认存在。</returns>
        public bool Enable() => Enable(AutoStartMode.WorkerWithUI);

        /// <summary>
        /// 按给定形态创建（或覆盖）计划任务。
        /// </summary>
        /// <param name="mode">
        /// 自启形态。<see cref="AutoStartMode.None"/> 会被拒绝 —— 那等于「不要自启」，
        /// 正确动作是 <see cref="Disable"/>，在这里静默当成功会让「关掉自启」变成
        /// 「任务还在但没人知道它带什么参数」。
        /// </param>
        /// <returns>true = 创建成功且已回查确认存在。</returns>
        public bool Enable(AutoStartMode mode)
        {
            if (mode == AutoStartMode.None)
            {
                _log.Error("创建计划任务失败：AutoStartMode.None 表示不要自启，应调用 Disable()");
                return false;
            }

            string? exePath = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exePath))
            {
                _log.Error("创建计划任务失败：无法定位 Zentwik.exe 路径");
                return false;
            }

            string? userId = ResolveCurrentUserIdentity();
            if (string.IsNullOrEmpty(userId))
            {
                _log.Error("创建计划任务失败：无法获取当前用户标识");
                return false;
            }

            string? tempXml = null;
            try
            {
                tempXml = Path.Combine(Path.GetTempPath(), $"Zentwik-AutoStart-{Guid.NewGuid():N}.xml");

                string arguments = BuildArguments(mode);
                string xml = BuildTaskXml(exePath, arguments, userId, mode);
                // UTF-16 + BOM：schtasks /XML 最稳定的编码
                File.WriteAllText(tempXml, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

                // /F 即「存在则覆盖」，不必先 Delete。
                // 先删再建存在一个空窗：建失败时旧任务已经没了，用户只剩一个关着的开关。
                (int exitCode, string output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F");

                if (exitCode != 0)
                {
                    _log.Error($"创建计划任务失败（退出码 {exitCode}）：{output}");
                    return false;
                }

                // 回查：schtasks 偶有「返回 0 但任务没落地」的情况（多半是权限或策略拦截）
                if (!IsEnabled())
                {
                    _log.Error($"计划任务 {TaskName} 创建后回查不到，视为失败。原始输出：{output}");
                    return false;
                }

                _log.Info(
                    $"计划任务 {TaskName} 创建成功：用户 {userId} · 登录后延时 {DelaySeconds} 秒 · 最高权限 · " +
                    $"{DescribeMode(mode)}（{exePath} {DescribeLaunch(arguments)}）");

                // 把「下次运行时间 / 上次结果 / 运行方式」写进日志。
                // 用户下次开机发现没起来时，这三个字段直接指出是没触发还是启动后崩了。
                LogTaskState();
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"创建计划任务异常：{ex.Message}");
                return false;
            }
            finally
            {
                if (tempXml != null)
                {
                    try
                    {
                        File.Delete(tempXml);
                    }
                    catch
                    {
                        // 临时文件清理失败不影响主流程
                    }
                }
            }
        }

        /// <summary>
        /// 计划任务里的可执行路径与当前进程不一致时就地改写。
        /// </summary>
        /// <remarks>
        /// 【为什么需要这一步】
        /// 计划任务保存的是绝对路径。应用换了安装目录（例如从构建输出目录搬到正式部署目录）后，
        /// 任务仍然指着旧位置 —— 开机拉起的是旧构建里的 Worker，再由那个 Worker 拉起旧界面。
        /// 用户看到的现象是「开机自启开的是老 UI」，而设置页里的开关还是「已开启」。
        ///
        /// 【只在路径真的不同的时候写】
        /// 每次都重写会让任务的「上次运行结果」等状态被清掉，也白白多一次提权写操作。
        /// 形态（参数）沿用任务里原有的，不擅自替用户改「要不要开界面」。
        /// </remarks>
        /// <returns>true = 已改写成功。</returns>
        public bool RepairRegisteredPath()
        {
            try
            {
                string? exePath = ResolveExecutablePath();
                if (string.IsNullOrEmpty(exePath) || !IsEnabled())
                    return false;

                (int exitCode, string xml) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
                if (exitCode != 0)
                    return false;

                string? registered = ReadXmlText(xml, "Command");
                if (string.IsNullOrWhiteSpace(registered))
                    return false;

                if (PathsEqual(registered.Trim(), exePath))
                    return false;

                AutoStartMode mode = ParseArgumentsMode(ReadXmlText(xml, "Arguments"));
                _log.Warn($"计划任务 {TaskName} 指向 {registered}，与当前 {exePath} 不一致，正在改写");
                return Enable(mode);
            }
            catch (Exception ex)
            {
                // 校正失败不阻断启动：任务还指着旧路径，下次启动会再试一次。
                _log.Warn($"校正计划任务路径失败：{ex.Message}");
                return false;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 路径非法（含通配符等）时按「不相等」处理，交给 Enable 覆盖成正确路径。
                return false;
            }
        }

        /// <summary>从任务的参数推回自启形态。认不出来时按「只拉 Worker」处理，最保守。</summary>
        internal static AutoStartMode ParseArgumentsMode(string? arguments)
        {
            string args = arguments ?? string.Empty;

            if (args.Contains(TrayArgument, StringComparison.OrdinalIgnoreCase))
                return AutoStartMode.WorkerWithTray;

            if (args.Contains(OpenUiArgument, StringComparison.OrdinalIgnoreCase))
                return AutoStartMode.WorkerWithUI;

            return AutoStartMode.WorkerOnly;
        }

        /// <summary>
        /// 取 XML 里某个叶子标签的文本。
        /// </summary>
        /// <remarks>
        /// 不用 XDocument.Parse：schtasks 输出带 <c>&lt;?xml encoding="UTF-16"?&gt;</c> 声明，
        /// 从字符串解析会撞「没有 BOM 无法切到 Unicode」。这里只关心一两个标签，
        /// 用正则取更稳，也不受命名空间前缀影响。
        /// </remarks>
        private static string? ReadXmlText(string xml, string tag)
        {
            Match match = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>删除计划任务。</summary>
        /// <returns>true = 已删除或本就不存在。</returns>
        public bool Disable()
        {
            try
            {
                if (!IsEnabled())
                {
                    _log.Info($"计划任务 {TaskName} 不存在，无需删除");
                    return true;
                }

                (int exitCode, string output) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                if (exitCode == 0)
                {
                    _log.Info($"计划任务 {TaskName} 已删除（开机自启已关闭）");
                    return true;
                }

                _log.Error($"删除计划任务失败（退出码 {exitCode}）：{output}");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"删除计划任务异常：{ex.Message}");
                return false;
            }
        }

        /// <summary>参数为空时不要留一个悬空的空格（日志里看得出「没带参数」而不是「参数被截断」）。</summary>
        private static string DescribeLaunch(string arguments) =>
            string.IsNullOrEmpty(arguments) ? "(无参数)" : arguments;

        /// <summary>
        /// 构造任务定义 XML。
        /// </summary>
        /// <param name="exePath">Worker 可执行文件完整路径（`Environment.ProcessPath` 的解析结果）。</param>
        /// <param name="arguments">启动参数（<see cref="BuildArguments"/> 的结果，可能为空串）。</param>
        /// <param name="userId">当前用户 SID 或 DOMAIN\User；触发器和主体必须用同一份。</param>
        /// <param name="mode">自启形态，只用于生成任务描述。</param>
        private string BuildTaskXml(string exePath, string arguments, string userId, AutoStartMode mode)
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            XDocument doc = new(
                new XDeclaration("1.0", "UTF-16", null),
                new XElement(ns + "Task",
                    new XAttribute("version", "1.2"),

                    new XElement(ns + "RegistrationInfo",
                        new XElement(ns + "Author", "Zentwik"),
                        new XElement(ns + "Description",
                            $"Zentwik 登录自启：延时 15 秒后以最高权限启动后台服务（重新应用 CO / FMax、启动进程调度监听）。当前形态：{DescribeMode(mode)}。")),

                    // 触发器：该用户登录，延时 15 秒。
                    // UserId 必须写 —— 只写 Enabled + Delay 在组主体任务上永远不触发。
                    new XElement(ns + "Triggers",
                        new XElement(ns + "LogonTrigger",
                            new XElement(ns + "Enabled", "true"),
                            new XElement(ns + "UserId", userId),
                            new XElement(ns + "Delay", $"PT{DelaySeconds}S"))),

                    // 主体：当前用户 + 交互令牌 + 最高权限。
                    // InteractiveToken 是不存密码、不弹 UAC 的关键。
                    new XElement(ns + "Principals",
                        new XElement(ns + "Principal",
                            new XAttribute("id", "Author"),
                            new XElement(ns + "UserId", userId),
                            new XElement(ns + "LogonType", "InteractiveToken"),
                            new XElement(ns + "RunLevel", "HighestAvailable"))),

                    // 元素顺序与「任务计划程序」自身导出的格式一致
                    new XElement(ns + "Settings",
                        new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                        new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                        new XElement(ns + "StopIfGoingOnBatteries", "false"),
                        new XElement(ns + "AllowHardTerminate", "true"),
                        // 错过的触发补跑：快速启动（休眠）恢复时，登录事件可能早于任务服务就绪
                        new XElement(ns + "StartWhenAvailable", "true"),
                        new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                        new XElement(ns + "IdleSettings",
                            new XElement(ns + "StopOnIdleEnd", "false"),
                            new XElement(ns + "RestartOnIdle", "false")),
                        new XElement(ns + "AllowStartOnDemand", "true"),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "Hidden", "false"),
                        new XElement(ns + "RunOnlyIfIdle", "false"),
                        // 登录触发不需要唤醒机器：WakeToRun 只对时间/空闲触发器有意义
                        new XElement(ns + "WakeToRun", "false"),
                        new XElement(ns + "ExecutionTimeLimit", "PT0S")),         // 不限时长

                    // 动作：拉起 Worker（Worker 再按配置决定是否开 UI）
                    new XElement(ns + "Actions",
                        new XAttribute("Context", "Author"),
                        new XElement(ns + "Exec",
                            new XElement(ns + "Command", exePath),
                            new XElement(ns + "Arguments", arguments)))));
            return doc.Declaration + Environment.NewLine + doc.ToString();
        }

        /// <summary>
        /// 把计划任务的关键状态写进日志：下次运行时间、上次运行时间、上次结果、运行方式。
        /// schtasks 输出是本地化的，因此同时匹配中英文标签。
        /// </summary>
        private void LogTaskState()
        {
            try
            {
                (int code, string output) = RunSchtasks($"/Query /TN \"{TaskName}\" /V /FO LIST");
                if (code != 0 || string.IsNullOrWhiteSpace(output))
                {
                    _log.Warn($"读取计划任务 {TaskName} 状态失败（退出码 {code}）");
                    return;
                }

                // 只看排障需要的几行，不把整份 30 行 dump 塞进日志页
                string[] keys =
                {
                    "状态", "Status",
                    "运行方式", "Run As User",
                    "上次运行时间", "Last Run Time",
                    "上次结果", "Last Result",
                    "下次运行时间", "Next Run Time"
                };

                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0)
                        continue;

                    if (keys.Any(k => line.StartsWith(k + ":", StringComparison.OrdinalIgnoreCase)))
                        _log.Info($"[自启] {line}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"读取计划任务状态异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 取当前用户标识：优先 SID（与账户改名、域名变更无关），取不到再回退 DOMAIN\User。
        /// </summary>
        private string? ResolveCurrentUserIdentity()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                string? sid = identity.User?.Value;
                if (!string.IsNullOrWhiteSpace(sid) && sid.StartsWith("S-1-", StringComparison.Ordinal))
                    return sid;
            }
            catch
            {
                // 身份查询失败，走回退路径
            }

            try
            {
                string user = Environment.UserName;
                if (string.IsNullOrWhiteSpace(user))
                    return null;

                string domain = Environment.UserDomainName;
                return string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>定位当前可执行文件（含自包含发布与单文件两种情况）。</summary>
        private string? ResolveExecutablePath()
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;

            try
            {
                path = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            catch
            {
                // 忽略
            }

            return null;
        }

        /// <summary>执行 schtasks 并收集输出（不弹窗、不阻塞 UI）。</summary>
        private (int ExitCode, string Output) RunSchtasks(string arguments)
        {
            ProcessStartInfo psi = new("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 schtasks.exe");

            // 两个管道必须并发读。先 ReadToEnd(stdout) 再读 stderr 是经典死锁：
            // 若子进程先把 stderr 的 4KB 缓冲区写满，它会阻塞在那里等父进程读，
            // 而父进程正阻塞在等 stdout 关闭——双方互等，直到超时也拿不到退出码。
            // 用异步读先把两路数据抽干，再等退出。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            bool exited = process.WaitForExit(15000);
            if (!exited)
            {
                // 超时不杀就会残留一个 schtasks 进程，下次再调又是一份
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出或无权 */ }

                // 两个读取任务此刻多半还未完成，Dispose 会中断管道让它们以异常结束。
                // 必须在这里观察掉，否则会变成未观察的 task 异常（可能触发
                // TaskScheduler.UnobservedTaskException，在严格配置下影响进程）。
                ObserveQuietly(stdoutTask);
                ObserveQuietly(stderrTask);

                return (-1, "schtasks 超时（15s）未返回");
            }

            // 进程已退出，剩下的读取会立刻完成；给它一个短上限防止异常挂起
            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 2000);
            string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";

            string combined = string.Join(" ",
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            return (process.ExitCode, combined);
        }

        /// <summary>
        /// 静默观察一个可能已失败的 Task：吞掉异常，避免变成未观察异常。
        /// </summary>
        private void ObserveQuietly(Task task)
        {
            try
            {
                task.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch
            {
                // 观察失败本身无影响
            }
        }
    }
}
