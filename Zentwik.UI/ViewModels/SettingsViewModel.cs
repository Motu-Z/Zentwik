using System.Reflection;
using System.Windows.Media;
using Zentwik.UI.Services;

namespace Zentwik.UI.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppThemeService _theme = AppThemeService.Instance;
    private readonly AppSettings _settings = AppSettings.Instance;

    // 彩虹滑块
    private double _hueValue = 198.44;
    private string _hexInput = "#38BDF8";
    private string _lastValidHex = "#38BDF8";

    public SettingsViewModel()
    {
        // 与服务当前状态对齐：滑块与 HEX 反映「此刻生效的强调色」，
        // 而不是写死的初值 —— 否则切到别的页面再回来会看到旧值。
        SyncFromAccent(_theme.AccentColor);

        // 服务状态变化 → 刷新投影出来的单选态与派生属性。
        _theme.ThemeModeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsLightSelected));
            OnPropertyChanged(nameof(IsDarkSelected));
            OnPropertyChanged(nameof(IsSystemSelected));
            OnPropertyChanged(nameof(ThemeModeHint));
        };

        _theme.AccentModeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsSystemAccentSelected));
            OnPropertyChanged(nameof(IsCustomAccentSelected));
            OnPropertyChanged(nameof(IsColorPanelVisible));
            OnPropertyChanged(nameof(AccentHint));

            // 模式一变，滑块 / HEX 应对齐当前生效的强调色，
            // 否则切回「个性」时显示的还是上一个模式的残留值。
            SyncFromAccent(_theme.AccentColor);
        };

        _theme.ThemeChanged += (_, _) => OnPropertyChanged(nameof(BrandAccentColor));

        _settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.ErrorBubble):
                    OnPropertyChanged(nameof(NotifyOnError));
                    break;

                case nameof(AppSettings.NotifyOnTraySuccess):
                    OnPropertyChanged(nameof(NotifyOnTraySuccess));
                    break;

                case nameof(AppSettings.ToastDurationSeconds):
                    OnPropertyChanged(nameof(ToastDuration));
                    OnPropertyChanged(nameof(ToastDurationText));
                    break;
            }
        };

        // 入口开关会被「清除全部数据」重置，界面要跟着回读。
        UiEntrySettings.Changed += () =>
        {
            OnPropertyChanged(nameof(DesktopMenu));
            OnPropertyChanged(nameof(TrayPlanMenu));
        };

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

    // 主题模式（投影自服务）

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

    /// <summary>当前主题模式的一句话说明。</summary>
    public string ThemeModeHint => _theme.Mode switch
    {
        AppTheme.Light => "界面保持明亮主题",
        AppTheme.Dark => "界面保持漆黑主题",
        _ => "随 Windows 自动切换"
    };

    // 色彩模式（投影自服务）

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

    /// <summary>当前色彩来源的一句话说明。</summary>
    public string AccentHint => _theme.AccentMode == AccentSource.System
        ? "使用 Windows 强调色"
        : "自定义界面主题色";

    // 彩虹滑块

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

    // 派生属性

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

    // 入口

    /// <summary>桌面空白右键菜单。写 HKCU，不需要提权。</summary>
    public bool DesktopMenu
    {
        get => UiEntrySettings.DesktopMenu;
        set
        {
            if (UiEntrySettings.DesktopMenu == value) return;

            UiEntrySettings.DesktopMenu = value;
            DesktopContextMenuService.Sync(value);
            OnPropertyChanged();
        }
    }

    /// <summary>托盘右键菜单里是否列出三类方案。</summary>
    public bool TrayPlanMenu
    {
        get => UiEntrySettings.TrayPlanMenu;
        set
        {
            if (UiEntrySettings.TrayPlanMenu == value) return;

            UiEntrySettings.TrayPlanMenu = value;
            OnPropertyChanged();
        }
    }

    // 通知

    public bool NotifyOnError
    {
        get => _settings.ErrorBubble;
        set
        {
            if (_settings.ErrorBubble == value) return;

            _settings.ErrorBubble = value;
            OnPropertyChanged();
        }
    }

    public bool NotifyOnTraySuccess
    {
        get => _settings.NotifyOnTraySuccess;
        set
        {
            if (_settings.NotifyOnTraySuccess == value) return;

            _settings.NotifyOnTraySuccess = value;
            OnPropertyChanged();
        }
    }

    public int ToastDuration
    {
        get => _settings.ToastDurationSeconds;
        set
        {
            int clamped = Math.Clamp(value, 1, 30);

            if (_settings.ToastDurationSeconds == clamped) return;

            _settings.ToastDurationSeconds = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToastDurationText));
        }
    }

    public string ToastDurationText => $"{ToastDuration} 秒";
}
