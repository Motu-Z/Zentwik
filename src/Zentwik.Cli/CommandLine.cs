namespace Zentwik.Cli;

public sealed class CliUsageException(string message) : Exception(message);

public sealed class CommandLine
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _repeats = new(StringComparer.Ordinal);

    private CommandLine(List<string> positionals) => Positionals = positionals;

    public IReadOnlyList<string> Positionals { get; }

    public static CommandLine Parse(string[] args)
    {
        var positionals = new List<string>();
        var line = new CommandLine(positionals);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "-h" or "--help")
            {
                line._options["--help"] = "true";
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            var name = token;
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (name is "--core" or "--rule" or "--set" or "--plan" or "--trigger")
            {
                if (!line._repeats.TryGetValue(name, out var list))
                {
                    list = [];
                    line._repeats[name] = list;
                }

                if (value is null)
                {
                    throw new CliUsageException($"{name} 需要一个值。");
                }

                list.Add(value);
                continue;
            }

            line._options[name] = value ?? "true";
        }

        return line;
    }

    public bool Help => _options.ContainsKey("--help");

    public string? Value(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) => _options.ContainsKey(name);

    public IReadOnlyList<string> Repeated(string name) =>
        _repeats.TryGetValue(name, out var values) ? values : [];

    public string Require(string name)
    {
        var value = Value(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException($"缺少参数 {name}。");
        }

        return value;
    }

    public void EnsureOnly(params string[] allowed)
    {
        var permitted = new HashSet<string>(allowed, StringComparer.Ordinal) { "--help", "--json" };
        foreach (var key in _options.Keys)
        {
            if (!permitted.Contains(key))
            {
                throw new CliUsageException($"未知参数 {key}。");
            }
        }

        foreach (var key in _repeats.Keys)
        {
            if (!permitted.Contains(key))
            {
                throw new CliUsageException($"未知参数 {key}。");
            }
        }
    }

    public string Positional(int index, string name)
    {
        if (index >= Positionals.Count)
        {
            throw new CliUsageException($"缺少 {name}。");
        }

        return Positionals[index];
    }
}
