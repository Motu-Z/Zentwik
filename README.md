# Zentwik

轻量、离线的 Windows 本地调优工具：逐核调 AMD Ryzen 的 Curve Optimizer 电压与 FMax 上限，用 CPU Sets 或 Affinity Mask 把进程绑到指定核心，切换电源方案并调整其中的策略。

进程启停、接通或拔下电源时，可以按存好的方案自动切换。

![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
[![license](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![release](https://img.shields.io/github/v/release/Motu-Z/Zentwik)](https://github.com/Motu-Z/Zentwik/releases)

## 功能

- **调校**：给每个核心单独设 Curve Optimizer 电压偏移，并限制 FMax 最大频率。
- **调度**：用 CPU Sets 或 Affinity Mask 把进程绑到指定核心，掩码表可自建、改名、编辑。
- **电源**：切换电源方案，调整提升模式、最小处理器状态、链路电源管理等策略，交流与电池分开保存。
- **场景**：监听进程启停与电源通断，套用事先存好的调校、调度或电源方案。

## 运行要求

- Windows 10 1709 及以上，x64
- .NET 10 Desktop Runtime（`Setup.exe` 与 `Standalone.zip` 已自带）
- 调校只支持 AMD Ryzen，写 SMU 需要 [PawnIO](https://github.com/namazso/PawnIO) 驱动；没有驱动或平台不符时，其余功能照常可用
- 首次启动会为后台服务申请一次管理员权限

## 安装

从 [Releases](https://github.com/Motu-Z/Zentwik/releases) 选一个：

| 文件 | 说明 |
| --- | --- |
| `Setup.exe` | 完整安装包，自带运行时，约 63 MB |
| `Setup-Lite.exe` | 精简安装包，不含运行时，约 6 MB |
| `Standalone.zip` | 免安装，自带运行时 |
| `Portable.zip` | 免安装，需自备 .NET 10 Desktop Runtime |

两个安装包可以互相覆盖安装，覆盖即视为升级。配置位于 `%APPDATA%\Zentwik`，卸载时默认保留。

## 构建

需要 .NET 10 SDK 与桌面开发工作负载。

```bash
dotnet build Zentwik.slnx -c Release
dotnet build Zentwik.UI/Zentwik.UI.csproj -c Release
dotnet test  Zentwik.Tests/Zentwik.Tests.csproj -c Release
```

`Zentwik.UI` 是 WPF 界面，`Zentwik.Worker` 是以 Native AOT 发布的提权服务，SMU 访问层位于 `Zentwik.Core/Smu`。

## 参考

- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool/)：Ryzen SMU 参数读写的参考实现
- [CPUSetSetter](https://github.com/SimonvBez/CPUSetSetter)：CPU Sets 调度与双 CCD 拓扑识别的行为参照
- [PawnIO](https://github.com/namazso/PawnIO)：SMU 访问的内核驱动
- [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)：`RyzenSMU` 驱动模块（LGPL-2.1）

## 许可证

[Apache-2.0](LICENSE)。`libs/PawnIO/RyzenSMU.bin` 来自 [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)（LGPL-2.1），作为独立文件随包分发，用户可自行替换。
