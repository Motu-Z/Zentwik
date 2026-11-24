using Microsoft.Win32;

namespace Zentwik.Core.Helpers
{
    /// <summary>系统浅色/深色偏好。「应用模式」与「Windows 模式」是两个键，可分别设置。</summary>
    public static class SystemAppearance
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>应用模式是否为浅色。</summary>
        public static bool IsAppLight() => ReadFlag("AppsUseLightTheme", fallback: false);

        /// <summary>任务栏是否为浅色。读不到时与应用模式一致，避免判反。</summary>
        public static bool IsTaskbarLight() => ReadFlag("SystemUsesLightTheme", fallback: IsAppLight());

        private static bool ReadFlag(string name, bool fallback)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);

                if (key?.GetValue(name) is int value)
                {
                    return value != 0;
                }
            }
            catch (Exception)
            {
                // 注册表不可读：用兜底值。
            }

            return fallback;
        }
    }
}
