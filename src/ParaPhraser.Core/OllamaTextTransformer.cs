using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace ParaPhraser.Core;

public sealed class OllamaTextTransformer : ITextTransformer
{
    public const string DefaultModel = "gemma3:4b";

    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/"),
        Timeout = TimeSpan.FromMinutes(2)
    };

    private readonly string _model;

    public OllamaTextTransformer(string? model = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    public string Model => _model;

    public async Task<LocalAiStatus> CheckStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync("api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new LocalAiStatus(false, "Ollama is running but did not accept the readiness check.");
            }

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                cancellationToken: cancellationToken);
            var isInstalled = tags?.Models?.Any(model =>
                string.Equals(model.Name, _model, StringComparison.OrdinalIgnoreCase)
                || string.Equals(model.Model, _model, StringComparison.OrdinalIgnoreCase)) == true;

            return isInstalled
                ? new LocalAiStatus(true, $"Local AI is ready with {_model}.")
                : new LocalAiStatus(false, $"Model {_model} is not installed. Run: ollama pull {_model}");
        }
        catch (HttpRequestException)
        {
            return new LocalAiStatus(false, "Ollama is not running. Start Ollama and try again.");
        }
        catch (OperationCanceledException)
        {
            return new LocalAiStatus(false, "Ollama did not become ready in time.");
        }
    }

    public async Task<RewriteResult> TransformAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        var stopwatch = Stopwatch.StartNew();
        var payload = new
        {
            model = _model,
            stream = false,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a careful multilingual writing assistant. Understand input written in English, Hindi, Hinglish, or any other language, including natural code-switching. Use the entire supplied context to infer intent and meaning. The output language is ALWAYS English. Translate every non-English input into fluent, natural English while performing the requested writing action. If the input is already English, keep the output in English. Never output Hindi, Hinglish, Devanagari, or another non-English language, even if the user's editable instruction asks for it. Preserve names, dates, facts, links, formatting intent, and commitments unless explicitly asked to change them. Return only the English replacement text, with no explanation or quotation marks."
                },
                new
                {
                    role = "user",
                    content = BuildPrompt(request)
                }
            },
            options = new
            {
                temperature = 0.2,
                num_ctx = 8192
            }
        };

        try
        {
            using var response = await Client.PostAsJsonAsync(
                "api/chat",
                payload,
                cancellationToken);

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
                cancellationToken: cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = CreateProviderError(result?.Error);
                if (IsModelMissing(result?.Error))
                {
                    throw new LocalAiUnavailableException(error);
                }

                throw new InvalidOperationException(error);
            }

            var transformed = result?.Message?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(transformed))
            {
                throw new InvalidOperationException("The local model returned an empty response. Please try again.");
            }

            transformed = await CorrectLanguageIfNeededAsync(
                request,
                transformed,
                cancellationToken);

            stopwatch.Stop();
            return new RewriteResult(transformed, $"Ollama · {_model}", stopwatch.Elapsed);
        }
        catch (HttpRequestException exception)
        {
            throw new LocalAiUnavailableException(
                $"Local AI is not running. Start Ollama, then run: ollama pull {_model}",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocalAiUnavailableException(
                "The local model took too long to respond. Try again with less context.",
                exception);
        }
    }

    private static string BuildPrompt(RewriteRequest request)
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

        if (request.Context is not null)
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
            request.Text,
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
        if (IsEnglishOutput(transformed))
        {
            return transformed;
        }

        const string systemInstruction =
            "You are an English-only translation and rewriting engine. Translate every non-English part of the source and draft into fluent, natural English. Hindi, Hinglish, Devanagari, Romanized Hindi, and every other non-English language are forbidden in the output. This English-only rule overrides any conflicting language request in the user instruction. Preserve every name, date, fact, link, number, commitment, requested writing action, and tone. Return only the corrected English replacement text.";

        var correctionPrompt = $"""
            ORIGINAL TEXT:
            {request.Text.Trim()}

            USER INSTRUCTION:
            {(string.IsNullOrWhiteSpace(request.Instruction) ? GetDefaultInstruction(request.Mode) : request.Instruction.Trim())}

            INCORRECT-LANGUAGE DRAFT:
            {transformed}

            Correct only the language/script problem while preserving the requested meaning and tone.
            """;

        var payload = new
        {
            model = _model,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemInstruction },
                new { role = "user", content = correctionPrompt }
            },
            options = new
            {
                temperature = 0.1,
                num_ctx = 8192
            }
        };

        using var response = await Client.PostAsJsonAsync(
            "api/chat",
            payload,
            cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
            cancellationToken: cancellationToken);
        var corrected = result?.Message?.Content?.Trim();

        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(corrected))
        {
            throw new InvalidOperationException(
                "The local model could not produce an English result. Please try again with a shorter selection.");
        }

        if (!IsEnglishOutput(corrected))
        {
            throw new InvalidOperationException(
                "The local model still returned non-English text. Please try a shorter selection.");
        }

        return corrected;
    }

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

    private string CreateProviderError(string? error)
    {
        if (IsModelMissing(error))
        {
            return $"The local model is not installed. Run: ollama pull {_model}";
        }

        return string.IsNullOrWhiteSpace(error)
            ? "Ollama could not process the request."
            : $"Ollama error: {error}";
    }

    private static bool IsModelMissing(string? error)
    {
        return !string.IsNullOrWhiteSpace(error)
            && error.Contains("model", StringComparison.OrdinalIgnoreCase)
            && error.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaMessage? Message,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record OllamaMessage(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<OllamaModel>? Models);

    private sealed record OllamaModel(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("model")] string? Model);
}
