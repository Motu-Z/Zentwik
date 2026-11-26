# Zentwik Next

Windows 下的 CPU 调校、电源与进程调度管理器。当前阶段以 CLI 为正式产品入口，WPF 界面暂停开发。

## 分层

```
Contracts   IPC 契约、DTO、JSON 上下文        ← 表现层唯一依赖
Domain      方案/场景/账本/持久化模型（纯领域，无 IO）
Platform    硬件与系统访问（PawnIO/SMU、电源、调度、遥测、能力探针）
Core        应用服务（方案协调、场景引擎、账本、状态存储、诊断）
Agent       命名管道服务端 + Windows 服务宿主
Cli         命令行客户端
Desktop     WPF 壳（暂停）
```

依赖方向严格单向：`Cli`/`Desktop` → `Contracts`；`Agent` → `Core` → `Domain` + `Platform`。表现层不得引用 `Platform`，不得持有设备句柄，不得承载领域规则。

## 构建与测试

```bash
dotnet build Zentwik.slnx          # 0 警告 0 错误（TreatWarningsAsErrors）
dotnet test  Zentwik.slnx          # 185 个用例
```

界面测试（`tests/Zentwik.Desktop.Tests`）在单个 STA 线程里创建真实窗口，用自动化 peer 校验控件名称、用容器实例化数量校验列表虚拟化。

## 运行

代理（控制台模式，当前用户数据目录）：

```bash
dotnet run --project src/Zentwik.Agent -- --fake        # 无硬件模拟
dotnet run --project src/Zentwik.Agent                  # 真实探测
```

代理（服务模式，需要管理员）：

```bash
Zentwik.Agent.exe --install-service      # 安装为 LocalSystem 自动启动服务（含崩溃重启恢复动作）
Zentwik.Agent.exe --start-service
Zentwik.Agent.exe --stop-service
Zentwik.Agent.exe --uninstall-service
```

服务启动参数为 `--service --name --data-dir`；安装时把当前用户 SID 写入 `owner.sid`，服务内的管道只接受该用户的中完整性交互会话。客户端连接后校验对端进程属于 SYSTEM 或当前用户，因此服务以 SYSTEM 运行时普通用户也能连上。`--service` 在交互会话中直接启动时退回控制台模式，便于调试。代理自身退出码：`0` 成功、`3` 失败。

CLI：

```bash
dotnet run --project src/Zentwik.Cli -- status
dotnet run --project src/Zentwik.Cli -- tuning --json
```

数据目录：控制台模式 `%APPDATA%\ZentwikNext`，服务或安装模式 `%ProgramData%\ZentwikNext`。可用 `--data-dir` 覆盖。含 `state.json`、`owner.sid`、`logs\`。

## CLI

全局：`--json` 机器可读输出、`--pipe NAME` 管道名、`-h/--help`。

| 命令 | 作用 |
| --- | --- |
| `status` | 代理状态、协议版本、能力概览 |
| `caps` | 能力清单与状态 |
| `modules` | 内置模块与聚合状态 |
| `monitor [--count N] [--interval MS]` | CPU/内存持续采样 |
| `tuning` | AMD：通道状态与逐核 CO 偏移；`--intel`：电压偏移与睿频上限 |
| `plans list\|show\|apply\|revert\|delete\|save\|create` | 方案管理（三类方案独立） |
| `scenes list\|show\|activate\|deactivate\|enable\|disable\|delete\|save\|create` | 场景管理与手动执行 |
| `ledger [release <target>]` | 状态账本、释放用户接管 |
| `power list\|create\|rename\|delete` | Windows 电源方案：列出/派生/改名/删除（内置拒删、活动方案先切走）。列表含不可见的系统模板，带 `hidden` 标记——它们同样可改名、可删除、可作模板 |
| `settings` | 读取或修改语言、主题、强调色、缩放 |
| `doctor` | 运行模式、路径、模块状态、最近错误 |
| `logs [--limit N] [--level error]` | 最近日志 |

退出码：`0` 成功、`1` 代理不可用、`2` 用法错误、`3` 操作失败、`4` 能力不可用、`5` 超时、`6` 部分成功。

方案应用语义：写前记录前值 → 写入 → 回读 → 期望与实际不一致即判定失败；结果落在状态账本，`Owner` 区分 Scene/Plan/User。硬件不可用时返回失败原因，不伪造成功。

调校通道可用性：`available` / `notSupported` / `driverUnavailable` / `moduleMissing` / `privilegeMissing`（PawnIO 拒绝访问，需要管理员运行代理）/ `probeFailed` / `rejected`（通道可用但平台拒绝该操作，例如固件锁定 CO）。

核地址编码固定为 `[31:28]` CCD、`[27:24]` CCX、`[23:20]` 核内序号（数值字段，不是独热位）；写入参数低 16 位放 margin 的补码。核的物理位置优先取自 APOB 固件映射（`ZENTWIK_APOB_TABLE` 指向原始表），否则由 CPUID `0x8000001E` 的硬件核号与系统 L3 缓存域推导，并与物理核数、线程数交叉校验；两种来源都不可用时状态报 `rejected` 并给出原因，不按 `序号 / 8` 推算。

场景恢复：场景退出且 `RestoreOnExit` 为真时回退自己的写入；代理停止时同样回退；上次异常退出遗留的写入在下次启动（同一启动周期内）补回退，整机重启后不擅自改写。

## 当前未实现

- 硬件功耗限制（AMD STAPM/PPT FAST/SLOW/TDC/EDC、Intel PL1/PL2/Tau）尚未实现；HX 平台默认不展示常规入口，仅允许用户主动选择受控尝试。
- Intel 通道已实现 OC Mailbox 写协议（命令字、bit63 忙位轮询、全局互斥、写后回读），但无 Intel 真机验证，仅位域单测。
- GPU 遥测、托盘、WPF 界面与安装器未实现（界面阶段暂停）。
- 服务异常终止后的 SCM 恢复动作尚未实测。
- 真实休眠/唤醒回调、重启后保持、多处理器组绑定未验证。

已实现并真机验证（不再列为未实现）：AMD 逐核 CO 读写与回退、FMax 写入闭环、CPU Sets/亲和性/优先级三路径、调度持续 enforce、场景接管与回退、Session 0 服务连接。
