using System;
using System.Collections.Generic;
using Zentwik.Core.Smu;

namespace Zentwik.Core.Services
{
    /// <summary>Intel 电压平面：核心、核显、缓存。位值对应 OC Mailbox 的 bit8..bit10。</summary>
    public enum IntelVoltagePlane
    {
        Core = 0,
        Gpu = 1,
        Cache = 2
    }

    /// <summary>
    /// Intel 调校编解码：电压偏移走 MSR 0x150（OC Mailbox），睿频上限走 MSR 0x1AD。
    /// </summary>
    public static class IntelTuningCodec
    {
        public const uint MsrOcMailbox = 0x150;
        public const uint MsrTurboRatioLimit = 0x1AD;
        public const uint MsrPkgPowerLimit = 0x610;
        public const uint MsrPkgPowerInfo = 0x614;

        /// <summary>PL1/PL2 各占 15 位，单位由 MSR 0x614 的 power unit 决定。</summary>
        public const int PowerLimitMask = 0x7FFF;
        public const int PowerLimit1Shift = 0;
        public const int PowerLimit2Shift = 32;
        public const int PowerEnable1Bit = 15;
        public const int PowerEnable2Bit = 47;

        /// <summary>时间窗 7 位：高 2 位尾数、低 5 位指数，秒数 = 2^Z × (1 + Y/4)；0 表示不限。</summary>
        public const int TimeWindow1Shift = 17;
        public const int TimeWindow2Shift = 49;
        public const int TimeWindowMask = 0x7F;

        /// <summary>bit21..31 为有符号 11 位偏移量，负值为降压。</summary>
        public const int VoltageMask = 0x7FF;
        public const int VoltageSignBit = 0x400;
        public const int VoltageShift = 21;

        /// <summary>毫伏换算系数：寄存器步进为 1/1024 V。</summary>
        public const double MilliVoltScale = 1.024;

        /// <summary>睿频档位数，对应 1 到 8 个活跃核心。</summary>
        public const int RatioSlots = 8;

        public static uint PlaneBits(IntelVoltagePlane plane) => plane switch
        {
            IntelVoltagePlane.Gpu => 1u << 8,
            IntelVoltagePlane.Cache => 1u << 9,
            _ => 0u
        };

        /// <summary>写命令：高位 0x80000011，低 32 位为电压数据。</summary>
        public static ulong EncodeVoltageWrite(IntelVoltagePlane plane, int milliVolts)
        {
            int scaled = (int)Math.Round(milliVolts * MilliVoltScale, MidpointRounding.AwayFromZero);
            uint data = (uint)((scaled & VoltageMask) << VoltageShift);
            return ((ulong)(0x80000011u | PlaneBits(plane)) << 32) | data;
        }

        /// <summary>读命令：高位 0x80000010，低 32 位留空。</summary>
        public static ulong EncodeVoltageRead(IntelVoltagePlane plane)
            => (ulong)(0x80000010u | PlaneBits(plane)) << 32;

        /// <summary>从邮箱读数还原偏移。负值为降压。</summary>
        public static int DecodeVoltage(ulong mailbox)
        {
            int raw = (int)((mailbox >> VoltageShift) & VoltageMask);
            if ((raw & VoltageSignBit) != 0)
                raw -= VoltageMask + 1;

            return (int)Math.Round(raw / MilliVoltScale, MidpointRounding.AwayFromZero);
        }

        /// <summary>八档倍频，下标 0 为「1 个活跃核心」，落在 bits 7:0（SDM 定义 TURBO_RATIO_LIMIT）。</summary>
        public static int[] DecodeRatios(ulong value)
        {
            int[] ratios = new int[RatioSlots];
            for (int i = 0; i < RatioSlots; i++)
                ratios[i] = (int)((value >> (i * 8)) & 0xFF);
            return ratios;
        }

        public static ulong EncodeRatios(IReadOnlyList<int> ratios)
        {
            ulong value = 0;
            for (int i = 0; i < RatioSlots && i < ratios.Count; i++)
                value |= (ulong)(uint)Math.Clamp(ratios[i], 0, 255) << (i * 8);
            return value;
        }

