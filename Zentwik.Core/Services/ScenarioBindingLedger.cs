using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 场景下发的绑定账本：PID → 规则 Id。
    /// 场景走的是另一条路径，不记在这里的话，解绑与清空都看不见它。
    /// </summary>
    public sealed class ScenarioBindingLedger
    {
        private readonly Dictionary<int, string> _entries = new();
        private readonly object _gate = new();

        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        public void Note(int pid, string owner)
        {
            lock (_gate) _entries[pid] = owner;
        }

        public bool Contains(int pid)
        {
            lock (_gate) return _entries.ContainsKey(pid);
        }

        public int[] ToArray()
        {
            lock (_gate) return new List<int>(_entries.Keys).ToArray();
        }

        /// <summary>取走要解绑的 PID：<paramref name="owner"/> 为空时取全部。</summary>
        public int[] Take(string? owner)
        {
            lock (_gate)
            {
                List<int> taken = new();

                foreach ((int pid, string id) in _entries)
                {
                    if (owner is null || string.Equals(id, owner, System.StringComparison.Ordinal))
                        taken.Add(pid);
                }

                foreach (int pid in taken)
                    _entries.Remove(pid);

                return taken.ToArray();
            }
        }

        public void Remove(int pid)
        {
            lock (_gate) _entries.Remove(pid);
        }

        public void PruneTo(HashSet<int> alive)
        {
            lock (_gate)
            {
                List<int> gone = new();

                foreach (int pid in _entries.Keys)
                {
                    if (!alive.Contains(pid))
                        gone.Add(pid);
                }

                foreach (int pid in gone)
                    _entries.Remove(pid);
            }
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }
    }
}
