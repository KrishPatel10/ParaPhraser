namespace ParaPhraser.Core;

public interface ITextTransformer
{
    Task<RewriteResult> TransformAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default);
}

