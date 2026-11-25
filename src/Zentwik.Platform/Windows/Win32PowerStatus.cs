using Zentwik.Platform;

namespace Zentwik.Platform.Windows;

public sealed class Win32PowerStatus : IPowerStatus
{
    public bool? OnAcPower
    {
        get
        {
            if (!Native.GetSystemPowerStatus(out var status))
            {
                return null;
            }

            return status.AcLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
        }
    }
}
