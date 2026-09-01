using ParaPhraser.Core;

namespace ParaPhraser.Desktop.Services;

public sealed class RewriteCoordinator
{
    private readonly ClipboardSelectionService _selectionService;
    private readonly ITextTransformer _transformer;
    private readonly OverlayWindow _overlay;
    private readonly SemaphoreSlim _singleOperation = new(1, 1);

    public event EventHandler<string>? LocalAiUnavailable;

    public RewriteCoordinator(
        ClipboardSelectionService selectionService,
        ITextTransformer transformer,
        OverlayWindow overlay)
    {
        _selectionService = selectionService;
        _transformer = transformer;
        _overlay = overlay;
    }

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
                _overlay.ShowError(
                    "Select some editable text, then press the shortcut again.",
                    canRetry: false);
                return;
            }

            _overlay.ShowComposer(
                mode,
                context.Text,
                GetSourceLabel(context),
                GetDefaultInstruction(mode),
                (selectedText, instruction, selectedMode, tone) => GenerateAsync(
                    context,
                    selectedMode,
                    selectedText,
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
        string selectedText,
        string instruction,
        RewriteTone tone,
        CancellationToken cancellationToken)
    {
        if (!await _singleOperation.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            _overlay.ShowGenerating();
            var result = await _transformer.TransformAsync(
                new RewriteRequest(
                    selectedText,
                    mode,
                    instruction,
                    new RewriteContext(context.ApplicationName, context.WindowTitle),
                    tone),
                cancellationToken);

            _overlay.ShowSuggestion(
                result,
                () => _selectionService.ReplaceAsync(context, result.Text, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected exit path.
        }
        catch (LocalAiUnavailableException exception)
        {
            _overlay.ShowError(exception.Message);
            LocalAiUnavailable?.Invoke(this, exception.Message);
        }
        catch (Exception exception)
        {
            _overlay.ShowError(exception.Message);
        }
        finally
        {
            _singleOperation.Release();
        }
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
        _ => "Improve this text while preserving its meaning."
    };
}
