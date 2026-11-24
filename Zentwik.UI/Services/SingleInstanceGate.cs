using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using Zentwik.Core.Ipc;

namespace Zentwik.UI.Services;

/// <summary>
/// 单实例闸门：第二个进程把「显示窗口」转给第一个进程后立刻退出。
/// 桌面右键与快捷方式都可能被连点，没有它就会开出两个托盘图标抢同一个后台服务。
/// </summary>
public static class SingleInstanceGate
{
    /// <summary>按会话隔离：远程桌面另开会话时，两边各留一个界面是合理的。</summary>
    private const string MutexName = @"Local\Zentwik.UI.SingleInstance";

    private static string PipeName => WorkerPipeContract.UiActivatePipeName;

    private static Mutex? _mutex;

    /// <summary>抢主实例。已被占用时通知现有实例并返回 false。</summary>
    public static bool TryTakeOwnership()
    {
        Mutex mutex = new(initiallyOwned: true, MutexName, out bool created);

        if (created)
        {
            _mutex = mutex;
            return true;
        }

        mutex.Dispose();
        NotifyExisting();
        return false;
    }

    /// <summary>在主实例里监听激活请求；回调已封送到 UI 线程。</summary>
    public static void Listen(Action onShow, Action onExit)
    {
        Thread thread = new(() => ListenLoop(onShow, onExit))
        {
            IsBackground = true,
            Name = "Zentwik.UiActivate",
        };

        thread.Start();
    }

    private static void ListenLoop(Action onShow, Action onExit)
    {
        while (true)
        {
            try
            {
                using NamedPipeServerStream server = new(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

                server.WaitForConnection();

                using StreamReader reader = new(server, Encoding.UTF8);

                string? command = reader.ReadLine();

                // 后台服务的托盘退出与第二个实例的唤出共用这条管道。
                if (string.Equals(command, WorkerPipeContract.UiActivateExit, StringComparison.Ordinal))
                {
                    Application.Current?.Dispatcher.BeginInvoke((Action)onExit);
                }
                else if (string.Equals(command, WorkerPipeContract.UiActivateShow, StringComparison.Ordinal))
                {
                    Application.Current?.Dispatcher.BeginInvoke((Action)onShow);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // 单次连接失败不结束监听：真正要停的是进程退出，不是某次握手。
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>主实例可能还在启动、尚未开监听，重试几次再放弃。</summary>
    private static void NotifyExisting()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out);
                client.Connect(400);

                using StreamWriter writer = new(client, new UTF8Encoding(false));
                writer.WriteLine(WorkerPipeContract.UiActivateShow);
                writer.Flush();
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
