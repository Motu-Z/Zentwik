# Zentwik

面向 AMD Ryzen 平台的本地调校工具：逐核 Curve Optimizer、FMax 上限、CPU Sets 进程绑定、电源方案切换，按进程与电源状态自动套用方案。

![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
[![license](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![release](https://img.shields.io/github/v/release/Motu-Z/Zentwik)](https://github.com/Motu-Z/Zentwik/releases)

## 主要功能

- **调校**：对支持 Curve Optimizer / FMax 的 AMD 处理器逐核设置电压偏移与最大频率限制。
- **调度**：通过 CPU Sets 将进程优先调度到指定核心，支持自建、重命名和编辑掩码表。
- **电源**：切换系统电源方案，调整 Boost、ASPM 等处理器策略，并可按交流 / 电池状态区分。
- **场景**：监听进程启停与电源状态，自动切换调校、调度、电源三类方案。

## 运行要求

- Windows 10 1709 及以上，x64
- .NET 10 Desktop Runtime（`Setup.exe` 与 `Standalone.zip` 已自带）
- 写入 SMU（CO / FMax）需要 [PawnIO](https://github.com/namazso/PawnIO) 驱动；未安装时调度、电源、场景、日志仍可用
- 首次启动会为后台服务请求一次管理员权限

## 开始使用

从 [Releases](https://github.com/Motu-Z/Zentwik/releases) 下载发布文件，四选一：

| 文件 | 说明 |
| --- | --- |
| `Setup.exe` | 完整安装包，自带 .NET 10 运行时，约 68 MB，推荐 |
| `Setup-Lite.exe` | 精简安装包，不含运行时，约 7 MB，需已安装 .NET 10 Desktop Runtime |
| `Standalone.zip` | 免安装，自带运行时，解压即用 |
| `Portable.zip` | 免安装，体积小，需自备 .NET 10 Desktop Runtime |

两个安装包共用同一 AppId，覆盖安装视为升级；卸载时可选择是否删除应用数据。

## 应用数据

用户配置位于 `%APPDATA%\Zentwik\`：`tuning.json`、`schedules.json`、`masks.json`、`ui.json`；运行日志在 `Logs\` 子目录。卸载默认保留，重装可沿用。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `Zentwik.UI/` | WPF 界面、托盘与方案编辑 |
| `Zentwik.Worker/` | Native AOT 提权后台服务与命名管道服务端 |
| `Zentwik.Core/` | 业务逻辑、IPC 契约、日志与存储 |
| `Zentwik.Tests/` | 单元测试 |
| `installer/` | Inno Setup 安装脚本与中文语言文件 |
| `build/`、`tools/` | 构建辅助（含 AOT 探针）与 XAML 静态检查 |
| `libs/` | ZenStates-Core 二进制依赖 |
| `.github/workflows/` | 发布与 AOT 门禁 |

界面不直接操作硬件与系统策略，均经 `Zentwik.UI/Services/WorkerBridge` 通过命名管道下发给 Worker。

## 自行构建

需要 .NET 10 SDK；界面额外需要 Windows 桌面开发工作负载。

```bash
dotnet build Zentwik.slnx -c Release                    # Core / Worker / Tests
dotnet build Zentwik.UI/Zentwik.UI.csproj -c Release    # 界面（不在 slnx 里）
dotnet test  Zentwik.Tests/Zentwik.Tests.csproj -c Release
```

两个可执行工程的 `OutputPath` 必须保持一致，否则 Worker 可能加载到旧界面。

发布由 `v*` tag 触发：UI 为框架依赖 + 单文件 + R2R（WPF 不支持 AOT），Worker 为 Native AOT 原生 exe，随后生成四个产物。Worker 的 AOT 依赖 WMI 走 `WmiLight`，改动进程监听时不要换回 `System.Management`。

## 参考与依赖

- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool/)：Ryzen SMU / MSR / PCI 参数读写的参考实现
- [CPUSetSetter](https://github.com/SimonvBez/CPUSetSetter)：CPU Sets 进程调度与双 CCD 拓扑识别的行为参照
- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core)：SMU 消息收发与 PawnIO 模块加载
- [PawnIO](https://github.com/namazso/PawnIO)：SMU 访问的内核驱动
- [WmiLight](https://github.com/MartinKuschnik/WmiLight)：Native AOT 下可用的 WMI 封装

## 许可证

[MIT](LICENSE)
