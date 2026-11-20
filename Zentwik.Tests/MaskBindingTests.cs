using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Services;

namespace Zentwik.Tests
{
    /// <summary>掩码跨处理器组时，绑定层必须明确拒绝而不是截断。</summary>
    public sealed class MaskBindingTests
    {
        [Fact]
        public void Low_bits_convert_as_is()
        {
            Assert.True(MaskBinding.TryToGroupMask(new CoreMask(0xFF00UL), out ulong groupMask));
            Assert.Equal(0xFF00UL, groupMask);
        }

        [Fact]
        public void High_bits_are_rejected_not_truncated()
        {
            Assert.False(MaskBinding.TryToGroupMask(new CoreMask(0xFFUL, 0x1UL), out ulong groupMask));
            Assert.Equal(0xFFUL, groupMask);
            Assert.False(string.IsNullOrEmpty(MaskBinding.HighBitsMessage));
        }
    }
}
