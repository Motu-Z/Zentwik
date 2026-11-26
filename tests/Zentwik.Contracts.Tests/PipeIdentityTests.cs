using System.IO.Pipes;
using Xunit;
using Zentwik.Contracts.Protocol;

namespace Zentwik.Contracts.Tests;

public sealed class PipeIdentityTests
{
    [Theory]
    [InlineData("S-1-5-21-1", "S-1-5-21-1", true)]
    [InlineData("S-1-5-18", "S-1-5-21-1", true)]
    [InlineData("S-1-5-18", "S-1-5-18", true)]
    [InlineData("S-1-5-32-544", "S-1-5-21-1", true)]
    [InlineData("S-1-5-21-2", "S-1-5-21-1", false)]
    [InlineData("S-1-5-19", "S-1-5-21-1", false)]
    [InlineData(null, "S-1-5-21-1", false)]
    [InlineData("S-1-5-21-1", null, false)]
    public void Trust_Accepts_System_Or_Current_User(string? server, string? current, bool expected)
    {
        Assert.Equal(expected, PipeIdentity.IsTrustedServer(server, current));
    }

    [Fact]
    public async Task Server_Sid_Resolves_To_Current_User()
    {
        var name = $"zentwik-idtest-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accept;

        var serverSid = PipeIdentity.ReadServerSid(client.SafePipeHandle.DangerousGetHandle());
        var currentSid = PipeIdentity.ReadCurrentSid();

        Assert.NotNull(serverSid);
        Assert.Equal(currentSid, serverSid);
        Assert.True(PipeIdentity.IsTrustedServer(serverSid, currentSid));
    }
}
