using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// PID → 进程创建时间。只为一件事存在：识别 PID 复用。
    ///
    /// PID 会被系统回收再分配给新进程。日志账本、漂移期望值都按 PID 记，
    /// 不区分实例的话，新进程会被「已处理」挡在门外，漂移日志也会拿着旧实例的
    /// 期望掩码对着新进程报个不停。
    /// </summary>
    internal sealed class ProcessInstanceLedger
    {
        private readonly Dictionary<int, long> _entries = new();
        private readonly object _gate = new();

        /// <summary>记下实例标识。<paramref name="ticks"/> 为 0（读不到）也记：下次读到再补。</summary>
        public void Note(int pid, long ticks)
        {
            lock (_gate)
                _entries[pid] = ticks;
        }

        public void Forget(int pid)
        {
            lock (_gate)
                _entries.Remove(pid);
        }

        public void Clear()
        {
            lock (_gate)
                _entries.Clear();
        }

        public void PruneTo(IReadOnlySet<int> alive)
        {
            lock (_gate)
            {
                List<int>? dead = null;

                foreach (int pid in _entries.Keys)
                {
                    if (!alive.Contains(pid))
                        (dead ??= new()).Add(pid);
                }

                if (dead is null)
                    return;

                foreach (int pid in dead)
                    _entries.Remove(pid);
            }
        }

        /// <summary>
        /// 取出创建时间已变的 PID，并从表里移除（下一轮全量扫描会当新进程重新判定）。
        ///
        /// 两种读不到都不判：表里没有值、或这次读不到。前者补记，后者跳过 ——
        /// 权限不足与「进程已换人」在这里分不开，宁可漏报也不误清账本。
        /// </summary>
        public int[] TakeReused(Func<int, long> readTicks)
        {
            lock (_gate)
            {
                List<int>? reused = null;

                foreach (KeyValuePair<int, long> pair in _entries)
                {
                    long current;

                    try
                    {
                        current = readTicks(pair.Key);
                    }
                    catch
                    {
                        continue;
                    }

                    if (current == 0)
                        continue;

                    if (pair.Value == 0)
                        _entries[pair.Key] = current;
                    else if (pair.Value != current)
                        (reused ??= new()).Add(pair.Key);
                }

                if (reused is null)
                    return Array.Empty<int>();

                foreach (int pid in reused)
                    _entries.Remove(pid);

                return reused.ToArray();
            }
        }
    }
}
