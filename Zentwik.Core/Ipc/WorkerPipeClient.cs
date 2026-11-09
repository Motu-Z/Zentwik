using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zentwik.Core.Serialization;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 管道客户端：UI 进程用它连上 Worker 并下发命令。
    ///
    /// 服务端在 Worker 进程内实现。两端共享的只有 <see cref="WorkerPipeContract"/> 与
    /// 三个消息类型，任何一端都没有对方的实现引用 —— 这是两个独立 exe 之间唯一正确的耦合方式。
    ///
    /// 管道是字节流，没有请求 ID 可以配对响应。并发发送会让两个响应的字节交错，
    /// 解析出的是两段各自都不合法的 JSON。因此用一个信号量把发送串行化：
    /// 同一时刻只允许一个请求在飞。控制类命令的频率本就极低，串行没有性能问题。
    ///
    /// 连不上 / 超时 / 对端关闭 / 版本不符 / 超限 一律抛 <see cref="IpcTransportException"/>，
    /// 绝不返回 null 让调用方去猜。任何一次传输失败之后本实例的连接都不可信，
    /// 内部会立即丢弃流状态，调用方必须重新 <see cref="ConnectAsync"/>。
    /// </summary>
    public sealed class WorkerPipeClient : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        private NamedPipeClientStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private int _disposed;

        /// <summary>当前是否持有一条已连通的管道。不含探测：返回 true 也可能下一秒就被对端关掉。</summary>
        public bool IsConnected
        {
            get
            {
                NamedPipeClientStream? stream = _stream;
                return stream is not null && stream.IsConnected;
            }
        }

        /// <summary>
        /// 连接 Worker 管道。已连接时直接返回（幂等）。
        /// </summary>
        /// <param name="timeout">连接超时。Worker 未启动时 <c>ConnectAsync</c> 会一直等下去。</param>
        /// <param name="cancellationToken">外部取消。只有它被触发才按取消处理，其余中断都算超时。</param>
        public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (IsConnected)
                    return;

                await DisposeStreamAsync().ConfigureAwait(false);

                NamedPipeClientStream stream = new(
                    ".",
                    WorkerPipeContract.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    await stream.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);

                    // 外部取消按取消语义原样传播，只有内部超时才折算成传输异常。
                    if (cancellationToken.IsCancellationRequested)
                        throw;

                    throw new IpcTransportException($"连接 Worker 管道超时（{timeout.TotalMilliseconds:F0} ms）。", ex);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw new IpcTransportException($"连接 Worker 管道失败：{ex.Message}", ex);
                }
                catch
                {
                    // stream 还是局部变量，字段没接手，漏掉就是一条泄漏的管道句柄。
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                // 两个包装器共享同一条流，leaveOpen 让流的生命周期归本类管。
                // bufferSize 给 1024 就够：单条消息走一次 WriteLine + Flush，不需要大缓冲。
                _stream = stream;
                _reader = new StreamReader(
                    stream,
                    WorkerPipeContract.Utf8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                _writer = new StreamWriter(
                    stream,
                    WorkerPipeContract.Utf8,
                    bufferSize: 1024,
                    leaveOpen: true);
            }
            finally
            {
                ReleaseGate();
            }
        }

        /// <summary>
        /// 发送一条请求并等待对应响应。
        /// </summary>
        /// <param name="request">请求体。<see cref="PipeRequest.Version"/> 会被原样发出。</param>
        /// <param name="timeout">从写入到读完整条响应的总超时。</param>
        /// <param name="cancellationToken">外部取消。</param>
        public async Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // 版本协商第 1 关：请求自带的版本先就地校验。
            // 必然失败的调用不该占用信号量，也不该让对端白跑一次拒绝。
            if (!WorkerPipeContract.ValidateVersion(request.Version, out _))
                return WorkerPipeContract.VersionMismatchResponse();

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                StreamReader? reader = _reader;
                StreamWriter? writer = _writer;

                if (!IsConnected || reader is null || writer is null)
                    throw new IpcTransportException("与 Worker 的管道尚未连接。");

                // 必须走源生成的上下文重载：反射式 Serialize(object) 在 AOT 裁剪后
                // 直接抛异常，且构建期会报 IL2026 / IL3050。
                string json = JsonSerializer.Serialize(request, IpcJsonContext.Default.PipeRequest);

                if (WorkerPipeContract.Utf8.GetByteCount(json) > WorkerPipeContract.MaxMessageBytes)
                    throw new IpcTransportException($"请求超过单条上限 {WorkerPipeContract.MaxMessageBytes} 字节。");

                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    await writer.WriteLineAsync(json.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                    await writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

                    string? line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);

                    if (line is null)
                        throw new IpcTransportException("Worker 关闭了管道连接。");

                    // 上限按字节算：line.Length 是 UTF-16 字符数，中文一字三字节会漏判。
                    // ReadLineAsync 已经把整行读进内存，这里只能事后拒绝。
                    if (WorkerPipeContract.Utf8.GetByteCount(line) > WorkerPipeContract.MaxMessageBytes)
                        throw new IpcTransportException($"响应超过单条上限 {WorkerPipeContract.MaxMessageBytes} 字节。");

                    PipeResponse? response = JsonSerializer.Deserialize(line, IpcJsonContext.Default.PipeResponse);
                    if (response is null)
                        throw new IpcTransportException("Worker 返回了空响应。");

                    // 版本协商第 2 关：对端回填的版本不匹配时，不抛异常、也不透传对端的响应，
                    // 直接换成本端构造的标准失败响应。调用方只判断 Success，不需要 catch。
                    // 连接不断开：版本不符是部署问题，重连多少次结果都一样，退不退出由调用方决定。
                    if (!WorkerPipeContract.ValidateVersion(response.Version, out _))
                        return WorkerPipeContract.VersionMismatchResponse();

                    return response;
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    await DisposeStreamAsync().ConfigureAwait(false);
                    throw new IpcTransportException($"等待 Worker 响应超时（{timeout.TotalMilliseconds:F0} ms）。", ex);
                }
                catch (JsonException ex)
                {
                    await DisposeStreamAsync().ConfigureAwait(false);
                    throw new IpcTransportException($"Worker 响应不是合法 JSON：{ex.Message}", ex);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    await DisposeStreamAsync().ConfigureAwait(false);
                    throw new IpcTransportException($"与 Worker 的通信中断：{ex.Message}", ex);
                }
                catch (OperationCanceledException)
                {
                    // 外部取消：连接可能停在写了半行 JSON 的状态，必须丢弃。
                    await DisposeStreamAsync().ConfigureAwait(false);
                    throw;
                }
                catch
                {
                    // 任何一次传输失败之后，本实例的连接都不可再复用。
                    await DisposeStreamAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                ReleaseGate();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _gate.WaitAsync().ConfigureAwait(false);

            try
            {
                await DisposeStreamAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseGate();
            }

            _gate.Dispose();
        }

        /// <summary>
        /// 归还发送信号量 —— <b>所有</b>获取路径的唯一归还点。
        ///
        /// <see cref="DisposeAsync"/> 结束时会把信号量释放掉，而另一个调用方可能已通过
        /// <c>_disposed</c> 检查、正停在 finally 里等待归还。裸 <c>Release()</c> 在已释放的
        /// 信号量上抛 <see cref="ObjectDisposedException"/>，而且是从 finally 抛出 ——
        /// 它会顶掉原始异常，让调用方拿到一个与被放弃对象无关的崩溃。
        ///
        /// <see cref="ConnectAsync"/>、<see cref="SendAsync"/>、<see cref="DisposeAsync"/>
        /// 三个方法的 finally 全部走这里：超时、对端关闭、JSON 非法、外部取消、
        /// 对象已释放，任意一条异常路径都不会漏掉归还，也就不会把后续请求永久卡在
        /// <c>WaitAsync</c> 上（UI 侧表现为「Ping 一次超时之后再也不响应」）。
        ///
        /// 这条分支意味着「<see cref="DisposeAsync"/> 在本方法持有者仍在飞的时候就走完了」，
        /// 属于生命周期顺序问题 —— 吞掉它就等于把这类 bug 永久藏起来。
        /// 但本类不持有日志服务（Core 的 Ipc 层刻意不依赖 Services 层），
        /// 因此只能落到 <see cref="System.Diagnostics.Debug"/>：Debug 构建可见、
        /// Release 构建被 <c>[Conditional("DEBUG")]</c> 整体移除，不产生任何运行时开销。
        /// 若将来需要生产可见性，应由宿主注入日志回调，而不是在这里 new 一个日志器。
        /// </summary>
        private void ReleaseGate()
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException ex)
            {
                // 对象已释放，信号量随之消失，无需归还；但留痕以便排查生命周期顺序问题。
                System.Diagnostics.Debug.WriteLine(
                    $"[WorkerPipeClient] 信号量已释放后才归还，说明 DisposeAsync 与在途调用重叠：{ex.Message}");
            }
        }

        /// <summary>
        /// 丢弃当前连接。写侧先尽量冲刷一次 —— 半条消息留在管道里会让对端的
        /// 下一行解析永远错位，而那已经是另一个连接的事了，冲刷只是减少对端的噪音。
        /// </summary>
        private async ValueTask DisposeStreamAsync()
        {
            StreamReader? reader = _reader;
            StreamWriter? writer = _writer;
            NamedPipeClientStream? stream = _stream;

            _reader = null;
            _writer = null;
            _stream = null;

            if (writer is not null)
            {
                try
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // 连接已断，冲刷失败不影响释放
                }

                writer.Dispose();
            }

            reader?.Dispose();

            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
