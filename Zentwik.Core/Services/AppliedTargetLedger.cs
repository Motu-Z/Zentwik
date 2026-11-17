using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 每个 PID 上次成功下发的目标：掩码、绑定方式、优先级。
    ///
    /// 保存规则、切方案、改掩码都会让监听重扫一遍全部进程。重扫本来只为「让改动生效」，
    /// 但目标没变的进程也会被重写一次 —— 多出几次跨进程调用，日志里再刷一行「已绑定」。
    /// 三项都相同就跳过。
    /// </summary>
    public sealed class AppliedTargetLedger
    {
        public readonly record struct Target(ulong Mask, bool ByAffinityMask, string Priority);

        private readonly Dictionary<int, Target> _entries = new();
        private readonly object _gate = new();

        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        public void Note(int pid, Target target)
        {
            lock (_gate) _entries[pid] = target;
        }

        public bool Matches(int pid, Target target)
        {
            lock (_gate) return _entries.TryGetValue(pid, out Target entry) && entry == target;
        }

        public void Forget(int pid)
        {
            lock (_gate) _entries.Remove(pid);
        }

        public void PruneTo(HashSet<int> alive)
        {
            lock (_gate)
            {
                List<int> stale = new();

                foreach (int pid in _entries.Keys)
                {
                    if (!alive.Contains(pid))
                        stale.Add(pid);
                }

                foreach (int pid in stale)
                    _entries.Remove(pid);
            }
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }
    }
}
