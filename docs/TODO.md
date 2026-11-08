# Zentwik 未实现功能清单（TODO）

> 用途：记录**明确知道要做、但目前没做**的功能。与 `docs/UFIX.md` 的分工是：
> **TODO = 功能不存在**；**UFIX = 功能存在但怀疑有问题或半成品**。
>
> 生成时间：2026-11-01（批次 A-3）。依据为当前工作区源码与 `CHANGELOG.md`，未运行 git 命令。
> 优先级口径：**高** = 影响发版、影响核心卖点或被公开承诺过；**中** = 功能缺口但有绕行办法；**低** = 锦上添花。

| # | 事项 | 涉及文件 | 一句话简述 | 优先级 | 计划批次 |
|---|---|---|---|---|---|
| 1 | 安装前 .NET 10 检测 | `build/installer/Zentwik.iss` | 安装包不检查 .NET 10 Runtime 是否已装，用户装完双击才崩，且崩在 `StartupCrashReporter` 之前时连日志都没有 | **高** | 批次五 |
| 2 | 自动更新检查 | 无（不存在） | 全仓无更新检查代码，用户只能手动下载新 zip / setup 覆盖 | 低 | 产品后期 |
| 3 | 电源策略（通道已定，底层已实装，UI 已接） | `Zentwik.Core/Services/PowerPolicyService.cs`、`PowerPolicyApi.cs`（原 `ProcessorPowerPolicy.cs` / `ProcessorPowerTuning.cs` 已于批次 A-2 删除） | 通道已裁决：走 Windows 电源 API（`powrprof.dll`），**不走 SMU**。批次十一已实装 EPP / Boost / 最大处理器状态 / C-State / ASPM 的读写与两条 IPC（`query-power-policy` / `apply-power-policy`）。后续补齐 DC（电池）值、电源方案切换 / 新建 / 改名 / 删除（`create-power-scheme` / `rename-power-scheme` / `delete-power-scheme`），协议版本升到 3。**剩余**：无 | ~~中~~ 已完成 | 已完成 |
| 4 | 场景规则编辑器 | 无 UI 工程（`Zentwik.Core/Services/ScenarioService.cs` 只有 `Save` / `Delete` 程序化入口） | 场景自动切换是核心卖点，但用户现在只能手编 `scenarios.json`；UI 侧无任何编辑器 | **高** | 批次四 |
| 5 | 切换失败回滚 | `Zentwik.Core/Services/ProfileService.cs:265-267`（注释明示未做）、`:293-295`（只记日志） | `Apply` 下发失败时不恢复上一份方案；「部分写成功」的回滚顺序（先 FMax 后 CO）缺实测依据，代码刻意不猜 | **中** | 待定（需先补齐回滚顺序实测） |
| 6 | 重扫的解绑语义 | `Zentwik.Core/Services/ProcessWatcher.cs:430-433`（注释明示不做解绑） | 方案切换后重扫只覆盖新命中进程，不再命中任何规则的进程会**保持上一次的调度**；恢复系统默认 CPU Set 需要「默认值」在各拓扑下的实测依据 | **中** | 待定 |
| 7 | 功耗墙（PPT / TDC / EDC / 温度墙） | `README.md`（公开承诺）、`Zentwik.Core/Services/ISmuReader.cs`（只有 `WriteCurveOptimizer` / `WriteFMax`）、`Helpers/TuningStore.cs`（模型只有 `CurveOptimizer[]` / `FMaxMHz`） | README 承诺可调功耗墙，实际**没有任何 SMU 写入通道**，持久化模型里也没有对应字段 | **高** | 待裁决（**先决定删 README 还是重写实现**） |
| 8 | 安装包确保桌面与开始菜单快捷方式都创建 | `build/installer/Zentwik.iss` | 后台轻量模式（自启只启 Worker、无托盘入口）下用户找不到入口；快捷方式必须在安装时确定创建，不能依赖运行时 | **中** | 批次五 |
| 9 | IPC 推送通道 | `Zentwik.Core/Ipc/WorkerPipeContract.cs`（只有请求-响应常量） | Worker 主动推 UI 的能力为零：进程集合变化 / 场景切换成功 / 场景切换失败 / 硬件操作失败 / Worker 即将退出，五类事件**当前全部不存在** | **高** | 批次三定义协议，批次四消费 |
| 10 | 缺失的查询类 IPC 命令 | 同上 | 读配置、改配置、切换方案、拉 SMU 诊断、设置进程优先级、卸载 / 清空配置 / 导出日志到受保护目录，均无对应命令常量 | **高** | 批次三 |
| 11 | 单测工程 | 无（`find` 零命中） | 仓库零测试工程，回归完全依赖 AotProbe + 手工真机 | 中 | 批次 A-4（已立项） |
| 12 | 代码签名 | 无（无 signtool / 证书 / pfx） | 分发的 exe 无签名，SmartScreen 拦截，README 要求用户自行加白 | 低 | 产品后期 |
| 13 | breeze 11 项基元组件的 WPF 重做 | `Legacy/docs/AI与开发者上手指南.md` §3.5（原 `docs/AI与开发者上手指南.md` 第 143-157 行） | 原 UI 基于 breeze 设计语言落了 11 项基元组件（`AppPageHeader` / `AppInfoBar` / `AppExpander` / `AppSegmentedControl` / `AppChoiceCard` / `AppProgressBar` / `AppSettingsCard` / `AppToggleSwitch` / `AppComboBox` 等）。**这些是 WinUI3 期的实现，不构成 WPF 新 UI 的约束。批次四按新设计需要自由决定要哪些组件，不需要对齐这份清单。此条记录事实而非待决策。** | 低 | 批次四（自由设计，无强制项） |
| 14 | 方案页的「调校配置」独立规则库 | `Zentwik.Core/Services/ProfileService.cs`（`ProfileSnapshot` 只带一份 `ScheduleRules`）、`Zentwik.Worker/PipeServerService.cs`（`query-scheduling-rules` 只投影调度规则） | 方案页的规则库目前**只有调度规则**（调度 + 优先级），`query-scheduling-rules` 是它唯一的数据来源，这一点在批次九已确认可用。调校参数（CO / FMax）仍然是「一张方案一组值」，没有可复用的规则片段。要做的话得先定片段模型与叠加规则（多片段命中同一项时谁覆盖谁），不是加一条 IPC 能解决的 | 中 | 待定（需先定片段模型） |

## 备注

- 第 3 条与第 7 条不是同一个东西：第 3 条是「原本有实现、被删掉待重写」，第 7 条是「从来没实现过、但 README 承诺了」。
- 第 9、10 条是双进程架构的硬前置：**不做推送通道，批次四的 UI 只能靠轮询**，实时性最差会退化到 30 秒级（受 `snapshot-logs` 调用频率限制）。
- 第 1、8 条同属批次五发版链收尾，与 `release.yml` / `Zentwik.iss` 的路径修复一起做。
- 第 13 条**不是待办**：它记录的是历史事实（旧 UI 曾有哪些组件），已裁定「不强制保留、批次四自由设计」，列在此处只为让批次四知道「有这份清单存在，且不必对齐」。优先级标低即此意。
- 第 14 条是批次九明确**划出本轮范围**的一项：本轮只把「调度规则库」的数据通道补全（`query-scheduling-rules` / `save-scheduling-rule` / `delete-scheduling-rule`，规则带 Id），调校侧的规则片段不在其中。下个对话做方案页 UI 时不要顺手假设它已经存在。
