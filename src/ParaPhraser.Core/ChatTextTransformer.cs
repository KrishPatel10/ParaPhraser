using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ParaPhraser.Core;

/// <summary>
/// Holds every prompt, so all modes behave the same on every provider.
/// </summary>
public sealed class ChatTextTransformer : ITextTransformer
{
    private const string WritingInstruction =
        "You are a careful multilingual writing assistant. Understand input written in English, Hindi, Hinglish, or any other language, including natural code-switching. Use the entire supplied context to infer intent and meaning. The output language is ALWAYS English. Translate every non-English input into fluent, natural English while performing the requested writing action. If the input is already English, keep the output in English. Never output Hindi, Hinglish, Devanagari, or another non-English language, even if the user's editable instruction asks for it. Preserve names, dates, facts, links, formatting intent, and commitments unless explicitly asked to change them. Return only the English replacement text, with no explanation or quotation marks.";

    private const string HtmlFormattingInstruction =
        "The selected text is HTML. Return HTML too, using only these tags: p, h1, h2, h3, ul, ol, li, b, i, u, br, a with href, and span with a style of color or background-color. Do not use Markdown. Keep bold, italic, underline, links, headings, and lists on the matching words, and keep every href unchanged. Every span in the selected text must appear in your result with its exact style attribute, wrapped around the words that carry the same meaning. Never turn a span into b, i, or u. Do not add new formatting, a document wrapper, or code fences.";

    // The model only names phrases here and never returns the text. Asked to
    // emphasize while rewriting, it invented sentences to have something to
    // bold. Do not add sample sentences either: it copied one into real output.
    private const string FormattingInstruction =
        """You choose formatting for text and never change the text. Return only JSON in the form {"bold": [], "italic": [], "underline": [], "highlight": [], "red": []}. Each entry must be a phrase of a few words copied exactly from the text. bold: a specific deadline, date, time, or amount. italic: the title of a book or film, or a term being defined. underline: an action the reader must take. highlight: the single most important point. red: a risk, blocker, or warning. Never list general words such as critical, essential, or important. Leave a list empty when nothing fits, so casual messages stay plain. If the instruction names what to format, follow it over these rules.""";

    private readonly IChatBackend _backend;

    public ChatTextTransformer(IChatBackend backend)
    {
        _backend = backend;
    }

    public IChatBackend Backend => _backend;

    public async Task<RewriteResult> TransformAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var stopwatch = Stopwatch.StartNew();
        var transformed = await _backend.CompleteAsync(
            new ChatPrompt(WritingInstruction, BuildPrompt(request), Temperature: 0.2),
            cancellationToken);

        transformed = await CorrectLanguageIfNeededAsync(
            request,
            transformed,
            cancellationToken);

