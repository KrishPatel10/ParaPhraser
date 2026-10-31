using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ParaPhraser.Core;
using ParaPhraser.Desktop.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace ParaPhraser.Desktop;

public partial class OverlayWindow : Window
{
    private static readonly (string Name, Color Color)[] TextColors =
    [
        ("Red", Color.FromRgb(0xC0, 0x00, 0x00)),
        ("Orange", Color.FromRgb(0xC5, 0x5A, 0x11)),
        ("Green", Color.FromRgb(0x2E, 0x7D, 0x32)),
        ("Blue", Color.FromRgb(0x00, 0x70, 0xC0)),
        ("Purple", Color.FromRgb(0x70, 0x30, 0xA0))
    ];

    private static readonly (string Name, Color Color)[] HighlightColors =
    [
        ("Yellow", Color.FromRgb(0xFF, 0xFF, 0x00)),
        ("Green", Color.FromRgb(0x00, 0xFF, 0x00)),
        ("Cyan", Color.FromRgb(0x00, 0xFF, 0xFF)),
        ("Pink", Color.FromRgb(0xFF, 0x00, 0xFF)),
        ("Orange", Color.FromRgb(0xFF, 0xC0, 0x00))
    ];

    private Func<RichDocument, string, RewriteMode, RewriteTone, Task>? _generateAsync;
    private Func<RichDocument, Task>? _replaceAsync;
    private Func<RichDocument, Task>? _copyAsync;
    private string? _lastDefaultInstruction;
    private bool _settingComposer;
    private bool _allowClose;
    private string _providerName = "Ollama";
    private bool _isLocalProvider = true;
    private Action? _stopGeneration;
    private bool _hasPartialText;

    public event EventHandler? SettingsRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        AddSwatches(
            TextColorSwatches,
            TextElement.ForegroundProperty,
            TextColors,
            "Default text color",
            (Brush)FindResource("OverlayPaperTextBrush"));
        AddSwatches(
            HighlightSwatches,
            TextElement.BackgroundProperty,
            HighlightColors,
            "No highlight",
            Brushes.Transparent);

