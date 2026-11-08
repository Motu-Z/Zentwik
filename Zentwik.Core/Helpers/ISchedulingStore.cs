using System.Collections.Generic;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 进程调度规则的持久化与查询。
    ///
    /// 读取操作返回的是内部快照引用而非深拷贝：调用方通常只做遍历，
    /// 每秒一次的轮询里复制整份规则列表没有意义。
    /// </summary>
    public interface ISchedulingStore
    {
        SchedulingProfile Profile { get; }

        /// <summary>是否存在「会实际生效」的规则。规则全关或目标为 None 时为 false。</summary>
        bool HasEnabledRules { get; }

        void Load();

        void Save(IEnumerable<ScheduleRule> rules);

        void ResetToFactory();

        /// <summary>从磁盘重读，让界面改动对后台监听立即生效。读取失败时保留内存中的规则。</summary>
        void Reload();

        ScheduleRule? LookupByName(string name);
    }
}
