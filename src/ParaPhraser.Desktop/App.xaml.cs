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
    private const int OllamaStartupAttempts = 5;

    private readonly StartupRegistrationService _startupRegistration = new();
    private Mutex? _singleInstanceMutex;
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _startupMenuItem;
    private ToolStripMenuItem? _providerMenuItem;
    private GlobalHotkeyService? _hotkeys;
    private OverlayWindow? _overlay;
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
        _coordinator = new RewriteCoordinator(selectionService, _transformer, _overlay);
        _coordinator.AiUnavailable += OnAiUnavailable;

        _hotkeys = new GlobalHotkeyService();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _hotkeys.Register(GrammarHotkeyId, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey.G);
        _hotkeys.Register(RewriteHotkeyId, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey.R);

        ConfigureTrayIcon();
        RegisterFormatHotkey();
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

    // Format is also in the action list, so losing this shortcut to another app
    // must not stop ParaPhraser from starting.
    private void RegisterFormatHotkey()
    {
        try
        {
            _hotkeys?.Register(FormatHotkeyId, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey.F);
        }
        catch (Win32Exception exception)
        {
            AppLog.Warn($"Ctrl+Alt+F could not be registered: {exception.Message}");
            ShowNotification(
                "Ctrl+Alt+F isn't available",
                $"{exception.Message} Use Format from the Action list instead.",
                ToolTipIcon.Warning);
        }
    }

    private async void OnHotkeyPressed(object? sender, int hotkeyId)
    {
        if (_coordinator is null)
        {
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
        menu.Items.Add(new ToolStripMenuItem("Ctrl+Alt+G  Fix grammar") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Ctrl+Alt+R  Rewrite") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Ctrl+Alt+F  Format") { Enabled = false });
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
            _settingsWindow = new SettingsWindow(_settings, OnSettingsSaved);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

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
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
