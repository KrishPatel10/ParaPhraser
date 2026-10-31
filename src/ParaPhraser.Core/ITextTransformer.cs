namespace ParaPhraser.Core;

public interface ITextTransformer
{
    /// <summary>The provider and model, for logs and status text.</summary>
    string Label { get; }

    /// <param name="request">The text and action to perform.</param>
    /// <param name="onText">Receives the result so far as it is written.</param>
    /// <param name="cancellationToken">Stops the work and any provider request.</param>
    Task<RewriteResult> TransformAsync(
        RewriteRequest request,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default);

    Task<FormattingResult> SuggestFormattingAsync(
        string text,
        string? instruction,
        CancellationToken cancellationToken = default);
}

