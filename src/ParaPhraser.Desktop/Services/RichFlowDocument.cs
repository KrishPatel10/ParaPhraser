using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ParaPhraser.Core;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using List = System.Windows.Documents.List;

namespace ParaPhraser.Desktop.Services;

/// <summary>
/// Converts between the rewrite pipeline's document and the one the overlay edits.
/// </summary>
public static class RichFlowDocument
{
    private static readonly Thickness ParagraphMargin = new(0, 0, 0, 6);

    public static FlowDocument ToFlowDocument(RichDocument document)
    {
        var flow = new FlowDocument { PagePadding = new Thickness(0) };
        List? list = null;
        foreach (var block in document.Blocks)
        {
            var paragraph = CreateParagraph(block);
            var marker = block.Kind switch
            {
                RichBlockKind.Bullet => TextMarkerStyle.Disc,
                RichBlockKind.Numbered => TextMarkerStyle.Decimal,
                _ => TextMarkerStyle.None
            };

            if (marker == TextMarkerStyle.None)
            {
                list = null;
                flow.Blocks.Add(paragraph);
                continue;
            }

            if (list?.MarkerStyle != marker)
            {
                list = new List
                {
                    MarkerStyle = marker,
                    Margin = ParagraphMargin,
                    Padding = new Thickness(24, 0, 0, 0)
                };
                flow.Blocks.Add(list);
            }

            list.ListItems.Add(new ListItem(paragraph));
        }

        return flow;
    }

    public static RichDocument ToRichDocument(FlowDocument flow)
    {
        var blocks = new List<RichBlock>();
        foreach (var block in flow.Blocks)
        {
            AddBlock(block, RichBlockKind.Paragraph, blocks);
        }

        return new RichDocument(blocks);
    }

    private static Paragraph CreateParagraph(RichBlock block)
    {
        var paragraph = new Paragraph { Margin = ParagraphMargin, Tag = block.Kind };
        if (block.Kind is RichBlockKind.Heading1 or RichBlockKind.Heading2 or RichBlockKind.Heading3)
        {
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.FontSize = block.Kind switch
            {
                RichBlockKind.Heading1 => 20,
                RichBlockKind.Heading2 => 18,
                _ => 16
            };
        }

        foreach (var run in block.Runs)
        {
            paragraph.Inlines.Add(CreateInline(run));
        }

        return paragraph;
    }

    private static Inline CreateInline(RichRun richRun)
    {
        var span = new Span();
        var parts = richRun.Text.Split('\n');
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                span.Inlines.Add(new LineBreak());
            }

            if (parts[index].Length > 0)
            {
                span.Inlines.Add(new Run(parts[index]));
            }
        }

        if (richRun.Bold)
        {
            span.FontWeight = FontWeights.Bold;
        }

        if (richRun.Italic)
        {
            span.FontStyle = FontStyles.Italic;
        }

        if (richRun.Underline)
        {
            span.TextDecorations = TextDecorations.Underline;
        }

        if (richRun.Color is not null)
        {
            span.Foreground = CreateBrush(richRun.Color);
        }

        if (richRun.Highlight is not null)
        {
            span.Background = CreateBrush(richRun.Highlight);
        }

        return Uri.TryCreate(richRun.Link, UriKind.Absolute, out var link)
            ? new Hyperlink(span) { NavigateUri = link }
            : span;
    }

    private static SolidColorBrush CreateBrush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private static void AddBlock(Block block, RichBlockKind kind, List<RichBlock> blocks)
    {
        switch (block)
        {
            case Paragraph paragraph:
                var isHeading = paragraph.Tag is RichBlockKind.Heading1
                    or RichBlockKind.Heading2
                    or RichBlockKind.Heading3;
                var builder = new RunBuilder(isHeading);
                foreach (var inline in paragraph.Inlines)
                {
                    builder.Add(inline, new RichRun(string.Empty));
                }

                builder.Flush();
                blocks.Add(new RichBlock(
                    isHeading ? (RichBlockKind)paragraph.Tag : kind,
                    builder.Runs));
                break;

            case List list:
                var itemKind = list.MarkerStyle == TextMarkerStyle.Decimal
                    ? RichBlockKind.Numbered
                    : RichBlockKind.Bullet;
                foreach (var item in list.ListItems)
                {
                    foreach (var child in item.Blocks)
                    {
                        AddBlock(child, itemKind, blocks);
                    }
                }

                break;

            case Section section:
                foreach (var child in section.Blocks)
                {
                    AddBlock(child, kind, blocks);
                }

                break;

            default:
                var text = new TextRange(block.ContentStart, block.ContentEnd).Text.Trim();
                if (text.Length > 0)
                {
                    blocks.Add(new RichBlock(kind, [new RichRun(text)]));
                }

                break;
        }
    }

    private sealed class RunBuilder(bool isHeading)
    {
        private readonly StringBuilder _text = new();
        private RichRun? _format;

        public List<RichRun> Runs { get; } = [];

        public void Add(Inline inline, RichRun inherited)
        {
            var format = inherited with
            {
                Underline = inherited.Underline || inline.TextDecorations.Any(
                    decoration => decoration.Location == TextDecorationLocation.Underline),
                Color = GetLocalColor(inline, TextElement.ForegroundProperty, inherited.Color),
                Highlight = GetLocalColor(inline, TextElement.BackgroundProperty, inherited.Highlight)
            };

            switch (inline)
            {
                case Hyperlink hyperlink:
                    foreach (var child in hyperlink.Inlines)
                    {
                        Add(child, format with
                        {
                            Link = hyperlink.NavigateUri?.OriginalString,
                            Underline = false
                        });
                    }

                    break;

                case Span span:
                    foreach (var child in span.Inlines)
                    {
                        Add(child, format);
                    }

                    break;

                case Run run:
                    Append(
                        run.Text,
                        format with
                        {
                            Bold = !isHeading && run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight(),
                            Italic = run.FontStyle != FontStyles.Normal,
                            Underline = format.Underline && format.Link is null
                        });
                    break;

                case LineBreak:
                    Append("\n", new RichRun(string.Empty));
                    break;
            }
        }

        public void Flush()
        {
            if (_format is not null && _text.Length > 0)
            {
                Runs.Add(_format with { Text = _text.ToString() });
            }

            _text.Clear();
            _format = null;
        }

        // Only a local value counts: the inherited one is the overlay's own text color.
        private static string? GetLocalColor(Inline inline, DependencyProperty property, string? inherited) =>
            inline.ReadLocalValue(property) is SolidColorBrush { Color.A: > 0 } brush
                ? RichDocument.NormalizeColor($"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}")
                : inline.ReadLocalValue(property) is SolidColorBrush ? null : inherited;

        private void Append(string text, RichRun format)
        {
            if (_format != format)
            {
                Flush();
                _format = format;
            }

            _text.Append(text);
        }
    }
}
