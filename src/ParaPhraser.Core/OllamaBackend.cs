using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParaPhraser.Core;

public sealed class OllamaBackend : IChatBackend
{
    public const string DefaultModel = "gemma3:4b";

    private static readonly Uri BaseAddress = new("http://127.0.0.1:11434/");

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HttpClient Client = new()
    {
        BaseAddress = BaseAddress,
        Timeout = TimeSpan.FromMinutes(2)
    };

    // A model download runs for many minutes, far past the chat timeout.
    private static readonly HttpClient DownloadClient = new()
    {
        BaseAddress = BaseAddress,
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string _model;

    public OllamaBackend(string? model = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    public string Model => _model;

    public string ProviderName => "Ollama";

    public string Label => $"Ollama · {_model}";

    public bool IsLocal => true;

    public async Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await ListModelsAsync(cancellationToken);
            return models.Contains(_model, StringComparer.OrdinalIgnoreCase)
                ? new AiStatus(true, $"Local AI is ready with {_model}.")
                : new AiStatus(false, $"Model {_model} is not installed yet. Download it in Settings.");
        }
        catch (AiUnavailableException exception)
        {
            return new AiStatus(false, exception.Message);
        }
        catch (OperationCanceledException)
        {
            return new AiStatus(false, "Ollama did not become ready in time.");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Client.GetAsync("api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiUnavailableException("Ollama is running but did not accept the readiness check.");
            }

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
                cancellationToken: cancellationToken);
            return tags?.Models?.Select(model => model.Name ?? model.Model).OfType<string>().ToArray() ?? [];
        }
        catch (HttpRequestException exception)
        {
            throw new AiUnavailableException(
                "Ollama is not running. Install or start it, then try again.",
                exception);
        }
    }

    public async Task<string> CompleteAsync(ChatPrompt prompt, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = _model,
            stream = false,
            format = prompt.ExpectJson ? "json" : null,
            messages = new[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User }
            },
            options = new
            {
                temperature = prompt.Temperature,
                num_ctx = 8192
            }
        };

        try
        {
            using var response = await Client.PostAsJsonAsync(
                "api/chat",
                payload,
                RequestOptions,
                cancellationToken);

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
                cancellationToken: cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = CreateProviderError(result?.Error);
                throw IsModelMissing(result?.Error)
                    ? new AiUnavailableException(error)
                    : new InvalidOperationException(error);
            }

            var content = result?.Message?.Content?.Trim();
            return string.IsNullOrWhiteSpace(content)
                ? throw new InvalidOperationException("The local model returned an empty response. Please try again.")
                : content;
        }
        catch (HttpRequestException exception)
        {
            throw new AiUnavailableException(
                "Local AI is not running. Start Ollama, or switch to online mode in Settings.",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiUnavailableException(
                "The local model took too long to respond. Try again with less text.",
                exception);
        }
    }

    public async Task DownloadModelAsync(IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new { model = _model, stream = true })
        };

        try
        {
            using var response = await DownloadClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var update = JsonSerializer.Deserialize<OllamaPullUpdate>(line);
                if (update?.Error is { } error)
                {
                    throw new InvalidOperationException($"Ollama could not download {_model}: {error}");
                }

                if (update is { Total: > 0, Completed: { } completed })
                {
                    var percent = (double)completed / update.Total.Value;
                    progress.Report($"Downloading {_model}: {percent.ToString("P0", CultureInfo.CurrentCulture)}");
                }
                else if (update?.Status is { } status)
                {
                    progress.Report(status);
                }
            }
        }
        catch (HttpRequestException exception)
        {
            throw new AiUnavailableException(
                "Ollama is not running. Install or start it, then try again.",
                exception);
        }
    }

    private string CreateProviderError(string? error)
    {
        if (IsModelMissing(error))
        {
            return $"Model {_model} is not installed yet. Download it in Settings.";
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

    private sealed record OllamaPullUpdate(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("total")] long? Total,
        [property: JsonPropertyName("completed")] long? Completed);
}
