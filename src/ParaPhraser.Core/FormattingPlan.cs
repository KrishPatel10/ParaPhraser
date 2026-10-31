using System.Text.Json.Serialization;

namespace ParaPhraser.Core;

/// <summary>
/// Phrases to format. It names existing words rather than carrying text, so
/// applying it can never change what the writer wrote.
/// </summary>
public sealed record FormattingPlan(
    [property: JsonPropertyName("bold")] IReadOnlyList<string>? Bold = null,
    [property: JsonPropertyName("italic")] IReadOnlyList<string>? Italic = null,
    [property: JsonPropertyName("underline")] IReadOnlyList<string>? Underline = null,
    [property: JsonPropertyName("highlight")] IReadOnlyList<string>? Highlight = null,
    [property: JsonPropertyName("red")] IReadOnlyList<string>? Red = null);

public sealed record FormattingResult(
    FormattingPlan Plan,
    string Provider,
    TimeSpan Duration);
