# 第三方组件与许可告知

本文件记录 Zentwik 随包分发或运行时依赖的第三方组件，以及相应的许可义务。
决策依据见研究仓库 `decisions/ADR-008-hardware-driver-and-apob.md`。

## 1. PawnIO.Modules 签名模块（随包分发）

| 项 | 内容 |
|---|---|
| 文件 | `modules/PawnIO/RyzenSMU.bin`、`modules/PawnIO/IntelMSR.bin`、`modules/PawnIO/AMDFamily17.bin`（安装后位于 Agent 目录 `PawnIO/`） |
| 来源 | <https://github.com/namazso/PawnIO.Modules>（AMDFamily17.bin 取自 LibreHardwareMonitor 0.2.11 打包的同名上游模块） |
| 许可 | GNU Lesser General Public License v2.1 or later（LGPL-2.1-or-later） |
| 许可证全文 | [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt) |
| 修改状态 | 未修改；保持上游签名原样，不重打包、不重签名 |

当前随包的模块文件标识（SHA-256，用于确认所分发的是上游原始文件）：

```
d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f  IntelMSR.bin
301d9ca397108e09f31bfbd5ac4c9bb4f352a5de68532c32db3ba7ddcde93450  RyzenSMU.bin
dae74615761b78bdf064dfb3e136252ddcc6fc727d88f14738d0e5800d427a91  AMDFamily17.bin
```

`AMDFamily17.bin` 提供 `ioctl_read_smn` 与 `ioctl_read_msr`，用于 CPU 封装温度与封装功耗的只读采样；它不提供任何写入能力。

LGPL-2.1 义务说明：分发这些二进制时须提供许可证全文与对应源码的获取途径（即上述上游仓库）。模块以独立文件形式随包，未与 Zentwik 自身代码静态链接。

## 2. PawnIO 驱动（**不**随包分发）

| 项 | 内容 |
|---|---|
| 来源 | <https://github.com/namazso/PawnIO>（下载见 <https://pawnio.eu>） |
| 许可 | GNU General Public License v2.0，附模块例外条款 |
| 分发状态 | **不随包分发**。由用户自行安装（如 `winget install namazso.PawnIO`） |

Zentwik 与驱动之间仅通过设备 IOCTL 接口通信，属于上游例外条款覆盖的"独立模块"关系，因此不构成 GPL 组合作品。若将来改为随包分发驱动本体，必须同时履行 GPL-2.0（提供源代码或书面要约），或先行取得上游替代授权。

## 3. 维护要求

- 升级模块时同步更新本文件的 SHA-256 与上游发布标识。
- 新增任何第三方二进制前，先在此登记来源、许可、是否修改。
