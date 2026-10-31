using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParaPhraser.Core;

/// <summary>
/// Covers every provider that speaks the OpenAI chat format. Temperature is left
/// out because several current models reject anything but their default.
/// </summary>
public sealed class OpenAiCompatibleBackend : IChatBackend
{
    private static readonly string[] NonChatModelWords =
    [
        "embedding", "whisper", "tts", "dall-e", "moderation", "transcribe",
        "realtime", "audio", "image", "imagen", "veo", "sora", "aqa"
    ];

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly OnlineProvider _provider;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _client;

    public OpenAiCompatibleBackend(
        OnlineProvider provider,
        string apiKey,
        string model,
        HttpClient? client = null)
    {
        _provider = provider;
        _apiKey = apiKey;
        _model = model;
        _client = client ?? SharedClient;
    }

    public string ProviderName => _provider.Name;

    public string Label => $"{_provider.Name} · {_model}";

    public bool IsLocal => false;

    public Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken = default) =>
        OnlineProvider.CheckKeyAsync(this, async () =>
        {
            using var response = await SendAsync(
                () => CreateRequest(HttpMethod.Get, _provider.KeyCheckPath),
                cancellationToken);
        });

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => CreateRequest(HttpMethod.Get, "models"), cancellationToken);
        var models = await response.Content.ReadFromJsonAsync<ModelListResponse>(cancellationToken: cancellationToken);

        // Gemini returns ids as "models/name", but chat requests take the bare name.
        return models?.Data?
            .Select(model => model.Id)
            .OfType<string>()
            .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id)
            .Where(id => !NonChatModelWords.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public async Task<string> CompleteAsync(ChatPrompt prompt, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User }
            }
        };

        using var response = await SendAsync(
            () =>
            {
                var request = CreateRequest(HttpMethod.Post, "chat/completions");
                request.Content = JsonContent.Create(body);
                return request;
            },
            cancellationToken);
        var completion = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: cancellationToken);
        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

        return string.IsNullOrWhiteSpace(content)
            ? throw new InvalidOperationException($"{_provider.Name} returned an empty response. Please try again.")
            : content;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_provider.OpenAiBaseUrl!), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendOnceWithRetryAsync(createRequest, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new AiUnavailableException(
                $"Could not reach {_provider.Name}. Check your internet connection.",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiUnavailableException(
                $"{_provider.Name} took too long to respond. Please try again.",
                exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await CreateErrorAsync(response, cancellationToken);
        }
    }

    // The first connection to a provider occasionally drops, and the Claude SDK
    // retries the same way. A request cannot be sent twice, so it is rebuilt.
    private async Task<HttpResponseMessage> SendOnceWithRetryAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = createRequest();
            return await _client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            using var retry = createRequest();
            return await _client.SendAsync(retry, cancellationToken);
        }
    }

    // A rejected key's error text is never shown, because some providers echo
    // part of the key back in it.
    private async Task<Exception> CreateErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await ReadErrorDetailAsync(response, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AiUnavailableException($"{_provider.Name} rejected your API key. Check it in Settings."),
            HttpStatusCode.NotFound =>
                new AiUnavailableException($"{_provider.Name} could not find model {_model}. Pick another in Settings."),
            HttpStatusCode.TooManyRequests =>
                new InvalidOperationException($"{_provider.Name} says you hit a rate limit or ran out of credit. Check your account."),
            >= HttpStatusCode.InternalServerError =>
                new InvalidOperationException($"{_provider.Name} is having problems. Please try again shortly."),
            _ when detail.Contains("api key", StringComparison.OrdinalIgnoreCase) =>
                new AiUnavailableException($"{_provider.Name} rejected your API key. Check it in Settings."),
            _ => new InvalidOperationException($"{_provider.Name} could not process this request: {detail}")
        };
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                ? document.RootElement[0]
                : document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? "unknown error";
            }
        }
        catch (JsonException)
        {
            // Some gateways answer with an HTML error page.
        }

        return "unknown error";
    }

    private sealed record ModelListResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(
        [property: JsonPropertyName("id")] string? Id);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record ChatMessage(
        [property: JsonPropertyName("content")] string? Content);
}
