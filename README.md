# Zentwik

轻量、离线的 Windows 本地调优工具：AMD Ryzen 处理器的 Curve Optimizer / FMax、CPU Sets 与关联掩码进程绑定、电源方案，可根据进程与电源状态自动应用对应方案。

![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
[![license](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![release](https://img.shields.io/github/v/release/Motu-Z/Zentwik)](https://github.com/Motu-Z/Zentwik/releases)

## 功能

- **调校**：AMD Ryzen 处理器 Curve Optimizer 电压偏移与 FMax 最大频率上限。
- **调度**：通过 CPU Sets 或关联掩码（affinity mask）将进程绑定到指定的逻辑处理器集合，掩码表可自建与编辑。
- **电源**：切换电源方案，调整处理器 Boost、PCIe ASPM 等策略，并区分交流供电与电池供电场景。
- **场景**：监视进程启动与退出、电源接通与拔下，自动应用上述三类方案。

## 运行要求

- Windows 10 1709 或更高版本，x64 架构
- .NET 10 Desktop Runtime（`Setup.exe` 与 `Standalone.zip` 已内置运行时）
- 调校功能仅适用于 AMD Ryzen 平台。SMU 写入依赖 [PawnIO](https://github.com/namazso/PawnIO) 驱动；若未安装该驱动或当前平台不支持调校，则仅调校不可用，其他功能不受影响。
- 首次启动时，后台服务将请求一次管理员权限

## 安装

从 [Releases](https://github.com/Motu-Z/Zentwik/releases) 页面选择以下任一安装包：

| 文件 | 说明 |
| --- | --- |
| `Setup.exe` | 完整安装包，自带运行时，约 63 MB |
| `Setup-Lite.exe` | 精简安装包，不含运行时，约 6 MB |
| `Standalone.zip` | 免安装，自带运行时 |
| `Portable.zip` | 免安装，需自备 .NET 10 Desktop Runtime |

两个安装包使用同一 AppId，覆盖安装视为升级。配置位于 `%APPDATA%\Zentwik`，卸载时默认保留。

## 构建

需要 .NET 10 SDK 及“.NET 桌面开发”工作负载。

```bash
dotnet build Zentwik.slnx -c Release
dotnet build Zentwik.UI/Zentwik.UI.csproj -c Release
dotnet test  Zentwik.Tests/Zentwik.Tests.csproj -c Release
```

`Zentwik.UI` 为 WPF 界面，`Zentwik.Worker` 为以 Native AOT 发布的提权服务，SMU 访问层在 `Zentwik.Core/Smu`。

## 参考

- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool/)：Ryzen SMU 参数读写的参考实现
- [CPUSetSetter](https://github.com/SimonvBez/CPUSetSetter)：CPU Sets 调度与双 CCD 拓扑识别的行为参考
- [PawnIO](https://github.com/namazso/PawnIO)：SMU 访问的内核驱动
- [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)：`RyzenSMU` 驱动模块（LGPL-2.1）

## 许可证

[Apache-2.0](LICENSE)。`libs/PawnIO/RyzenSMU.bin` 来自 [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)（LGPL-2.1），作为独立文件随包分发，用户可自行替换。
