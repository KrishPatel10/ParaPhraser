using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParaPhraser.Core;

namespace ParaPhraser.Desktop.Services;

public sealed class ProviderSettings
{
    public string? Model { get; set; }

    /// <summary>Encrypted for this Windows account. The plain key is never written to disk.</summary>
    public string? EncryptedApiKey { get; set; }
}

public sealed class AppSettings
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ParaPhraser",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool IsOnline { get; set; }

    public string? OfflineModel { get; set; }

    public string? OnlineProviderId { get; set; }

    public Dictionary<string, ProviderSettings> Providers { get; set; } = [];

    /// <summary>
    /// A missing or damaged file starts from defaults, so the app always opens.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Writing beside the target first means a crash cannot leave half a file.
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, target, overwrite: true);
    }

    public ProviderSettings GetProvider(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out var provider))
        {
            provider = new ProviderSettings();
            Providers[providerId] = provider;
        }

        return provider;
    }

    public bool HasApiKey(string providerId) =>
        Providers.TryGetValue(providerId, out var provider) && provider.EncryptedApiKey is not null;

    public void SetApiKey(string providerId, string? apiKey)
    {
        GetProvider(providerId).EncryptedApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(apiKey.Trim()),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// Returns null when the key cannot be read, such as on another Windows account.
    /// </summary>
    public string? GetApiKey(string providerId)
    {
        if (!HasApiKey(providerId))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(Providers[providerId].EncryptedApiKey!),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public IChatBackend CreateBackend()
    {
        if (!IsOnline)
        {
            return new OllamaBackend(
                OfflineModel ?? Environment.GetEnvironmentVariable("PARAPHRASER_OLLAMA_MODEL"));
        }

        var provider = OnlineProvider.Find(OnlineProviderId);
        return provider.CreateBackend(GetApiKey(provider.Id) ?? string.Empty, GetProvider(provider.Id).Model ?? string.Empty);
    }
}
