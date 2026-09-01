using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ParaPhraser.Core;
using ParaPhraser.Desktop.Services;
using Clipboard = System.Windows.Clipboard;
using Point = System.Windows.Point;

namespace ParaPhraser.Desktop;

public partial class OverlayWindow : Window
{
    private Func<string, string, RewriteMode, RewriteTone, Task>? _generateAsync;
    private Func<Task>? _replaceAsync;
    private string? _lastDefaultInstruction;
    private bool _settingComposer;
    private bool _allowClose;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void ShowComposer(
        RewriteMode mode,
        string original,
        string source,
        string instruction,
        Func<string, string, RewriteMode, RewriteTone, Task> generateAsync)
    {
        _settingComposer = true;
        ModeText.Text = GetModeLabel(mode);
        SourceText.Text = source;
        OriginalText.Text = original;
        InstructionText.Text = instruction;
        _lastDefaultInstruction = instruction;
        ActionComboBox.SelectedIndex = mode switch
        {
            RewriteMode.Grammar => 0,
            RewriteMode.Shorten => 2,
            _ => 1
        };
        ToneComboBox.SelectedIndex = 0;
        _settingComposer = false;
        ResultText.Text = string.Empty;
        ResultPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "No history is stored";
        GenerateButton.IsEnabled = true;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        _generateAsync = generateAsync;
        _replaceAsync = null;

        ShowNearCursor();
        InstructionText.Focus();
        InstructionText.CaretIndex = InstructionText.Text.Length;
    }

    public void ShowGenerating()
    {
        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Text = "Generating locally…";
        StatusText.Text = "Waiting for Ollama";
        GenerateButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        _replaceAsync = null;
        UpdateLayout();
        KeepOnScreen();
    }

    public void ShowSuggestion(
        RewriteResult result,
        Func<Task> replaceAsync)
    {
        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Text = result.Text;
        StatusText.Text = $"{result.Provider} · {result.Duration.TotalSeconds:0.0} s";
        GenerateButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
        ReplaceButton.IsEnabled = true;
        _replaceAsync = replaceAsync;
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
            SourceText.Text = "Local assistant";
        }

        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Text = message;
        StatusText.Text = "Press Esc to close";
        GenerateButton.IsEnabled = canRetry && _generateAsync is not null;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
        _replaceAsync = null;
        ShowNearCursor();
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
        await GenerateAsync();
    }

    private async Task GenerateAsync()
    {
        if (_generateAsync is null || !GenerateButton.IsEnabled)
        {
            return;
        }

        var instruction = InstructionText.Text.Trim();
        var selectedContext = OriginalText.Text.Trim();
        if (string.IsNullOrWhiteSpace(selectedContext))
        {
            StatusText.Text = "Selected context cannot be empty";
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
            selectedContext,
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

        ReplaceButton.IsEnabled = false;
        var replace = _replaceAsync;
        _replaceAsync = null;
        Hide();

        try
        {
            await replace();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (CopyButton.IsEnabled && !string.IsNullOrWhiteSpace(ResultText.Text))
        {
            try
            {
                await ClipboardRetry.ExecuteAsync(() => Clipboard.SetText(ResultText.Text));
                StatusText.Text = "Copied";
            }
            catch (ClipboardBusyException exception)
            {
                StatusText.Text = exception.Message;
            }
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
        _ => mode.ToString()
    };

    private RewriteMode GetSelectedMode() => ActionComboBox.SelectedIndex switch
    {
        0 => RewriteMode.Grammar,
        2 => RewriteMode.Shorten,
        _ => RewriteMode.Rewrite
    };

    private static string GetDefaultInstruction(RewriteMode mode) => mode switch
    {
        RewriteMode.Grammar => "Correct grammar, spelling, and punctuation. Preserve the meaning and tone.",
        RewriteMode.Shorten => "Make this concise while preserving the important information.",
        _ => "Rewrite this clearly and naturally. Preserve the meaning and important details."
    };
}
