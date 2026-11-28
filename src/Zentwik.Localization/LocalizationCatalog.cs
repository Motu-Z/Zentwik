using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Zentwik.Localization;

/// <summary>
/// JSON 键值表而非 resx：模块与插件需要各自贡献翻译片段，JSON 可直接合并，
/// resx 会按程序集裂成多个卫星程序集。
/// 回退链 zh-TW → zh-CN → en-US → 键本身，漏翻必须可见。
/// </summary>
public sealed class LocalizationCatalog
{
    public const string FallbackCulture = "en-US";

    private static readonly string[] SupportedLanguages = ["zh-CN"];
    private static readonly string[] EmbeddedCultures = ["zh-CN", "zh-TW", "en-US"];

    private readonly Dictionary<string, FrozenDictionary<string, string>> _catalogs;
    private readonly Lock _gate = new();
    private string _current;

    private LocalizationCatalog(Dictionary<string, FrozenDictionary<string, string>> catalogs, string current)
    {
        _catalogs = catalogs;
        _current = current;
    }

    public event Action? CultureChanged;

    public static IReadOnlyList<string> SupportedCultures => SupportedLanguages;

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_gate)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var table in _catalogs.Values)
                {
                    foreach (var key in table.Keys)
                    {
                        keys.Add(key);
                    }
                }

                return keys;
            }
        }
    }

    public string CurrentCulture
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
        set
        {
            var normalized = Normalize(value);
            lock (_gate)
            {
                if (string.Equals(_current, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _current = normalized;
            }

            CultureChanged?.Invoke();
        }
    }

    public static LocalizationCatalog CreateDefault()
    {
        var catalogs = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in EmbeddedCultures)
        {
            var entries = LoadEmbedded(culture);
            if (entries.Count > 0)
            {
                catalogs[culture] = entries.ToFrozenDictionary(StringComparer.Ordinal);
            }
        }

        var initial = Normalize(CultureInfo.CurrentUICulture.Name);
        if (!SupportedLanguages.Contains(initial, StringComparer.OrdinalIgnoreCase))
        {
            initial = SupportedLanguages[0];
        }

        return new LocalizationCatalog(catalogs, initial);
    }

    public string this[string key] => Get(key);

    public string Get(string key) => Get(key, CurrentCulture);

    public string Get(string key, string culture)
    {
        foreach (var candidate in FallbackChain(culture))
        {
            if (TryLookup(candidate, key, out var value))
            {
                return value;
            }
        }

        return key;
    }

    public string Get(string key, params object[] args)
    {
        var template = Get(key);
        return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
    }

    public void Merge(string culture, IReadOnlyDictionary<string, string> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var normalized = Normalize(culture);
        lock (_gate)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_catalogs.TryGetValue(normalized, out var existing))
            {
                foreach (var pair in existing)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in entries)
            {
                merged[pair.Key] = pair.Value;
            }

            _catalogs[normalized] = merged.ToFrozenDictionary(StringComparer.Ordinal);
        }

        if (string.Equals(normalized, CurrentCulture, StringComparison.Ordinal))
        {
            CultureChanged?.Invoke();
        }
    }

    public static string Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return FallbackCulture;
        }

        foreach (var supported in EmbeddedCultures)
        {
            if (string.Equals(supported, culture, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        return culture.Split('-', '_')[0].ToLowerInvariant() switch
        {
            "zh" => "zh-CN",
            "en" => "en-US",
            _ => FallbackCulture,
        };
    }

    private bool TryLookup(string culture, string key, out string value)
    {
        lock (_gate)
        {
            if (_catalogs.TryGetValue(culture, out var table) && table.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> FallbackChain(string culture)
    {
        yield return culture;

        var language = culture.Split('-')[0].ToLowerInvariant();
        if (!string.Equals(language, culture, StringComparison.OrdinalIgnoreCase))
        {
            var parent = language switch
            {
                "zh" => "zh-CN",
                "en" => "en-US",
                _ => null,
            };

            if (parent is not null && !string.Equals(parent, culture, StringComparison.OrdinalIgnoreCase))
            {
                yield return parent;
            }
        }

        if (!string.Equals(culture, FallbackCulture, StringComparison.OrdinalIgnoreCase))
        {
            yield return FallbackCulture;
        }
    }

    private static Dictionary<string, string> LoadEmbedded(string culture)
    {
        var assembly = typeof(LocalizationCatalog).Assembly;
        var name = $"Zentwik.Localization.Resources.strings.{culture}.json";
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parsed = JsonSerializer.Deserialize(reader.ReadToEnd(), LocalizationJson.Default.DictionaryStringString);
        return parsed is null ? [] : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocalizationJson : System.Text.Json.Serialization.JsonSerializerContext;
