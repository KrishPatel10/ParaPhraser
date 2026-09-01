using Microsoft.Win32;

namespace ParaPhraser.Desktop.Services;

internal sealed class StartupRegistrationService
{
    internal const string ValueName = "ParaPhraser";
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string command
                && !string.IsNullOrWhiteSpace(command);
        }
    }

    internal void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows could not determine the ParaPhraser executable path.");
            key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
