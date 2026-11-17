using System.IO;
using Zentwik.Core.Helpers;
using Zentwik.Tests.Fakes;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>编辑已有掩码时，绑定方式也要跟着改。</summary>
    public sealed class MaskUpsertTests : IDisposable
    {
        private readonly FakeAppPaths _paths = new();
        private readonly MaskStore _store;

        public MaskUpsertTests()
        {
            _store = new MaskStore(new FakeLogService(), _paths);
            _store.Load();
        }

        public void Dispose()
        {
            if (Directory.Exists(_paths.DataDirectory))
            {
                Directory.Delete(_paths.DataDirectory, recursive: true);
            }
        }

        [Fact]
        public void Upsert_updates_bind_type_of_existing_mask()
        {
            Assert.True(_store.Upsert(
                new CustomMask { Id = "m1", Name = "大核", Mask = 0xFFUL, Type = MaskType.CpuSets },
                out _));

            Assert.True(_store.Upsert(
                new CustomMask { Id = "m1", Name = "大核", Mask = 0xFFUL, Type = MaskType.AffinityMask },
                out _));

            Assert.Equal(MaskType.AffinityMask, _store.FindById("m1")!.Type);
        }

        [Fact]
        public void Upsert_keeps_single_entry_per_id()
        {
            Assert.True(_store.Upsert(
                new CustomMask { Id = "m1", Name = "大核", Mask = 0xFFUL, Type = MaskType.CpuSets },
                out _));

            Assert.True(_store.Upsert(
                new CustomMask { Id = "m1", Name = "改名", Mask = 0x0FUL, Type = MaskType.AffinityMask },
                out _));

            Assert.Single(_store.Masks);
            Assert.Equal("改名", _store.FindById("m1")!.Name);
            Assert.Equal(0x0FUL, _store.FindById("m1")!.Mask);
        }
    }
}