        /// <summary>每格瓦数 = 2^-unit，unit 取自 MSR 0x614 bits 3:0；读不到按 1/8 W。</summary>
        public static double WattsPerUnit(ulong powerInfo)
        {
            int unit = (int)(powerInfo & 0xF);
            return unit is > 0 and <= 7 ? 1.0 / (1 << unit) : 0.125;
        }

        public static int DecodePowerLimit(ulong value, int shift)
            => (int)((value >> shift) & PowerLimitMask);

        public static uint EncodePowerLimit(int watts, double wattsPerUnit)
            => (uint)Math.Clamp(
                (int)Math.Round(watts / wattsPerUnit, MidpointRounding.AwayFromZero),
                0,
                PowerLimitMask);

        /// <summary>时间窗解码为秒；0 表示不限。</summary>
        public static int DecodeTimeWindow(ulong value, int shift)
        {
            int raw = (int)((value >> shift) & TimeWindowMask);
            if (raw == 0)
                return 0;

            int mantissa = (raw >> 5) & 0x3;
            int exponent = raw & 0x1F;
            return (int)Math.Round((1 + (mantissa / 4.0)) * (1 << exponent));
        }

        /// <summary>秒编码为时间窗最接近的一档；0 或负数写 0，表示不限。</summary>
        public static uint EncodeTimeWindow(int seconds)
        {
            if (seconds <= 0)
                return 0;

            uint best = 0;
            double bestGap = double.MaxValue;
            for (int exponent = 0; exponent < 32; exponent++)
            {
                for (int mantissa = 0; mantissa < 4; mantissa++)
                {
                    // raw 0 表示不限，不能用来表达 1 秒。
                    if (exponent == 0 && mantissa == 0)
                        continue;

                    double candidate = (1 + (mantissa / 4.0)) * (1u << exponent);
                    double gap = Math.Abs(candidate - seconds);
                    if (gap >= bestGap)
                        continue;

                    bestGap = gap;
                    best = (uint)((mantissa << 5) | exponent);
                }
            }

            return best;
        }

        /// <summary>把 PL1/PL2 与时间窗写进既有寄存器值，其余位（含 clamp）保持原样。</summary>
        public static ulong ComposePowerLimit(
            ulong current,
            uint powerLimit1,
            uint powerLimit2,
            uint timeWindow2)
        {
            ulong value = current;

            value &= ~((ulong)PowerLimitMask << PowerLimit1Shift);
            value |= (ulong)powerLimit1 << PowerLimit1Shift;
            value |= 1UL << PowerEnable1Bit;
            value &= ~((ulong)TimeWindowMask << TimeWindow1Shift);

            value &= ~((ulong)PowerLimitMask << PowerLimit2Shift);
            value |= (ulong)powerLimit2 << PowerLimit2Shift;
            value |= 1UL << PowerEnable2Bit;
            value &= ~((ulong)TimeWindowMask << TimeWindow2Shift);
            value |= (ulong)(timeWindow2 & TimeWindowMask) << TimeWindow2Shift;

            return value;
        }
    }

    /// <summary>一次 Intel 调校读数。<c>null</c> 表示该平面读不到。</summary>
    public sealed class IntelTuningSnapshot
    {
        public bool Success { get; set; }
        public string? Error { get; set; }

        public int? CoreMilliVolts { get; set; }
        public int? GpuMilliVolts { get; set; }
        public int? CacheMilliVolts { get; set; }

        /// <summary>八档倍频；空表示读不到。</summary>
        public int[] Ratios { get; set; } = Array.Empty<int>();

        /// <summary>PL1 持续功耗（W）；null 表示读不到。</summary>
        public int? PowerLimit1Watts { get; set; }

        /// <summary>PL2 短时功耗（W）；null 表示读不到。</summary>
        public int? PowerLimit2Watts { get; set; }

        /// <summary>PL2 持续时间（秒）；0 表示不限，null 表示读不到。</summary>
        public int? TauSeconds { get; set; }
    }

