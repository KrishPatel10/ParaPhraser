using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ParaPhraser.Core;
using ParaPhraser.Desktop.Services;

namespace ParaPhraser.Desktop;

public partial class SettingsWindow : Window
{
    private const string OllamaDownloadUrl = "https://ollama.com/download";

    private readonly AppSettings _settings;
    private readonly Action _onSaved;
    private readonly Action<bool> _pauseHotkeys;
    private readonly ObservableCollection<SavedInstruction> _savedInstructions;
    private CancellationTokenSource? _download;

    /// <param name="settings">The settings to edit.</param>
    /// <param name="onSaved">Runs after a successful save.</param>
    /// <param name="pauseHotkeys">
    /// Pauses global shortcuts while one is being chosen, or pressing an existing
    /// one would run it instead of reaching the box.
    /// </param>
    public SettingsWindow(AppSettings settings, Action onSaved, Action<bool> pauseHotkeys)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;
        _pauseHotkeys = pauseHotkeys;

        GrammarShortcutBox.Text = settings.Shortcuts.Grammar;
        RewriteShortcutBox.Text = settings.Shortcuts.Rewrite;
        FormatShortcutBox.Text = settings.Shortcuts.Format;
        InstantShortcutBox.Text = settings.Shortcuts.Instant;
        InstantModeComboBox.SelectedIndex = settings.InstantMode switch
        {
            RewriteMode.Rewrite => 1,
            RewriteMode.Shorten => 2,
            _ => 0
        };

        // Edited as copies, so Cancel leaves the saved list alone.
        _savedInstructions = new ObservableCollection<SavedInstruction>(settings.SavedInstructions.Select(
            saved => new SavedInstruction { Name = saved.Name, Instruction = saved.Instruction }));
        SavedInstructionsList.ItemsSource = _savedInstructions;

        ProviderComboBox.ItemsSource = OnlineProvider.All;
        ProviderComboBox.SelectedItem = OnlineProvider.Find(settings.OnlineProviderId);
        OfflineModelComboBox.Text = new OllamaBackend(settings.OfflineModel).Model;
        (settings.IsOnline ? OnlineMode : OfflineMode).IsChecked = true;
        _ = RefreshOllamaAsync();
    }

    private OnlineProvider SelectedProvider => (OnlineProvider)ProviderComboBox.SelectedItem;

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (AiTab is null || ShortcutsTab is null || InstructionsTab is null)
        {
            return;
        }

        AiTab.Visibility = AiTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsTab.Visibility = ShortcutsTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InstructionsTab.Visibility = InstructionsTabButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsStatusText.Text = string.Empty;
    }

    private void ShortcutBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _pauseHotkeys(true);

    private void ShortcutBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => _pauseHotkeys(false);

    private void ShortcutBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var shortcut = HotkeyText.Format(Keyboard.Modifiers, key);
        if (shortcut is null)
        {
            SettingsStatusText.Text = "Use Ctrl, Alt, or Win together with another key.";
            return;
        }

        ((System.Windows.Controls.TextBox)sender).Text = shortcut;
        SettingsStatusText.Text = string.Empty;
    }

    private void AddInstructionButton_Click(object sender, RoutedEventArgs e) =>
        _savedInstructions.Add(new SavedInstruction());

    private void RemoveInstructionButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SavedInstruction saved)
        {
            _savedInstructions.Remove(saved);
        }
    }

    /// <summary>
    /// Keeps the save valid on tabs the user may not be looking at.
    /// </summary>
    private bool TryValidateTabs()
    {
        string[] shortcuts = [GrammarShortcutBox.Text, RewriteShortcutBox.Text, FormatShortcutBox.Text, InstantShortcutBox.Text];
        if (shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != shortcuts.Length)
        {
            ShortcutsTabButton.IsChecked = true;
            SettingsStatusText.Text = "Each action needs its own shortcut.";
            return false;
        }

        if (_savedInstructions.Any(saved =>
            string.IsNullOrWhiteSpace(saved.Name) != string.IsNullOrWhiteSpace(saved.Instruction)))
        {
            InstructionsTabButton.IsChecked = true;
            SettingsStatusText.Text = "Each saved instruction needs both a name and its text.";
            return false;
        }

        return true;
    }

    private void ApplyTabs()
    {
        _settings.Shortcuts.Grammar = GrammarShortcutBox.Text;
        _settings.Shortcuts.Rewrite = RewriteShortcutBox.Text;
        _settings.Shortcuts.Format = FormatShortcutBox.Text;
        _settings.Shortcuts.Instant = InstantShortcutBox.Text;
        _settings.InstantMode = InstantModeComboBox.SelectedIndex switch
        {
            1 => RewriteMode.Rewrite,
            2 => RewriteMode.Shorten,
            _ => RewriteMode.Grammar
        };
        _settings.SavedInstructions = _savedInstructions
            .Where(saved => !string.IsNullOrWhiteSpace(saved.Name))
            .ToList();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        var isOnline = OnlineMode.IsChecked == true;
        OnlinePanel.Visibility = isOnline ? Visibility.Visible : Visibility.Collapsed;
        OfflinePanel.Visibility = isOnline ? Visibility.Collapsed : Visibility.Visible;
        SettingsStatusText.Text = string.Empty;
        _ = LoadSavedKeyModelsAsync();
    }

    /// <summary>
    /// Someone here to escape an overloaded model should see their choices at
    /// once, without pressing Load models first.
    /// </summary>
    private async Task LoadSavedKeyModelsAsync()
    {
        if (OnlineMode.IsChecked == true && _settings.HasApiKey(SelectedProvider.Id))
        {
            await LoadModelsAsync();
        }
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
        _ = LoadSavedKeyModelsAsync();
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

    private async void LoadModelsButton_Click(object sender, RoutedEventArgs e) => await LoadModelsAsync();

    private async Task LoadModelsAsync()
    {
        var provider = SelectedProvider;
        var apiKey = GetEnteredApiKey();
        if (apiKey is null)
        {
            SettingsStatusText.Text = "Paste your API key first.";
            ApiKeyBox.Focus();
            return;
        }

        LoadModelsButton.IsEnabled = false;
        SettingsStatusText.Text = $"Loading models from {provider.Name}…";

        try
        {
            var models = await provider.CreateBackend(apiKey, string.Empty).ListModelsAsync();

            // The user may have picked another provider while this was loading.
            if (provider != SelectedProvider)
            {
                return;
            }

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
        if (!TryValidateTabs())
        {
            return;
        }

        if (OnlineMode.IsChecked == true && HasOnlineChoiceChanged() && !await TryPrepareOnlineAsync())
        {
            AiTabButton.IsChecked = true;
            return;
        }

        var isOnline = OnlineMode.IsChecked == true;
        ApplyTabs();
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
    /// Changing only a shortcut must not depend on the provider being reachable.
    /// </summary>
    private bool HasOnlineChoiceChanged() =>
        !_settings.IsOnline
        || _settings.OnlineProviderId != SelectedProvider.Id
        || _settings.GetProvider(SelectedProvider.Id).Model != OnlineModelComboBox.Text.Trim()
        || !string.IsNullOrWhiteSpace(ApiKeyBox.Password);

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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _download?.Cancel();
        base.OnClosed(e);
    }

    private static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
