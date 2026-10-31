using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using ParaPhraser.Core;
using ParaPhraser.Desktop.Services;
using Application = System.Windows.Application;

namespace ParaPhraser.Desktop;

public partial class App : Application
{
    private const int GrammarHotkeyId = 1;
    private const int RewriteHotkeyId = 2;
    private const int FormatHotkeyId = 3;
    private const int InstantHotkeyId = 4;
    private const int OllamaStartupAttempts = 5;
    private const int FallbackListSeconds = 8;

    private readonly StartupRegistrationService _startupRegistration = new();
    private readonly HashSet<string> _triedFallbackModels = [];
    private Mutex? _singleInstanceMutex;
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _startupMenuItem;
    private ToolStripMenuItem? _providerMenuItem;
    private ToolStripMenuItem? _grammarMenuItem;
    private ToolStripMenuItem? _rewriteMenuItem;
    private ToolStripMenuItem? _formatMenuItem;
    private ToolStripMenuItem? _instantMenuItem;
    private GlobalHotkeyService? _hotkeys;
    private OverlayWindow? _overlay;
    private InstantToast? _toast;
    private RewriteCoordinator? _coordinator;
    private ChatTextTransformer? _transformer;
    private AppSettings? _settings;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "Local\\ParaPhraser.Desktop",
            createdNew: out var isFirstInstance);

        if (!isFirstInstance)
        {
            // This instance never owned the mutex, so OnExit must not release it.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _overlay = new OverlayWindow();
        var selectionService = new ClipboardSelectionService();
        var isFirstRun = !File.Exists(AppSettings.DefaultPath);
        _settings = AppSettings.Load();
        _transformer = new ChatTextTransformer(_settings.CreateBackend());
        AppLog.Info($"ParaPhraser started, version {GetType().Assembly.GetName().Version}, using {_transformer.Label}");
        _toast = new InstantToast();
        _coordinator = new RewriteCoordinator(selectionService, _transformer, _overlay, _toast)
        {
            FindFallbackModelAsync = FindFallbackModelAsync,
            UseModel = UseFallbackModel
        };
        _coordinator.AiUnavailable += OnAiUnavailable;
        _coordinator.RewriteSucceeded += OnRewriteSucceeded;
        _overlay.SettingsRequested += (_, _) => OpenSettings();
        _overlay.SetSavedInstructions(_settings.SavedInstructions);

        _hotkeys = new GlobalHotkeyService();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        ConfigureTrayIcon();
        RegisterShortcuts();
        ShowCurrentProvider();
        _ = NotifyAiStatusAsync(openSettingsIfNotReady: isFirstRun);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled error on the UI thread", args.Exception);
            _overlay.ShowError(args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled error, the app is closing", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLog.Error("Unobserved background task error", args.Exception);
    }

    // Another app can own any shortcut, and that must not stop ParaPhraser from
    // starting, so each failure is reported and the rest still register.
    private void RegisterShortcuts()
    {
        if (_hotkeys is null || _settings is null)
        {
            return;
        }

        _hotkeys.UnregisterAll();
        var shortcuts = _settings.Shortcuts;
        (int Id, string Text)[] bindings =
        [
            (GrammarHotkeyId, shortcuts.Grammar),
            (RewriteHotkeyId, shortcuts.Rewrite),
            (FormatHotkeyId, shortcuts.Format),
            (InstantHotkeyId, shortcuts.Instant)
        ];

        var failed = new List<string>();
        foreach (var (id, text) in bindings)
        {
            try
            {
                if (HotkeyText.TryParse(text, out var modifiers, out var virtualKey))
                {
                    _hotkeys.Register(id, modifiers, virtualKey);
                }
                else
                {
                    failed.Add(text);
                }
            }
            catch (Win32Exception exception)
            {
                AppLog.Warn($"{text} could not be registered: {exception.Message}");
                failed.Add(text);
            }
        }

        if (_grammarMenuItem is not null)
        {
            _grammarMenuItem.Text = $"{shortcuts.Grammar}  Fix grammar";
            _rewriteMenuItem!.Text = $"{shortcuts.Rewrite}  Rewrite";
            _formatMenuItem!.Text = $"{shortcuts.Format}  Format";
            _instantMenuItem!.Text = $"{shortcuts.Instant}  Direct replace";
        }

        if (failed.Count > 0)
        {
            ShowNotification(
                "A shortcut isn't available",
                $"{string.Join(", ", failed)} may be used by another app. Pick another in Settings.",
                ToolTipIcon.Warning);
        }
    }

    private void PauseHotkeys(bool pause)
    {
        if (pause)
        {
            _hotkeys?.UnregisterAll();
        }
        else if (_settingsWindow is not null)
        {
            RegisterShortcuts();
        }
    }

    private async Task<string?> FindFallbackModelAsync()
    {
        if (_settings is not { IsOnline: true } || _transformer is null)
        {
            return null;
        }

        var provider = _settings.GetProvider(OnlineProvider.Find(_settings.OnlineProviderId).Id);
        var currentModel = provider.Model ?? string.Empty;
        _triedFallbackModels.Add(currentModel);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(FallbackListSeconds));
            var models = await _transformer.Backend.ListModelsAsync(timeout.Token);
            return OnlineProvider.PickFallbackModel(models, currentModel, provider.LastWorkingModel, _triedFallbackModels);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            AppLog.Warn($"Could not list fallback models: {exception.GetType().Name}");
            return null;
        }
    }

    private void UseFallbackModel(string model)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.GetProvider(OnlineProvider.Find(_settings.OnlineProviderId).Id).Model = model;
        SaveSettings();
        _transformer = new ChatTextTransformer(_settings.CreateBackend());
        AppLog.Info($"Switched to fallback model {_transformer.Label}");
        _coordinator?.UseTransformer(_transformer);
        ShowCurrentProvider();
    }

    private void OnRewriteSucceeded(object? sender, EventArgs e)
    {
        _triedFallbackModels.Clear();
        if (_settings is not { IsOnline: true })
        {
            return;
        }

        var provider = _settings.GetProvider(OnlineProvider.Find(_settings.OnlineProviderId).Id);
        if (provider.LastWorkingModel != provider.Model)
        {
            provider.LastWorkingModel = provider.Model;
            SaveSettings();
        }
    }

    // Bookkeeping only, so a failed write must not interrupt a finished rewrite.
    private void SaveSettings()
    {
        try
        {
            _settings?.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not save settings: {exception.GetType().Name}");
        }
    }

    private async void OnHotkeyPressed(object? sender, int hotkeyId)
    {
        if (_coordinator is null)
        {
            return;
        }

        if (hotkeyId == InstantHotkeyId)
        {
            await _coordinator.HandleInstantAsync(_settings?.InstantMode ?? RewriteMode.Grammar);
            return;
        }

        var mode = hotkeyId switch
        {
            GrammarHotkeyId => RewriteMode.Grammar,
            RewriteHotkeyId => RewriteMode.Rewrite,
            FormatHotkeyId => RewriteMode.Format,
            _ => (RewriteMode?)null
        };

        if (mode is not null)
        {
            await _coordinator.HandleAsync(mode.Value);
        }
    }

    private void ConfigureTrayIcon()
    {
        var menu = new ContextMenuStrip();
        _grammarMenuItem = new ToolStripMenuItem { Enabled = false };
        _rewriteMenuItem = new ToolStripMenuItem { Enabled = false };
        _formatMenuItem = new ToolStripMenuItem { Enabled = false };
        _instantMenuItem = new ToolStripMenuItem { Enabled = false };
        menu.Items.Add(_grammarMenuItem);
        menu.Items.Add(_rewriteMenuItem);
        menu.Items.Add(_formatMenuItem);
        menu.Items.Add(_instantMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        _providerMenuItem = new ToolStripMenuItem { Enabled = false };
        menu.Items.Add(_providerMenuItem);
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open logs folder", null, (_, _) => OpenLogsFolder());
        menu.Items.Add(new ToolStripSeparator());
        _startupMenuItem = new ToolStripMenuItem("Run ParaPhraser at startup")
        {
            Checked = _startupRegistration.IsEnabled
        };
        _startupMenuItem.Click += ToggleStartupRegistration;
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _trayIcon = new NotifyIcon
        {
            Text = "ParaPhraser",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.BalloonTipClicked += (_, _) => OpenSettings();
    }

    private static void OpenLogsFolder()
    {
        var folder = Path.GetDirectoryName(AppLog.FilePath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, OnSettingsSaved, PauseHotkeys);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                RegisterShortcuts();

                // Hand focus back so the user can press Generate straight away.
                if (_overlay?.IsVisible == true)
                {
                    _overlay.Activate();
                }
            };
            _settingsWindow.Show();
        }

        // The overlay is always on top, so Settings must be too or it opens behind
        // it. Only then, because the links it opens would land behind Settings.
        _settingsWindow.Topmost = _overlay?.IsVisible == true;
        _settingsWindow.Activate();
    }

    private void OnSettingsSaved()
    {
        if (_settings is null)
        {
            return;
        }

        _transformer = new ChatTextTransformer(_settings.CreateBackend());
        AppLog.Info($"Settings saved, now using {_transformer.Label}");
        _coordinator?.UseTransformer(_transformer);
        _triedFallbackModels.Clear();
        _overlay?.SetSavedInstructions(_settings.SavedInstructions);
        ShowCurrentProvider();
        _ = NotifyAiStatusAsync(openSettingsIfNotReady: false);
    }

    private void ShowCurrentProvider()
    {
        if (_transformer is null)
        {
            return;
        }

        var backend = _transformer.Backend;
        _overlay?.SetProvider(backend.ProviderName, backend.IsLocal);
        if (_providerMenuItem is not null)
        {
            _providerMenuItem.Text = $"Using {backend.Label}";
        }
    }

    private async Task NotifyAiStatusAsync(bool openSettingsIfNotReady)
    {
        if (_transformer is null)
        {
            return;
        }

        // Ollama may still be starting with Windows. An online check will not
        // change by retrying.
        var backend = _transformer.Backend;
        var attempts = backend.IsLocal ? OllamaStartupAttempts : 1;

        AiStatus status = new(false, $"{backend.ProviderName} is not ready.");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(backend.IsLocal ? 3 : 15));
            status = await backend.CheckStatusAsync(timeout.Token);
            if (status.IsReady)
            {
                break;
            }

            if (attempt < attempts - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        if (status.IsReady)
        {
            AppLog.Info($"Status check passed: {status.Message}");
        }
        else
        {
            AppLog.Warn($"Status check failed: {status.Message}");
        }

        ShowNotification(
            status.IsReady ? "ParaPhraser is ready" : "AI isn't ready",
            status.Message,
            status.IsReady ? ToolTipIcon.Info : ToolTipIcon.Warning);

        if (!status.IsReady && openSettingsIfNotReady)
        {
            OpenSettings();
        }
    }

    private void OnAiUnavailable(object? sender, string message)
    {
        ShowNotification("AI isn't ready", message, ToolTipIcon.Warning);
    }

    private void ToggleStartupRegistration(object? sender, EventArgs e)
    {
        if (_startupMenuItem is null)
        {
            return;
        }

        try
        {
            var enable = !_startupRegistration.IsEnabled;
            _startupRegistration.SetEnabled(enable);
            _startupMenuItem.Checked = enable;
            ShowNotification(
                "Startup setting updated",
                enable
                    ? "ParaPhraser will start when you sign in to Windows."
                    : "ParaPhraser will no longer start with Windows.",
                ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _startupMenuItem.Checked = _startupRegistration.IsEnabled;
            ShowNotification("Could not update startup", exception.Message, ToolTipIcon.Error);
        }
    }

    private void ShowNotification(string title, string message, ToolTipIcon icon)
    {
        _trayIcon?.ShowBalloonTip(5000, title, message, icon);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_coordinator is not null)
        {
            _coordinator.AiUnavailable -= OnAiUnavailable;
            _coordinator.RewriteSucceeded -= OnRewriteSucceeded;
        }

        if (_hotkeys is not null)
        {
            _hotkeys.HotkeyPressed -= OnHotkeyPressed;
            _hotkeys.Dispose();
        }

        if (_trayIcon is not null)
        {
            if (_startupMenuItem is not null)
            {
                _startupMenuItem.Click -= ToggleStartupRegistration;
            }

            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _overlay?.CloseForShutdown();
        _toast?.Close();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
