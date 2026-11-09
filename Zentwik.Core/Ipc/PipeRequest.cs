namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 一条管道请求。字段刻意保持三个：协议只有一个版本、一条命令、一段可选载荷。
    ///
    /// 加字段之前先想清楚「这是协议扩展还是要塞业务数据」：
    /// 业务数据该进 <see cref="Payload"/> 并自带序列化类型，
    /// 而不是给每个新功能在请求体上开一个平铺字段 —— 那样两端字段集合会长期对不齐。
    ///
    /// 创建本类型实例时**必须**显式赋值 <c>Version = WorkerPipeContract.ProtocolVersion</c>，
    /// 不能依赖默认值。默认值 0 会被 <see cref="WorkerPipeContract.ValidateVersion"/>
    /// 判定为版本不一致。
    /// 漏赋值不会编译失败，也不会抛异常：对端只回一句
    /// <see cref="WorkerPipeContract.VersionMismatchMessage"/>。这条文案指向
    /// 「装错了 exe」，与真实原因（构造代码少写一个初始化器）毫无关系 ——
    /// 排查时会先怀疑新旧版本混装，再怀疑协议，最后才轮到构造代码。
    /// 因此 <c>new PipeRequest { Command = ... }</c> 一律视为缺陷，
    /// 不是「能用就行」的简写。
    /// </summary>
    public sealed class PipeRequest
    {
        /// <summary>
        /// 协议版本。<b>默认值为 0，表示「未赋值」</b>，由调用方显式填
        /// <see cref="WorkerPipeContract.ProtocolVersion"/>。
        ///
        /// 默认成合法版本号会让两个不同的事实变得不可区分：
        /// ①「调用方明确声明了版本 1」；②「调用方根本没填，字段缺失」。
        /// 而 System.Text.Json 反序列化时，JSON 里缺字段会保留初始化器的值 ——
        /// 于是版本协商里「缺失也必须拒绝」这条规则永远无法落地。
        /// 用 0 作哨兵值后，<see cref="WorkerPipeContract.ValidateVersion"/> 对
        /// 「缺失」与「非法」给出同一个结论，两条规则合成一条判定。
        ///
        /// 代价是 <c>new PipeRequest { Command = "ping" }</c> 不再自动带版本，
        /// 必然在 <see cref="WorkerPipeClient.SendAsync"/> 第一关被判为版本不符。
        /// 这是刻意的：版本号属于协议声明，不该由默认值代办。
        /// </summary>
        public int Version { get; set; }

        /// <summary>命令字。取值见 <see cref="WorkerPipeContract"/> 的 Command* 常量。</summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// 命令载荷。无参数的命令（ping / shutdown）留空。
        ///
        /// 本协议只跑在本机两个进程之间，两个 exe 由同一次发布产出、版本由
        /// <see cref="WorkerPipeContract.ProtocolVersion"/> 强制对齐，不存在
        /// 「对端是未知语言 / 未知版本」的情况。用弱类型节点只会把解析责任推给运行时，
        /// 还多一层序列化开销；用 <c>object</c> 更糟 —— 它在 AOT 下等于放弃源生成。
        /// 载荷的具体形状由 <see cref="Command"/> 分支自行解释。
        /// </summary>
        public string? Payload { get; set; }
    }
}
