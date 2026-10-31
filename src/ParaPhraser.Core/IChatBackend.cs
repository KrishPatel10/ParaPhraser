namespace ParaPhraser.Core;

public sealed record ChatPrompt(
    string System,
    string User,
    double Temperature = 0.2,
    bool ExpectJson = false);

public interface IChatBackend
{
    /// <summary>The provider's name, shown wherever the app says where text goes.</summary>
    string ProviderName { get; }

    /// <summary>The provider and model, shown beside each result.</summary>
    string Label { get; }

    /// <summary>False when selected text leaves this computer.</summary>
    bool IsLocal { get; }

    Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns non-empty model text, or throws with a message fit to show the
    /// user. <paramref name="onText"/> receives all the text so far as it arrives.
    /// </summary>
    Task<string> CompleteAsync(
        ChatPrompt prompt,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default);
}
