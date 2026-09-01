using System.Windows;
using Clipboard = System.Windows.Clipboard;

namespace ParaPhraser.Desktop.Services;

internal sealed class ClipboardSnapshot
{
    private readonly System.Windows.IDataObject? _data;

    private ClipboardSnapshot(System.Windows.IDataObject? data)
    {
        _data = data;
    }

    internal static async Task<ClipboardSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await ClipboardRetry.ExecuteAsync(
            () => Clipboard.GetDataObject(),
            cancellationToken);
        return new ClipboardSnapshot(data);
    }

    internal async Task<bool> TryRestoreAsync()
    {
        try
        {
            await ClipboardRetry.ExecuteAsync(() =>
            {
                if (_data is null)
                {
                    Clipboard.Clear();
                }
                else
                {
                    Clipboard.SetDataObject(_data, copy: true);
                }
            });

            return true;
        }
        catch (ClipboardBusyException)
        {
            // Restoration is best-effort. A successful selection capture or
            // paste must not be reported as failed only because another app
            // retained the clipboard after the operation.
            return false;
        }
    }
}
