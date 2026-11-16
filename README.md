# Zentwik

轻量、离线的 Windows 本地调优工具：AMD Ryzen 处理器 Curve Optimizer 与 FMax 设定、调度进程至指定核心，修改电源计划与相关策略；监听进程启停与电源通断，自动切换至指定方案。

![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
[![license](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![release](https://img.shields.io/github/v/release/Motu-Z/Zentwik)](https://github.com/Motu-Z/Zentwik/releases)

## 主要功能

- **调校**：逐核 Curve Optimizer 电压偏移与 FMax 最大频率限制。
- **调度**：通过 CPU Sets 或 Affinity Mask 将进程调度至指定核心。
- **电源**：切换电源方案，调整电源策略。
- **场景**：监听进程启停与电源通断，自动套用指定方案。

## 运行要求

- Windows 10 1709 及以上，x64
- .NET 10 Desktop Runtime（`Setup.exe` 与 `Standalone.zip` 已自带）
- 调校只支持 AMD Ryzen，写入 SMU 需要 [PawnIO](https://github.com/namazso/PawnIO) 驱动；没有驱动或平台不符时，其余功能仍然可用
- 首次启动会为后台服务申请一次管理员权限

## 安装

从 [Releases](https://github.com/Motu-Z/Zentwik/releases) 选一个：

| 文件 | 说明 |
| --- | --- |
| `Setup.exe` | 完整安装包，自带运行时 |
| `Setup-Lite.exe` | 精简安装包，不含运行时 |
| `Standalone.zip` | 便携免安装，自带运行时 |
| `Portable.zip` | 便携免安装，不含运行时 |

安装包可以覆盖安装，行为视为升级。配置文件位于 `%APPDATA%\Zentwik`，卸载时默认保留。

## 构建

需要 .NET 10 SDK 与桌面开发工作负载。

```bash
dotnet build Zentwik.slnx -c Release
dotnet build Zentwik.UI/Zentwik.UI.csproj -c Release
dotnet test  Zentwik.Tests/Zentwik.Tests.csproj -c Release
```

## 参考

- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool/)：Ryzen SMU 参数读写的参考实现
- [CPUSetSetter](https://github.com/SimonvBez/CPUSetSetter)：CPU Sets 与双 CCD 拓扑识别的行为参照
- [PawnIO](https://github.com/namazso/PawnIO)：SMU 访问的内核驱动
- [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)：`RyzenSMU` 驱动模块（LGPL-2.1）

## 许可

[Apache-2.0](LICENSE)。`libs/PawnIO/RyzenSMU.bin` 来自 [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)（LGPL-2.1），作为独立文件随包分发，用户可自行替换。
