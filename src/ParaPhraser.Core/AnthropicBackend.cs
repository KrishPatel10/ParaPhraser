using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;

namespace ParaPhraser.Core;

/// <summary>
/// Temperature is left out because current Claude models reject it.
/// </summary>
public sealed class AnthropicBackend : IChatBackend
{
    private const int MaxOutputTokens = 16000;

    private readonly OnlineProvider _provider;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly AnthropicClient _client;

    public AnthropicBackend(
        OnlineProvider provider,
        string apiKey,
        string model,
        string baseUrl = "https://api.anthropic.com")
    {
        _provider = provider;
        _model = model;
        _apiKey = apiKey;

        // BaseUrl is always set, or the SDK would follow ANTHROPIC_BASE_URL and
        // could send the user's key to whatever address that variable names.
        _client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            Timeout = TimeSpan.FromMinutes(2),
            MaxRetries = 1
        };
    }

    public string ProviderName => _provider.Name;

    public string Label => $"{_provider.Name} · {_model}";

    public bool IsLocal => false;

    public Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken = default) =>
        OnlineProvider.CheckKeyAsync(this, () => ListModelsAsync(cancellationToken));

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _client.Models.List(new ModelListParams { Limit = 100 }, cancellationToken);
            return page.Items.Select(model => model.ID).ToArray();
        }
        catch (Exception exception) when (TranslateError(exception) is { } error)
        {
            LogError(exception);
            throw error;
        }
    }

    public async Task<string> CompleteAsync(
        ChatPrompt prompt,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = MaxOutputTokens,
            System = prompt.System,
            Messages = [new MessageParam(new UserMessageParam { Content = prompt.User })]
        };

        try
        {
            var text = new StringBuilder();
            await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters, cancellationToken))
            {
                // Only text deltas count. Thinking arrives as a different delta.
                if (streamEvent.TryPickContentBlockDelta(out var contentDelta)
                    && contentDelta.Delta.TryPickText(out var textDelta))
                {
                    text.Append(textDelta.Text);
                    onText?.Invoke(text.ToString());
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    ThrowIfUnfinished(messageDelta.Delta.StopReason == "refusal", messageDelta.Delta.StopReason == "max_tokens");
                }
            }

            var content = text.ToString().Trim();
            return string.IsNullOrWhiteSpace(content)
                ? throw new InvalidOperationException("Claude returned an empty response. Please try again.")
                : content;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
            && exception is not OperationCanceledException)
        {
            // Stopping mid-stream can surface as a connection error from the SDK.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (TranslateError(exception) is { } error)
        {
            LogError(exception);
            throw error;
        }
    }

    private void ThrowIfUnfinished(bool wasRefused, bool wasCutOff)
    {
        if (wasRefused)
        {
            AppLog.Warn($"{Label}: request refused by the model");
            throw new InvalidOperationException("Claude declined to work on this text.");
        }

        if (wasCutOff)
        {
            AppLog.Warn($"{Label}: result cut off by the model's length limit");
            throw new InvalidOperationException("The result was too long and got cut off. Try a shorter selection.");
        }
    }

    // A rejected key's error text is never logged, in case it echoes the key.
    private void LogError(Exception exception)
    {
        var message = _apiKey.Length == 0
            ? exception.Message
            : exception.Message.Replace(_apiKey, "***", StringComparison.Ordinal);
        var detail = exception is AnthropicUnauthorizedException or AnthropicForbiddenException
            ? string.Empty
            : $": {message}";
        AppLog.Warn($"{Label}: {exception.GetType().Name}{detail}");
    }

    private Exception? TranslateError(Exception exception) => exception switch
    {
        AnthropicUnauthorizedException or AnthropicForbiddenException =>
            new AiUnavailableException($"{_provider.Name} rejected your API key. Check it in Settings."),
        AnthropicNotFoundException =>
            new AiUnavailableException($"{_provider.Name} could not find model {_model}. Pick another in Settings."),
        AnthropicRateLimitException =>
            new InvalidOperationException($"{_provider.Name} says you hit a rate limit or ran out of credit. Check your account."),
        Anthropic5xxException =>
            new ModelBusyException($"{_provider.Name} is having problems. Try again shortly, or pick another model in Settings."),
        AnthropicIOException =>
            new AiUnavailableException($"Could not reach {_provider.Name}. Check your internet connection."),
        IOException or HttpRequestException =>
            new AiUnavailableException($"The connection to {_provider.Name} dropped. Please try again."),
        AnthropicApiException =>
            new InvalidOperationException($"{_provider.Name} could not process this request with {_model}. Try another model in Settings."),
        _ => null
    };
}
