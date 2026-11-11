using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zentwik.Core.Ipc
{
    /// <summary>规则集的稳定指纹。两端各算一遍，相等即视为同一份规则。</summary>
    public static class ScheduleRuleDigest
    {
        /// <summary>FNV-1a 64，输出 16 位十六进制。无反射，AOT 安全。</summary>
        public static string Of(IEnumerable<ScheduleRulePayload>? rules)
        {
            List<string> parts = new();

            foreach (ScheduleRulePayload rule in rules ?? Array.Empty<ScheduleRulePayload>())
            {
                parts.Add(string.Join(
                    '|',
                    (rule.ProcessName ?? string.Empty).Trim().ToLowerInvariant(),
                    rule.TargetType ?? "none",
                    rule.TargetValue ?? string.Empty,
                    rule.Priority ?? string.Empty,
                    rule.Enabled ? "1" : "0"));
            }

            // 规则顺序在存储里不稳定，先排序再算。
            parts.Sort(StringComparer.Ordinal);

            ulong hash = 14695981039346656037UL;

            foreach (string part in parts)
            {
                foreach (char c in part)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }

                hash ^= '\n';
                hash *= 1099511628211UL;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
