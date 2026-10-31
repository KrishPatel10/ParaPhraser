using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ParaPhraser.Core;

public enum RichBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Numbered
}

public sealed record RichRun(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    string? Link = null,
    string? Color = null,
    string? Highlight = null);

public sealed record RichBlock(RichBlockKind Kind, IReadOnlyList<RichRun> Runs);

/// <summary>
/// The formatting ParaPhraser keeps across capture, the model, and paste.
/// HTML is reduced to this model on the way in, so clipboard markup and model
/// output pass through the same allowlist.
/// </summary>
public sealed partial class RichDocument
{
    private const string FragmentStart = "<!--StartFragment-->";
    private const string FragmentEnd = "<!--EndFragment-->";

    // Browsers stamp a color on every run, so black, white, and near-grey text
    // or backgrounds count as the app's default rather than as formatting.
    private const int NeutralColorTolerance = 40;

    private const int MaxEmphasisLength = 60;

    private const int MaxColoredPhrases = 2;

    // Without mso-highlight, Word pastes a background as shading, which its
    // Highlight tool cannot remove.
    private static readonly Dictionary<string, string> WordHighlights = new()
    {
        ["#ffff00"] = "yellow",
        ["#00ff00"] = "lime",
        ["#00ffff"] = "aqua",
        ["#ff00ff"] = "fuchsia",
        ["#0000ff"] = "blue",
        ["#ff0000"] = "red",
        ["#000080"] = "navy",
        ["#008080"] = "teal",
        ["#008000"] = "green",
        ["#800080"] = "purple",
        ["#800000"] = "maroon",
        ["#808000"] = "olive"
    };

    public RichDocument(IReadOnlyList<RichBlock> blocks)
    {
        Blocks = blocks;
    }

    public IReadOnlyList<RichBlock> Blocks { get; }

    public bool HasFormatting => Blocks.Any(block =>
        block.Kind != RichBlockKind.Paragraph
        || block.Runs.Any(run => run.Bold
            || run.Italic
            || run.Underline
            || run.Link is not null
            || run.Color is not null
            || run.Highlight is not null));

    public static RichDocument FromPlainText(string text) => new(text
        .ReplaceLineEndings("\n")
        .Split('\n')
        .Select(line => new RichBlock(
            RichBlockKind.Paragraph,
            line.Length == 0 ? [] : [new RichRun(line)]))
        .ToArray());

    /// <summary>
    /// Prefers the clipboard's plain text unless the HTML carries formatting,
    /// so unformatted selections keep their exact spacing.
    /// </summary>
    public static RichDocument FromClipboard(string plainText, string? clipboardHtml)
    {
        if (SplitClipboardHtml(clipboardHtml) is { } parts)
        {
            var document = ParseHtml(parts.Fragment);
            if (document.HasFormatting)
            {
                return document;
            }
        }

        return FromPlainText(plainText);
    }

    /// <summary>
    /// Tables and images have no equivalent here, so Replace would drop them.
    /// </summary>
    public static bool ContainsUnsupportedContent(string? clipboardHtml) =>
        SplitClipboardHtml(clipboardHtml) is { } parts
        && UnsupportedContentRegex().IsMatch(parts.Fragment);

    public static RichDocument FromModelOutput(string output)
    {
        var text = CodeFenceRegex().Replace(output.Trim(), "$1");
        if (!AnyTagRegex().IsMatch(text))
        {
            text = FromPlainText(text).ToHtml();
        }

        // Models fall back to Markdown even when told not to, and asterisks
        // must not end up in the document.
        text = MarkdownBoldRegex().Replace(text, "<b>$1</b>");
        text = MarkdownItalicRegex().Replace(text, "<i>$1</i>");
        return ParseHtml(text);
    }

    public static RichDocument ParseHtml(string html) => new HtmlReader().Read(html).Document;

