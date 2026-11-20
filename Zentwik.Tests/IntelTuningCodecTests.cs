using System.Text.Json;
using System.Text.Json.Nodes;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;
using Zentwik.Core.Services;
using Zentwik.Worker;
using Xunit;

namespace Zentwik.Tests
{
    /// <summary>Intel 调校编解码：电压偏移走 OC Mailbox，倍频上限走 0x1AD。</summary>
    public sealed class IntelTuningCodecTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-50)]
        [InlineData(-125)]
        [InlineData(-200)]
        [InlineData(50)]
        public void Voltage_roundtrips_within_one_millivolt(int milliVolts)
        {
            foreach (IntelVoltagePlane plane in Enum.GetValues<IntelVoltagePlane>())
            {
                ulong write = IntelTuningCodec.EncodeVoltageWrite(plane, milliVolts);
                int decoded = IntelTuningCodec.DecodeVoltage(write);

                Assert.InRange(decoded, milliVolts - 1, milliVolts + 1);
            }
        }

        [Fact]
        public void Voltage_command_and_data_land_in_expected_bits()
        {
            ulong write = IntelTuningCodec.EncodeVoltageWrite(IntelVoltagePlane.Core, -50);

            // 高 32 位是命令 0x80000011；低 32 位 bit21..31 为 0x800-51=0x7CD。
            Assert.Equal(0x80000011u, (uint)(write >> 32));
            Assert.Equal(0x7CDu, (uint)((write >> 21) & 0x7FF));

            Assert.Equal(0x80000111u, (uint)(IntelTuningCodec.EncodeVoltageWrite(IntelVoltagePlane.Gpu, 0) >> 32));
            Assert.Equal(0x80000211u, (uint)(IntelTuningCodec.EncodeVoltageWrite(IntelVoltagePlane.Cache, 0) >> 32));

            // 读命令与写命令只差最低位。
            Assert.Equal(0x80000010u, (uint)(IntelTuningCodec.EncodeVoltageRead(IntelVoltagePlane.Core) >> 32));
            Assert.Equal(0u, (uint)IntelTuningCodec.EncodeVoltageRead(IntelVoltagePlane.Core));
        }

        [Fact]
        public void Voltage_field_is_sign_extended_eleven_bits()
        {
            // 0 表示无偏移；0x400 是 11 位符号位，落成 -1024 个步进即 -1000 mV。
            Assert.Equal(0, IntelTuningCodec.DecodeVoltage(0));
            Assert.Equal(-1000, IntelTuningCodec.DecodeVoltage(0x400UL << 21));
            Assert.Equal(-50, IntelTuningCodec.DecodeVoltage(0x7CDUL << 21));
        }

        [Fact]
        public void Ratios_pack_single_core_into_lowest_byte()
        {
            // 0x1AD 的 bits 7:0 是「1 个活跃核心」，bits 63:56 是 8 个。
            ulong value = IntelTuningCodec.EncodeRatios([45, 44, 43, 42, 41, 40, 39, 38]);

            Assert.Equal(45, (int)(value & 0xFF));
            Assert.Equal(38, (int)((value >> 56) & 0xFF));

            int[] decoded = IntelTuningCodec.DecodeRatios(value);
            Assert.Equal([45, 44, 43, 42, 41, 40, 39, 38], decoded);
        }

        [Fact]
        public void Ratios_out_of_range_are_clamped_not_wrapped()
        {
            int[] decoded = IntelTuningCodec.DecodeRatios(IntelTuningCodec.EncodeRatios([300, -1, 0, 0, 0, 0, 0, 0]));

            Assert.Equal(255, decoded[0]);
            Assert.Equal(0, decoded[1]);
        }

        [Fact]
        public void Snapshot_maps_to_payload_without_flattening_availability()
        {
            IntelTuningSnapshot snapshot = new()
            {
                Success = true,
                CoreMilliVolts = -75,
                GpuMilliVolts = null,
                CacheMilliVolts = -50,
                Ratios = [45, 44, 43, 42, 41, 40, 39, 38]
            };

            PipeResponse response = PipeServerService.BuildIntelTuningResponse(snapshot);

            Assert.True(response.Success);
            Assert.Equal(WorkerPipeContract.ProtocolVersion, response.Version);

            IntelTuningPayload payload = JsonSerializer.Deserialize(
                response.Payload!,
                IpcJsonContext.Default.IntelTuningPayload)!;

            Assert.True(payload.Available);
            Assert.Equal(-75, payload.CoreMilliVolts);
            Assert.Null(payload.GpuMilliVolts);
            Assert.Equal(-50, payload.CacheMilliVolts);
            Assert.Equal(8, payload.Ratios!.Length);
            Assert.Equal(45, payload.Ratios[0]);
        }

        [Fact]
        public void Failed_snapshot_keeps_payload_and_reports_reason()
        {
            PipeResponse response = PipeServerService.BuildIntelTuningResponse(
                new IntelTuningSnapshot { Success = false, Error = "无法读取 MSR 0x150 与 0x1AD" });

            Assert.False(response.Success);
            Assert.Contains("0x150", response.Message);

            JsonNode payload = JsonNode.Parse(response.Payload!)!;
            Assert.False(payload["available"]!.GetValue<bool>());
            Assert.Null(payload["ratios"]);
        }
    }
}
