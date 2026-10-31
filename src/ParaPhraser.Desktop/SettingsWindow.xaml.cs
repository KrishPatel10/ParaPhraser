using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ParaPhraser.Core;
using ParaPhraser.Desktop.Services;

namespace ParaPhraser.Desktop;

public partial class SettingsWindow : Window
{
    private const string OllamaDownloadUrl = "https://ollama.com/download";

    private readonly AppSettings _settings;
    private readonly Action _onSaved;
    private CancellationTokenSource? _download;

    public SettingsWindow(AppSettings settings, Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;

        ProviderComboBox.ItemsSource = OnlineProvider.All;
        ProviderComboBox.SelectedItem = OnlineProvider.Find(settings.OnlineProviderId);
        OfflineModelComboBox.Text = new OllamaBackend(settings.OfflineModel).Model;
        (settings.IsOnline ? OnlineMode : OfflineMode).IsChecked = true;
        _ = RefreshOllamaAsync();
    }

    private OnlineProvider SelectedProvider => (OnlineProvider)ProviderComboBox.SelectedItem;

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        var isOnline = OnlineMode.IsChecked == true;
        OnlinePanel.Visibility = isOnline ? Visibility.Visible : Visibility.Collapsed;
        OfflinePanel.Visibility = isOnline ? Visibility.Collapsed : Visibility.Visible;
        SettingsStatusText.Text = string.Empty;
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApiKeyBox.Clear();
        OnlineModelComboBox.ItemsSource = null;
        OnlineModelComboBox.Text = _settings.GetProvider(SelectedProvider.Id).Model ?? string.Empty;
        PrivacyNoticeText.Text =
            $"Your selected text and instruction are sent to {SelectedProvider.Name}, whose terms and pricing apply. "
            + "App names and window titles are not sent. Your key is encrypted for your Windows account.";
        UpdateKeyState();
    }

    private void UpdateKeyState()
    {
        var hasKey = _settings.HasApiKey(SelectedProvider.Id);
        RemoveKeyButton.IsEnabled = hasKey;
        KeyHintText.Text = hasKey
            ? "A key is saved. Leave this empty to keep it."
            : $"Paste your {SelectedProvider.Name} API key.";
    }

    private void GetOllamaButton_Click(object sender, RoutedEventArgs e) => OpenInBrowser(OllamaDownloadUrl);

    private void GetKeyButton_Click(object sender, RoutedEventArgs e) => OpenInBrowser(SelectedProvider.KeyPageUrl);

    private async void CheckOllamaButton_Click(object sender, RoutedEventArgs e) => await RefreshOllamaAsync();

    private async Task RefreshOllamaAsync()
    {
        OllamaStatusText.Text = "Checking Ollama…";
        var backend = new OllamaBackend(OfflineModelComboBox.Text);
        OllamaStatusText.Text = (await backend.CheckStatusAsync()).Message;

        try
        {
            var typedModel = OfflineModelComboBox.Text;
            OfflineModelComboBox.ItemsSource = await backend.ListModelsAsync();
            OfflineModelComboBox.Text = typedModel;
        }
        catch (AiUnavailableException)
        {
            // The status line above already says Ollama is unreachable.
        }
    }

    private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_download is not null)
        {
            _download.Cancel();
            return;
        }

        _download = new CancellationTokenSource();
        DownloadModelButton.Content = "Stop download";
        var backend = new OllamaBackend(OfflineModelComboBox.Text);

        try
        {
            await backend.DownloadModelAsync(
                new Progress<string>(update => SettingsStatusText.Text = update),
                _download.Token);
            SettingsStatusText.Text = $"{backend.Model} is ready.";
            await RefreshOllamaAsync();
        }
        catch (OperationCanceledException)
        {
            SettingsStatusText.Text = "Download stopped. It resumes where it left off next time.";
        }
        catch (InvalidOperationException exception)
        {
            SettingsStatusText.Text = exception.Message;
        }
        finally
        {
            _download.Dispose();
            _download = null;
            DownloadModelButton.Content = "Download model";
        }
    }

    private void RemoveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetApiKey(SelectedProvider.Id, null);
        _settings.Save();
        ApiKeyBox.Clear();
        UpdateKeyState();
        SettingsStatusText.Text = "Key removed.";

        // The running provider still holds the key in memory until it is rebuilt.
        if (_settings.IsOnline)
        {
            _onSaved();
        }
    }

    private async void LoadModelsButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = GetEnteredApiKey();
        if (apiKey is null)
        {
            SettingsStatusText.Text = "Paste your API key first.";
            ApiKeyBox.Focus();
            return;
        }

        LoadModelsButton.IsEnabled = false;
        SettingsStatusText.Text = $"Loading models from {SelectedProvider.Name}…";

        try
        {
            var models = await SelectedProvider.CreateBackend(apiKey, string.Empty).ListModelsAsync();
            var typedModel = OnlineModelComboBox.Text;
            OnlineModelComboBox.ItemsSource = models;
            OnlineModelComboBox.Text = typedModel;
            OnlineModelComboBox.IsDropDownOpen = string.IsNullOrWhiteSpace(typedModel);
            SettingsStatusText.Text = $"{models.Count} models available. Choose one, then Save.";
        }
        catch (InvalidOperationException exception)
        {
            SettingsStatusText.Text = exception.Message;
        }
        finally
        {
            LoadModelsButton.IsEnabled = true;
        }
    }

    private string? GetEnteredApiKey() => string.IsNullOrWhiteSpace(ApiKeyBox.Password)
        ? _settings.GetApiKey(SelectedProvider.Id)
        : ApiKeyBox.Password.Trim();

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (OnlineMode.IsChecked == true && !await TryPrepareOnlineAsync())
        {
            return;
        }

        var isOnline = OnlineMode.IsChecked == true;
        _settings.IsOnline = isOnline;
        _settings.OfflineModel = OfflineModelComboBox.Text.Trim();

        if (isOnline)
        {
            _settings.OnlineProviderId = SelectedProvider.Id;
            _settings.GetProvider(SelectedProvider.Id).Model = OnlineModelComboBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                _settings.SetApiKey(SelectedProvider.Id, ApiKeyBox.Password);
            }
        }

        try
        {
            _settings.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SettingsStatusText.Text = $"Could not save settings: {exception.Message}";
            return;
        }

        _onSaved();
        Close();
    }

    /// <summary>
    /// A bad key should fail here, not on the user's first rewrite.
    /// </summary>
    private async Task<bool> TryPrepareOnlineAsync()
    {
        var apiKey = GetEnteredApiKey();
        if (apiKey is null)
        {
            SettingsStatusText.Text = "Paste your API key first.";
            ApiKeyBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(OnlineModelComboBox.Text))
        {
            SettingsStatusText.Text = "Choose a model first. Load models lists what your key can use.";
            OnlineModelComboBox.Focus();
            return false;
        }

        SaveButton.IsEnabled = false;
        SettingsStatusText.Text = $"Checking your key with {SelectedProvider.Name}…";
        var status = await SelectedProvider
            .CreateBackend(apiKey, OnlineModelComboBox.Text.Trim())
            .CheckStatusAsync();
        SaveButton.IsEnabled = true;
        SettingsStatusText.Text = status.Message;
        return status.IsReady;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _download?.Cancel();
        base.OnClosed(e);
    }

    private static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