    /// <summary>
    /// The model can drop a span or invent one even when told not to. Colors
    /// the source never had are removed, and each one it did have goes back on
    /// its words if they survived the rewrite.
    /// </summary>
    public RichDocument MatchColorsTo(RichDocument source)
    {
        var sourceRuns = source.Blocks.SelectMany(block => block.Runs).ToArray();
        var blocks = Blocks
            .Select(block => block with
            {
                Runs = block.Runs
                    .Select(run => run with
                    {
                        Color = sourceRuns.Any(sourceRun => sourceRun.Color == run.Color) ? run.Color : null,
                        Highlight = sourceRuns.Any(sourceRun => sourceRun.Highlight == run.Highlight) ? run.Highlight : null
                    })
                    .ToArray()
            })
            .ToArray();
        var coloredRuns = sourceRuns
            .Where(run => run.Color is not null || run.Highlight is not null);

        foreach (var colored in coloredRuns)
        {
            // ponytail: exact words only, a reworded phrase stays uncolored for the picker.
            var words = colored.Text.Trim();
            var isAlreadyColored = blocks.Any(block => block.Runs.Any(run =>
                run.Color == colored.Color
                && run.Highlight == colored.Highlight
                && run.Text.Contains(words, StringComparison.OrdinalIgnoreCase)));
            if (words.Length == 0 || isAlreadyColored)
            {
                continue;
            }

            ApplyToFirstMatch(blocks, words, run => run with
            {
                Color = run.Color ?? colored.Color,
                Highlight = run.Highlight ?? colored.Highlight
            });
        }

        return new RichDocument(blocks);
    }

    /// <summary>
    /// Formats phrases already in the text, so it can never change a word.
    /// Phrases that are missing, long, or whole sentences are ignored.
    /// </summary>
    public RichDocument ApplyFormatting(FormattingPlan plan, bool followModelStrictly = false)
    {
        var blocks = Blocks.ToArray();

        // The model always finds something ("lunch place was great"), so unless
        // the writer asked for specific formatting, each pick must earn its
        // place: bold needs a real detail, red a real warning, and a highlight
        // needs the text to have a detail or warning worth drawing attention to.
        var boldCount = ApplyToPhrases(
            blocks,
            (plan.Bold ?? []).Where(phrase => followModelStrictly || SpecificDetailRegex().IsMatch(phrase)),
            run => run with { Bold = true });
        ApplyToPhrases(blocks, plan.Italic ?? [], run => run with { Italic = true });
        ApplyToPhrases(blocks, plan.Underline ?? [], run => run with { Underline = true });
        var redCount = ApplyToPhrases(
            blocks,
            (plan.Red ?? [])
                .Where(phrase => followModelStrictly || WarningRegex().IsMatch(phrase))
                .Take(MaxColoredPhrases),
            run => run with { Color = run.Color ?? "#c00000" });

        if (followModelStrictly || boldCount + redCount > 0)
        {
            ApplyToPhrases(
                blocks,
                (plan.Highlight ?? []).Take(MaxColoredPhrases),
                run => run with { Highlight = run.Highlight ?? "#ffff00" });
        }

        return new RichDocument(blocks);
    }

    private static int ApplyToPhrases(RichBlock[] blocks, IEnumerable<string> phrases, Func<RichRun, RichRun> change)
    {
        var applied = 0;
        foreach (var phrase in phrases.Select(phrase => phrase.Trim()))
        {
            if (phrase.Length is > 0 and <= MaxEmphasisLength
                && phrase[^1] is not ('.' or '!' or '?')
                && ApplyToFirstMatch(blocks, phrase, change))
            {
                applied++;
            }
        }

        return applied;
    }

    private static bool ApplyToFirstMatch(RichBlock[] blocks, string words, Func<RichRun, RichRun> change)
    {
        var pattern = $@"(?<!\w){Regex.Escape(words)}(?!\w)";
        for (var index = 0; index < blocks.Length; index++)
        {
            var text = string.Concat(blocks[index].Runs.Select(run => run.Text));
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                blocks[index] = blocks[index] with
                {
                    Runs = ApplyFormat(blocks[index].Runs, match.Index, match.Index + match.Length, change)
                };
                return true;
            }
        }

