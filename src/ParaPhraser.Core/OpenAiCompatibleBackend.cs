using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
        "realtime", "audio", "image", "imagen", "veo", "sora", "aqa",
        "live", "deep-research", "computer-use", "antigravity", "customtools"
    ];

    private const string UnknownError = "unknown error";

    private const int MaxLoggedErrorLength = 300;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly OnlineProvider _provider;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _client;
    private readonly TimeSpan _firstResponseTimeout;

    /// <param name="provider">The provider to call.</param>
    /// <param name="apiKey">The user's key.</param>
    /// <param name="model">The model to use.</param>
    /// <param name="client">Replaces the shared client in tests.</param>
    /// <param name="firstResponseTimeout">
    /// How long to wait for the provider to start answering. An overloaded Gemini
    /// model holds the request open with no reply at all, so waiting longer only
    /// leaves the user looking at an empty box. A streaming reply starts almost
    /// at once, so this does not limit how long a result can take to write.
    /// </param>
    public OpenAiCompatibleBackend(
        OnlineProvider provider,
        string apiKey,
        string model,
        HttpClient? client = null,
        TimeSpan? firstResponseTimeout = null)
    {
        _provider = provider;
        _apiKey = apiKey;
        _model = model;
        _client = client ?? SharedClient;
        _firstResponseTimeout = firstResponseTimeout ?? TimeSpan.FromSeconds(30);
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

    public async Task<string> CompleteAsync(
        ChatPrompt prompt,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            model = _model,
            stream = true,
            messages = new[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User }
            }
        };

        // A fixed-length body, because JsonContent sends chunked and some gateways reject that.
        var json = JsonSerializer.Serialize(body);
        using var response = await SendAsync(
            () =>
            {
                var request = CreateRequest(HttpMethod.Post, "chat/completions");
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return request;
            },
            cancellationToken);

        try
        {
            var content = (await ReadStreamAsync(response, onText, cancellationToken)).Trim();
            return string.IsNullOrWhiteSpace(content)
                ? throw new InvalidOperationException($"{_provider.Name} returned an empty response. Please try again.")
                : content;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            && !cancellationToken.IsCancellationRequested)
        {
            throw new AiUnavailableException(
                $"The connection to {_provider.Name} dropped. Please try again.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{_provider.Name} sent a reply that could not be read. Please try again.",
                exception);
        }
    }

    /// <summary>
    /// Reads server-sent events. Other lines, such as OpenRouter's keep-alive
    /// comments, are skipped.
    /// </summary>
    private async Task<string> ReadStreamAsync(
        HttpResponseMessage response,
        Action<string>? onText,
        CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
            if (chunk?.Error?.Message is { } error)
            {
                AppLog.Warn($"{Label}: error during streaming: {Shorten(HideApiKey(error))}");
                throw new InvalidOperationException(
                    $"{_provider.Name} could not finish this request: {HideApiKey(error)}");
            }

            var choice = chunk?.Choices?.FirstOrDefault();
            if (!string.IsNullOrEmpty(choice?.Delta?.Content))
            {
                text.Append(choice.Delta.Content);
                onText?.Invoke(text.ToString());
            }

            if (choice?.FinishReason == "length")
            {
                AppLog.Warn($"{Label}: result cut off by the model's length limit");
                throw new InvalidOperationException("The result was too long and got cut off. Try a shorter selection.");
            }
        }

        return text.ToString();
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
            response = await SendWithRetriesAsync(createRequest, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new AiUnavailableException(
                $"Could not reach {_provider.Name}. Check your internet connection.",
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

    // Dropped connections and brief server errors are routine for busy models,
    // and the Claude SDK retries them too. A request cannot be sent twice, so
    // each attempt rebuilds it. Rate limits are not retried, because they can
    // mean the account is out of credit.
    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var canRetry = attempt < RetryDelays.Length;
            using var firstResponse = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            firstResponse.CancelAfter(_firstResponseTimeout);

            try
            {
                using var request = createRequest();
                var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    firstResponse.Token);

                if (!canRetry || (int)response.StatusCode < 500)
                {
                    return response;
                }

                AppLog.Warn($"{Label}: server error {(int)response.StatusCode} on attempt {attempt + 1}, retrying");
                response.Dispose();
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Not retried: a model this busy stays busy, and a retry would
                // only double the wait.
                AppLog.Warn($"{Label}: no response within {_firstResponseTimeout.TotalSeconds:0} s");
                throw new AiUnavailableException(
                    $"{_provider.Name} did not respond, so {_model} may be overloaded. Try again shortly, or pick another model in Settings.",
                    exception);
            }
            catch (HttpRequestException exception) when (canRetry)
            {
                AppLog.Warn($"{Label}: connection failed on attempt {attempt + 1}, retrying: {exception.Message}");
            }

            await Task.Delay(RetryDelays[attempt], cancellationToken);
        }
    }

    // A rejected key's error text is never shown, because some providers echo
    // part of the key back in it.
    private async Task<Exception> CreateErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await ReadErrorDetailAsync(response, cancellationToken);
        var isKeyError = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        AppLog.Warn($"{Label}: HTTP {(int)response.StatusCode}{(isKeyError ? string.Empty : $": {Shorten(HideApiKey(detail))}")}");

        var serverError = detail == UnknownError
            ? $"{_provider.Name} is having problems ({(int)response.StatusCode}). Try again shortly, or pick another model in Settings."
            : $"{_provider.Name} is having problems ({(int)response.StatusCode}): {HideApiKey(detail)} You can also pick another model in Settings.";

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AiUnavailableException($"{_provider.Name} rejected your API key. Check it in Settings."),
            HttpStatusCode.NotFound =>
                new AiUnavailableException($"{_provider.Name} could not find model {_model}. Pick another in Settings."),
            HttpStatusCode.TooManyRequests =>
                new InvalidOperationException($"{_provider.Name} says you hit a rate limit or ran out of credit. Check your account."),
            >= HttpStatusCode.InternalServerError => new InvalidOperationException(serverError),
            _ when detail.Contains("api key", StringComparison.OrdinalIgnoreCase) =>
                new AiUnavailableException($"{_provider.Name} rejected your API key. Check it in Settings."),
            _ => new InvalidOperationException($"{_provider.Name} could not process this request: {HideApiKey(detail)}")
        };
    }

    private string HideApiKey(string text) =>
        _apiKey.Length == 0 ? text : text.Replace(_apiKey, "***", StringComparison.Ordinal);

    private static string Shorten(string text) => text.Length <= MaxLoggedErrorLength
        ? text
        : text[..MaxLoggedErrorLength];

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
                return message.GetString() ?? UnknownError;
            }
        }
        catch (JsonException)
        {
            // Some gateways answer with an HTML error page.
        }

        return UnknownError;
    }

    private sealed record ModelListResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(
        [property: JsonPropertyName("id")] string? Id);

    private sealed record StreamChunk(
        [property: JsonPropertyName("choices")] IReadOnlyList<StreamChoice>? Choices,
        [property: JsonPropertyName("error")] StreamError? Error);

    private sealed record StreamChoice(
        [property: JsonPropertyName("delta")] StreamDelta? Delta,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record StreamDelta(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record StreamError(
        [property: JsonPropertyName("message")] string? Message);
}
