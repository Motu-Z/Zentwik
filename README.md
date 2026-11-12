# Zentwik

轻量、离线的 Windows 本地调优工具：AMD 处理器 Curve Optimizer / FMax、CPU Sets 进程调度、电源方案，可按进程与电源状态自动套用。

![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
[![license](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![release](https://img.shields.io/github/v/release/Motu-Z/Zentwik)](https://github.com/Motu-Z/Zentwik/releases)

## 功能

- **调校**：逐核 Curve Optimizer 电压偏移与 FMax 最大频率限制。
- **调度**：按 CPU Sets 把进程绑到指定核心，支持自建、重命名、编辑掩码表。
- **电源**：切换电源方案，调整 Boost、ASPM 等策略，区分交流与电池。
- **场景**：监听进程启停与电源状态，自动切换上述三类方案。

## 运行要求

- Windows 10 1709 及以上，x64
- .NET 10 Desktop Runtime（`Setup.exe` 与 `Standalone.zip` 已自带）
- 写入 SMU（CO / FMax）需 [PawnIO](https://github.com/namazso/PawnIO) 驱动；未安装时调度、电源、场景、日志仍可用
- 首次启动会为后台服务请求一次管理员权限

## 安装

从 [Releases](https://github.com/Motu-Z/Zentwik/releases) 选择一项：

| 文件 | 说明 |
| --- | --- |
| `Setup.exe` | 完整安装包，自带运行时，约 63 MB |
| `Setup-Lite.exe` | 精简安装包，不含运行时，约 6 MB |
| `Standalone.zip` | 免安装，自带运行时 |
| `Portable.zip` | 免安装，需自备 .NET 10 Desktop Runtime |

两个安装包共用同一 AppId，覆盖安装视为升级。配置位于 `%APPDATA%\Zentwik`，卸载默认保留。

## 构建

需要 .NET 10 SDK 与桌面开发工作负载。

```bash
dotnet build Zentwik.slnx -c Release
dotnet build Zentwik.UI/Zentwik.UI.csproj -c Release
dotnet test  Zentwik.Tests/Zentwik.Tests.csproj -c Release
```

`Zentwik.UI` 为 WPF 界面，`Zentwik.Worker` 为 Native AOT 提权服务，SMU 访问层在 `Zentwik.Core/Smu`。

## 参考

- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool/)：Ryzen SMU 参数读写的参考实现
- [CPUSetSetter](https://github.com/SimonvBez/CPUSetSetter)：CPU Sets 调度与双 CCD 拓扑识别的行为参照
- [PawnIO](https://github.com/namazso/PawnIO)：SMU 访问的内核驱动
- [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)：`RyzenSMU` 驱动模块（LGPL-2.1）

## 许可证

[Apache-2.0](LICENSE)。`libs/PawnIO/RyzenSMU.bin` 来自 [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)（LGPL-2.1），以独立文件随包分发，可由用户替换。