        return false;
    }

    private static List<RichRun> ApplyFormat(
        IReadOnlyList<RichRun> runs,
        int start,
        int end,
        Func<RichRun, RichRun> change)
    {
        var result = new List<RichRun>();
        var offset = 0;
        foreach (var run in runs)
        {
            var from = Math.Clamp(start - offset, 0, run.Text.Length);
            var to = Math.Clamp(end - offset, 0, run.Text.Length);
            offset += run.Text.Length;

            result.Add(run with { Text = run.Text[..from] });
            result.Add(change(run with { Text = run.Text[from..to] }));
            result.Add(run with { Text = run.Text[to..] });
        }

        return result.Where(run => run.Text.Length > 0).ToList();
    }

    /// <summary>
    /// Returns a color as lowercase #rrggbb, or null for a neutral or unreadable color.
    /// </summary>
    public static string? NormalizeColor(string value) =>
        TryParseColor(value, out var color) ? color : null;

    public string ToPlainText()
    {
        var lines = new List<string>();
        var number = 0;
        foreach (var block in Blocks)
        {
            number = block.Kind == RichBlockKind.Numbered ? number + 1 : 0;
            var prefix = block.Kind switch
            {
                RichBlockKind.Bullet => "• ",
                RichBlockKind.Numbered => $"{number}. ",
                _ => string.Empty
            };
            lines.Add(prefix + string.Concat(block.Runs.Select(run => run.Text)));
        }

        return string.Join(Environment.NewLine, lines).ReplaceLineEndings();
    }

    public string ToHtml() => ToHtml(string.Empty, string.Empty, forClipboard: false);

    /// <summary>
    /// Builds a CF_HTML payload. The rewrite is placed inside the source's own
    /// document, so style sheets the fragment depends on (Word keeps its fonts
    /// there) still apply when the target reads it.
    /// </summary>
    public string ToClipboardHtml(string? sourceClipboardHtml = null)
    {
        var parts = SplitClipboardHtml(sourceClipboardHtml)
            ?? new ClipboardHtmlParts("<html><body>", string.Empty, "</body></html>");
        var source = new HtmlReader().Read(parts.Fragment);
        var blockAttributes = parts.Fragment.Contains("class=MsoNormal", StringComparison.Ordinal)
            ? " class=MsoNormal"
            : string.Empty;

        // ponytail: one font for the whole rewrite, per-run fonts need the model to return them.
        // A source fragment with no closing block tag means the selection stopped
        // mid-paragraph. Ending inline keeps the rest of that paragraph in place.
        var endsMidParagraph = parts.Fragment.Length > 0 && !BlockEndRegex().IsMatch(parts.Fragment);
        var fragment = endsMidParagraph && Blocks[^1].Kind == RichBlockKind.Paragraph
            ? new RichDocument(Blocks.SkipLast(1).ToArray()).ToHtml(blockAttributes, source.FontStyle, forClipboard: true)
                + RenderRuns(Blocks[^1], source.FontStyle, forClipboard: true)
            : ToHtml(blockAttributes, source.FontStyle, forClipboard: true);

        const string headerFormat =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var before = parts.Before + FragmentStart;
        var after = FragmentEnd + parts.After;

        // CF_HTML offsets count UTF-8 bytes, not characters.
        var startHtml = Encoding.ASCII.GetByteCount(string.Format(headerFormat, 0, 0, 0, 0));
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);

        return string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment)
            + before
            + fragment
            + after;
    }

    private string ToHtml(string blockAttributes, string fontStyle, bool forClipboard)
    {
        var html = new StringBuilder();
        string? openList = null;
        foreach (var block in Blocks)
        {
            var list = block.Kind switch
            {
                RichBlockKind.Bullet => "ul",
                RichBlockKind.Numbered => "ol",
                _ => null
            };
            if (list != openList)
            {
                html.Append(openList is null ? string.Empty : $"</{openList}>\n");
                html.Append(list is null ? string.Empty : $"<{list}>\n");
                openList = list;
            }

            var (tag, attributes, style) = block.Kind switch
            {
                RichBlockKind.Heading1 => ("h1", string.Empty, string.Empty),
                RichBlockKind.Heading2 => ("h2", string.Empty, string.Empty),
                RichBlockKind.Heading3 => ("h3", string.Empty, string.Empty),
                RichBlockKind.Paragraph => ("p", blockAttributes, fontStyle),
                _ => ("li", blockAttributes, fontStyle)
            };
            html.Append($"<{tag}{attributes}>")
                .Append(block.Runs.Count == 0 ? "<br>" : RenderRuns(block, style, forClipboard))
                .Append($"</{tag}>\n");
        }

        html.Append(openList is null ? string.Empty : $"</{openList}>\n");
        return html.ToString().TrimEnd();
    }

    private static string RenderRuns(RichBlock block, string fontStyle, bool forClipboard)
    {
        var html = new StringBuilder();
        foreach (var run in block.Runs)
        {
            var text = Escape(run.Text).Replace("\n", "<br>", StringComparison.Ordinal);
            text = run.Underline ? $"<u>{text}</u>" : text;
            text = run.Italic ? $"<i>{text}</i>" : text;
            text = run.Bold ? $"<b>{text}</b>" : text;
            var colors = string.Join(
                ';',
                new[]
                {
                    run.Color is null ? null : $"color:{run.Color}",
                    run.Highlight is null ? null : $"background-color:{run.Highlight}",
                    forClipboard && run.Highlight is not null
                        && WordHighlights.TryGetValue(run.Highlight, out var wordHighlight)
                            ? $"mso-highlight:{wordHighlight}"
                            : null
                }.OfType<string>());
            text = colors.Length == 0 ? text : $"<span style=\"{colors}\">{text}</span>";
            html.Append(run.Link is null
                ? text
                : $"<a href=\"{WebUtility.HtmlEncode(run.Link)}\">{text}</a>");
        }

        return fontStyle.Length == 0
            ? html.ToString()
            : $"<span style=\"{WebUtility.HtmlEncode(fontStyle)}\">{html}</span>";
    }

    /// <summary>
    /// Returns false for values that leave the inherited color in place, such
    /// as transparent. A neutral color returns true with a null result.
    /// </summary>
    private static bool TryParseColor(string value, out string? color)
    {
        color = null;
        value = value.Trim();

        System.Drawing.Color parsed;
        var rgb = RgbColorRegex().Match(value);
        if (rgb.Success)
        {
            var alpha = rgb.Groups["alpha"].Value.TrimEnd('%');
            if (double.TryParse(alpha, CultureInfo.InvariantCulture, out var opacity) && opacity == 0)
            {
                return false;
            }

            parsed = System.Drawing.Color.FromArgb(
                Math.Min(int.Parse(rgb.Groups["red"].Value), 255),
                Math.Min(int.Parse(rgb.Groups["green"].Value), 255),
                Math.Min(int.Parse(rgb.Groups["blue"].Value), 255));
        }
        else if (HexColorRegex().IsMatch(value))
        {
            parsed = System.Drawing.ColorTranslator.FromHtml(value);
        }
        else
        {
            parsed = System.Drawing.Color.FromName(value);
            if (!parsed.IsKnownColor || parsed.IsSystemColor || parsed.A == 0)
            {
                return false;
            }
        }

        int[] channels = [parsed.R, parsed.G, parsed.B];
        if (channels.Max() - channels.Min() > NeutralColorTolerance)
        {
            color = $"#{parsed.R:x2}{parsed.G:x2}{parsed.B:x2}";
        }

        return true;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static ClipboardHtmlParts? SplitClipboardHtml(string? clipboardHtml)
    {
        if (string.IsNullOrEmpty(clipboardHtml))
        {
            return null;
        }

        var start = clipboardHtml.IndexOf(FragmentStart, StringComparison.OrdinalIgnoreCase);
        var end = clipboardHtml.IndexOf(FragmentEnd, StringComparison.OrdinalIgnoreCase);
        var documentStart = clipboardHtml.IndexOf('<');
        if (start < 0 || end < start)
        {
            return null;
        }

        return new ClipboardHtmlParts(
            clipboardHtml[documentStart..start],
            clipboardHtml[(start + FragmentStart.Length)..end],
            clipboardHtml[(end + FragmentEnd.Length)..]);
    }

    private sealed record ClipboardHtmlParts(string Before, string Fragment, string After);

    private readonly record struct RunFormat(
        bool Bold,
        bool Italic,
        bool Underline,
        string? Link,
        string? Color,
        string? Highlight);

    private sealed record OpenTag(string Name, RunFormat PreviousFormat, RichBlockKind? Kind);

    private sealed class HtmlReader
    {
        private readonly List<RichBlock> _blocks = [];
        private readonly List<RichRun> _runs = [];
        private readonly List<OpenTag> _open = [];
        private RunFormat _format;
        private string? _fontFamily;
        private string? _fontSize;

        public (RichDocument Document, string FontStyle) Read(string html)
        {
            var position = 0;
            foreach (Match token in TokenRegex().Matches(html))
            {
                AddText(WebUtility.HtmlDecode(html[position..token.Index]));
                position = token.Index + token.Length;

                if (token.Groups["marker"].Success)
                {
                    ApplyListMarker(token.Groups["marker"].Value);
                }
                else if (token.Groups["name"].Success)
                {
                    HandleTag(
                        token.Groups["name"].Value.ToLowerInvariant(),
                        token.Groups["close"].Length > 0,
                        token.Groups["attributes"].Value);
                }
            }

            AddText(WebUtility.HtmlDecode(html[position..]));
            FlushBlock();

            var fontStyle = string.Join(
                ';',
                new[]
                {
                    _fontFamily is null ? null : $"font-family:{_fontFamily}",
                    _fontSize is null ? null : $"font-size:{_fontSize}"
                }.OfType<string>());
            return (new RichDocument(_blocks), fontStyle);
        }

        private void HandleTag(string name, bool isClosing, string attributes)
        {
            if (isClosing)
            {
                CloseTag(name);
                return;
            }

            if (name == "br")
            {
                AddRun("\n");
                return;
            }

            if (name is "td" or "th")
            {
                AddText(" ");
            }

            if (IsBlockTag(name))
            {
                FlushBlock();
            }

            if (IsVoidTag(name) || attributes.EndsWith('/'))
            {
                return;
            }

            var style = GetAttribute(attributes, "style");
            _open.Add(new OpenTag(name, _format, GetBlockKind(name, style)));
            _format = ApplyTag(_format, name, attributes);
            ApplyStyle(style);
        }

        private void CloseTag(string name)
        {
            var index = _open.FindLastIndex(tag => tag.Name == name);
            if (index < 0)
            {
                return;
            }

            if (IsBlockTag(name))
            {
                FlushBlock();
            }

            _format = _open[index].PreviousFormat;
            _open.RemoveRange(index, _open.Count - index);
        }

        private RichBlockKind? GetBlockKind(string name, string style) => name switch
        {
            "h1" => RichBlockKind.Heading1,
            "h2" => RichBlockKind.Heading2,
            "h3" or "h4" or "h5" or "h6" => RichBlockKind.Heading3,
            "li" => _open.LastOrDefault(tag => tag.Name is "ul" or "ol")?.Name == "ol"
                ? RichBlockKind.Numbered
                : RichBlockKind.Bullet,
            "p" when style.Contains("mso-list:", StringComparison.OrdinalIgnoreCase) => RichBlockKind.Bullet,
            _ => null
        };

        // Word writes lists as styled paragraphs, with the marker as literal text.
        private void ApplyListMarker(string marker)
        {
            var text = WebUtility.HtmlDecode(AnyTagRegex().Replace(marker, string.Empty));
            var index = _open.FindLastIndex(tag => tag.Kind == RichBlockKind.Bullet);
            if (index >= 0 && NumberedMarkerRegex().IsMatch(text))
            {
                _open[index] = _open[index] with { Kind = RichBlockKind.Numbered };
            }
        }

        private static RunFormat ApplyTag(RunFormat format, string name, string attributes) => name switch
        {
            "b" or "strong" => format with { Bold = true },
            "i" or "em" => format with { Italic = true },
            "u" or "ins" => format with { Underline = true },
            "a" => format with { Link = GetSafeLink(attributes) },
            "mark" => format with { Highlight = "#ffff00" },
            "font" when TryParseColor(GetAttribute(attributes, "color"), out var color) =>
                format with { Color = color },
            _ => format
        };

        private void ApplyStyle(string style)
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = declaration.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var value = declaration[(separator + 1)..].Trim();
                switch (declaration[..separator].Trim().ToLowerInvariant())
                {
                    case "font-weight":
                        _format = _format with { Bold = BoldWeightRegex().IsMatch(value) };
                        break;
                    case "font-style":
                        _format = _format with
                        {
                            Italic = value.StartsWith("italic", StringComparison.OrdinalIgnoreCase)
                                || value.StartsWith("oblique", StringComparison.OrdinalIgnoreCase)
                        };
                        break;
                    case "text-decoration" or "text-decoration-line":
                        _format = _format with
                        {
                            Underline = value.Contains("underline", StringComparison.OrdinalIgnoreCase)
                        };
                        break;
                    case "color" when TryParseColor(value, out var color):
                        _format = _format with { Color = color };
                        break;
                    case "background" or "background-color" when TryParseColor(value, out var highlight):
                        _format = _format with { Highlight = highlight };
                        break;
                    case "font-family" when !IsInHeading():
                        _fontFamily ??= value;
                        break;
                    case "font-size" when !IsInHeading():
                        _fontSize ??= value;
                        break;
                }
            }
        }

        private bool IsInHeading() => CurrentBlockKind()
            is RichBlockKind.Heading1 or RichBlockKind.Heading2 or RichBlockKind.Heading3;

        private RichBlockKind CurrentBlockKind() =>
            _open.LastOrDefault(tag => tag.Kind is not null)?.Kind ?? RichBlockKind.Paragraph;

        private void AddText(string text)
        {
            text = WhitespaceRegex().Replace(text, " ");
            var hasText = _runs.Count > 0 && _runs[^1].Text.Length > 0;
            if (text.Length == 0 || (text == " " && !hasText))
            {
                return;
            }

            if (hasText && text[0] == ' ' && char.IsWhiteSpace(_runs[^1].Text[^1]))
            {
                text = text[1..];
            }

            AddRun(text);
        }

        private void AddRun(string text)
        {
            if (text.Length == 0)
            {
                return;
            }

            // Links already look like links, and headings are already bold.
            var run = new RichRun(
                text,
                _format.Bold && !IsInHeading(),
                _format.Italic,
                _format.Underline && _format.Link is null,
                _format.Link,
                _format.Link is null ? _format.Color : null,
                _format.Highlight);

            if (_runs.Count > 0 && _runs[^1] with { Text = string.Empty } == run with { Text = string.Empty })
            {
                _runs[^1] = _runs[^1] with { Text = _runs[^1].Text + text };
            }
            else
            {
                _runs.Add(run);
            }
        }

        private void FlushBlock()
        {
            var firstIndex = _runs.FindIndex(run => !string.IsNullOrWhiteSpace(run.Text));
            var lastIndex = _runs.FindLastIndex(run => !string.IsNullOrWhiteSpace(run.Text));
            if (firstIndex >= 0)
            {
                var runs = _runs.GetRange(firstIndex, lastIndex - firstIndex + 1);
                runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
                runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
                _blocks.Add(new RichBlock(CurrentBlockKind(), runs));
            }
            else if (_runs is [{ Text: "\n" }])
            {
                // A lone <br> is the blank line between paragraphs of plain text.
                _blocks.Add(new RichBlock(RichBlockKind.Paragraph, []));
            }

            _runs.Clear();
        }

        private static bool IsBlockTag(string name) => name is
            "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "ul" or "ol"
            or "tr" or "table" or "blockquote" or "pre" or "section" or "article" or "header"
            or "footer" or "hr";

        private static bool IsVoidTag(string name) => name is
            "br" or "hr" or "img" or "meta" or "link" or "input" or "col" or "area" or "base" or "wbr";

        private static string? GetSafeLink(string attributes)
        {
            var href = GetAttribute(attributes, "href").Trim();
            return SafeLinkRegex().IsMatch(href) ? href : null;
        }

        private static string GetAttribute(string attributes, string name)
        {
            foreach (Match attribute in AttributeRegex().Matches(attributes))
            {
                if (string.Equals(attribute.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    return WebUtility.HtmlDecode(attribute.Groups["value"].Value);
                }
            }

            return string.Empty;
        }
    }

    [GeneratedRegex(
        """
        <!(?:--)?\[if\ !supportLists\](?:--)?>(?<marker>.*?)<!(?:--)?\[endif\](?:--)?>
        |<!--.*?-->
        |<![^>]*>
        |<(?<skipped>style|script|title|head)\b.*?</\k<skipped>\s*>
        |<(?<close>/?)(?<name>[a-zA-Z][\w:-]*)(?<attributes>(?:"[^"]*"|'[^']*'|[^'">])*)>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("""(?<name>[\w:-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s>]+))""")]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"</(?:p|div|li|ul|ol|h[1-6]|blockquote|pre|table)\s*>\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<(?:img|table)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnsupportedContentRegex();

    [GeneratedRegex(@"^```\w*\s*(.*?)\s*```$", RegexOptions.Singleline)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(
        @"\d|[$₹€£]|\b(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday|january|february|march|april|may|june|july|august|september|october|november|december|today|tonight|tomorrow|noon|midnight|rupees?|dollars?|euros?|pounds?|lakhs?|crores?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpecificDetailRegex();

    [GeneratedRegex(
        @"\b(?:block\w*|risk\w*|warn\w*|danger\w*|urgent|overdue|fail\w*|delay\w*|cannot|can't|never|do not|don't|must not)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"\*\*(?=\S)([^\n]+?)(?<=\S)\*\*")]
    private static partial Regex MarkdownBoldRegex();

    [GeneratedRegex(@"(?<![\w*])\*(?=[^\s*])([^*\n]+?)(?<=[^\s*])\*(?![\w*])")]
    private static partial Regex MarkdownItalicRegex();

    [GeneratedRegex(@"^\s*\w+[.)]")]
    private static partial Regex NumberedMarkerRegex();

    [GeneratedRegex(
        @"^rgba?\(\s*(?<red>\d{1,3})[\s,]+(?<green>\d{1,3})[\s,]+(?<blue>\d{1,3})(?:[\s,/]+(?<alpha>[\d.]+%?))?\s*\)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RgbColorRegex();

    [GeneratedRegex(@"^#(?:[0-9a-f]{3}|[0-9a-f]{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(@"^(?:bold|[6-9]\d\d)", RegexOptions.IgnoreCase)]
    private static partial Regex BoldWeightRegex();

    [GeneratedRegex(@"^(?:https?:|mailto:)", RegexOptions.IgnoreCase)]
    private static partial Regex SafeLinkRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
