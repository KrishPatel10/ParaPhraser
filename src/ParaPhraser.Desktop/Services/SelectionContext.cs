namespace ParaPhraser.Desktop.Services;

public sealed record SelectionContext(
    string Text,
    nint TargetWindowHandle,
    string? ApplicationName = null,
    string? WindowTitle = null);
