using System.IO;
using Microsoft.Win32;

namespace BinSweeper.App;

internal static class SettingsStore
{
    private const string KeyPath = @"Software\BinSweeper";
    private const string AutoCleanValue = "AutoClean";

    public static bool AutoClean
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(AutoCleanValue) is int value ? value != 0 : true;
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key?.SetValue(AutoCleanValue, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BinSweeper";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    /// <summary>写 HKCU 的 Run 键实现开机自启；开发态由 dotnet.exe 启动，拿不到真实 exe，直接拒绝。</summary>
    public static bool SetEnabled(bool enabled)
    {
        var exePath = Environment.ProcessPath;
        if (enabled && (exePath is null
            || Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return false;
        }

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        return true;
    }
}
