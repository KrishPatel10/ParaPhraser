using System.Drawing;
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
    private const int OllamaStartupAttempts = 5;

    private readonly StartupRegistrationService _startupRegistration = new();
    private Mutex? _singleInstanceMutex;
    private NotifyIcon? _trayIcon;
    private ToolStripMenuItem? _startupMenuItem;
    private GlobalHotkeyService? _hotkeys;
    private OverlayWindow? _overlay;
    private RewriteCoordinator? _coordinator;
    private OllamaTextTransformer? _ollamaTransformer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "Local\\ParaPhraser.Desktop",
            createdNew: out var isFirstInstance);

        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        _overlay = new OverlayWindow();
        var selectionService = new ClipboardSelectionService();
        var configuredModel = Environment.GetEnvironmentVariable("PARAPHRASER_OLLAMA_MODEL");
        _ollamaTransformer = new OllamaTextTransformer(configuredModel);
        _coordinator = new RewriteCoordinator(selectionService, _ollamaTransformer, _overlay);
        _coordinator.LocalAiUnavailable += OnLocalAiUnavailable;

        _hotkeys = new GlobalHotkeyService();
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _hotkeys.Register(GrammarHotkeyId, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey.G);
        _hotkeys.Register(RewriteHotkeyId, HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey.R);

        ConfigureTrayIcon();
        _ = NotifyOllamaStatusAsync();

        DispatcherUnhandledException += (_, args) =>
        {
            _overlay.ShowError(args.Exception.Message);
            args.Handled = true;
        };
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
    }

    private async Task NotifyOllamaStatusAsync()
    {
        if (_ollamaTransformer is null)
        {
            return;
        }

        LocalAiStatus status = new(false, "Ollama is not ready.");
        for (var attempt = 0; attempt < OllamaStartupAttempts; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            status = await _ollamaTransformer.CheckStatusAsync(timeout.Token);
            if (status.IsReady)
            {
                break;
            }

            if (attempt < OllamaStartupAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        ShowNotification(
            status.IsReady ? "ParaPhraser is ready" : "Local AI isn't ready",
            status.Message,
            status.IsReady ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void OnLocalAiUnavailable(object? sender, string message)
    {
        ShowNotification("Local AI isn't ready", message, ToolTipIcon.Warning);
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
            _coordinator.LocalAiUnavailable -= OnLocalAiUnavailable;
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
