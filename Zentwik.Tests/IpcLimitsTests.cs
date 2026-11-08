using System.Text.Json;
using Xunit;
using Zentwik.Core.Ipc;
using Zentwik.Core.Serialization;

namespace Zentwik.Tests
{
    /// <summary>
    /// 单条消息上限的边界测试。
    ///
    /// 【为什么不是端到端管道测试】
    /// <see cref="WorkerPipeClient"/> 连的是固定管道名 <c>Zentwik.Worker</c>。测试进程里
    /// 再起一个同名服务端并不能保证客户端连到测试实例上（同名多实例时连接是任意的），
    /// 机器上真有 Worker 在跑时还会互相干扰 —— 那种测试会随机通过或随机失败。
    /// 这里改为验证真正决定上限的两件事：常量值，以及大 Payload 能否经源生成上下文
    /// 原样往返。服务端的读取循环只是把这个常量当容量与闸门，没有额外的 64 KB 分支。
    /// </summary>
    public sealed class IpcLimitsTests
    {
        [Fact]
        public void MaxMessageBytes_Is512Kb()
        {
            // 旧值是 64 KB，进程列表与日志快照会稳定超限。
            Assert.Equal(512 * 1024, WorkerPipeContract.MaxMessageBytes);
        }

        [Fact]
        public void LargePayload_RoundTripsThroughSourceGenContext()
        {
            // 400 KB：明显超过旧的 64 KB，又留出 JSON 转义后的余量。
            string big = new('x', 400 * 1024);

            PipeResponse original = new()
            {
                Version = WorkerPipeContract.ProtocolVersion,
                Success = true,
                Message = "ok",
                Payload = big
            };

            string json = JsonSerializer.Serialize(original, IpcJsonContext.Default.PipeResponse);

            // 确认这条消息在旧上限下会被判超限 —— 否则这个测试证明不了任何事。
            Assert.True(WorkerPipeContract.Utf8.GetByteCount(json) > 64 * 1024);
            Assert.True(WorkerPipeContract.Utf8.GetByteCount(json) <= WorkerPipeContract.MaxMessageBytes);

            PipeResponse? back = JsonSerializer.Deserialize(json, IpcJsonContext.Default.PipeResponse);

            Assert.NotNull(back);
            Assert.True(back!.Success);
            Assert.Equal(big.Length, back.Payload!.Length);
        }
    }
}
