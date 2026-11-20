using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using Zentwik.Core.Helpers;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;

namespace Zentwik.Tests
{
    /// <summary>
    /// 128 位掩码在真实序列化上下文里的形状。
    /// 走源生成的 AppJsonContext / IpcJsonContext，而不是默认 options ——
    /// 线格式与落盘格式的兼容性只在这两个上下文里成立。
    /// </summary>
    public sealed class CoreMaskWireFormatTests
    {
        [Fact]
        public void Ipc_mask_list_writes_hex_and_reads_it_back()
        {
            MaskListPayload payload = new()
            {
                Limit = 10,
                Masks = new List<MaskEntryPayload>
                {
                    new()
                    {
                        Id = "m1",
                        Name = "跨组",
                        Mask = new CoreMask(0xFUL, 0x1UL),
                        CoreCount = 5
                    }
                }
            };

            string json = JsonSerializer.Serialize(payload, IpcJsonContext.Default.MaskListPayload);

            Assert.Contains("\"0x0000000000000001000000000000000F\"", json);

            MaskListPayload? back = JsonSerializer.Deserialize(json, IpcJsonContext.Default.MaskListPayload);

            Assert.NotNull(back);
            Assert.Equal(0xFUL, back!.Masks[0].Mask.Low);
            Assert.Equal(1UL, back.Masks[0].Mask.High);
        }

        [Fact]
        public void Save_mask_payload_accepts_both_hex_and_legacy_number()
        {
            SaveMaskPayload? hex = JsonSerializer.Deserialize(
                "{\"name\":\"a\",\"mask\":\"0x1F\",\"type\":\"cpuSets\"}",
                IpcJsonContext.Default.SaveMaskPayload);

            SaveMaskPayload? legacy = JsonSerializer.Deserialize(
                "{\"name\":\"a\",\"mask\":31}",
                IpcJsonContext.Default.SaveMaskPayload);

            Assert.NotNull(hex);
            Assert.NotNull(legacy);
            Assert.Equal(0x1FUL, hex!.Mask.Low);
            Assert.Equal(hex.Mask, legacy!.Mask);
        }

        [Fact]
        public void Stored_masks_file_reads_legacy_numbers_and_writes_hex()
        {
            // 老版本落盘的就是数字，读不进来等于用户升级后掩码全丢。
            const string legacy = "[{\"Id\":\"a\",\"Name\":\"CCD0\",\"Mask\":65535,\"Preset\":true}]";

            List<CustomMask>? masks = JsonSerializer.Deserialize(legacy, AppJsonContext.Default.ListCustomMask);

            Assert.NotNull(masks);
            Assert.Equal(0xFFFFUL, masks![0].Mask.Low);

            string json = JsonSerializer.Serialize(masks, AppJsonContext.Default.ListCustomMask);

            Assert.Contains("\"0xFFFF\"", json);
        }
    }
}
