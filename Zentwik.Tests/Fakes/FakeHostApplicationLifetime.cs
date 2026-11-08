using System.Threading;
using Microsoft.Extensions.Hosting;

namespace Zentwik.Tests.Fakes
{
    /// <summary>
    /// 只为满足 PipeServerService 的构造注入：测试里不会真的启动 / 停止宿主。
    /// 三个令牌都用 <see cref="CancellationToken.None"/>，避免任何代码在测试中观察到取消。
    /// </summary>
    public sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }
}
