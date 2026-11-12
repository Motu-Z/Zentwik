using System;
using System.Collections.Generic;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 掩码名称的校验策略（长度 + 重名）。名称上限此前散落在三处且互不同步：
    /// XAML 的 MaxLength、保存时的 Length 判断、<see cref="MaskStore.Upsert"/> 里的
    /// Substring 截断。这里把它们收敛成唯一来源，所有入口统一引用本类。
    /// </summary>
    /// <remarks>
    /// 字符数上限在中英文下表现完全不同：16 个英文约 112px，16 个中文约 208px，
    /// 而左栏列表留给名称的宽度约 188px。同一个 16 的阈值，英文绰绰有余、中文必然溢出。
    /// 所以改成按显示宽度计权重：全角字符计 2，半角字符计 1。
    ///
    /// 用户确定的口径：上限 = 6 个中日韩文字或全角符号，或 12 个拉丁字母 / 数字 / 半角符号。
    /// 带变音符的拉丁字母（é、ü、ß 等）按半角计 1，不做特殊处理 —— 它们的宽度与
    /// 普通拉丁字母一致，单独加权会让规则难以解释。
    /// </remarks>
    internal static class MaskNamePolicy
    {
        /// <summary>总权重上限。12 = 6 个全角字符，或 12 个半角字符。</summary>
        public const int MaxWeight = 12;

        /// <summary>输入框下方的常驻提示，说明上限的换算方式。</summary>
        public const string Hint = "最多 6 个中文或 12 个英文";

        /// <summary>名称与他人重复时的统一提示文案。</summary>
        public const string DuplicateError = "这个名称已经用过了，请换一个。";

        /// <summary>单个字符的显示权重：全角 / 中日韩 / 代理对（emoji）计 2，其余计 1。</summary>
        private static int WeightOf(char c)
        {
            if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c))
                return 2;   // 代理对的一半，成对出现时由 Weight(string) 合并成一次计数

            int code = c;

            // 韩文字母（Hangul Jamo）
            if (code >= 0x1100 && code <= 0x115F) return 2;
            // CJK 部首补充、康熙部首、CJK 统一汉字及扩展 A
            if (code >= 0x2E80 && code <= 0xA4CF) return 2;
            // 韩文音节
            if (code >= 0xAC00 && code <= 0xD7A3) return 2;
            // CJK 兼容汉字
            if (code >= 0xF900 && code <= 0xFAFF) return 2;
            // CJK 兼容形式（竖排标点等）
            if (code >= 0xFE30 && code <= 0xFE4F) return 2;
            // 全角 ASCII 与全角符号
            if (code >= 0xFF00 && code <= 0xFF60) return 2;
            if (code >= 0xFFE0 && code <= 0xFFE6) return 2;

            return 1;
        }

        /// <summary>
        /// 文本的显示总权重。代理对（emoji）按一个字符计 2，不会算成两次。
        /// </summary>
        public static int Weight(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int total = 0;
            for (int i = 0; i < text.Length; i++)
            {
                // emoji 等增补平面字符占两个 char，跳过低代理，避免重复计数
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    total += 2;
                    i++;
                    continue;
                }
                total += WeightOf(text[i]);
            }
            return total;
        }
    }
}
