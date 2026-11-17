using System;
using System.Collections.Generic;

namespace Zentwik.Core.Helpers
{
    /// <summary>掩码下拉项参与解析的四个字段。</summary>
    public interface IMaskEntry
    {
        string Key { get; }

        string TargetType { get; }

        string TargetValue { get; }

        string? MaskId { get; }
    }

    /// <summary>掩码下拉项的查找与规则目标换算。</summary>
    public static class MaskResolver
    {
        /// <summary>下拉项唯一键。自定义掩码用 Id，其余用值。</summary>
        public static string MakeKey(string targetType, string targetValue) => targetType + "|" + targetValue;

        /// <summary>按键命中选项。</summary>
        public static T? Find<T>(IEnumerable<T> options, string? key) where T : class, IMaskEntry
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            foreach (T option in options)
            {
                if (string.Equals(option.Key, key, StringComparison.Ordinal))
                {
                    return option;
                }
            }

            return null;
        }

        /// <summary>
        /// 规则目标换算成掩码 Id：先认界面选中的键，再按值命中，最后把值当成 Id（旧规则）。
        /// 三者都不中返回 null。
        /// </summary>
        public static string? ResolveMaskId<T>(IEnumerable<T> options, string? key, string? targetValue)
            where T : class, IMaskEntry
        {
            if (Find(options, key)?.MaskId is { } byKey)
            {
                return byKey;
            }

            foreach (T option in options)
            {
                if (option.TargetType != "mask" || option.MaskId is null)
                {
                    continue;
                }

                if (string.Equals(option.TargetValue, targetValue, StringComparison.Ordinal)
                    || string.Equals(option.MaskId, targetValue, StringComparison.Ordinal))
                {
                    return option.MaskId;
                }
            }

            return null;
        }
    }
}
