using System.Text.RegularExpressions;

namespace ParaPhraser.Core;

public enum ChangeKind
{
    Same,
    Added,
    Removed
}

public sealed record TextChange(ChangeKind Kind, string Text);

/// <summary>
/// Shows what a rewrite changed, because models quietly drop details.
/// </summary>
public static partial class TextComparison
{
    // ponytail: the comparison table grows with the square of the text, so very
    // long changed sections show as replaced whole. Use a linear diff if needed.
    private const int MaxTokensToCompare = 4000;

    private static readonly string[] DateWords =
    [
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "today", "tonight", "tomorrow", "yesterday"
    ];

    // Only counted when capitalized, because "march on" and "may help" are not dates.
    private static readonly string[] MonthWords =
    [
        "January", "February", "March", "April", "May", "June", "July", "August",
        "September", "October", "November", "December"
    ];

    public static IReadOnlyList<TextChange> Compare(string original, string revised)
    {
        var before = Tokenize(original);
        var after = Tokenize(revised);

        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < before.Length - prefix
            && suffix < after.Length - prefix
            && before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }

        var changes = new List<TextChange>();
        Add(changes, ChangeKind.Same, before[..prefix]);
        CompareMiddle(
            before[prefix..(before.Length - suffix)],
            after[prefix..(after.Length - suffix)],
            changes);
        Add(changes, ChangeKind.Same, before[(before.Length - suffix)..]);
        return changes;
    }

    /// <summary>
    /// Finds numbers, dates, names, and links from the original that the result
    /// no longer contains. It can miss a detail written in lowercase, so it
    /// supports reading the changes rather than replacing that.
    /// </summary>
    public static IReadOnlyList<string> FindMissingDetails(string original, string revised)
    {
        var revisedDigits = revised.Replace(",", string.Empty, StringComparison.Ordinal);
        var missing = new List<string>();

        foreach (Match number in NumberRegex().Matches(original))
        {
            var digits = number.Value.Replace(",", string.Empty, StringComparison.Ordinal);
            if (!Regex.IsMatch(revisedDigits, $@"(?<!\d){Regex.Escape(digits)}(?!\d)"))
            {
                missing.Add(number.Value);
            }
        }

        foreach (Match link in LinkRegex().Matches(original))
        {
            if (!revised.Contains(link.Value.TrimEnd('.', ','), StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(link.Value.TrimEnd('.', ','));
            }
        }

        foreach (Match word in WordRegex().Matches(original))
        {
            var isDate = DateWords.Contains(word.Value, StringComparer.OrdinalIgnoreCase)
                || MonthWords.Contains(word.Value, StringComparer.Ordinal);
            var isName = char.IsUpper(word.Value[0]) && !IsSentenceStart(original, word.Index);
            if ((isDate || isName)
                && !Regex.IsMatch(revised, $@"\b{Regex.Escape(word.Value)}", RegexOptions.IgnoreCase))
            {
                missing.Add(word.Value);
            }
        }

        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsSentenceStart(string text, int index)
    {
        for (var position = index - 1; position >= 0; position--)
        {
            if (!char.IsWhiteSpace(text[position]))
            {
                return text[position] is '.' or '!' or '?' or ':' or '•';
            }
        }

        return true;
    }

    private static string[] Tokenize(string text) =>
        TokenRegex().Matches(text).Select(match => match.Value).ToArray();

    private static void CompareMiddle(string[] before, string[] after, List<TextChange> changes)
    {
        if (before.Length > MaxTokensToCompare || after.Length > MaxTokensToCompare)
        {
            Add(changes, ChangeKind.Removed, before);
            Add(changes, ChangeKind.Added, after);
            return;
        }

        var lengths = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = before[i] == after[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var beforeIndex = 0;
        var afterIndex = 0;
        while (beforeIndex < before.Length || afterIndex < after.Length)
        {
            if (beforeIndex < before.Length && afterIndex < after.Length && before[beforeIndex] == after[afterIndex])
            {
                Add(changes, ChangeKind.Same, [before[beforeIndex]]);
                beforeIndex++;
                afterIndex++;
            }
            else if (afterIndex == after.Length
                || (beforeIndex < before.Length && lengths[beforeIndex + 1, afterIndex] >= lengths[beforeIndex, afterIndex + 1]))
            {
                Add(changes, ChangeKind.Removed, [before[beforeIndex++]]);
            }
            else
            {
                Add(changes, ChangeKind.Added, [after[afterIndex++]]);
            }
        }
    }

    private static void Add(List<TextChange> changes, ChangeKind kind, string[] tokens)
    {
        var text = string.Concat(tokens);
        if (text.Length == 0)
        {
            return;
        }

        if (changes.Count > 0 && changes[^1].Kind == kind)
        {
            changes[^1] = changes[^1] with { Text = changes[^1].Text + text };
        }
        else
        {
            changes.Add(new TextChange(kind, text));
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}_']+|\s+|[^\p{L}\p{N}_'\s]")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\d[\d,]*\d|\d")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"https?://\S+|[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\b\p{L}{2,}\b")]
    private static partial Regex WordRegex();
}
