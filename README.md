# Zentwik

AMD 平台本地调校工具（曾用名 Zenwik / ZenForge）。可调各核 Curve Optimizer 偏移与 FMax 上限、切换系统电源方案、按 CPU Sets 把进程绑定到指定核心，并按进程启停与电源状态自动切换调校 / 调度 / 电源三类方案。

## 运行要求

- Windows 10 1709 及以上，x64
- .NET 10 Desktop Runtime（界面需要；后台服务是 Native AOT 原生 exe，不再依赖运行时）
- 写 SMU（CO / FMax）需要 PawnIO 驱动；未安装时进程调度、电源方案、场景切换、日志仍可用
- 首次启动会为后台服务请求一次管理员权限

## 架构

| 进程 | 权限 | 职责 |
| --- | --- | --- |
| `Zentwik.UI` | 普通 | WPF 界面、托盘、方案编辑 |
| `Zentwik.Worker` | 管理员 | SMU、CPU Sets、电源策略、场景仲裁、进程监听、日志落盘 |

两者通过命名管道 `Zentwik.Worker` 通信（协议版本 10，一连接一条消息）。界面不直接碰硬件与系统策略，业务调用统一经 `Services/WorkerBridge` 下发。Worker 可由界面拉起，也可由计划任务在登录时单独启动；「完全退出」会一并收掉后台服务，「仅留服务」则保留。

## 构建与发布

```bash
dotnet build Zentwik.slnx -c Release                    # Core / Worker / Tests
dotnet build Zentwik.UI/Zentwik.UI.csproj -c Release   # 界面（不在 slnx 里）
```

两个可执行工程的 `OutputPath` 都指向 `D:\Zentwik-App\`（Release 平铺为安装目录，Debug 落子目录），必须保持同目录，否则 Worker 会拉到别处的旧界面。

推送 `v*` tag 会触发 `.github/workflows/release.yml`：UI 走框架依赖 + 单文件 + R2R（WPF 不支持 AOT），Worker 走 **Native AOT**（原生 exe，不依赖 .NET 运行时），两端装进同一目录后打成一个 zip 附到 Release。打包前会校验 AOT 产物形态（不存在托管 dll / runtimeconfig.json）。`aot-probe.yml` 独立跑两条 AOT 门禁：Core 探针（编译 + 运行）与 Worker（编译 + 链接，`-warnaserror`）。

Worker 能 AOT 的前提是 WMI 走 `WmiLight` 而不是 `System.Management`（后者依赖运行时代码生成）。改进程监听时不要把它换回去。

## 回归

```bash
dotnet test Zentwik.Tests/Zentwik.Tests.csproj -c Release
Zentwik.UI.exe --selftest --itest   # 报告写 %TEMP%\zentwik-selftest.txt
Zentwik.UI.exe --perf               # 10 秒静置 CPU / 内存
```

`--screenshot` / `--selftest` / `--itest` / `--perf` 不拉起 Worker、不写用户配置；`--xxxprobe` 与 `--powerclean` 会真实拉起提权 Worker 做往返。全部诊断参数见 `Zentwik.UI/App.xaml.cs`。

## 仓库结构

| 路径 | 内容 |
| --- | --- |
| `Zentwik.Core/` | 业务逻辑、IPC 契约、日志与存储 |
| `Zentwik.Worker/` | 提权后台服务与管道服务端 |
| `Zentwik.UI/` | WPF 界面 |
| `Zentwik.Tests/` | 单元测试 |
| `build/` | AOT 探针、UI 冒烟探针、安装包脚本 |
| `libs/` | ZenStates-Core 原生依赖 |
| `tools/` | XAML 静态检查脚本 |
| `.github/workflows/` | 发布与 AOT 门禁 |
| `UI_Contract.md` | 前后端接口契约存档 |
| `CHANGELOG.md` | 更新日志 |

见 [CHANGELOG.md](CHANGELOG.md)。
