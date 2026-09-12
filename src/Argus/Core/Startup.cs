using Microsoft.Win32;

namespace Argus.Core;

/// <summary>
/// Run-on-login, via the per-user Run key.
///
/// Deliberately the Run key rather than a scheduled task. A logon-triggered
/// scheduled task running an interactive process is precisely the arrangement
/// that flashes a console window on this machine twice a day - it would be a
/// poor joke for the panel built to catch that to arrive the same way.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Argus";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Log.Warn($"startup state unreadable: {ex.Message}");
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    Log.Warn("cannot resolve own path; startup not registered");
                    return;
                }
                key.SetValue(ValueName, $"\"{exe}\"");
                Log.Info($"startup registered: {exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("startup removed");
            }
        }
        catch (Exception ex)
        {
            Log.Error("startup registration failed", ex);
        }
    }

    /// <summary>Brings the registry in line with settings, and repairs a stale path after a move.</summary>
    public static void Sync(ArgusSettings settings)
    {
        if (!settings.RunOnStartup)
        {
            if (IsEnabled()) Set(false);
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            var expected = $"\"{Environment.ProcessPath}\"";

            // Rebuilding to a new folder leaves the old path registered and
            // silently stops Argus appearing at login. Repair it every start.
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                Set(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"startup sync failed: {ex.Message}");
        }
    }
}
