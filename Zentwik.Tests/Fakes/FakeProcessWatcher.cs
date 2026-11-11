using Zentwik.Core.Services;

namespace Zentwik.Tests.Fakes
{
    /// <summary>空壳 IProcessWatcher：ProfileService.Apply 只会调 ReapplyNow，其余不参与。</summary>
    public sealed class FakeProcessWatcher : IProcessWatcher
    {
        public bool HasOwnership => false;
        public bool IsRunning => false;
        public event Action? ProcessSetChanged { add { } remove { } }

        /// <summary>Apply 触发的重扫次数，供断言「有没有让在跑的进程跟上新规则」。</summary>
        public int ReapplyCount { get; private set; }

        public void Start() { }
        public void Stop() { }
        public void ResetCache() { }
        public void ReapplyNow() => ReapplyCount++;

        /// <summary>已绑集合：与真实实现同语义，供分派层测试断言增删。</summary>
        private readonly HashSet<int> _bound = new();

        /// <summary>测试用：模拟「绑定成功」。返回 false 表示这个 pid 已在集合里。</summary>
        public bool SimulateBind(int pid)
        {
            lock (_bound) return _bound.Add(pid);
        }

        /// <summary>ClearAllSchedules 时会被清掉的 pid，供断言「到底动了谁」。</summary>
        public List<int> UnboundPids { get; } = new();

        public int[] GetBoundProcessIds()
        {
            lock (_bound) return _bound.ToArray();
        }

        public int ClearAllSchedules()
        {
            lock (_bound)
            {
                UnboundPids.AddRange(_bound);
                int count = _bound.Count;
                _bound.Clear();
                return count;
            }
        }

        public bool ClearSchedule(int pid)
        {
            lock (_bound)
            {
                if (!_bound.Remove(pid))
                    return false;

                UnboundPids.Add(pid);
                return true;
            }
        }
    }
}
