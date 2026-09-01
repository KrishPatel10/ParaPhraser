using System.Runtime.InteropServices;

namespace ParaPhraser.Desktop.Services;

internal sealed class ClipboardBusyException : InvalidOperationException
{
    internal ClipboardBusyException(COMException innerException)
        : base(
            "Another application is temporarily using the clipboard. Wait a moment and try again.",
            innerException)
    {
    }
}

internal static class ClipboardRetry
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    // Teams, browsers, clipboard managers, and rich editors can retain the
    // clipboard beyond the paste keystroke. These delays provide roughly
    // three seconds of recovery without blocking the WPF dispatcher thread.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(1)
    ];

    internal static async Task<T> ExecuteAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return operation();
            }
            catch (COMException exception)
                when (exception.HResult == ClipboardCannotOpen
                    && attempt < RetryDelays.Length)
            {
                // Do not use ConfigureAwait(false). WPF clipboard operations
                // must resume on the original STA dispatcher thread.
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
            catch (COMException exception)
                when (exception.HResult == ClipboardCannotOpen)
            {
                throw new ClipboardBusyException(exception);
            }
        }
    }

    internal static Task ExecuteAsync(
        Action operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(() =>
        {
            operation();
            return null;
        }, cancellationToken);
    }
}
