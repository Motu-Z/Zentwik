using System;
using System.Threading;
using System.Threading.Tasks;

namespace Zentwik.Worker
{
    /// <summary>Worker 的可启停阶段：按注册顺序启动，逆序停止。</summary>
    internal interface IWorkerService
    {
        Task StartAsync(CancellationToken cancellationToken);

        Task StopAsync(CancellationToken cancellationToken);
    }

    /// <summary>Worker 停止信号，由管道 shutdown 命令触发。</summary>
    internal interface IWorkerLifetime
    {
        CancellationToken StoppingToken { get; }

        void Stop();
    }

    internal sealed class WorkerLifetime : IWorkerLifetime, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken StoppingToken => _cts.Token;

        public void Stop()
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();
        }

        public void Dispose() => _cts.Dispose();
    }
}
