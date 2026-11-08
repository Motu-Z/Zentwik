using System;
using System.Collections.Generic;
using System.Linq;
using Zentwik.Core.Helpers;

namespace Zentwik.Tests.Fakes
{
    /// <summary>
    /// 内存版 IMaskStore。
    ///
    /// 只实现 <c>FindById</c> 与 <c>Upsert</c> 的真实语义：分派层测 save-scheduling-rule 时，
    /// 要验证的是「掩码 Id 能不能解析成掩码值」，不是 MaskStore 自己的落盘逻辑
    /// （那部分由它自己的测试与 AotProbe 覆盖）。
    /// </summary>
    public sealed class FakeMaskStore : IMaskStore
    {
        private readonly List<CustomMask> _masks = new();

        public IReadOnlyList<CustomMask> Masks => _masks;

        public bool IsFull => _masks.Count >= MaskStore.MaxCount;

        public event Action? Changed { add { } remove { } }

        public IEnumerable<string> NamesExceptId(string? excludeId) =>
            _masks
                .Where(m => excludeId is null || !string.Equals(m.Id, excludeId, StringComparison.Ordinal))
                .Select(m => m.Name);

        public void Load() { }

        public void ResetToFactory() => _masks.Clear();

        public bool Upsert(CustomMask mask, out MaskStore.UpsertError error)
        {
            error = default;

            if (string.IsNullOrWhiteSpace(mask.Name))
            {
                error = MaskStore.UpsertError.BadName;
                return false;
            }

            int index = _masks.FindIndex(m => string.Equals(m.Id, mask.Id, StringComparison.Ordinal));
            if (index >= 0)
                _masks[index] = mask;
            else
                _masks.Add(mask);

            return true;
        }

        public void Remove(string id) => _masks.RemoveAll(m => m.Id == id);

        public CustomMask? FindById(string id) =>
            _masks.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

        public CustomMask? FindByName(string name) =>
            _masks.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>测试用：直接塞一条掩码进去。</summary>
        public CustomMask Seed(string name, ulong mask)
        {
            CustomMask created = new() { Name = name, Mask = mask };
            _masks.Add(created);
            return created;
        }
    }
}