        // Closing the overlay must not leave an online request running and billing.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                _stopGeneration?.Invoke();
            }
        };
    }

    private void AddSwatches(
        System.Windows.Controls.Panel panel,
        DependencyProperty property,
        (string Name, Color Color)[] colors,
        string clearName,
        Brush clearBrush)
    {
        var style = (Style)FindResource("SwatchButtonStyle");
        foreach (var (name, color) in colors)
        {
            var brush = new SolidColorBrush(color);
            panel.Children.Add(CreateSwatch(name, brush, string.Empty, style, property, brush));
        }

        panel.Children.Add(CreateSwatch(clearName, Brushes.White, "×", style, property, clearBrush));
    }

    private System.Windows.Controls.Button CreateSwatch(
        string name,
        Brush background,
        string label,
        Style style,
        DependencyProperty property,
        Brush value)
    {
        var swatch = new System.Windows.Controls.Button
        {
            Background = background,
            Content = label,
            Style = style,
            ToolTip = name
        };
        AutomationProperties.SetName(swatch, name);
        swatch.Click += (_, _) => ApplyToSuggestion(property, value);
        return swatch;
    }

    private void ApplyToSuggestion(DependencyProperty property, Brush value)
    {
        ColorPopup.IsOpen = false;
        ResultText.Focus();
        if (ResultText.Selection.IsEmpty)
        {
            StatusText.Text = "Select text in the suggestion first";
            return;
        }

        ResultText.Selection.ApplyPropertyValue(property, value);
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = true;
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            TextColorSwatches.Children[0].Focus();
        }
    }

    private void ColorPopup_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ColorPopup.IsOpen = false;
            ColorButton.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Keeps the overlay honest about where the selected text goes.
    /// </summary>
    public void SetProvider(string providerName, bool isLocal)
    {
        _providerName = providerName;
        _isLocalProvider = isLocal;
        PrivacyText.Text = isLocal
            ? "Private processing · No history stored"
            : $"Sent to {providerName} · No history stored here";

        if (IsVisible && _stopGeneration is null)
        {
            StatusText.Text = "Settings updated. Press Generate to try again.";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    public void ShowComposer(
        RewriteMode mode,
        RichDocument original,
        string source,
        string instruction,
        string status,
        Func<RichDocument, string, RewriteMode, RewriteTone, Task> generateAsync)
    {
        _settingComposer = true;
        ModeText.Text = GetModeLabel(mode);
        SourceText.Text = source;
        OriginalText.Document = RichFlowDocument.ToFlowDocument(original);
        InstructionText.Text = instruction;
        _lastDefaultInstruction = instruction;
        ActionComboBox.SelectedIndex = mode switch
        {
            RewriteMode.Grammar => 0,
            RewriteMode.Shorten => 2,
            RewriteMode.Format => 3,
            _ => 1
        };
        ToneComboBox.SelectedIndex = 0;
        _settingComposer = false;
        UpdateModeControls(mode);
        ShowResultMessage(string.Empty);
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = status;
        GenerateButton.IsEnabled = true;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        _generateAsync = generateAsync;
        _replaceAsync = null;
        _copyAsync = null;

        ShowNearCursor();
        InstructionText.Focus();
        InstructionText.CaretIndex = InstructionText.Text.Length;
    }

    public void ShowGenerating(Action stop)
    {
        ResultPanel.Visibility = Visibility.Visible;
        ShowResultMessage(_isLocalProvider ? "Generating locally…" : "Generating…");
        StatusText.Text = $"Waiting for {_providerName}";
        _stopGeneration = stop;
        _hasPartialText = false;
        GenerateButton.Content = "Stop  ■";
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        _replaceAsync = null;
        _copyAsync = null;
        UpdateLayout();
        KeepOnScreen();
    }

    public void ShowPartial(RichDocument partial)
    {
        if (_stopGeneration is null)
        {
            return;
        }

        _hasPartialText = true;
        ResultText.Document = RichFlowDocument.ToFlowDocument(partial);
        ResultText.ScrollToEnd();
        StatusText.Text = $"Writing with {_providerName}…";
        UpdateLayout();
        KeepOnScreen();
    }

    /// <summary>
    /// Keeps what was written visible, but a cut-off rewrite cannot be pasted
    /// over the original.
    /// </summary>
    public void ShowStopped()
    {
        if (!_hasPartialText)
        {
            ShowResultMessage("Stopped before any text arrived.");
        }

        StatusText.Text = "Stopped. Generate again when you're ready.";
        EndGeneration();
    }

    private void EndGeneration()
    {
        _stopGeneration = null;
        GenerateButton.Content = "Generate  ✦";
    }

    public void ShowSuggestion(
        RichDocument suggestion,
        string status,
        Func<RichDocument, Task> replaceAsync,
        Func<RichDocument, Task> copyAsync)
    {
        EndGeneration();
        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Document = RichFlowDocument.ToFlowDocument(suggestion);
        ResultText.IsReadOnly = false;
        FormatToolbar.IsEnabled = true;
        StatusText.Text = status;
        GenerateButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
        ReplaceButton.IsEnabled = true;
        _replaceAsync = replaceAsync;
        _copyAsync = copyAsync;
        UpdateLayout();
        KeepOnScreen();
    }

    public void ShowError(string message, bool canRetry = true)
    {
        if (!canRetry)
        {
            _generateAsync = null;
        }

        if (string.IsNullOrWhiteSpace(ModeText.Text))
        {
            ModeText.Text = "Needs attention";
        }

        if (string.IsNullOrWhiteSpace(SourceText.Text))
        {
            SourceText.Text = "Writing assistant";
        }

        EndGeneration();
        ResultPanel.Visibility = Visibility.Visible;
        ShowResultMessage(message);
        StatusText.Text = "Press Esc to close";
        GenerateButton.IsEnabled = canRetry && _generateAsync is not null;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        _replaceAsync = null;
        _copyAsync = null;
        ShowNearCursor();
    }

    private void ShowResultMessage(string message)
    {
        ResultText.Document = RichFlowDocument.ToFlowDocument(RichDocument.FromPlainText(message));
        ResultText.IsReadOnly = true;
        FormatToolbar.IsEnabled = false;
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void ShowNearCursor()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
        UpdateLayout();

        var cursor = NativeMethods.GetCursorPosition();
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var position = transform.Transform(new Point(cursor.X, cursor.Y));

        Left = position.X + 12;
        Top = position.Y + 16;
        KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        Left = Math.Clamp(
            Left,
            SystemParameters.VirtualScreenLeft + 8,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth - 8);

        Top = Math.Clamp(
            Top,
            SystemParameters.VirtualScreenTop + 8,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight - 8);
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stopGeneration is not null)
        {
            StatusText.Text = "Stopping…";
            _stopGeneration();
            return;
        }

        await GenerateAsync();
    }

    private async Task GenerateAsync()
    {
        if (_generateAsync is null || !GenerateButton.IsEnabled || _stopGeneration is not null)
        {
            return;
        }

        var instruction = InstructionText.Text.Trim();
        var selection = RichFlowDocument.ToRichDocument(OriginalText.Document);
        if (string.IsNullOrWhiteSpace(selection.ToPlainText()))
        {
            StatusText.Text = "Selected text cannot be empty";
            OriginalText.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            StatusText.Text = "Add an instruction first";
            InstructionText.Focus();
            return;
        }

        var tone = ToneComboBox.SelectedIndex switch
        {
            1 => RewriteTone.Polite,
            2 => RewriteTone.Professional,
            3 => RewriteTone.Casual,
            4 => RewriteTone.Emojified,
            _ => RewriteTone.Original
        };

        await _generateAsync(
            selection,
            instruction,
            GetSelectedMode(),
            tone);
    }

    private void ActionComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingComposer || InstructionText is null)
        {
            return;
        }

        var mode = GetSelectedMode();
        var nextDefault = GetDefaultInstruction(mode);
        if (string.IsNullOrWhiteSpace(InstructionText.Text)
            || string.Equals(
                InstructionText.Text.Trim(),
                _lastDefaultInstruction,
                StringComparison.Ordinal))
        {
            InstructionText.Text = nextDefault;
            InstructionText.CaretIndex = InstructionText.Text.Length;
        }

        _lastDefaultInstruction = nextDefault;
        ModeText.Text = GetModeLabel(mode);
        UpdateModeControls(mode);
    }

    // Format keeps the writer's words, so tone and the English-only rule do not apply.
    private void UpdateModeControls(RewriteMode mode)
    {
        var isFormat = mode == RewriteMode.Format;
        ToneComboBox.IsEnabled = !isFormat;
        EnglishOutputBadge.Visibility = isFormat ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        await ReplaceAsync();
    }

    private async Task ReplaceAsync()
    {
        if (_replaceAsync is null)
        {
            return;
        }

        var replacement = RichFlowDocument.ToRichDocument(ResultText.Document);
        if (string.IsNullOrWhiteSpace(replacement.ToPlainText()))
        {
            StatusText.Text = "The suggestion is empty";
            ResultText.Focus();
            return;
        }

        ReplaceButton.IsEnabled = false;
        var replace = _replaceAsync;
        _replaceAsync = null;
        Hide();

        try
        {
            await replace(replacement);
        }
        catch (Exception exception)
        {
            AppLog.Error("Replace failed", exception);
            ShowError(exception.Message);
        }
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var content = RichFlowDocument.ToRichDocument(ResultText.Document);
        if (_copyAsync is not null && !string.IsNullOrWhiteSpace(content.ToPlainText()))
        {
            try
            {
                await _copyAsync(content);
                StatusText.Text = "Copied";
            }
            catch (ClipboardBusyException exception)
            {
                AppLog.Warn("Copy failed: the clipboard was busy");
                StatusText.Text = exception.Message;
            }
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await GenerateAsync();
        }
    }

    private static string GetModeLabel(RewriteMode mode) => mode switch
    {
        RewriteMode.Grammar => "Fix grammar",
        RewriteMode.Rewrite => "Rewrite",
        RewriteMode.Shorten => "Shorten",
        RewriteMode.Professional => "Professional",
        RewriteMode.Format => "Format",
        _ => mode.ToString()
    };

    private RewriteMode GetSelectedMode() => ActionComboBox.SelectedIndex switch
    {
        0 => RewriteMode.Grammar,
        2 => RewriteMode.Shorten,
        3 => RewriteMode.Format,
        _ => RewriteMode.Rewrite
    };

    private static string GetDefaultInstruction(RewriteMode mode) => mode switch
    {
        RewriteMode.Grammar => "Correct grammar, spelling, and punctuation. Preserve the meaning and tone.",
        RewriteMode.Shorten => "Make this concise while preserving the important information.",
        RewriteMode.Format => "Add bold, italic, underline, color, and highlight where they help. Keep every word unchanged.",
        _ => "Rewrite this clearly and naturally. Preserve the meaning and important details."
    };
}
