using System;

namespace Zentwik.Core.Ipc
{
    /// <summary>
    /// 管道传输层故障：连不上、超时、对端关闭、协议版本不符、消息超限。
    ///
    /// 调用方对这两类失败的处置完全不同：传输失败该重连（必要时重新拉起 Worker），
    /// 业务失败只该提示。若统一抛 IOException，调用方只能靠 catch 范围去猜，
    /// 最后的结果通常是把「参数错了」也当成「断线了」然后反复重启进程。
    /// </summary>
    public sealed class IpcTransportException : Exception
    {
        public IpcTransportException(string message)
            : base(message)
        {
        }

        public IpcTransportException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
