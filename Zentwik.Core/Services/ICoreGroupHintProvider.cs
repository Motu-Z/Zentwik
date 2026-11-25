namespace Zentwik.Core.Services
{
    /// <summary>
    /// 核心组宽度提示：每组的物理核数。属于可选能力 —— 拿不到时调度按观测到的
    /// L3 缓存域分组，再退化为单组，不因缺少厂商信息而失效。
    /// </summary>
    public interface ICoreGroupHintProvider
    {
        /// <summary>读取每核心组的物理核数；不可用时返回 false（不触发硬件探测）。</summary>
        bool TryGetCoresPerGroup(out int cores);
    }

    /// <summary>没有厂商提示时的实现：永远返回 false，调度走观测回退。</summary>
    public sealed class NullCoreGroupHintProvider : ICoreGroupHintProvider
    {
        public static NullCoreGroupHintProvider Instance { get; } = new();

        public bool TryGetCoresPerGroup(out int cores)
        {
            cores = 0;
            return false;
        }
    }
}
