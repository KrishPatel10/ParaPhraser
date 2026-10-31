using System.Diagnostics;
using ParaPhraser.Core;

namespace ParaPhraser.Desktop.Services;

public sealed class RewriteCoordinator
{
    private readonly ClipboardSelectionService _selectionService;
    private readonly OverlayWindow _overlay;
    private readonly InstantToast _toast;
    private readonly SemaphoreSlim _singleOperation = new(1, 1);
    private ITextTransformer _transformer;

    public event EventHandler<string>? AiUnavailable;

    /// <summary>A rewrite worked, so the current model is a good fallback later.</summary>
    public event EventHandler? RewriteSucceeded;

    /// <summary>Finds a model to offer when the current one is busy, if any.</summary>
    public Func<Task<string?>>? FindFallbackModelAsync { get; set; }

    /// <summary>Switches to the model the user accepted.</summary>
    public Action<string>? UseModel { get; set; }

    public RewriteCoordinator(
        ClipboardSelectionService selectionService,
        ITextTransformer transformer,
        OverlayWindow overlay,
        InstantToast toast)
    {
        _selectionService = selectionService;
        _transformer = transformer;
        _overlay = overlay;
        _toast = toast;
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
            var context = await CaptureAsync(cancellationToken);
            if (context is not null)
            {
                ShowComposer(context, mode, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected exit path.
        }
        finally
        {
            _singleOperation.Release();
        }
    }

    /// <summary>
    /// Rewrites and pastes with no review step. It only applies a result that
    /// passes every check, and otherwise opens the overlay so nothing is lost.
    /// </summary>
    public async Task HandleInstantAsync(
        RewriteMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!await _singleOperation.WaitAsync(0, cancellationToken))
        {
            return;
        }

        SelectionContext? context = null;
        RichDocument? suggestion = null;
        Exception? failure = null;
        var clock = Stopwatch.StartNew();

        try
        {
            context = await CaptureAsync(cancellationToken);
            if (context is null)
            {
                return;
            }

            var selection = RichDocument.FromClipboard(context.Text, context.ClipboardHtml);
            AppLog.Info($"Started instant {mode} with {_transformer.Label} in {context.ApplicationName ?? "unknown app"}, "
                + $"{selection.ToPlainText().Length} characters");

            using var generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _toast.ShowWorking($"{GetActionLabel(mode)}…", generation.Cancel);

            var isHtml = selection.HasFormatting;
            var result = await _transformer.TransformAsync(
                new RewriteRequest(
                    isHtml ? selection.ToHtml() : selection.ToPlainText(),
                    mode,
                    GetDefaultInstruction(mode),
                    new RewriteContext(context.ApplicationName, context.WindowTitle),
                    RewriteTone.Original,
                    isHtml,
                    UsesDefaultInstruction: true),
                cancellationToken: generation.Token);

            suggestion = isHtml
                ? RichDocument.FromModelOutput(result.Text).MatchColorsTo(selection)
                : RichDocument.FromPlainText(result.Text);

            var missing = TextComparison.FindMissingDetails(selection.ToPlainText(), suggestion.ToPlainText());
            RewriteSucceeded?.Invoke(this, EventArgs.Empty);

            if (missing.Count > 0 || RichDocument.ContainsUnsupportedContent(context.ClipboardHtml))
            {
                AppLog.Info($"Instant {mode} sent for review: {missing.Count} details may be missing");
                _toast.Hide();
                var capturedContext = context;
                var reviewed = suggestion;
                ShowComposer(capturedContext, mode, cancellationToken);
                _overlay.ShowSuggestion(
                    reviewed,
                    GetReviewStatus(missing, $"{result.Provider} · {result.Duration.TotalSeconds:0.0} s"),
                    replacement => _selectionService.ReplaceAsync(capturedContext, replacement, cancellationToken),
                    content => _selectionService.CopyAsync(capturedContext, content, cancellationToken),
                    selection);
                return;
            }

            _toast.Hide();
            await _selectionService.ReplaceAsync(context, suggestion, cancellationToken);
            AppLog.Info($"Instant {mode} finished in {clock.Elapsed.TotalSeconds:0.0} s");

            var replacedContext = context;
            _toast.ShowFinished($"{GetActionLabel(mode)} done", () => _ = UndoAsync(replacedContext));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"Stopped instant {mode} after {clock.Elapsed.TotalSeconds:0.0} s");
            _toast.Hide();
        }
        catch (Exception exception)
        {
            AppLog.Error($"Instant {mode} failed", exception);
            _toast.Hide();
            failure = exception;
        }
        finally
        {
            _singleOperation.Release();
        }

