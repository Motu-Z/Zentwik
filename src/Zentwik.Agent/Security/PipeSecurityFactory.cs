using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Zentwik.Agent.Security;

public static class PipeSecurityFactory
{
    /// <summary>
    /// 显式 DACL：SYSTEM 与管理员完全控制，已认证用户可读写但不可创建实例。
    /// 具体的调用方限制在连接建立后按 SID、完整性与会话校验，ACL 只负责第一道边界。
    /// </summary>
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null)
        {
            security.AddAccessRule(new PipeAccessRule(current, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }
}
