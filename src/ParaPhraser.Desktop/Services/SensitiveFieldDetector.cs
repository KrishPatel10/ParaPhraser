using System.Runtime.InteropServices;
using System.Windows.Automation;
using ParaPhraser.Core;

namespace ParaPhraser.Desktop.Services;

/// <summary>
/// Stops a capture before anything sensitive reaches the clipboard or a provider.
/// </summary>
internal sealed class SensitiveSelectionException : InvalidOperationException
{
    internal SensitiveSelectionException(string message)
        : base(message)
    {
    }
}

internal static class SensitiveFieldDetector
{
    private static readonly TimeSpan CheckLimit = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Asks Windows whether the focused control is a password box. UI Automation
    /// can hang on an unresponsive app, so it runs off the UI thread with a limit.
    /// </summary>
    /// <remarks>
    /// ponytail: fails open, because not every app reports this and refusing all
    /// of them would break the hotkey. The clipboard marker check is the second layer.
    /// </remarks>
    internal static async Task<bool> IsPasswordFieldFocusedAsync(CancellationToken cancellationToken = default)
    {
        var check = Task.Run(IsFocusedElementPassword, CancellationToken.None);
        var finished = await Task.WhenAny(check, Task.Delay(CheckLimit, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        if (finished != check)
        {
            AppLog.Warn("Password field check timed out, continuing");
            return false;
        }

        return await check;
    }

    private static bool IsFocusedElementPassword()
    {
        try
        {
            return AutomationElement.FocusedElement?.Current.IsPassword == true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
            or InvalidOperationException
            or COMException
            or TimeoutException)
        {
            AppLog.Warn($"Password field check failed, continuing: {exception.GetType().Name}");
            return false;
        }
    }
}
