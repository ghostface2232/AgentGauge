using Microsoft.Win32;

namespace Gauge.Services;

/// <summary>
/// Run-on-startup for an unpackaged app: registers/unregisters the executable in the
/// current user's Run key (HKCU\…\CurrentVersion\Run). No admin rights needed.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Gauge";

    /// <summary>True if our Run entry is currently present.</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("settings", $"Run-key read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Registers or removes the Run entry. Returns the resulting state.</summary>
    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path))
                {
                    return IsEnabled();
                }

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key?.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("settings", $"Run-key write ({enabled}) failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Report the real state so the menu check can reflect any failure.
        return IsEnabled();
    }
}
