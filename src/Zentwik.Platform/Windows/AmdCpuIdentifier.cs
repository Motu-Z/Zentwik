using System.Globalization;

namespace Zentwik.Platform.Windows;

/// <summary>
/// Windows 报告的处理器标识形如 <c>AMD64 Family 25 Model 97 Stepping 2</c>；数字是十进制，
/// 25/97 对应 Family 19h / Model 61h。
/// </summary>
internal static class AmdCpuIdentifier
{
    public static bool TryParse(string? identifier, out uint family, out uint model)
    {
        family = 0;
        model = 0;
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        family = ReadNumber(identifier, "Family");
        model = ReadNumber(identifier, "Model");
        return family != 0;
    }

    private static uint ReadNumber(string text, string key)
    {
        var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        var start = index + key.Length;
        while (start < text.Length && text[start] == ' ')
        {
            start++;
        }

        var end = start;
        while (end < text.Length && char.IsAsciiLetterOrDigit(text[end]))
        {
            end++;
        }

        var span = text.AsSpan(start, end - start);
        return uint.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
