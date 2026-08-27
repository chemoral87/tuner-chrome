using Microsoft.Win32;

namespace Tuner;

/// <summary>
/// Manages the Windows registry key for auto-starting the application with Windows.
/// Uses HKCU\Software\Microsoft\Windows\CurrentVersion\Run for per-user auto-start.
/// </summary>
public static class AutoStartManager
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ChromaticTuner";

    /// <summary>
    /// Gets the full path to the current executable.
    /// </summary>
    private static string GetExecutablePath()
    {
        return Environment.ProcessPath ?? "";
    }

    /// <summary>
    /// Checks if auto-start is currently enabled.
    /// </summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            var value = key?.GetValue(AppName);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables or disables auto-start with Windows.
    /// When enabled, the app will start with --minimized flag.
    /// </summary>
    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);

            if (key == null)
                return;

            if (enabled)
            {
                string exePath = GetExecutablePath();
                if (!string.IsNullOrEmpty(exePath))
                {
                    // The --minimized flag tells the app to start in the system tray
                    key.SetValue(AppName, $"\"{exePath}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to update auto-start registry: {ex.Message}");
        }
    }
}
