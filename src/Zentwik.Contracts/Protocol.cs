using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zentwik.Contracts.Protocol;

public static class ProtocolInfo
{
    public const int Version = 2;

    public const string PipeName = "zentwik-next-agent";

    public const int MaxFrameBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
}

public static class Ops
{
    public const string Ping = "agent.ping";
    public const string Status = "agent.status";
    public const string CapabilitiesList = "capabilities.list";
    public const string ModulesList = "modules.list";
    public const string ModulesEnable = "modules.enable";
    public const string ModulesDisable = "modules.disable";
    public const string TelemetrySubscribe = "telemetry.subscribe";
    public const string TelemetryUnsubscribe = "telemetry.unsubscribe";
    public const string TelemetryHistory = "telemetry.history";

    public const string PlansList = "plans.list";
    public const string PlansGet = "plans.get";
    public const string PlansSave = "plans.save";
    public const string PlansDelete = "plans.delete";
    public const string PlansApply = "plans.apply";
    public const string PlansRevert = "plans.revert";

    public const string ScenesList = "scenes.list";
    public const string ScenesGet = "scenes.get";
    public const string ScenesSave = "scenes.save";
    public const string ScenesDelete = "scenes.delete";
    public const string ScenesActivate = "scenes.activate";
    public const string ScenesDeactivate = "scenes.deactivate";

    public const string LedgerList = "ledger.list";
    public const string LedgerRelease = "ledger.release";

    public const string PowerSchemesList = "power.scheme.list";
    public const string PowerSchemeCreate = "power.scheme.create";
    public const string PowerSchemeRename = "power.scheme.rename";
    public const string PowerSchemeDelete = "power.scheme.delete";
    public const string PowerSchemeBackup = "power.scheme.backup";
    public const string PowerSchemeBackups = "power.scheme.backups";
    public const string PowerSchemeRestore = "power.scheme.restore";

    public const string SettingsGet = "settings.get";
    public const string SettingsSet = "settings.set";

    public const string TuningRead = "hardware.tuning.read";
    public const string IntelTuningRead = "hardware.tuning.intel";
    public const string PowerLimitRead = "hardware.power.limit.read";
    public const string PowerLimitApply = "hardware.power.limit.apply";

    public const string DiagnosticsDoctor = "diagnostics.doctor";
    public const string DiagnosticsLog = "diagnostics.log";
}

public static class Events
{
    public const string TelemetrySample = "telemetry.sample";
    public const string SceneChanged = "scene.changed";
}

public static class ErrorCodes
{
    public const string ProtocolVersionMismatch = "protocol.version_mismatch";
    public const string ProtocolMalformed = "protocol.malformed";
    public const string UnknownOperation = "request.unknown_operation";
    public const string PayloadInvalid = "request.payload_invalid";
    public const string Unauthorized = "request.unauthorized";
    public const string Internal = "agent.internal";

    public const string PlanNotFound = "plan.not_found";
    public const string PlanInvalid = "plan.invalid";
    public const string SceneNotFound = "scene.not_found";
    public const string SceneInvalid = "scene.invalid";
    public const string LedgerEntryMissing = "ledger.entry_missing";

    public const string HardwareUnavailable = "hardware.unavailable";
    public const string HardwareWriteFailed = "hardware.write_failed";
    public const string HardwareVerifyFailed = "hardware.verify_failed";

    public const string PowerSchemeNotFound = "power.scheme_not_found";
    public const string PowerSchemeBuiltIn = "power.scheme_builtin";
    public const string PowerSchemeNameConflict = "power.scheme_name_conflict";
    public const string PowerSchemeCreateFailed = "power.scheme_create_failed";
    public const string PowerSchemeDeleteFailed = "power.scheme_delete_failed";
    public const string PowerSchemeBackupFailed = "power.scheme_backup_failed";
    public const string PowerSchemeBackupNotFound = "power.scheme_backup_not_found";
    public const string PowerSchemeRestoreFailed = "power.scheme_restore_failed";

    public const string ModuleNotFound = "module.not_found";
    public const string ModuleNotOptional = "module.not_optional";
    public const string ModuleDisabled = "module.disabled";

    public const string PowerLimitRejected = "power.limit_rejected";
    public const string PowerLimitInvalid = "power.limit_invalid";
    public const string PowerLimitUnsupported = "power.limit_unsupported";
}

public sealed class RequestEnvelope
{
    public int V { get; set; }
    public long Id { get; set; }
    public string Op { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
}

public sealed class ResponseEnvelope
{
    public int V { get; set; }
    public long Id { get; set; }
    public string Op { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Detail { get; set; }
    public JsonElement? Data { get; set; }

    /// <summary>事件帧 Id 恒为 0，与请求响应区分。</summary>
    public bool IsEvent => Id == 0 && Ok;

    public static ResponseEnvelope Event(string op, JsonElement? data) =>
        new() { V = ProtocolInfo.Version, Id = 0, Op = op, Ok = true, Data = data };

    public static ResponseEnvelope Failure(long id, string op, string error, string? detail = null) =>
        new() { V = ProtocolInfo.Version, Id = id, Op = op, Ok = false, Error = error, Detail = detail };

    public static ResponseEnvelope Success(long id, string op, JsonElement? data) =>
        new() { V = ProtocolInfo.Version, Id = id, Op = op, Ok = true, Data = data };
}

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>
/// 4 字节小端长度 + UTF-8 正文。长度前缀让分帧不依赖正文内容，超限也能在读取正文前拒绝。
/// </summary>
public static class Frames
{
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (body.Length > ProtocolInfo.MaxFrameBytes)
        {
            throw new ProtocolException($"frame too large: {body.Length}");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read != 4)
        {
            throw new ProtocolException("truncated frame header");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ProtocolInfo.MaxFrameBytes)
        {
            throw new ProtocolException($"invalid frame length: {length}");
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var body = rented.AsMemory(0, length);
            var filled = await stream.ReadAtLeastAsync(body, length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (filled != length)
            {
                throw new ProtocolException("truncated frame body");
            }

            return body.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static byte[] Encode<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    public static string Describe(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(body);
}
