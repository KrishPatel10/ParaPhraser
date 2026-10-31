using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace ParaPhraser.Desktop.Services;

internal sealed class ClipboardSnapshot
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    // Password managers set this empty format to keep secrets out of Windows
    // clipboard history, so a restore must carry it even though it has no data.
    internal const string ExcludeFromClipboardHistory = "ExcludeClipboardContentFromMonitorProcessing";

    // Office takes up to 700 ms to render each of these on demand, and they
    // repeat content already held in the text, HTML, RTF, and bitmap formats.
    private static readonly string[] SlowOfficeFormats =
        ["EnhancedMetafile", "MetaFilePict", "ShadowWorkbook", "SymbolicLink"];

    private readonly DataObject? _data;

    private ClipboardSnapshot(DataObject? data)
    {
        _data = data;
    }

    internal static async Task<ClipboardSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        // Clipboard.GetDataObject is a live view of the clipboard, so holding on
        // to it would read back whatever was copied next.
        var data = await ClipboardRetry.ExecuteAsync(
            () => CopyFormats(Clipboard.GetDataObject()),
            cancellationToken);
        return new ClipboardSnapshot(data);
    }

    internal static DataObject? CopyFormats(System.Windows.IDataObject? source)
    {
        if (source is null)
        {
            return null;
        }

        var formats = source.GetFormats(autoConvert: false);
        var fastFormats = formats.Except(SlowOfficeFormats).ToArray();

        var copy = new DataObject();
        foreach (var format in fastFormats.Length > 0 ? fastFormats : formats)
        {
            try
            {
                var value = format == ExcludeFromClipboardHistory
                    ? new MemoryStream()
                    : source.GetData(format, autoConvert: false);
                if (value is not null)
                {
                    copy.SetData(format, value, autoConvert: false);
                }
            }
            catch (COMException exception) when (exception.HResult != ClipboardCannotOpen)
            {
                // Keep the formats that can be read rather than losing them all.
            }
        }

        return copy.GetFormats(autoConvert: false).Length == 0 ? null : copy;
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
