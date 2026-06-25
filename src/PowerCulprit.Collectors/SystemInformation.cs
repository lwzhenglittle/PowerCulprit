using System.Runtime.InteropServices;

namespace PowerCulprit.Collectors;

/// <summary>
/// Static helpers for Windows system power information.
/// </summary>
internal static class SystemInformation
{
    /// <summary>
    /// Returns whether the device is currently connected to AC power.
    /// </summary>
    public static bool IsAcOnline()
    {
        var status = new SYSTEM_POWER_STATUS();
        if (GetSystemPowerStatus(ref status))
        {
            return status.ACLineStatus == 1; // 1 = Online, 0 = Offline, 255 = Unknown
        }

        // Fallback: try WinRT API
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (report is not null)
            {
                var batteryStatus = report.Status;
                return batteryStatus == Windows.System.Power.BatteryStatus.Charging ||
                       batteryStatus == Windows.System.Power.BatteryStatus.Idle;
            }
        }
        catch
        {
            // silently fall through
        }

        return true; // Assume AC if we can't determine
    }

    /// <summary>
    /// Returns the current Windows power mode as a string, or null if unavailable.
    /// </summary>
    public static string? GetPowerMode()
    {
        // PowerMode is informational; return null if APIs are unavailable.
        try
        {
            var saverStatus = Windows.System.Power.PowerManager.EnergySaverStatus;
            return saverStatus == Windows.System.Power.EnergySaverStatus.On
                ? "BatterySaver"
                : "Normal";
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
