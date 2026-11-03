using Microsoft.Win32;

namespace Altabi.Services;

internal static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Altabi";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable was not found.");
            key.SetValue(ValueName, FormatCommand(executable), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    /// <summary>Points an existing startup registration at the running executable.</summary>
    public static void RefreshEnabledRegistration()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            if (key.GetValue(ValueName) is not string current) return;
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            string command = FormatCommand(executable);
            if (!string.Equals(current, command, StringComparison.Ordinal))
                key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        catch
        {
            // Startup maintenance must never prevent the switcher from running.
        }
    }

    internal static string FormatCommand(string executable) => $"\"{executable}\" --background";

    /// <summary>
    /// WinKit starts managed utilities with WINKIT_MANAGED=1 and owns their startup and updates;
    /// Altabi's own startup entry must then stay untouched.
    /// </summary>
    public static bool IsManagedByWinKit { get; } =
        Environment.GetEnvironmentVariable("WINKIT_MANAGED") == "1";
}
