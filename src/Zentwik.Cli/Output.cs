using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zentwik.Cli;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int AgentUnavailable = 1;
    public const int Usage = 2;
    public const int Failed = 3;
    public const int Unavailable = 4;
    public const int Timeout = 5;
    public const int Partial = 6;
}

public sealed class Output(bool json)
{
    public bool Json { get; } = json;

    public void Line(string text)
    {
        if (!Json)
        {
            Console.WriteLine(text);
        }
    }

    public void JsonValue<T>(T value, JsonTypeInfo<T> info)
    {
        if (Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, info));
        }
    }

    public void Error(string code, string detail)
    {
        if (Json)
        {
            using var stream = Console.OpenStandardOutput();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
            writer.Flush();
            Console.WriteLine();
        }
        else
        {
            Console.Error.WriteLine($"{code}: {detail}");
        }
    }

    public static string Status(bool value) => value ? "yes" : "no";
}
