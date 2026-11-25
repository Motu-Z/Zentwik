using System;
using System.Security.Principal;

namespace Zentwik.Core.Helpers
{
    /// <summary>当前进程是否以管理员令牌运行。启动横幅与状态查询共用一份判断。</summary>
    public static class ProcessElevation
    {
        public static bool IsElevated()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
