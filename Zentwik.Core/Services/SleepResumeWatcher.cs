using System;
using System.Threading;
using Microsoft.Win32;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 睡眠 / 休眠恢复监听。
    /// </summary>
    /// <remarks>
    /// 进程在睡眠里不会被杀掉，只是被冻结 —— 唤醒后它原地继续跑。
    /// 真正丢的是 SMU 里的寄存器值：S3 与休眠恢复时主板会把 CO 偏移、
    /// FMax 这类设置清回出厂默认，于是「进程还活着、参数已经没了」。
    /// 所以这里只负责报告「刚醒过来」，要不要补写由订阅方决定。
    ///
    /// 与 <see cref="PowerStatusWatcher"/> 分开而不是共用：那个只认电源类型翻转
    /// （插拔电源），刻意忽略 Suspend / Resume —— 合盖再打开供电状态没变，
    /// 跟着重评一遍场景等于白切一次方案。两者关心的 Mode 完全不同。
    /// </remarks>
    public sealed class SleepResumeWatcher
    {
        private readonly ILogService _log;

        private int _attached;

        public SleepResumeWatcher(ILogService log)
        {
            ArgumentNullException.ThrowIfNull(log);
            _log = log;
        }

        /// <summary>系统刚从睡眠 / 休眠恢复。在系统事件线程上触发。</summary>
        public event Action? Resumed;

        /// <summary>开始监听。幂等：重复调用只挂一次。</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _attached, 1) != 0)
                return;

            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                _log.Info("休眠感知：已开始监听系统恢复事件");
            }
            catch (Exception ex)
            {
                // 挂不上就退回到「不感知」：功能没了，但进程不能因为这个起不来。
                Interlocked.Exchange(ref _attached, 0);
                _log.Warn($"休眠感知：订阅系统电源事件失败，本次不启用：{ex.Message}");
            }
        }

        /// <summary>停止监听。幂等。</summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _attached, 0) == 0)
                return;

            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                _log.Info("休眠感知：已停止监听系统恢复事件");
            }
            catch
            {
                // 卸载失败无需处理：进程退出时系统自行回收
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            // 只要 Resume。PBT_APMRESUMEAUTOMATIC 这类「无人值守被叫醒」的恢复
            // 也一并接受：多写一次 SMU 是无害的（参数就在本地），
            // 而漏掉一次就是「醒来参数没了」，后者代价大得多。
            if (e.Mode != PowerModes.Resume)
                return;

            Action? handler = Resumed;
            if (handler is null)
                return;

            try { handler(); }
            catch (Exception ex) { _log.Warn($"休眠感知：恢复回调处理失败：{ex.Message}"); }
        }
    }
}
