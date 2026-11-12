# Zentwik

轻量、离线的 Windows 本地调优工具：支持 AMD 处理器进行 Curve Optimizer / FMax，提供 CPU Sets 进程调度、电源方案修改，并按进程与电源状态自动套用方案。

![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
[![license](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
[![release](https://img.shields.io/github/v/release/Motu-Z/Zentwik)](https://github.com/Motu-Z/Zentwik/releases)

## 主要功能

- **调校**：对支持 Curve Optimizer / FMax 的 AMD 处理器进行逐核电压偏移与最大频率限制。
- **调度**：通过 CPU Sets 将进程优先调度到指定核心，支持自建、重命名和编辑掩码表。
- **电源**：切换系统电源方案，调整 Boost、ASPM 等处理器策略，并可按交流 / 电池状态区分。
- **场景**：监听进程启停与电源状态，自动切换调校、调度、电源三类方案。

## 运行要求

- Windows 10 1709 及以上，x64
- .NET 10 Desktop Runtime（`Setup.exe` 与 `Standalone.zip` 已自带）
- 写入 SMU（CO / FMax）需要 [PawnIO](https://github.com/namazso/PawnIO) 驱动；未安装时调度、电源、场景、日志仍可用
- 首次启动会为后台服务请求一次管理员权限

## 安装

从 [Releases](https://github.com/Motu-Z/Zentwik/releases) 选择一项下载：

| 文件 | 说明 |
| --- | --- |
| `Setup.exe` | 完整安装包，自带运行时，约 68 MB，推荐 |
| `Setup-Lite.exe` | 精简安装包，不含运行时，约 7 MB，需自备 .NET 10 Desktop Runtime |
| `Standalone.zip` | 免安装，自带运行时，解压即用 |
| `Portable.zip` | 免安装，体积小，需自备 .NET 10 Desktop Runtime |

两个安装包共用同一 AppId，覆盖安装视为升级。

## 应用数据

用户配置位于 `%APPDATA%\Zentwik` 目录：`tuning.json`、`schedules.json`、`masks.json`、`ui.json`；运行日志在其中的 `Logs` 子目录。卸载默认保留，重装可沿用。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `Zentwik.UI/` | WPF 界面、托盘与方案编辑 |
| `Zentwik.Worker/` | Native AOT 提权后台服务 |
| `Zentwik.Core/` | 业务逻辑、日志与存储 |
| `Zentwik.Tests/` | 单元测试 |
| `installer/` | Inno Setup 安装脚本与中文语言文件 |
| `build/`、`tools/` | 构建辅助（含 AOT 探针）与 XAML 静态检查 |
| `libs/` | ZenStates-Core 二进制依赖 |
| `.github/workflows/` | 发布与 AOT 门禁 |

## 自行构建

需要 .NET 10 SDK 与 .NET 桌面开发工作负载。

```bash
dotnet build Zentwik.slnx -c Release                    # Core / Worker / Tests
dotnet build Zentwik.UI/Zentwik.UI.csproj -c Release    # UI
dotnet test  Zentwik.Tests/Zentwik.Tests.csproj -c Release
```

## 参考与依赖

- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool/)：Ryzen SMU 参数读写的参考实现
- [CPUSetSetter](https://github.com/SimonvBez/CPUSetSetter)：CPU Sets 进程调度与双 CCD 拓扑识别的行为参照
- [ZenStates-Core-AOT](https://github.com/Motu-Z/ZenStates-Core-AOT)：SMU 消息收发与 PawnIO 模块加载
- [PawnIO](https://github.com/namazso/PawnIO)：SMU 访问的内核驱动
- [WmiLight](https://github.com/MartinKuschnik/WmiLight)：Native AOT 下可用的 WMI 封装

## 许可证

[GPL-3.0](LICENSE)。本项目随包分发 [ZenStates-Core-AOT](https://github.com/Motu-Z/ZenStates-Core-AOT)（对 [irusanov/ZenStates-Core](https://github.com/irusanov/ZenStates-Core) 的 Native AOT 适配构建），因此整体以 GPL-3.0 发布。Zentwik 的完整源码即本仓库；ZenStates-Core-AOT 及其上游的对应源码见其对应仓库。
