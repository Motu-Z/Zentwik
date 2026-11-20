using System;
using Zentwik.Core.Helpers;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 按效率类把物理核分成 P / E 两组。混合架构的 P / E 边界只有效率类能给。
    ///
    /// 档次多于两档的机器（P / E / LP-E）也不留空档：最高一档算 P，其余全归 E。
    /// </summary>
    public static class MixedCorePlan
    {
        /// <summary>效率类不止一档时返回 true，并给出两组的逻辑处理器掩码。</summary>
        public static bool TrySplit(
            CoreMask[] coreMasks,
            int[] classes,
            out CoreMask performance,
            out CoreMask efficiency)
        {
            performance = CoreMask.Zero;
            efficiency = CoreMask.Zero;

            if (coreMasks.Length == 0 || classes.Length < coreMasks.Length)
                return false;

            int low = int.MaxValue;
            int high = int.MinValue;

            for (int i = 0; i < coreMasks.Length; i++)
            {
                low = Math.Min(low, classes[i]);
                high = Math.Max(high, classes[i]);
            }

            if (low == high)
                return false;

            for (int i = 0; i < coreMasks.Length; i++)
            {
                if (classes[i] == high)
                    performance |= coreMasks[i];
                else
                    efficiency |= coreMasks[i];
            }

            return !performance.IsZero && !efficiency.IsZero;
        }
    }
}