        stopwatch.Stop();
        return new RewriteResult(transformed, _backend.Label, stopwatch.Elapsed);
    }

    public async Task<FormattingResult> SuggestFormattingAsync(
        string text,
        string? instruction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var stopwatch = Stopwatch.StartNew();
        var response = await _backend.CompleteAsync(
            new ChatPrompt(
                FormattingInstruction,
                $"INSTRUCTION\n{instruction}\n\nTEXT\n{text}",
                Temperature: 0.1,
                ExpectJson: true),
            cancellationToken);

        stopwatch.Stop();
        return new FormattingResult(ParseFormattingPlan(response), _backend.Label, stopwatch.Elapsed);
    }

    /// <summary>
    /// Online models can wrap the JSON in a code fence or a sentence.
    /// </summary>
    private static FormattingPlan ParseFormattingPlan(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');

        try
        {
            return start >= 0 && end > start
                ? JsonSerializer.Deserialize<FormattingPlan>(response[start..(end + 1)]) ?? new FormattingPlan()
                : throw new JsonException("No JSON object found.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The model returned formatting that could not be read. Please try again.",
                exception);
        }
    }

    private string BuildPrompt(RewriteRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("INSTRUCTION");
        prompt.AppendLine(string.IsNullOrWhiteSpace(request.Instruction)
            ? GetDefaultInstruction(request.Mode)
            : request.Instruction.Trim());

        prompt.AppendLine();
        prompt.AppendLine("TONE");
        prompt.AppendLine(GetToneInstruction(request.Tone));

        prompt.AppendLine();
        prompt.AppendLine("REQUIRED OUTPUT LANGUAGE");
        prompt.AppendLine(GetLanguageInstruction(request));

        if (request.IsHtml)
        {
            prompt.AppendLine();
            prompt.AppendLine("FORMATTING");
            prompt.AppendLine(HtmlFormattingInstruction);
        }

        // Window titles can name private documents, so they stay on this PC.
        if (request.Context is not null && _backend.IsLocal)
        {
            prompt.AppendLine();
            prompt.AppendLine("SOURCE CONTEXT");
            AppendValue(prompt, "Application", request.Context.ApplicationName);
            AppendValue(prompt, "Window or document", request.Context.WindowTitle);
            AppendValue(prompt, "Surrounding text", request.Context.SurroundingText);
        }

        prompt.AppendLine();
        prompt.AppendLine("SELECTED TEXT TO REPLACE");
        prompt.AppendLine(request.Text.Trim());
        return prompt.ToString();
    }

    private static void AppendValue(StringBuilder prompt, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            prompt.Append(label).Append(": ").AppendLine(value.Trim());
        }
    }

    private static string GetDefaultInstruction(RewriteMode mode) => mode switch
    {
        RewriteMode.Grammar => "Correct grammar, spelling, and punctuation while preserving the original meaning and tone.",
        RewriteMode.Rewrite => "Rewrite this clearly and naturally while preserving its meaning and important details.",
        RewriteMode.Shorten => "Make this concise while preserving the important information.",
        RewriteMode.Professional => "Rewrite this in a polished, professional tone while preserving its meaning.",
        _ => "Improve this text while preserving its meaning."
    };

    private static string GetToneInstruction(RewriteTone tone) => tone switch
    {
        RewriteTone.Polite => "Use a considerate, courteous tone without sounding overly formal.",
        RewriteTone.Professional => "Use a polished, confident, workplace-appropriate tone.",
        RewriteTone.Casual => "Use a relaxed, friendly, conversational tone.",
        RewriteTone.Emojified => "Add a small number of relevant emojis naturally while keeping the message clear and preserving every important fact.",
        _ => "Preserve the tone of the selected text."
    };

    private static string GetLanguageInstruction(RewriteRequest request)
    {
        var sourceLanguage = WritingLanguageResolver.Resolve(
            GetVisibleText(request, request.Text),
            WritingLanguage.Auto);

        return sourceLanguage switch
        {
            WritingLanguage.Hindi =>
                "ENGLISH ONLY. The source appears to be Hindi. Translate its complete meaning into fluent, natural English, then perform the requested writing action. Do not output Devanagari or Romanized Hindi.",
            WritingLanguage.Hinglish =>
                "ENGLISH ONLY. The source appears to be Hinglish. Understand all Romanized Hindi and code-switching, translate the complete meaning, and return fluent, natural English without Hinglish words.",
            _ =>
                "ENGLISH ONLY. If any part of the source is not English, translate it into natural English. The final replacement must contain English only, regardless of any conflicting language request in the editable instruction."
        };
    }

    private async Task<string> CorrectLanguageIfNeededAsync(
        RewriteRequest request,
        string transformed,
        CancellationToken cancellationToken)
    {
        if (IsEnglishOutput(GetVisibleText(request, transformed)))
        {
            return transformed;
        }

        var systemInstruction =
            "You are an English-only translation and rewriting engine. Translate every non-English part of the source and draft into fluent, natural English. Hindi, Hinglish, Devanagari, Romanized Hindi, and every other non-English language are forbidden in the output. This English-only rule overrides any conflicting language request in the user instruction. Preserve every name, date, fact, link, number, commitment, requested writing action, and tone. Return only the corrected English replacement text.";
        if (request.IsHtml)
        {
            systemInstruction += " " + HtmlFormattingInstruction;
        }

        var correctionPrompt = $"""
            ORIGINAL TEXT:
            {request.Text.Trim()}

            USER INSTRUCTION:
            {(string.IsNullOrWhiteSpace(request.Instruction) ? GetDefaultInstruction(request.Mode) : request.Instruction.Trim())}

            INCORRECT-LANGUAGE DRAFT:
            {transformed}

            Correct only the language/script problem while preserving the requested meaning and tone.
            """;

        var corrected = await _backend.CompleteAsync(
            new ChatPrompt(systemInstruction, correctionPrompt, Temperature: 0.1),
            cancellationToken);

        if (!IsEnglishOutput(GetVisibleText(request, corrected)))
        {
            throw new InvalidOperationException(
                "The model still returned non-English text. Please try a shorter selection.");
        }

        return corrected;
    }

    private static string GetVisibleText(RewriteRequest request, string text) =>
        request.IsHtml ? RichDocument.FromModelOutput(text).ToPlainText() : text;

    private static bool IsEnglishOutput(string text)
    {
        if (WritingLanguageResolver.Resolve(text, WritingLanguage.Auto)
            != WritingLanguage.English)
        {
            return false;
        }

        return text.EnumerateRunes().All(rune =>
            !Rune.IsLetter(rune) || IsLatinLetter(rune.Value));
    }

    private static bool IsLatinLetter(int value) =>
        value is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        or >= 0x00C0 and <= 0x024F
        or >= 0x1E00 and <= 0x1EFF;
}