    /// <summary>写入结果。失败项按名称记录，供日志与界面提示。</summary>
    public sealed class IntelTuningResult
    {
        public bool AllSucceeded { get; set; }
        public string? Fatal { get; set; }
        public List<string> Failures { get; } = new();
        public Dictionary<string, int> Applied { get; } = new();
    }

    /// <summary>
    /// 一次写入请求。为 null 的平面不动；<c>Ratios</c> 为空不动倍频。
    /// </summary>
    public sealed class IntelTuningRequest
    {
        public int? CoreMilliVolts { get; set; }
        public int? GpuMilliVolts { get; set; }
        public int? CacheMilliVolts { get; set; }
        public int[]? Ratios { get; set; }

        public int? PowerLimit1Watts { get; set; }
        public int? PowerLimit2Watts { get; set; }

        /// <summary>PL2 持续时间（秒）；0 表示不限。</summary>
        public int? TauSeconds { get; set; }

        /// <summary>回到首次读到的值，忽略上面的字段。</summary>
        public bool RestoreBaseline { get; set; }
    }

    /// <summary>
    /// Intel 调校通道。写入后立即回读，不一致即判失败。
    /// </summary>
    public sealed class IntelTuningSession : IDisposable
    {
        /// <summary>回读容差（mV）：寄存器按 1/1024 V 量化，取整会有 1 mV 误差。</summary>
        public const int VoltageTolerance = 2;

        private readonly IntelMsrDevice _device;
        private readonly object _gate = new();

        private IntelTuningSession(IntelMsrDevice device) => _device = device;

        public static IntelTuningSession? TryOpen(string modulePath)
        {
            IntelMsrDevice? device = IntelMsrDevice.TryOpen(modulePath);
            return device is null ? null : new IntelTuningSession(device);
        }

        public bool IsOpen => _device.IsOpen;

        public IntelTuningSnapshot Read()
        {
            lock (_gate)
            {
                IntelTuningSnapshot snapshot = new()
                {
                    CoreMilliVolts = ReadVoltage(IntelVoltagePlane.Core),
                    GpuMilliVolts = ReadVoltage(IntelVoltagePlane.Gpu),
                    CacheMilliVolts = ReadVoltage(IntelVoltagePlane.Cache),
                };

                if (_device.TryRead(IntelTuningCodec.MsrTurboRatioLimit, out ulong ratios))
                    snapshot.Ratios = IntelTuningCodec.DecodeRatios(ratios);

                if (_device.TryRead(IntelTuningCodec.MsrPkgPowerLimit, out ulong limits))
                {
                    int raw1 = IntelTuningCodec.DecodePowerLimit(limits, IntelTuningCodec.PowerLimit1Shift);
                    int raw2 = IntelTuningCodec.DecodePowerLimit(limits, IntelTuningCodec.PowerLimit2Shift);
                    if (raw1 != 0 || raw2 != 0)
                    {
                        double wattsPerUnit = ReadWattsPerUnit();
                        snapshot.PowerLimit1Watts = (int)Math.Round(raw1 * wattsPerUnit);
                        snapshot.PowerLimit2Watts = (int)Math.Round(raw2 * wattsPerUnit);
                        snapshot.TauSeconds = IntelTuningCodec.DecodeTimeWindow(
                            limits,
                            IntelTuningCodec.TimeWindow2Shift);
                    }
                }

                snapshot.Success = snapshot.CoreMilliVolts.HasValue
                    || snapshot.Ratios.Length > 0
                    || snapshot.PowerLimit1Watts.HasValue;
                if (!snapshot.Success)
                    snapshot.Error = "无法读取 MSR 0x150、0x1AD 与 0x610";

                return snapshot;
            }
        }

