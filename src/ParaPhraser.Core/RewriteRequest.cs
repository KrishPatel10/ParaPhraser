namespace ParaPhraser.Core;

public sealed record RewriteRequest(
    string Text,
    RewriteMode Mode,
    string? Instruction = null,
    RewriteContext? Context = null,
    RewriteTone Tone = RewriteTone.Original);
