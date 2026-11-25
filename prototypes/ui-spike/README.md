# UI 框架对比原型

用途：在选定 UI 框架前测同一图表负载的绘制成本、CPU 与内存。结论见 `research/04-tech-stack-evaluation.md` §4。

## 环境

- Windows 11 26300，Ryzen 9 9955HX（32 线程），31.2 GB，RTX 5070 Ti Laptop + Radeon 610M
- .NET SDK 10.0.403，Release、JIT（未开 AOT / ReadyToRun）
- Avalonia 12.2.0，默认 FluentTheme

## 负载

8 条序列 × 600 点，每次刷新整块重绘（模拟滚动窗口，无增量优化），窗口 1200×800，运行 10 秒。

## 运行

```bash
prototypes/ui-spike/WpfSpike/bin/Release/net10.0-windows/WpfSpike.exe --hz 10 --seconds 10
prototypes/ui-spike/AvaloniaSpike/bin/Release/net10.0/AvaloniaSpike.exe --hz 10 --seconds 10
```

结果写入各自输出目录下的 `result-<framework>-<hz>.txt`。

## 结果（2026-11-25，单次采样）

| 场景 | 渲染 p50 | p95 | max | 实际帧率 | 帧间隔 p50 | CPU（%单核） | RSS |
|---|---|---|---|---|---|---|---|
| WPF 10 Hz | 0.254 ms | 0.798 ms | 18.2 ms | 9.1 Hz | 108.9 ms | 16.6% | 120 MB |
| Avalonia 10 Hz | 0.303 ms | 0.687 ms | 11.7 ms | 9.1 Hz | 109.1 ms | 29.4% | 237 MB |
| WPF 60 Hz | 0.195 ms | 0.446 ms | 12.2 ms | 39.8 Hz | 30.5 ms | 44.1% | 172 MB |
| Avalonia 60 Hz | 0.235 ms | 0.582 ms | 9.6 ms | 44.1 Hz | 18.1 ms | 82.7% | 287 MB |

## 限制

- 单次运行、单机、无双盲；CPU 含进程全部线程，不只是渲染。
- 启动到首帧的指标脚本有缺陷（恒为 0），本次未采信。
- `DispatcherTimer` 受系统定时器粒度限制，60 Hz 达不到；生产若要 60 Hz 必须用 vsync 回调。
- 未测表格滚动、文本渲染、UIA 可访问性、IME、高 DPI。
- Avalonia 内存与 CPU 偏高原因未查明（可能与默认主题、合成器路径、JIT 有关），不能据此断言框架更差。
