namespace ParaPhraser.Core;

public interface ITextTransformer
{
    Task<RewriteResult> TransformAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default);

    Task<FormattingResult> SuggestFormattingAsync(
        string text,
        string? instruction,
        CancellationToken cancellationToken = default);
}

