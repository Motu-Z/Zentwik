using System.Collections.Generic;
using Xunit;
using Zentwik.Core.Helpers;

namespace Zentwik.Tests
{
    /// <summary>下拉掩码项的查找与规则目标换算。</summary>
    public sealed class MaskResolverTests
    {
        private sealed class Entry : IMaskEntry
        {
            public string Key { get; set; } = string.Empty;

            public string TargetType { get; set; } = "mask";

            public string TargetValue { get; set; } = string.Empty;

            public string? MaskId { get; set; }
        }

        private static Entry Make(string targetType, string targetValue, string? maskId)
            => new()
            {
                TargetType = targetType,
                TargetValue = targetValue,
                MaskId = maskId,
                Key = MaskResolver.MakeKey(targetType, maskId ?? targetValue)
            };

        [Fact]
        public void Find_hits_by_key()
        {
            Entry a = Make("mask", "255", "a");
            Entry b = Make("mask", "65280", "b");
            List<Entry> options = new() { Make("none", string.Empty, null), a, b };

            Assert.Same(b, MaskResolver.Find(options, b.Key));
        }

        [Fact]
        public void Find_empty_key_returns_null()
        {
            List<Entry> options = new() { Make("mask", "255", "a") };

            Assert.Null(MaskResolver.Find(options, null));
            Assert.Null(MaskResolver.Find(options, string.Empty));
        }

        /// <summary>规则里存的是掩码位（2.4.0 之前的格式），要按值换成 Id。</summary>
        [Fact]
        public void Resolve_falls_back_to_target_value()
        {
            List<Entry> options = new() { Make("mask", "255", "abc") };

            Assert.Equal("abc", MaskResolver.ResolveMaskId(options, "mask|255", "255"));
        }

        /// <summary>目标值本身就是 Id 时（旧规则被新版本写过），按 Id 命中。</summary>
        [Fact]
        public void Resolve_falls_back_to_mask_id()
        {
            List<Entry> options = new() { Make("mask", "255", "abc") };

            Assert.Equal("abc", MaskResolver.ResolveMaskId(options, null, "abc"));
        }

        [Fact]
        public void Resolve_unknown_returns_null()
        {
            List<Entry> options = new() { Make("mask", "255", "abc") };

            Assert.Null(MaskResolver.ResolveMaskId(options, "mask|zzz", "zzz"));
            Assert.Null(MaskResolver.ResolveMaskId(new List<Entry>(), "mask|255", "255"));
        }

        /// <summary>位值相同、Id 不同的两个掩码（换绑定方式）不能互相顶掉 —— 2.4.0 修过的 bug。</summary>
        [Fact]
        public void Same_bits_with_different_ids_stay_distinct()
        {
            Entry cs = Make("mask", "255", "cs-id");
            Entry am = Make("mask", "255", "am-id");
            List<Entry> options = new() { cs, am };

            Assert.NotEqual(cs.Key, am.Key);
            Assert.Same(cs, MaskResolver.Find(options, cs.Key));
            Assert.Same(am, MaskResolver.Find(options, am.Key));
            Assert.Equal("cs-id", MaskResolver.ResolveMaskId(options, cs.Key, "255"));
            Assert.Equal("am-id", MaskResolver.ResolveMaskId(options, am.Key, "255"));
        }

        /// <summary>非掩码项（默认 / CCD）没有 Id，命中键也换不出 Id。</summary>
        [Fact]
        public void Non_mask_entry_has_no_mask_id()
        {
            List<Entry> options = new() { Make("none", string.Empty, null) };

            Assert.NotNull(MaskResolver.Find(options, MaskResolver.MakeKey("none", string.Empty)));
            Assert.Null(MaskResolver.ResolveMaskId(options, MaskResolver.MakeKey("none", string.Empty), string.Empty));
        }
    }
}
