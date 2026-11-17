using System;
using System.Collections.Generic;
using System.Linq;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 记下每个 PID 当初下发的是哪一组核、用哪种方式下发的。
    ///
    /// 读回比对要用它当期望值：进程的绑定可能被它自己、被其他调度工具、
    /// 或被任务管理器改掉，只有记住原值才知道偏了没有。
    /// </summary>
    public sealed class ScheduleDriftTracker
    {
        public readonly record struct Entry(int Pid, string Name, ulong Mask, bool ByAffinityMask);

        private readonly object _gate = new();
        private readonly Dictionary<int, Entry> _entries = new();

        public int Count
        {
            get
            {
                lock (_gate)
                    return _entries.Count;
            }
        }

        public void Note(int pid, string name, ulong mask, bool byAffinityMask)
        {
            if (pid <= 0 || mask == 0)
                return;

            lock (_gate)
                _entries[pid] = new Entry(pid, name, mask, byAffinityMask);
        }

        public void Forget(int pid)
        {
            lock (_gate)
                _entries.Remove(pid);
        }

        public Entry[] Snapshot()
        {
            lock (_gate)
                return _entries.Values.ToArray();
        }

        /// <summary>读单个 PID 的当前记录，用于探测前确认账本没有被改过。</summary>
        public bool TryGet(int pid, out Entry entry)
        {
            lock (_gate)
                return _entries.TryGetValue(pid, out entry);
        }

        public void PruneTo(IReadOnlySet<int> alive)
        {
            lock (_gate)
            {
                foreach (int pid in _entries.Keys.ToArray())
                {
                    if (!alive.Contains(pid))
                        _entries.Remove(pid);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
                _entries.Clear();
        }
    }
}