        // Shown only after the lock is free, so Generate works straight away.
        if (failure is not null && context is not null)
        {
            ShowComposer(context, mode, cancellationToken);
            await ShowFailureAsync(failure);
        }
    }

    private async Task UndoAsync(SelectionContext context)
    {
        try
        {
            await _selectionService.UndoAsync(context);
            AppLog.Info("Instant replace undone");
        }
        catch (Exception exception)
        {
            AppLog.Error("Undo failed", exception);
            _overlay.ShowError(exception.Message, canRetry: false);
        }
    }

    private async Task<SelectionContext?> CaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            var context = await _selectionService.CaptureAsync(cancellationToken);
            if (context is null)
            {
                AppLog.Info("Hotkey pressed, but no selected text was captured");
                _overlay.ShowError(
                    "Select some editable text, then press the shortcut again.",
                    canRetry: false);
            }

            return context;
        }
        catch (SensitiveSelectionException exception)
        {
            AppLog.Info($"Capture refused: {exception.Message}");
            _overlay.ShowError(exception.Message, canRetry: false);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Error("Could not capture the selection", exception);
            _overlay.ShowError(exception.Message);
            return null;
        }
    }

    private void ShowComposer(SelectionContext context, RewriteMode mode, CancellationToken cancellationToken)
    {
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
        Exception? failure = null;

        try
        {
            _overlay.ShowGenerating(generation.Cancel);
            if (mode == RewriteMode.Format)
            {
                await FormatAsync(context, selection, instruction, generation.Token, cancellationToken);
                AppLog.Info($"Finished {mode} in {clock.Elapsed.TotalSeconds:0.0} s");
                RewriteSucceeded?.Invoke(this, EventArgs.Empty);
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
                    isHtml,
                    UsesDefaultInstruction: instruction == GetDefaultInstruction(mode)),
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
            RewriteSucceeded?.Invoke(this, EventArgs.Empty);

            var suggestion = isHtml
                ? RichDocument.FromModelOutput(result.Text).MatchColorsTo(selection)
                : RichDocument.FromPlainText(result.Text);
            var missing = TextComparison.FindMissingDetails(selection.ToPlainText(), suggestion.ToPlainText());

            _overlay.ShowSuggestion(
                suggestion,
                GetReviewStatus(missing, $"{result.Provider} · {result.Duration.TotalSeconds:0.0} s"),
                replacement => _selectionService.ReplaceAsync(context, replacement, cancellationToken),
                content => _selectionService.CopyAsync(context, content, cancellationToken),
                selection);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info($"Stopped {mode} after {clock.Elapsed.TotalSeconds:0.0} s");
            _overlay.ShowStopped();
        }
        catch (Exception exception)
        {
            AppLog.Error($"Failed {summary}", exception);
            failure = exception;
        }
        finally
        {
            _singleOperation.Release();
        }

        if (failure is not null)
        {
            await ShowFailureAsync(failure);
        }
    }

    private async Task ShowFailureAsync(Exception failure)
    {
        _overlay.ShowError(failure.Message);

        if (failure is AiUnavailableException)
        {
            AiUnavailable?.Invoke(this, failure.Message);
        }

        if (failure is ModelBusyException && FindFallbackModelAsync is not null)
        {
            var fallback = await FindFallbackModelAsync();
            if (fallback is not null)
            {
                _overlay.OfferFallback(fallback, () => UseModel?.Invoke(fallback));
            }
        }
    }

    private static string GetReviewStatus(IReadOnlyList<string> missing, string fallback) => missing.Count > 0
        ? $"Check before replacing, this may be missing: {string.Join(", ", missing)}"
        : fallback;

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

    private static string GetActionLabel(RewriteMode mode) => mode switch
    {
        RewriteMode.Grammar => "Grammar fix",
        RewriteMode.Shorten => "Shortening",
        _ => "Rewrite"
    };

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
