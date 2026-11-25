using System.Windows;
using Zentwik.Localization;

namespace Zentwik.Desktop.Services;

/// <summary>语言目录 → String.* 资源键，供 DynamicResource 绑定。</summary>
public sealed class LocalizationBridge
{
    private readonly LocalizationCatalog _catalog;

    public LocalizationBridge(LocalizationCatalog catalog)
    {
        _catalog = catalog;
        _catalog.CultureChanged += Apply;
    }

    public void Apply()
    {
        var resources = Application.Current.Resources;
        foreach (var key in _catalog.Keys)
        {
            resources["String." + KeySuffix(key)] = _catalog.Get(key);
        }
    }

    /// <summary>资源键不能含点号，否则会被当成路径。</summary>
    private static string KeySuffix(string key)
    {
        var parts = key.Split('.');
        return string.Concat(parts.Select(static p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
