using System;
using System.Collections.Generic;
using System.Linq;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 逻辑核掩码与可读文本的双向转换。
    ///
    /// 设计目标：让用户能像 CPU Sets 那样直接给一个掩码，而不是只能从预置项里挑。
    /// 支持三种输入写法（解析时自动识别）：
    ///   1. 十六进制：0x0000FFFF / FFFF
    ///   2. 区间列表：0-7,16-23     （逗号分隔，区间用连字符）
    ///   3. 枚举列表：0,1,2,3
    ///
    /// 输出统一为「区间列表」（0-7,16-23），比纯十六进制更易读；
    /// 同时提供十六进制形式用于悬停提示。
    /// </summary>
    public static class MaskFormatter
    {
        private static readonly char[] Separators = { ',', '，', ' ', ';', '；' };

        /// <summary>位掩码转「区间列表」文本，如 0x00FF00FF → "0-7,16-23"。</summary>
        public static string ToRangeText(ulong mask)
        {
            if (mask == 0)
                return "无";

            List<string> parts = new();
            int i = 0;

            while (i < 64)
            {
                while (i < 64 && (mask & (1UL << i)) == 0)
                    i++;

                if (i >= 64)
                    break;

                int start = i;
                while (i < 64 && (mask & (1UL << i)) != 0)
                    i++;

                int end = i - 1;
                parts.Add(start == end ? $"{start}" : $"{start}-{end}");
            }

            return string.Join(",", parts);
        }

        /// <summary>
        /// 解析用户输入的掩码文本。
        /// 返回 true 表示解析成功，mask 为结果，error 为失败原因。
        /// </summary>
        public static bool TryParse(string? text, out ulong mask, out string? error)
        {
            mask = 0;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "掩码不能为空";
                return false;
            }

            string input = text.Trim();

            // 形如 CCD0 / Tile1 / Core3 的标识符（首字符字母 + 含数字，且整体恰好都是 hex 字符）
            // 不拦会被当成十六进制："CCD0" 解析出来是 0xCCD0。要求加 0x 前缀就无歧义了。
            if (!input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                input.Length > 0 &&
                char.IsAsciiLetter(input[0]) &&
                input.Any(char.IsAsciiDigit) &&
                input.All(Uri.IsHexDigit))
            {
                error = $"“{input}”看起来是名字，请输入区间（如 0-7,16-23）或带前缀的掩码（如 0xFFFFFFFF）";
                return false;
            }

            return LooksLikeHex(input)
                ? TryParseHex(input, out mask, out error)
                : TryParseList(input, out mask, out error);
        }

        /// <summary>
        /// 十六进制的判定要收紧：纯数字（如 "16"）必须留给枚举列表，
        /// 否则用户想表达 0-16 区间之外的单个核时会被误读成十六进制。
        /// </summary>
        private static bool LooksLikeHex(string input) =>
            input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            (input.Length > 0 &&
             !input.Contains(',') &&
             !input.Contains('-') &&
             input.All(Uri.IsHexDigit) &&
             input.Any(char.IsLetter));

        private static bool TryParseHex(string input, out ulong mask, out string? error)
        {
            string body = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? input.Substring(2)
                : input;

            if (!ulong.TryParse(body, System.Globalization.NumberStyles.HexNumber, null, out mask))
            {
                error = $"“{input}”不是合法的十六进制掩码";
                mask = 0;
                return false;
            }

            return Validate(mask, out error);
        }

        /// <summary>区间列表（0-7,16-23）与枚举列表（0,1,2,3）共用一套分隔符切分。</summary>
        private static bool TryParseList(string input, out ulong mask, out string? error)
        {
            mask = 0;

            foreach (string rawPart in input.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = rawPart.Trim();
                if (part.Length == 0)
                    continue;

                int dash = part.IndexOf('-');
                bool ok = dash > 0
                    ? TryParseRange(part, dash, ref mask, out error)
                    : TryParseSingle(part, ref mask, out error);

                if (!ok)
                    return false;
            }

            return Validate(mask, out error);
        }

        private static bool TryParseRange(string part, int dash, ref ulong mask, out string? error)
        {
            if (!int.TryParse(part.Substring(0, dash).Trim(), out int from) ||
                !int.TryParse(part.Substring(dash + 1).Trim(), out int to))
            {
                error = $"区间“{part}”格式不正确，应为 起始-结束";
                return false;
            }

            if (from < 0 || to > 63 || from > to)
            {
                error = $"区间“{part}”超出 0-63 范围或起止颠倒";
                return false;
            }

            for (int i = from; i <= to; i++)
                mask |= 1UL << i;

            error = null;
            return true;
        }

        private static bool TryParseSingle(string part, ref ulong mask, out string? error)
        {
            if (!int.TryParse(part, out int core))
            {
                error = $"“{part}”既不是数字也不是区间";
                return false;
            }

            if (core < 0 || core > 63)
            {
                error = $"逻辑核 {core} 超出 0-63 范围";
                return false;
            }

            mask |= 1UL << core;
            error = null;
            return true;
        }

        private static bool Validate(ulong mask, out string? error)
        {
            error = null;
            if (mask == 0)
            {
                error = "掩码不能为 0（至少选择一个逻辑核）";
                return false;
            }

            return true;
        }
    }
}
