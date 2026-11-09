using System.Reflection;
using System.Windows.Media;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppThemeService _theme = AppThemeService.Instance;

    // ---- 彩虹滑块 ----
    private double _hueValue = 198.44;
    private string _hexInput = "#38BDF8";
    private string _lastValidHex = "#38BDF8";

    public SettingsViewModel()
    {
        // 与服务当前状态对齐：滑块与 HEX 反映「此刻生效的强调色」，
        // 而不是写死的初值 —— 否则切到别的页面再回来会看到旧值。
        SyncFromAccent(_theme.AccentColor);

        // 服务状态变化 → 刷新投影出来的单选态与派生属性。
        //
        // ThemeChanged 是「解析后的明暗」（系统模式下会自己变），
        // ThemeModeChanged 是「用户选了什么」（只有用户操作才变）。
        // 分段控件的选中态必须跟后者，否则系统主题一变，
        // 用户明明选了「深色」，按钮却跳到别的地方去。
        _theme.ThemeModeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsLightSelected));
            OnPropertyChanged(nameof(IsDarkSelected));
            OnPropertyChanged(nameof(IsSystemSelected));
        };

        _theme.AccentModeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsSystemAccentSelected));
            OnPropertyChanged(nameof(IsCustomAccentSelected));
            OnPropertyChanged(nameof(IsColorPanelVisible));

            // 模式一变，滑块 / HEX 应对齐当前生效的强调色，
            // 否则切回「个性」时显示的还是上一个模式的残留值。
            SyncFromAccent(_theme.AccentColor);
        };

        _theme.ThemeChanged += (_, _) => OnPropertyChanged(nameof(BrandAccentColor));

        _theme.AccentChanged += (_, color) =>
        {
            OnPropertyChanged(nameof(AccentColor));
            OnPropertyChanged(nameof(BrandAccentColor));

            // 跟随系统色时，系统强调色变了，滑块与 HEX 也要跟着走 ——
            // 否则界面显示的还是上一次读到的系统色，看起来就像「没反应」。
            if (_theme.AccentMode == AccentSource.System)
            {
                SyncFromAccent(color);
            }
        };
    }

    // ==================== 主题模式（投影自服务） ====================

    public bool IsLightSelected
    {
        get => _theme.Mode == AppTheme.Light;
        set
        {
            // 只处理「变为选中」。「变为未选中」是 RadioButton 组互斥的副作用，
            // 不是用户意图 —— 忽略它，否则三个单选会互相触发。
            if (value) _theme.SetThemeMode(AppTheme.Light);
        }
    }

    public bool IsDarkSelected
    {
        get => _theme.Mode == AppTheme.Dark;
        set
        {
            if (value) _theme.SetThemeMode(AppTheme.Dark);
        }
    }

    public bool IsSystemSelected
    {
        get => _theme.Mode == AppTheme.System;
        set
        {
            if (value) _theme.SetThemeMode(AppTheme.System);
        }
    }

    // ==================== 色彩模式（投影自服务） ====================

    public bool IsSystemAccentSelected
    {
        get => _theme.AccentMode == AccentSource.System;
        set
        {
            if (value) _theme.SetAccentMode(AccentSource.System);
        }
    }

    public bool IsCustomAccentSelected
    {
        get => _theme.AccentMode == AccentSource.Custom;
        set
        {
            if (value) _theme.SetAccentMode(AccentSource.Custom);
        }
    }

    public bool IsColorPanelVisible => _theme.AccentMode == AccentSource.Custom;

    // ==================== 彩虹滑块 ====================

    public double HueValue
    {
        get => _hueValue;
        set
        {
            if (!SetProperty(ref _hueValue, value)) return;

            if (!_initialized) return;

            // 固定饱和度与亮度，色相自由 —— 与参考 HTML 的 hslToHex(h, 85, 65) 一致
            var (r, g, b) = ColorMath.HslToRgb(value, 85, 65);
            var color = Color.FromRgb(r, g, b);

            _theme.SetAccentColor(color);

            // 滑块拖动 → 实时改写 HEX 输入框（这里必须用规范格式，
            // 因为用户此刻不是在编辑文本，而是在看滑块的即时结果）
            string hex = ColorMath.ToHex(color);
            _lastValidHex = hex;
            _hexInput = hex;
            OnPropertyChanged(nameof(HexInput));
        }
    }

    private bool _initialized;

    public void OnViewLoaded()
    {
        _initialized = true;
    }

    public string HexInput
    {
        get => _hexInput;
        set
        {
            if (!SetProperty(ref _hexInput, value)) return;
            // 注意：此处不做解析。解析在 CommitHexInput 中进行（LostFocus / Enter）。
        }
    }

    public void CommitHexInput()
    {
        if (ColorMath.TryParseHex(_hexInput, out var color))
        {
            // 解析成功 → 更新强调色，滑块反向同步 H 值。
            // 顺序很重要：先定颜色，再回写滑块 —— 反过来会覆盖刚设好的颜色。
            _theme.SetAccentColor(color);
            SyncFromAccent(color);
        }
        else
        {
            // 非法输入 → 回退到上一个有效值
            _hexInput = _lastValidHex;
            OnPropertyChanged(nameof(HexInput));
        }
    }

    private void SyncFromAccent(Color color)
    {
        var (h, _, _) = ColorMath.RgbToHsl(color.R, color.G, color.B);

        _hueValue = h;
        OnPropertyChanged(nameof(HueValue));

        _lastValidHex = ColorMath.ToHex(color);
        _hexInput = _lastValidHex;
        OnPropertyChanged(nameof(HexInput));
    }

    // ==================== 派生属性 ====================

    public Color AccentColor => _theme.AccentColor;

    public Color BrandAccentColor => ColorMath.DeriveBrandColor(_theme.AccentColor, _theme.IsDark);

    public string AppVersion
    {
        get
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(version)) return "v0.0.0";

            int plus = version.IndexOf('+');
            return "v" + (plus > 0 ? version[..plus] : version);
        }
    }

    public string RepositoryUrl => "https://github.com/Motu-Z/Zentwik";

    public string ReleasesUrl => "https://github.com/Motu-Z/Zentwik/releases";
}
