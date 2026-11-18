using System;
using System.Diagnostics;
using System.Reflection;

namespace Zentwik.Core.Helpers;

/// <summary>
/// 应用版本号，写进日志用。
/// 走 Win32 资源而不是 <see cref="Assembly"/> 元数据：Native AOT 下反射拿特性可能被裁掉，
/// 而没有版本号就分不清「界面 2.4.22 配后台 2.4.21」这类混版问题。
/// </summary>
public static class AppVersion
{
    /// <summary>形如 <c>2.4.22</c>；取不到返回 <c>未知</c>。</summary>
    public static string Current
    {
        get
        {
            try
            {
                string? path = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(path))
                {
                    string? product = FileVersionInfo.GetVersionInfo(path).ProductVersion;

                    if (!string.IsNullOrWhiteSpace(product))
                    {
                        // 形如 2.4.22+abc1234，只留版本
                        int plus = product.IndexOf('+');
                        return plus > 0 ? product[..plus].Trim() : product.Trim();
                    }
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.ComponentModel.Win32Exception)
            {
                // 读不到就退到程序集版本。
            }

            Version? fallback = typeof(AppVersion).Assembly.GetName().Version;
            return fallback is null ? "未知" : fallback.ToString();
        }
    }
}
