namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 一条管道响应。
    ///
    /// 「命令执行失败」是业务结果（例如 shutdown 被拒绝、载荷格式不对），
    /// 它必须能被记录、被展示、被重试；而「管道断了」才是传输层故障，
    /// 那一种由 <see cref="IpcTransportException"/> 抛出。两者混在一起，
    /// 调用方就分不清该重连还是该改参数。
    ///
    /// 创建本类型实例时**必须**显式赋值
    /// <c>Version = WorkerPipeContract.ProtocolVersion</c>，不能依赖默认值。
    /// 默认值 0 会被 <see cref="WorkerPipeContract.ValidateVersion"/> 判定为版本不一致。
    /// 服务端还有一条附加约束：<c>Version</c> 必须填**本端**已知的协议版本，
    /// 不得把请求里收到的版本号原样透传 —— 透传会把「对端版本不对」这件事
    /// 伪装成「版本一致」，协商逻辑就此失效。
    /// </summary>
    public sealed class PipeResponse
    {
        /// <summary>
        /// 协议版本。与请求版本不一致时客户端必须拒绝这条响应。
        ///
        /// 【默认值为 0，表示「未赋值」】与 <see cref="PipeRequest.Version"/> 同一条理由：
        /// 反序列化时 JSON 缺字段会保留初始化器值，默认给合法版本号等于
        /// 悄悄替对端补上声明，让「字段缺失」这种情况在客户端完全不可见。
        /// 用 0 之后，服务端忘记回填版本会被客户端当成版本不符直接拒绝 ——
        /// 故障在第一次联调就暴露，而不是等到用户环境里出现无法解释的空响应。
        /// </summary>
        public int Version { get; set; }

        /// <summary>命令是否执行成功。</summary>
        public bool Success { get; set; }

        /// <summary>给人看的一句话结果。成功时可为空，失败时必须说明原因。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 命令载荷。ping 返回 <c>pong</c>，其余命令按需填充。
        ///
        /// 本机进程间通信、同版本强对齐，无跨语言 / 跨版本兼容压力。
        /// 载荷内容由 <c>Command</c> 决定，两端各自按命令字解析这段字符串。
        /// </summary>
        public string? Payload { get; set; }
    }
}