        public IntelTuningResult ApplyVoltage(IReadOnlyDictionary<IntelVoltagePlane, int> planes)
        {
            IntelTuningResult result = new();
            lock (_gate)
            {
                foreach ((IntelVoltagePlane plane, int milliVolts) in planes)
                {
                    string label = PlaneLabel(plane);

                    if (!_device.TryWrite(
                            IntelTuningCodec.MsrOcMailbox,
                            IntelTuningCodec.EncodeVoltageWrite(plane, milliVolts)))
                    {
                        result.Failures.Add($"{label}：写入被拒绝");
                        continue;
                    }

                    int? readBack = ReadVoltage(plane);
                    if (!readBack.HasValue)
                    {
                        result.Failures.Add($"{label}：无法回读");
                        continue;
                    }

                    if (Math.Abs(readBack.Value - milliVolts) > VoltageTolerance)
                    {
                        result.Failures.Add($"{label}：回读 {readBack.Value} mV，与写入 {milliVolts} mV 不一致");
                        continue;
                    }

                    result.Applied[label] = readBack.Value;
                }
            }

            result.AllSucceeded = result.Failures.Count == 0;
            return result;
        }

        public IntelTuningResult ApplyRatios(IReadOnlyList<int> ratios)
        {
            IntelTuningResult result = new();
            if (ratios.Count != IntelTuningCodec.RatioSlots)
            {
                result.Fatal = $"倍频档位应为 {IntelTuningCodec.RatioSlots} 项";
                return result;
            }

            lock (_gate)
            {
                ulong target = IntelTuningCodec.EncodeRatios(ratios);

                if (!_device.TryWrite(IntelTuningCodec.MsrTurboRatioLimit, target))
                {
                    result.Failures.Add("倍频上限：写入被拒绝");
                    result.AllSucceeded = false;
                    return result;
                }

                if (!_device.TryRead(IntelTuningCodec.MsrTurboRatioLimit, out ulong readBack))
                {
                    result.Failures.Add("倍频上限：无法回读");
                    result.AllSucceeded = false;
                    return result;
                }

                int[] actual = IntelTuningCodec.DecodeRatios(readBack);
                for (int i = 0; i < IntelTuningCodec.RatioSlots; i++)
                {
                    int expected = (int)((target >> (i * 8)) & 0xFF);
                    if (actual[i] != expected)
                        result.Failures.Add($"{i + 1} 核：回读 {actual[i]}，与写入 {expected} 不一致");
                }

                result.AllSucceeded = result.Failures.Count == 0;
            }

            return result;
        }

        public IntelTuningResult ApplyPowerLimits(int powerLimit1Watts, int powerLimit2Watts, int tauSeconds)
        {
            IntelTuningResult result = new();
            if (powerLimit1Watts <= 0 || powerLimit2Watts <= 0)
            {
                result.Fatal = "功耗上限必须大于 0 W";
                return result;
            }

            lock (_gate)
            {
                double wattsPerUnit = ReadWattsPerUnit();
                uint raw1 = IntelTuningCodec.EncodePowerLimit(powerLimit1Watts, wattsPerUnit);
                uint raw2 = IntelTuningCodec.EncodePowerLimit(powerLimit2Watts, wattsPerUnit);
                uint rawTau = IntelTuningCodec.EncodeTimeWindow(tauSeconds);

                if (!_device.TryRead(IntelTuningCodec.MsrPkgPowerLimit, out ulong current))
                {
                    result.Failures.Add("功耗限制：无法读取当前值");
                    result.AllSucceeded = false;
                    return result;
                }

                ulong target = IntelTuningCodec.ComposePowerLimit(current, raw1, raw2, rawTau);
                if (!_device.TryWrite(IntelTuningCodec.MsrPkgPowerLimit, target))
                {
                    result.Failures.Add("功耗限制：写入被拒绝");
                    result.AllSucceeded = false;
                    return result;
                }

                if (!_device.TryRead(IntelTuningCodec.MsrPkgPowerLimit, out ulong readBack))
                {
                    result.Failures.Add("功耗限制：无法回读");
                    result.AllSucceeded = false;
                    return result;
                }

                VerifyPower(result, "持续功耗", raw1,
                    IntelTuningCodec.DecodePowerLimit(readBack, IntelTuningCodec.PowerLimit1Shift), wattsPerUnit);
                VerifyPower(result, "短时功耗", raw2,
                    IntelTuningCodec.DecodePowerLimit(readBack, IntelTuningCodec.PowerLimit2Shift), wattsPerUnit);

                uint actualTau = (uint)((readBack >> IntelTuningCodec.TimeWindow2Shift)
                    & IntelTuningCodec.TimeWindowMask);
                if (actualTau != rawTau)
                {
                    result.Failures.Add(
                        $"短时功耗时长：回读 {IntelTuningCodec.DecodeTimeWindow(readBack, IntelTuningCodec.TimeWindow2Shift)} 秒，"
                        + $"与写入 {tauSeconds} 秒不一致");
                }

                result.AllSucceeded = result.Failures.Count == 0;
            }

            return result;
        }

