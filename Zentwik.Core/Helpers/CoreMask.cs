using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// 逻辑处理器掩码，128 位。位 i = 逻辑处理器 i。
    ///
    /// 表达层（存储 / 协议 / 界面 / 文本解析）用这个类型；
    /// 绑定层（CPU Sets / Affinity 写入）仍是单个 ulong —— 一次写入只覆盖一个处理器组，
    /// 跨组绑定是另一件事，靠 <see cref="HasHighBits"/> 在调用处显式拒绝，不做静默截断。
    ///
    /// 与 ulong 之间只提供**隐式进入**：把窄类型当宽类型用永远安全。
    /// 反方向必须写 <see cref="Low"/>，强制调用方交代高位怎么处理。
    /// </summary>
    [JsonConverter(typeof(CoreMaskJsonConverter))]
    public readonly struct CoreMask : IEquatable<CoreMask>
    {
        /// <summary>可表达的逻辑处理器上限。</summary>
        public const int Width = 128;

        private readonly ulong _low;
        private readonly ulong _high;

        public CoreMask(ulong low, ulong high = 0)
        {
            _low = low;
            _high = high;
        }

        public static CoreMask Zero => default;

        /// <summary>位 0-63，即处理器组 0 内可直接写入的那部分。</summary>
        public ulong Low => _low;

        /// <summary>位 64-127。</summary>
        public ulong High => _high;

        public bool IsZero => (_low | _high) == 0;

        /// <summary>有 64 号以上的位。绑定层只能处理组 0，遇到它必须拒绝。</summary>
        public bool HasHighBits => _high != 0;

        public int PopCount => BitOperations.PopCount(_low) + BitOperations.PopCount(_high);

        /// <summary>最低置位号，空掩码返回 -1。</summary>
        public int FirstBit
        {
            get
            {
                if (_low != 0)
                    return BitOperations.TrailingZeroCount(_low);

                return _high != 0 ? 64 + BitOperations.TrailingZeroCount(_high) : -1;
            }
        }

        public bool this[int bit] => bit switch
        {
            >= 0 and < 64 => (_low & (1UL << bit)) != 0,
            >= 64 and < Width => (_high & (1UL << (bit - 64))) != 0,
            _ => false
        };

        /// <summary>置上一位，返回新值（结构体不可变）。</summary>
        public CoreMask With(int bit) => bit switch
        {
            >= 0 and < 64 => new CoreMask(_low | (1UL << bit), _high),
            >= 64 and < Width => new CoreMask(_low, _high | (1UL << (bit - 64))),
            _ => this
        };

        /// <summary>逐位升序枚举置位号。</summary>
        public IEnumerable<int> Bits()
        {
            ulong low = _low;

            while (low != 0)
            {
                int bit = BitOperations.TrailingZeroCount(low);
                yield return bit;
                low &= low - 1;
            }

            ulong high = _high;

            while (high != 0)
            {
                int bit = BitOperations.TrailingZeroCount(high);
                yield return 64 + bit;
                high &= high - 1;
            }
        }

        public static CoreMask FromBits(IEnumerable<int> bits)
        {
            CoreMask mask = default;

            foreach (int bit in bits)
                mask = mask.With(bit);

            return mask;
        }

        /// <summary>位 0 起的连续区间，超出 128 位的部分忽略。</summary>
        public static CoreMask FromRange(int from, int to)
        {
            CoreMask mask = default;

            for (int bit = Math.Max(0, from); bit <= to && bit < Width; bit++)
                mask = mask.With(bit);

            return mask;
        }

        /// <summary>
        /// 处理器组内的相对掩码 → 全局位。Windows 的 GROUP_AFFINITY 每组都从位 0 起，
        /// 不换算就会让第 65 个处理器与第 1 个撞在同一位上。
        /// 超出 <see cref="Width"/> 的组返回空掩码，由调用方决定是否告警。
        /// </summary>
        public static CoreMask FromGroupMask(int groupNumber, ulong groupMask) => groupNumber switch
        {
            0 => new CoreMask(groupMask, 0),
            1 => new CoreMask(0, groupMask),
            _ => Zero
        };

        public static CoreMask operator |(CoreMask left, CoreMask right) =>
            new(left._low | right._low, left._high | right._high);

        public static CoreMask operator &(CoreMask left, CoreMask right) =>
            new(left._low & right._low, left._high & right._high);

        public static CoreMask operator ^(CoreMask left, CoreMask right) =>
            new(left._low ^ right._low, left._high ^ right._high);

        public static CoreMask operator ~(CoreMask value) =>
            new(~value._low, ~value._high);

        public static implicit operator CoreMask(ulong value) => new(value);

        public static bool operator ==(CoreMask left, CoreMask right) => left.Equals(right);

        public static bool operator !=(CoreMask left, CoreMask right) => !left.Equals(right);

        public bool Equals(CoreMask other) => _low == other._low && _high == other._high;

        public override bool Equals(object? obj) => obj is CoreMask other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(_low, _high);

        /// <summary>十六进制形式，便于日志与悬停提示；界面上仍用区间文本。</summary>
        public override string ToString() => _high == 0 ? $"0x{_low:X}" : $"0x{_high:X16}{_low:X16}";
    }

    /// <summary>
    /// 线格式与落盘格式：十六进制字符串（<c>"0x1F"</c>）。
    ///
    /// 不用数字：128 位十进制既超出 JSON 常见精度，也让手改配置的人难核对。
    /// 读的时候同时接受数字，老数据（掩码存的是 ulong）不必迁移。
    /// </summary>
    public sealed class CoreMaskJsonConverter : JsonConverter<CoreMask>
    {
        public override CoreMask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string? text = reader.GetString();

                if (string.IsNullOrWhiteSpace(text))
                    return CoreMask.Zero;

                return MaskFormatter.TryParseHexToken(text, out CoreMask parsed) ? parsed : CoreMask.Zero;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out ulong value))
                return value;

            return CoreMask.Zero;
        }

        public override void Write(Utf8JsonWriter writer, CoreMask value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
