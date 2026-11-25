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
dotnet test  Zentwik.slnx          # 49 个用例
```

## 运行

代理（控制台模式，当前用户数据目录）：

```bash
dotnet run --project src/Zentwik.Agent -- --fake        # 无硬件模拟
dotnet run --project src/Zentwik.Agent                  # 真实探测
```

代理（服务模式，需要管理员）：

```bash
Zentwik.Agent.exe --install-service      # 安装为 LocalSystem 自动启动服务
Zentwik.Agent.exe --uninstall-service
```

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
| `tuning` | CPU 调校通道状态与逐核电压偏移 |
| `plans list\|show\|apply\|revert\|delete\|save\|create` | 方案管理（三类方案独立） |
| `scenes list\|show\|activate\|deactivate\|enable\|disable\|delete\|save\|create` | 场景管理与手动执行 |
| `ledger [release <target>]` | 状态账本、释放用户接管 |
| `settings` | 读取或修改语言、主题、强调色、缩放 |
| `doctor` | 运行模式、路径、模块状态、最近错误 |
| `logs [--limit N] [--level error]` | 最近日志 |

退出码：`0` 成功、`1` 代理不可用、`2` 用法错误、`3` 操作失败、`4` 能力不可用、`5` 超时、`6` 部分成功。

方案应用语义：写前记录前值 → 写入 → 回读 → 期望与实际不一致即判定失败；结果落在状态账本，`Owner` 区分 Scene/Plan/User。硬件不可用时返回失败原因，不伪造成功。

## 当前未实现

- Intel MSR 调校通道、FMax 写入、CPU Sets 写入、GPU 遥测、进程调度持续 enforce。
- 服务安装/卸载与 Session 0 → 用户会话通信未上机实测。
- AMD CO 真实硬件写入闭环未在物理机验证（现有 fake 与只读探测）。
