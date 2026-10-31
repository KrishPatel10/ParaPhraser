using System.Diagnostics;
using System.Windows;
using ParaPhraser.Core;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using TextDataFormat = System.Windows.TextDataFormat;

namespace ParaPhraser.Desktop.Services;

public sealed class ClipboardSelectionService
{
    private const ushort VirtualKeyC = 0x43;
    private const ushort VirtualKeyV = 0x56;
    private const ushort VirtualKeyZ = 0x5A;
    private const int FocusAttempts = 12;
    private const int FocusAttemptDelayMilliseconds = 50;
    private const int ClipboardReadyDelayMilliseconds = 60;
    private const int PasteProcessingDelayMilliseconds = 750;

    public async Task<SelectionContext?> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        await WaitForHotkeyModifiersToBeReleasedAsync(cancellationToken);

        var targetWindow = NativeMethods.GetForegroundWindow();
        if (targetWindow == nint.Zero)
        {
            return null;
        }

        // Checked before Ctrl+C, so a password never reaches the clipboard or
        // Windows clipboard history.
        if (await SensitiveFieldDetector.IsPasswordFieldFocusedAsync(cancellationToken))
        {
            throw new SensitiveSelectionException("ParaPhraser does not work in password fields.");
        }

        var applicationName = GetApplicationName(targetWindow);
        var windowTitle = NativeMethods.GetWindowTitle(targetWindow);

        var clipboardBeforeCapture = await ClipboardSnapshot.CaptureAsync(cancellationToken);
        var originalSequence = NativeMethods.GetClipboardSequenceNumber();

        try
        {
            NativeMethods.SendControlChord(VirtualKeyC);

            var didChange = await WaitForClipboardChangeAsync(originalSequence, cancellationToken);
            if (!didChange)
            {
                return null;
            }

            var containsText = await ClipboardRetry.ExecuteAsync(
                () => Clipboard.ContainsText(TextDataFormat.UnicodeText),
                cancellationToken);
            if (!containsText)
            {
                return null;
            }

            // Apps mark secrets this way to keep them out of clipboard history.
            var isMarkedSensitive = await ClipboardRetry.ExecuteAsync(
                () => Clipboard.ContainsData(ClipboardSnapshot.ExcludeFromClipboardHistory),
                cancellationToken);
            if (isMarkedSensitive)
            {
                throw new SensitiveSelectionException("This app marked the selection as sensitive, so ParaPhraser left it alone.");
            }

            var selectedText = await ClipboardRetry.ExecuteAsync(
                () => Clipboard.GetText(TextDataFormat.UnicodeText),
                cancellationToken);
            var selectedHtml = await ClipboardRetry.ExecuteAsync(
                () => Clipboard.GetData(DataFormats.Html) as string,
                cancellationToken);
            return string.IsNullOrWhiteSpace(selectedText)
                ? null
                : new SelectionContext(
                    selectedText,
                    targetWindow,
                    applicationName,
                    windowTitle,
                    selectedHtml);
        }
        finally
        {
            _ = await clipboardBeforeCapture.TryRestoreAsync();
        }
    }

    private static async Task WaitForHotkeyModifiersToBeReleasedAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40 && NativeMethods.AreHotkeyModifiersPressed(); attempt++)
        {
            await Task.Delay(15, cancellationToken);
        }
    }

    public Task CopyAsync(
        SelectionContext context,
        RichDocument content,
        CancellationToken cancellationToken = default) =>
        ClipboardRetry.ExecuteAsync(
            () => Clipboard.SetDataObject(CreateClipboardData(context, content), copy: true),
            cancellationToken);

    private static DataObject CreateClipboardData(SelectionContext context, RichDocument content)
    {
        var data = new DataObject();
        data.SetText(content.ToPlainText(), TextDataFormat.UnicodeText);
        if (content.HasFormatting)
        {
            data.SetData(DataFormats.Html, content.ToClipboardHtml(context.ClipboardHtml));
        }

        return data;
    }

    public async Task ReplaceAsync(
        SelectionContext context,
        RichDocument replacement,
        CancellationToken cancellationToken = default)
    {
        var replacementText = replacement.ToPlainText();
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementText);

        var clipboardBeforePaste = await ClipboardSnapshot.CaptureAsync(cancellationToken);
        var restorePreviousClipboard = true;
        try
        {
            if (!NativeMethods.IsWindow(context.TargetWindowHandle)
                || !await ActivateTargetWindowAsync(
                    context.TargetWindowHandle,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "Windows could not return focus to the original text field. Copy the suggestion instead.");
            }

            await ClipboardRetry.ExecuteAsync(
                () => Clipboard.SetDataObject(CreateClipboardData(context, replacement), copy: true),
                cancellationToken);
            await Task.Delay(ClipboardReadyDelayMilliseconds, cancellationToken);

            var containsReplacement = await ClipboardRetry.ExecuteAsync(
                () => Clipboard.ContainsText(TextDataFormat.UnicodeText),
                cancellationToken);
            var preparedReplacement = containsReplacement
                ? await ClipboardRetry.ExecuteAsync(
                    () => Clipboard.GetText(TextDataFormat.UnicodeText),
                    cancellationToken)
                : null;
            if (!containsReplacement
                || !string.Equals(
                    preparedReplacement,
                    replacementText,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Windows could not prepare the replacement text on the clipboard. Copy the suggestion instead.");
            }

            if (NativeMethods.GetForegroundWindow() != context.TargetWindowHandle
                && !await ActivateTargetWindowAsync(
                    context.TargetWindowHandle,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The original text field lost focus. Copy the suggestion instead.");
            }

            var replacementSequence = NativeMethods.GetClipboardSequenceNumber();
            NativeMethods.SendControlChord(VirtualKeyV);
            await Task.Delay(PasteProcessingDelayMilliseconds, cancellationToken);

            // Do not overwrite clipboard content created by another application
            // while the paste was being processed.
            restorePreviousClipboard =
                NativeMethods.GetClipboardSequenceNumber() == replacementSequence;
        }
        finally
        {
            if (restorePreviousClipboard)
            {
                _ = await clipboardBeforePaste.TryRestoreAsync();
            }
        }
    }

    /// <summary>Uses the target app's own undo, so it reverses exactly one paste.</summary>
    public async Task UndoAsync(SelectionContext context, CancellationToken cancellationToken = default)
    {
        if (!NativeMethods.IsWindow(context.TargetWindowHandle)
            || !await ActivateTargetWindowAsync(context.TargetWindowHandle, cancellationToken))
        {
            throw new InvalidOperationException("Windows could not return to the original app. Press Ctrl+Z there to undo.");
        }

        NativeMethods.SendControlChord(VirtualKeyZ);
    }

    private static async Task<bool> ActivateTargetWindowAsync(
        nint targetWindow,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FocusAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetForegroundWindow() == targetWindow)
            {
                return true;
            }

            _ = NativeMethods.SetForegroundWindow(targetWindow);
            await Task.Delay(FocusAttemptDelayMilliseconds, cancellationToken);
        }

        return NativeMethods.GetForegroundWindow() == targetWindow;
    }

    private static async Task<bool> WaitForClipboardChangeAsync(
        uint originalSequence,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetClipboardSequenceNumber() != originalSequence)
            {
                return true;
            }

            await Task.Delay(20, cancellationToken);
        }

        return false;
    }

    private static string? GetApplicationName(nint windowHandle)
    {
        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
