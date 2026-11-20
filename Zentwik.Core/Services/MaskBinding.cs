using Zentwik.Core.Helpers;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 表达层（128 位）与绑定层（单处理器组 ulong）之间的唯一转换点。
    ///
    /// 一次写入只覆盖一个处理器组，位 64 以上无法表达。这里统一拒绝，
    /// 不做截断 —— 截断的后果是用户勾了 16 个核、实际只绑了其中一半，且没有任何提示。
    /// 跨组绑定（多组 CPU Sets）是另一件事，不在本次范围内。
    /// </summary>
    public static class MaskBinding
    {
        public const string HighBitsMessage =
            "掩码包含 64 号以上的逻辑处理器，单次写入只覆盖一个处理器组，当前版本无法绑定该掩码";

        /// <summary>能整体放进一个处理器组时返回 true 并给出 ulong。</summary>
        public static bool TryToGroupMask(CoreMask mask, out ulong groupMask)
        {
            groupMask = mask.Low;
            return !mask.HasHighBits;
        }
    }
}
