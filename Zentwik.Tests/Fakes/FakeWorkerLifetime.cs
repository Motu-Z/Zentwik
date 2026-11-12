using System.Threading;
using Zentwik.Worker;

namespace Zentwik.Tests.Fakes
{
    /// <summary>只为满足 PipeServerService 的构造注入；令牌永不取消，测试里不会真的停宿主。</summary>
    public sealed class FakeWorkerLifetime : IWorkerLifetime
    {
        public CancellationToken StoppingToken => CancellationToken.None;

        public void Stop() { }
    }
}
