using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 绑定失败后的重试计划：按 PID 记次数与下次到期时刻，退避按 2 的幂增长。
    /// 失败不再占住「已处理」位，否则进程整个生命周期都不再重试。
    /// </summary>
    public sealed class ScheduleRetryLedger
    {
        private sealed class Entry
        {
            public string Name = string.Empty;
            public int Attempts;
            public long DueTick;
        }

        private const int MaxDelayMs = 60_000;

        private readonly Dictionary<int, Entry> _entries = new();
        private readonly object _gate = new();

        public ScheduleRetryLedger(int maxAttempts = 5, int baseDelayMs = 2000)
        {
            MaxAttempts = Math.Max(1, maxAttempts);
            BaseDelayMs = Math.Max(1, baseDelayMs);
        }

        public int MaxAttempts { get; }

        public int BaseDelayMs { get; }

        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        public int Bump(int pid, string name) => Bump(pid, name, Environment.TickCount64);

        internal int Bump(int pid, string name, long now)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(pid, out Entry? entry))
                    _entries[pid] = entry = new Entry { Name = name };

                entry.Attempts++;
                entry.DueTick = now + DelayOf(entry.Attempts);
                return entry.Attempts;
            }
        }

        /// <summary>到期且仍在存活名单里的重试项。</summary>
        public List<(int Pid, string Name)> Due(HashSet<int> alive) =>
            Due(alive, Environment.TickCount64);

        internal List<(int Pid, string Name)> Due(HashSet<int> alive, long now)
        {
            List<(int Pid, string Name)> due = new();

            lock (_gate)
            {
                foreach ((int pid, Entry entry) in _entries)
                {
                    if (entry.Attempts > MaxAttempts || now < entry.DueTick)
                        continue;

                    if (!alive.Contains(pid))
                        continue;

                    due.Add((pid, entry.Name));
                }
            }

            return due;
        }

        public void Remove(int pid)
        {
            lock (_gate) _entries.Remove(pid);
        }

        public void PruneTo(HashSet<int> alive)
        {
            lock (_gate)
            {
                foreach (int pid in new List<int>(_entries.Keys))
                {
                    if (!alive.Contains(pid))
                        _entries.Remove(pid);
                }
            }
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }

        private int DelayOf(int attempts)
        {
            long delay = (long)BaseDelayMs << Math.Min(attempts - 1, 5);
            return (int)Math.Min(delay, MaxDelayMs);
        }
    }
}
