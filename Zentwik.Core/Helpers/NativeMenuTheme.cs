using System.Runtime.InteropServices;

namespace Zentwik.Core.Helpers
{
    /// <summary>
    /// Win32 弹出菜单的明暗，走 uxtheme 的未文档化入口，只在 Windows 11 上可靠。
    /// </summary>
    /// <remarks>菜单先按旧模式画出来、几百毫秒后才重绘，所以必须在弹菜单之前设好。</remarks>
    public static class NativeMenuTheme
    {
        private const int Windows11Build = 22000;

        private const int ForceDark = 2;

        private const int ForceLight = 3;

        private static bool? _applied;

        public static void Apply(bool dark)
        {
            if (_applied == dark)
            {
                return;
            }

            try
            {
                if (Environment.OSVersion.Version.Build < Windows11Build)
                {
                    return;
                }

                SetPreferredAppMode(dark ? ForceDark : ForceLight);
                FlushMenuThemes();
                _applied = dark;
            }
            catch (EntryPointNotFoundException)
            {
                // 精简版系统缺这个导出：退回系统默认菜单。
            }
        }

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();
    }
}
