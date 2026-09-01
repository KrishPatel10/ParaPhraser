using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ParaPhraser.Core;

/// <summary>
/// A deterministic transformer used to validate the desktop interaction loop
/// before connecting a paid or local AI provider.
/// </summary>
public sealed partial class DemoTextTransformer : ITextTransformer
{
    public Task<RewriteResult> TransformAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var normalized = MultipleWhitespaceRegex().Replace(request.Text.Trim(), " ");

        var transformed = request.Mode switch
        {
            RewriteMode.Grammar => FixBasicGrammar(normalized),
            RewriteMode.Rewrite => Rewrite(normalized),
            RewriteMode.Shorten => Shorten(normalized),
            RewriteMode.Professional => MakeProfessional(normalized),
            _ => normalized
        };

        stopwatch.Stop();
        return Task.FromResult(new RewriteResult(transformed, "Built-in demo", stopwatch.Elapsed));
    }

    private static string FixBasicGrammar(string text)
    {
        var result = LowercaseStandaloneIRegex().Replace(text, "I");
        result = ImRegex().Replace(result, "I'm");
        result = DontRegex().Replace(result, "don't");
        result = CantRegex().Replace(result, "can't");

        if (char.IsLetter(result[0]))
        {
            result = char.ToUpperInvariant(result[0]) + result[1..];
        }

        if (!SentenceEndingRegex().IsMatch(result))
        {
            result += ".";
        }

        return result;
    }

    private static string Rewrite(string text)
    {
        var corrected = FixBasicGrammar(text);
        return corrected
            .Replace("I have made", "I've made", StringComparison.OrdinalIgnoreCase)
            .Replace("will be completed soon", "will be ready shortly", StringComparison.OrdinalIgnoreCase);
    }

    private static string Shorten(string text)
    {
        var corrected = FixBasicGrammar(text);
        if (corrected.Length <= 96)
        {
            return corrected;
        }

        var candidate = corrected[..93];
        var lastSpace = candidate.LastIndexOf(' ');
        return $"{candidate[..Math.Max(lastSpace, 1)]}…";
    }

    private static string MakeProfessional(string text)
    {
        var corrected = FixBasicGrammar(text);
        return corrected
            .Replace("Hey", "Hello", StringComparison.OrdinalIgnoreCase)
            .Replace("ASAP", "as soon as possible", StringComparison.OrdinalIgnoreCase)
            .Replace("soon", "shortly", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespaceRegex();

    [GeneratedRegex(@"\bi\b", RegexOptions.IgnoreCase)]
    private static partial Regex LowercaseStandaloneIRegex();

    [GeneratedRegex(@"\bim\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImRegex();

    [GeneratedRegex(@"\bdont\b", RegexOptions.IgnoreCase)]
    private static partial Regex DontRegex();

    [GeneratedRegex(@"\bcant\b", RegexOptions.IgnoreCase)]
    private static partial Regex CantRegex();

    [GeneratedRegex(@"[.!?…]$")]
    private static partial Regex SentenceEndingRegex();
}
