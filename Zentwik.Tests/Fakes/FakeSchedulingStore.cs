using System.Collections.Generic;
using System.Linq;
using Zentwik.Core.Helpers;

namespace Zentwik.Tests.Fakes
{
    /// <summary>内存版 ISchedulingStore：Save 记下最后一次规则集，Profile 由它派生。</summary>
    public sealed class FakeSchedulingStore : ISchedulingStore
    {
        private List<ScheduleRule> _rules = new();
        public SchedulingProfile Profile => new() { Rules = _rules };
        public bool HasEnabledRules => _rules.Any(r => r.Enabled);

        /// <summary>最后一次 Save 的规则集；null 表示还没保存过。</summary>
        public IReadOnlyList<ScheduleRule>? LastSaved { get; private set; }

        public void Load() { }
        public void ResetToFactory() { }
        public void Reload() { }

        public void Save(IEnumerable<ScheduleRule> rules)
        {
            _rules = rules.ToList();
            LastSaved = _rules;
        }

        public ScheduleRule? LookupByName(string name) => _rules.FirstOrDefault(r => r.Name == name);
    }
}
