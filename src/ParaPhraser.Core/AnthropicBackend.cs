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
    private readonly AnthropicClient _client;

    public AnthropicBackend(
        OnlineProvider provider,
        string apiKey,
        string model,
        string baseUrl = "https://api.anthropic.com")
    {
        _provider = provider;
        _model = model;

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
            throw error;
        }
    }

    public async Task<string> CompleteAsync(ChatPrompt prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await _client.Messages.Create(
                new MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = MaxOutputTokens,
                    System = prompt.System,
                    Messages = [new MessageParam(new UserMessageParam { Content = prompt.User })]
                },
                cancellationToken);

            if (message.StopReason == "refusal")
            {
                throw new InvalidOperationException("Claude declined to work on this text.");
            }

            if (message.StopReason == "max_tokens")
            {
                throw new InvalidOperationException("The result was too long and got cut off. Try a shorter selection.");
            }

            // Thinking blocks come before the text, so only the text blocks count.
            var text = string.Concat(message.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(block => block.Text))
                .Trim();

            return string.IsNullOrWhiteSpace(text)
                ? throw new InvalidOperationException("Claude returned an empty response. Please try again.")
                : text;
        }
        catch (Exception exception) when (TranslateError(exception) is { } error)
        {
            throw error;
        }
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
            new InvalidOperationException($"{_provider.Name} is having problems. Please try again shortly."),
        AnthropicIOException =>
            new AiUnavailableException($"Could not reach {_provider.Name}. Check your internet connection."),
        AnthropicApiException =>
            new InvalidOperationException($"{_provider.Name} could not process this request with {_model}. Try another model in Settings."),
        _ => null
    };
}
