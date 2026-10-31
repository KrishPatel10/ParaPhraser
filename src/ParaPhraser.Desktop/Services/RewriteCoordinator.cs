using System.Diagnostics;
using ParaPhraser.Core;

namespace ParaPhraser.Desktop.Services;

public sealed class RewriteCoordinator
{
    private readonly ClipboardSelectionService _selectionService;
    private readonly OverlayWindow _overlay;
    private readonly SemaphoreSlim _singleOperation = new(1, 1);
    private ITextTransformer _transformer;

    public event EventHandler<string>? AiUnavailable;

    public RewriteCoordinator(
        ClipboardSelectionService selectionService,
        ITextTransformer transformer,
        OverlayWindow overlay)
    {
        _selectionService = selectionService;
        _transformer = transformer;
        _overlay = overlay;
    }

    public void UseTransformer(ITextTransformer transformer) => _transformer = transformer;

    private const int PartialTextIntervalMilliseconds = 80;

    public async Task HandleAsync(
        RewriteMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!await _singleOperation.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var context = await _selectionService.CaptureAsync(cancellationToken);
            if (context is null)
            {
                AppLog.Info("Hotkey pressed, but no selected text was captured");
                _overlay.ShowError(
                    "Select some editable text, then press the shortcut again.",
                    canRetry: false);
                return;
            }

            _overlay.ShowComposer(
                mode,
                RichDocument.FromClipboard(context.Text, context.ClipboardHtml),
                GetSourceLabel(context),
                GetDefaultInstruction(mode),
                RichDocument.ContainsUnsupportedContent(context.ClipboardHtml)
                    ? "Images and tables in the selection won't be kept"
                    : string.Empty,
                (selection, instruction, selectedMode, tone) => GenerateAsync(
                    context,
                    selectedMode,
                    selection,
                    instruction,
                    tone,
                    cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected exit path.
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not capture the selection", exception);
            _overlay.ShowError(exception.Message);
        }
        finally
        {
            _singleOperation.Release();
        }
    }

    private async Task GenerateAsync(
        SelectionContext context,
        RewriteMode mode,
        RichDocument selection,
        string instruction,
        RewriteTone tone,
        CancellationToken cancellationToken)
    {
        if (!await _singleOperation.WaitAsync(0, cancellationToken))
        {
            return;
        }

        using var generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clock = Stopwatch.StartNew();
        var summary = $"{mode} with {_transformer.Label} in {context.ApplicationName ?? "unknown app"}, "
            + $"{selection.ToPlainText().Length} characters, formatted: {selection.HasFormatting}";
        AppLog.Info($"Started {summary}");

        try
        {
            _overlay.ShowGenerating(generation.Cancel);
            if (mode == RewriteMode.Format)
            {
                await FormatAsync(context, selection, instruction, generation.Token, cancellationToken);
                AppLog.Info($"Finished {mode} in {clock.Elapsed.TotalSeconds:0.0} s");
                return;
            }

            var isHtml = selection.HasFormatting;

            // Rebuilding the overlay's document on every token would stutter.
            var sinceLastUpdate = Stopwatch.StartNew();
            var result = await _transformer.TransformAsync(
                new RewriteRequest(
                    isHtml ? selection.ToHtml() : selection.ToPlainText(),
                    mode,
                    instruction,
                    new RewriteContext(context.ApplicationName, context.WindowTitle),
                    tone,
                    isHtml),
                partial =>
                {
                    if (sinceLastUpdate.ElapsedMilliseconds >= PartialTextIntervalMilliseconds)
                    {
                        sinceLastUpdate.Restart();
                        _overlay.ShowPartial(isHtml
                            ? RichDocument.FromPartialModelOutput(partial)
                            : RichDocument.FromPlainText(partial.TrimStart()));
                    }
                },
                generation.Token);

            AppLog.Info($"Finished {mode} in {clock.Elapsed.TotalSeconds:0.0} s, {result.Text.Length} characters");
            _overlay.ShowSuggestion(
                isHtml
                    ? RichDocument.FromModelOutput(result.Text).MatchColorsTo(selection)
                    : RichDocument.FromPlainText(result.Text),
                $"{result.Provider} · {result.Duration.TotalSeconds:0.0} s",
                replacement => _selectionService.ReplaceAsync(context, replacement, cancellationToken),
                content => _selectionService.CopyAsync(context, content, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"Stopped {mode} after {clock.Elapsed.TotalSeconds:0.0} s");
            _overlay.ShowStopped();
        }
        catch (AiUnavailableException exception)
        {
            AppLog.Error($"Failed {summary}", exception);
            _overlay.ShowError(exception.Message);
            AiUnavailable?.Invoke(this, exception.Message);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Failed {summary}", exception);
            _overlay.ShowError(exception.Message);
        }
        finally
        {
            _singleOperation.Release();
        }
    }

    private async Task FormatAsync(
        SelectionContext context,
        RichDocument selection,
        string instruction,
        CancellationToken generationToken,
        CancellationToken cancellationToken)
    {
        var result = await _transformer.SuggestFormattingAsync(
            selection.ToPlainText(),
            instruction,
            generationToken);
        var formatted = selection.ApplyFormatting(
            result.Plan,
            followModelStrictly: instruction != GetDefaultInstruction(RewriteMode.Format));

        var status = formatted.ToHtml() == selection.ToHtml()
            ? "No formatting needed"
            : context.ClipboardHtml is null
                ? "This app may not keep formatting. Copy it into one that does."
                : $"{result.Provider} · {result.Duration.TotalSeconds:0.0} s";

        _overlay.ShowSuggestion(
            formatted,
            status,
            replacement => _selectionService.ReplaceAsync(context, replacement, cancellationToken),
            content => _selectionService.CopyAsync(context, content, cancellationToken));
    }

    private static string GetSourceLabel(SelectionContext context)
    {
        var application = string.IsNullOrWhiteSpace(context.ApplicationName)
            ? "Current application"
            : context.ApplicationName;

        return string.IsNullOrWhiteSpace(context.WindowTitle)
            ? application
            : $"{application} · {context.WindowTitle}";
    }

    private static string GetDefaultInstruction(RewriteMode mode) => mode switch
    {
        RewriteMode.Grammar => "Correct grammar, spelling, and punctuation. Preserve the meaning and tone.",
        RewriteMode.Rewrite => "Rewrite this clearly and naturally. Preserve the meaning and important details.",
        RewriteMode.Shorten => "Make this concise while preserving the important information.",
        RewriteMode.Professional => "Rewrite this in a polished, professional tone. Preserve the meaning.",
        RewriteMode.Format => "Add bold, italic, underline, color, and highlight where they help. Keep every word unchanged.",
        _ => "Improve this text while preserving its meaning."
    };
}
