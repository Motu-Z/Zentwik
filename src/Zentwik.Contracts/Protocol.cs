using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zentwik.Contracts.Protocol;

public static class ProtocolInfo
{
    public const int Version = 1;

    public const string PipeName = "zentwik-next-agent";

    public const int MaxFrameBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
}

public static class Ops
{
    public const string Ping = "agent.ping";
    public const string Status = "agent.status";
    public const string CapabilitiesList = "capabilities.list";
    public const string TelemetrySubscribe = "telemetry.subscribe";
    public const string TelemetryUnsubscribe = "telemetry.unsubscribe";
    public const string PlansList = "plans.list";
    public const string ScenesList = "scenes.list";
    public const string SettingsGet = "settings.get";
    public const string SettingsSet = "settings.set";
}

public static class Events
{
    public const string TelemetrySample = "telemetry.sample";
}

public static class ErrorCodes
{
    public const string ProtocolVersionMismatch = "protocol.version_mismatch";
    public const string ProtocolMalformed = "protocol.malformed";
    public const string UnknownOperation = "request.unknown_operation";
    public const string PayloadInvalid = "request.payload_invalid";
    public const string Internal = "agent.internal";
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
