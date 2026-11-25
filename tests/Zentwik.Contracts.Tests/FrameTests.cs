using System.Text.Json;
using Xunit;
using Zentwik.Contracts.Json;
using Zentwik.Contracts.Protocol;

namespace Zentwik.Contracts.Tests;

public sealed class FrameTests
{
    [Fact]
    public async Task RoundTrip_PreservesBody()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { hello = "世界", value = 42 },
            new JsonSerializerOptions());

        using var stream = new MemoryStream();
        await Frames.WriteAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;

        var read = await Frames.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Read_Returns_Null_On_Clean_Eof()
    {
        using var stream = new MemoryStream();
        var read = await Frames.ReadAsync(stream, CancellationToken.None);
        Assert.Null(read);
    }

    [Fact]
    public async Task Read_Rejects_Truncated_Header()
    {
        using var stream = new MemoryStream([0x01, 0x02]);
        await Assert.ThrowsAsync<ProtocolException>(() =>
            Frames.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Read_Rejects_Length_Above_Limit()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, ProtocolInfo.MaxFrameBytes + 1);
        await stream.WriteAsync(header, CancellationToken.None);
        stream.Position = 0;

        await Assert.ThrowsAsync<ProtocolException>(() =>
            Frames.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Write_Rejects_Oversized_Body()
    {
        using var stream = new MemoryStream();
        var body = new byte[ProtocolInfo.MaxFrameBytes + 1];

        await Assert.ThrowsAsync<ProtocolException>(() =>
            Frames.WriteAsync(stream, body, CancellationToken.None));
    }

    [Fact]
    public void ResponseEnvelope_Requires_NonZero_Id_For_Responses()
    {
        var response = ResponseEnvelope.Success(7, Ops.Status, null);
        var evt = ResponseEnvelope.Event(Events.TelemetrySample, null);

        Assert.False(response.IsEvent);
        Assert.True(evt.IsEvent);
    }

    [Fact]
    public void Envelope_Serializes_With_Registered_Context()
    {
        var envelope = ResponseEnvelope.Failure(3, Ops.PlansList, ErrorCodes.PayloadInvalid, "bad");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ZentwikJson.Default.ResponseEnvelope);
        var back = JsonSerializer.Deserialize(bytes, ZentwikJson.Default.ResponseEnvelope);

        Assert.NotNull(back);
        Assert.Equal(3, back.Id);
        Assert.Equal(ErrorCodes.PayloadInvalid, back.Error);
        Assert.False(back.Ok);
    }
}