        private static void VerifyPower(
            IntelTuningResult result,
            string label,
            uint expectedRaw,
            int actualRaw,
            double wattsPerUnit)
        {
            if ((uint)actualRaw != expectedRaw)
            {
                result.Failures.Add(
                    $"{label}：回读 {Math.Round(actualRaw * wattsPerUnit)} W，"
                    + $"与写入 {Math.Round(expectedRaw * wattsPerUnit)} W 不一致");
                return;
            }

            result.Applied[label] = (int)Math.Round(expectedRaw * wattsPerUnit);
        }

        private double ReadWattsPerUnit()
            => _device.TryRead(IntelTuningCodec.MsrPkgPowerInfo, out ulong info)
                ? IntelTuningCodec.WattsPerUnit(info)
                : 0.125;

        public IntelTuningResult Restore(IntelTuningSnapshot baseline)
        {
            IntelTuningResult result = new();

            Dictionary<IntelVoltagePlane, int> planes = new();
            if (baseline.CoreMilliVolts.HasValue)
                planes[IntelVoltagePlane.Core] = baseline.CoreMilliVolts.Value;
            if (baseline.GpuMilliVolts.HasValue)
                planes[IntelVoltagePlane.Gpu] = baseline.GpuMilliVolts.Value;
            if (baseline.CacheMilliVolts.HasValue)
                planes[IntelVoltagePlane.Cache] = baseline.CacheMilliVolts.Value;

            if (planes.Count > 0)
            {
                IntelTuningResult voltage = ApplyVoltage(planes);
                result.Failures.AddRange(voltage.Failures);
                foreach ((string key, int value) in voltage.Applied)
                    result.Applied[key] = value;
            }

            if (baseline.Ratios.Length == IntelTuningCodec.RatioSlots)
            {
                IntelTuningResult ratios = ApplyRatios(baseline.Ratios);
                result.Failures.AddRange(ratios.Failures);
            }

            if (baseline.PowerLimit1Watts.HasValue && baseline.PowerLimit2Watts.HasValue)
            {
                IntelTuningResult power = ApplyPowerLimits(
                    baseline.PowerLimit1Watts.Value,
                    baseline.PowerLimit2Watts.Value,
                    baseline.TauSeconds ?? 0);
                result.Failures.AddRange(power.Failures);
                foreach ((string key, int value) in power.Applied)
                    result.Applied[key] = value;
            }

            result.AllSucceeded = result.Failures.Count == 0;
            return result;
        }

        private int? ReadVoltage(IntelVoltagePlane plane)
        {
            if (!_device.TryWrite(
                    IntelTuningCodec.MsrOcMailbox,
                    IntelTuningCodec.EncodeVoltageRead(plane)))
            {
                return null;
            }

            return _device.TryRead(IntelTuningCodec.MsrOcMailbox, out ulong value)
                ? IntelTuningCodec.DecodeVoltage(value)
                : null;
        }

        private static string PlaneLabel(IntelVoltagePlane plane) => plane switch
        {
            IntelVoltagePlane.Gpu => "核显电压",
            IntelVoltagePlane.Cache => "缓存电压",
            _ => "核心电压"
        };

        public void Dispose() => _device.Dispose();
    }
}
