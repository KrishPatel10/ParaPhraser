namespace ParaPhraser.Core;

public sealed record RewriteContext(
    string? ApplicationName = null,
    string? WindowTitle = null,
    string? SurroundingText = null);
