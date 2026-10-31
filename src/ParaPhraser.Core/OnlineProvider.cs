namespace ParaPhraser.Core;

/// <param name="OpenAiBaseUrl">Set for providers that speak the OpenAI chat format.</param>
/// <param name="KeyCheckPath">
/// An address that needs a valid key. OpenRouter's model list is public, so it
/// would accept any key.
/// </param>
public sealed record OnlineProvider(
    string Id,
    string Name,
    string KeyPageUrl,
    string? OpenAiBaseUrl = null,
    string KeyCheckPath = "models")
{
    public static IReadOnlyList<OnlineProvider> All { get; } =
    [
        new("anthropic", "Anthropic Claude", "https://platform.claude.com/settings/keys"),
        new("openai", "OpenAI", "https://platform.openai.com/api-keys", "https://api.openai.com/v1/"),
        new(
            "gemini",
            "Google Gemini",
            "https://aistudio.google.com/apikey",
            "https://generativelanguage.googleapis.com/v1beta/openai/"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/keys", "https://openrouter.ai/api/v1/", "key")
    ];

    public static OnlineProvider Find(string? id) =>
        All.FirstOrDefault(provider => provider.Id == id) ?? All[0];

    /// <summary>
    /// Picks a model to try when the current one is busy. It stays within the
    /// same family, so OpenRouter's long list cannot switch the user to an
    /// unrelated paid model, and prefers lighter models, which are less busy.
    /// </summary>
    public static string? PickFallbackModel(
        IEnumerable<string> models,
        string currentModel,
        string? lastWorkingModel,
        IReadOnlyCollection<string> alreadyTried)
    {
        var family = GetFamily(currentModel);
        var candidates = models
            .Where(model => model != currentModel
                && !alreadyTried.Contains(model)
                && GetFamily(model) == family
                && !IsUnstable(model))
            .ToArray();

        return lastWorkingModel is not null && candidates.Contains(lastWorkingModel)
            ? lastWorkingModel
            : candidates
                .OrderByDescending(GetLightness)
                .ThenByDescending(model => model, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }

    private static string GetFamily(string model) => model.Split('/', '-')[0];

    private static bool IsUnstable(string model) =>
        new[] { "preview", "latest", "exp" }.Any(word => model.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static int GetLightness(string model)
    {
        if (new[] { "lite", "mini", "haiku" }.Any(word => model.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return model.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    public IChatBackend CreateBackend(string apiKey, string model) => OpenAiBaseUrl is null
        ? new AnthropicBackend(this, apiKey, model)
        : new OpenAiCompatibleBackend(this, apiKey, model);

    /// <summary>
    /// Runs a call that costs nothing but proves the key works.
    /// </summary>
    internal static async Task<AiStatus> CheckKeyAsync(IChatBackend backend, Func<Task> check)
    {
        try
        {
            await check();
            return new AiStatus(true, $"Connected with {backend.Label}.");
        }
        catch (InvalidOperationException exception)
        {
            return new AiStatus(false, exception.Message);
        }
    }
}
