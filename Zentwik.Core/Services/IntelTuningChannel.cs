using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// Intel MSR 通道：电压三平面（核心 / 核显 / 缓存）与八档睿频上限。
    ///
    /// 与 AMD 通道完全独立：不打开 SMU 模块、不产生 CO 数组、也不使用 FMax 概念。
    /// 首次访问才打开设备；驱动缺失时以 <see cref="TuningChannelStatus.DriverUnavailable"/> 返回。
    /// </summary>
    public sealed class IntelTuningChannel : IIntelTuningChannel
    {
        private readonly ILogService _log;
        private readonly Lazy<bool> _initialized;

        private IntelTuningSession? _session;
        private IntelTuningSnapshot? _baseline;

        private TuningChannelStatus _status = TuningChannelStatus.Unknown;
        private bool _available;

        public IntelTuningChannel(ILogService log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
            _initialized = new Lazy<bool>(Open, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private static string ModulePath => Path.Combine(AppContext.BaseDirectory, "PawnIO", "IntelMSR.bin");

        public CpuVendor Vendor => CpuVendor.Intel;

        public TuningChannelStatus Status
        {
            get
            {
                EnsureInitialized();
                return _status;
            }
        }

        public bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _available;
            }
        }

        public string? LastError { get; private set; }

        private void EnsureInitialized()
        {
            _ = _initialized.Value;
        }

        /// <summary>打开 MSR 设备并读一次基线。读取失败时关闭会话，通道按不可用处理。</summary>
        private bool Open()
        {
            try
            {
                if (!File.Exists(ModulePath))
                {
                    _status = TuningChannelStatus.DriverUnavailable;
                    LastError = "IntelMSR 模块文件缺失（PawnIO\\IntelMSR.bin）";
                    _log.Warn(LastError);
                    return false;
                }

                _session = IntelTuningSession.TryOpen(ModulePath);
                if (_session is null)
                {
                    _status = TuningChannelStatus.DriverUnavailable;
                    LastError = "Intel MSR 模块不可用（PawnIO 驱动未安装或模块缺失）";
                    _log.Warn(LastError);
                    return false;
                }

                IntelTuningSnapshot snapshot = _session.Read();
                if (!snapshot.Success)
                {
                    _status = TuningChannelStatus.ProbeFailed;
                    LastError = snapshot.Error ?? "Intel MSR 读取失败";
                    _log.Warn($"Intel 调校通道不可用：{LastError}");
                    _session.Dispose();
                    _session = null;
                    return false;
                }

                _baseline = snapshot;
                _available = true;
                _status = TuningChannelStatus.Ready;
                LastError = null;
                _log.Info(
                    $"Intel 调校通道就绪：核心 {VoltageText(snapshot.CoreMilliVolts)} · " +
                    $"核显 {VoltageText(snapshot.GpuMilliVolts)} · 缓存 {VoltageText(snapshot.CacheMilliVolts)}");
                return true;
            }
            catch (Exception ex)
            {
                _session = null;
                _available = false;
                _status = TuningChannelStatus.ProbeFailed;
                LastError = ex.Message;
                _log.Error($"Intel 调校通道初始化失败：{ex}");
                return false;
            }
        }

        private static string VoltageText(int? milliVolts)
            => milliVolts.HasValue ? $"{milliVolts.Value} mV" : "读取失败";

        public bool WaitForAvailability(int maxSeconds = 60, CancellationToken token = default)
        {
            if (IsAvailable)
                return true;

            if (_status == TuningChannelStatus.NotSupported)
                return false;

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSeconds)
            {
                if (token.IsCancellationRequested)
                    return false;

                if (RetryOpen())
                    return true;

                if (token.WaitHandle.WaitOne(250))
                    return false;
            }

            _log.Error($"Intel MSR 未加载（{maxSeconds} 秒内未就绪）");
            return false;
        }

        /// <summary>重新打开设备。只有「设备打不开」才值得重试，读基线失败说明通道本身有问题。</summary>
        private bool RetryOpen()
        {
            if (_session is not null)
                return false;

            try
            {
                IntelTuningSession? session = IntelTuningSession.TryOpen(ModulePath);
                if (session is null)
                {
                    _status = TuningChannelStatus.DriverUnavailable;
                    LastError = "Intel MSR 模块不可用";
                    return false;
                }

                IntelTuningSnapshot snapshot = session.Read();
                if (!snapshot.Success)
                {
                    session.Dispose();
                    _status = TuningChannelStatus.ProbeFailed;
                    LastError = snapshot.Error ?? "Intel MSR 读取失败";
                    return false;
                }

                _session = session;
                _baseline = snapshot;
                _available = true;
                _status = TuningChannelStatus.Ready;
                LastError = null;
                _log.Info("Intel 调校通道已就绪（重试成功）");
                return true;
            }
            catch (Exception ex)
            {
                _status = TuningChannelStatus.DriverUnavailable;
                LastError = ex.Message;
                return false;
            }
        }

        public IntelTuningSnapshot ReadIntelTuning()
        {
            if (!IsAvailable || _session is null)
                return new IntelTuningSnapshot { Success = false, Error = LastError ?? "Intel 调校通道不可用" };

            return _session.Read();
        }

        public IntelTuningResult ApplyIntelTuning(IntelTuningRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!IsAvailable || _session is null)
                return new IntelTuningResult { Fatal = LastError ?? "Intel 调校通道不可用" };

            if (request.RestoreBaseline)
            {
                if (_baseline is null)
                    return new IntelTuningResult { Fatal = "缺少原始值快照" };

                IntelTuningResult restored = _session.Restore(_baseline);
                LogWrite("恢复原始值", restored);
                return restored;
            }

            Dictionary<IntelVoltagePlane, int> planes = new();
            if (request.CoreMilliVolts.HasValue)
                planes[IntelVoltagePlane.Core] = request.CoreMilliVolts.Value;
            if (request.GpuMilliVolts.HasValue)
                planes[IntelVoltagePlane.Gpu] = request.GpuMilliVolts.Value;
            if (request.CacheMilliVolts.HasValue)
                planes[IntelVoltagePlane.Cache] = request.CacheMilliVolts.Value;

            IntelTuningResult result = new();

            if (planes.Count > 0)
            {
                IntelTuningResult voltage = _session.ApplyVoltage(planes);
                result.Failures.AddRange(voltage.Failures);
                foreach (KeyValuePair<string, int> pair in voltage.Applied)
                    result.Applied[pair.Key] = pair.Value;
            }

            if (request.Ratios is { Length: IntelTuningCodec.RatioSlots })
            {
                IntelTuningResult ratios = _session.ApplyRatios(request.Ratios);
                result.Failures.AddRange(ratios.Failures);
            }

            result.AllSucceeded = result.Failures.Count == 0;
            LogWrite("写入", result);
            return result;
        }

        private void LogWrite(string action, IntelTuningResult result)
        {
            if (result.Failures.Count == 0)
            {
                _log.Info($"Intel 调校{action}完成");
                return;
            }

            foreach (string failure in result.Failures)
                _log.Error($"Intel 调校{action}：{failure}");
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}
