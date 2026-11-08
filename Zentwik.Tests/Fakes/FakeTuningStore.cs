using Zentwik.Core.Helpers;

namespace Zentwik.Tests.Fakes
{
    /// <summary>内存版 ITuningStore：Load 不动内存，Save 记录最后一次写入供断言。</summary>
    public sealed class FakeTuningStore : ITuningStore
    {
        public TuningProfile Profile { get; private set; } = new();

        /// <summary>最后一次 Save 的内容；null 表示还没保存过。</summary>
        public TuningProfile? LastSaved { get; private set; }

        /// <summary>Load 被调用的次数。用来验证「应用前从盘上重读」确实发生了。</summary>
        public int LoadCount { get; private set; }

        public void Load() => LoadCount++;

        public void Save(TuningProfile profile)
        {
            Profile = profile;
            LastSaved = profile;
        }

        public uint ResetToFactory() => 0;
    }
}
