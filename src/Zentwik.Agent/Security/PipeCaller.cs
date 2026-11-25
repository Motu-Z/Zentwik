using System.IO.Pipes;

namespace Zentwik.Agent.Security;

public enum IntegrityLevel
{
    Unknown = 0,
    Untrusted = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    System = 5,
    Protected = 6,
}

public sealed record CallerIdentity(
    int ProcessId,
    int SessionId,
    string? UserSid,
    IntegrityLevel Integrity,
    bool Elevated,
    string? ImagePath);

public sealed record CallerPolicy(
    string? OwnerSid,
    bool RequireInteractiveSession,
    IntegrityLevel MinimumIntegrity);

public static class PipeCaller
{
    public static CallerIdentity? Read(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle.DangerousGetHandle();
        if (!NativeSecurity.GetNamedPipeClientProcessId(handle, out var processId)
            || !NativeSecurity.GetNamedPipeClientSessionId(handle, out var sessionId))
        {
            return null;
        }

        var process = NativeSecurity.OpenProcess(NativeSecurity.ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return new CallerIdentity((int)processId, (int)sessionId, null, IntegrityLevel.Unknown, false, null);
        }

        try
        {
            if (!NativeSecurity.OpenProcessToken(process, NativeSecurity.TokenQuery, out var token))
            {
                return new CallerIdentity((int)processId, (int)sessionId, null, IntegrityLevel.Unknown, false,
                    NativeSecurity.ReadImagePath(process));
            }

            try
            {
                var rid = NativeSecurity.ReadIntegrityRid(token);
                return new CallerIdentity(
                    (int)processId,
                    (int)sessionId,
                    NativeSecurity.ReadUserSid(token),
                    MapIntegrity(rid),
                    NativeSecurity.ReadElevated(token),
                    NativeSecurity.ReadImagePath(process));
            }
            finally
            {
                NativeSecurity.CloseHandle(token);
            }
        }
        finally
        {
            NativeSecurity.CloseHandle(process);
        }
    }

    public static bool Validate(CallerIdentity caller, CallerPolicy policy, out string reason)
    {
        if (caller.ProcessId <= 0)
        {
            reason = "无法确定调用方进程。";
            return false;
        }

        if (policy.RequireInteractiveSession && caller.SessionId == 0)
        {
            reason = "拒绝 Session 0 调用方。";
            return false;
        }

        if (caller.Integrity < policy.MinimumIntegrity)
        {
            reason = $"调用方完整性级别 {caller.Integrity} 低于要求 {policy.MinimumIntegrity}。";
            return false;
        }

        if (policy.OwnerSid is not null && !string.Equals(caller.UserSid, policy.OwnerSid, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"调用方用户 {caller.UserSid ?? "未知"} 不是配置的所有者 {policy.OwnerSid}。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static IntegrityLevel MapIntegrity(int rid) => rid switch
    {
        0x0000 => IntegrityLevel.Untrusted,
        0x1000 => IntegrityLevel.Low,
        0x2000 => IntegrityLevel.Medium,
        0x3000 => IntegrityLevel.High,
        0x4000 => IntegrityLevel.System,
        >= 0x5000 => IntegrityLevel.Protected,
        _ => IntegrityLevel.Unknown,
    };
}
