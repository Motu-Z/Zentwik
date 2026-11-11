using System;
using Microsoft.Win32;

namespace Zentwik.Core.Services
{
    /// <summary>处理器厂商。只用来给核心组取名字，不参与任何分区判断。</summary>
    public enum CpuVendor
    {
        Unknown = 0,
        Amd = 1,
        Intel = 2
    }

    /// <summary>
    /// 读处理器厂商。
    ///
    /// 分区靠 L3 缓存域观测，跟厂商无关；厂商只决定预置项叫什么
    /// —— AMD 用户说 CCD，Intel 用户说 Tile，其余说分组。
    /// 判断错了最坏是一条预置项名字不好听，掩码位不会错。
    /// </summary>
    public static class CpuVendorProbe
    {
        private const string KeyPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        public static CpuVendor Detect()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KeyPath);
                string? id = key?.GetValue("VendorIdentifier") as string;

                if (string.IsNullOrEmpty(id))
                    return CpuVendor.Unknown;

                if (id.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                    return CpuVendor.Amd;

                if (id.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                    return CpuVendor.Intel;

                return CpuVendor.Unknown;
            }
            catch
            {
                return CpuVendor.Unknown;
            }
        }

        /// <summary>核心组的默认名。只有一个组时统一叫「全核心」，不分厂商。</summary>
        public static string GroupName(CpuVendor vendor, int index, int total) => total <= 1
            ? "全核心"
            : vendor switch
            {
                CpuVendor.Amd => $"CCD{index}",
                CpuVendor.Intel => $"Tile{index}",
                _ => $"分组{index}"
            };
    }
}
