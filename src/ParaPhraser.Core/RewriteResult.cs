namespace ParaPhraser.Core;

public sealed record RewriteResult(
    string Text,
    string Provider,
    TimeSpan Duration);

