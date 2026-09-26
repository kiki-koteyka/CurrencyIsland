using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland;

public sealed class SettingsData
{
    // false = boxed tooltip that follows the cursor (date + all 3 prices in
    // one card); true = crosshair - just a thin guide line and three small
    // colored value chips sitting right on each line, no box at all.
    public bool ChartCrosshairHover { get; set; }

    public bool AutoUpdate { get; set; } = true;
}

public static class AppSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CurrencyIsland";

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurrencyIsland", "settings.json");

    public static SettingsData Load()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var loaded = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(StateFile));
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // corrupt or unreadable state file - fall back to default settings
        }

        return new SettingsData();
    }

    public static void Save(SettingsData data)
    {
        try
        {
            var folder = Path.GetDirectoryName(StateFile)!;
            Directory.CreateDirectory(folder);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(data));
        }
        catch
        {
            // best-effort persistence only
        }
    }

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string existing
               && string.Equals(existing.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    // "Autostart doesn't work" for a debug/dev-built app is usually this:
    // the Run entry was written pointing at wherever the exe happened to be
    // the day it was toggled on, and every rebuild/rename/move since then
    // (bin/Debug getting overwritten, or the exe getting copied to a flash
    // drive) leaves that registry path stale - Windows tries to launch a
    // file that no longer exists there and silently does nothing at login.
    // Called once at every startup: if autostart is on at all, re-point it
    // at wherever THIS instance is actually running from right now.
    public static void RepairAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is not string existing) return;

            var current = $"\"{Environment.ProcessPath}\"";
            if (!string.Equals(existing, current, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(RunValueName, current);
            }
        }
        catch
        {
            // best-effort repair only
        }
    }
}
