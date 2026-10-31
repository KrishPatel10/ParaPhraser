using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ParaPhraser.Desktop.Services;
using Point = System.Windows.Point;

namespace ParaPhraser.Desktop;

/// <summary>
/// Small progress and undo card for direct replace. It never takes focus,
/// because the rewrite must paste into the app the user is working in.
/// </summary>
public partial class InstantToast : Window
{
    private static readonly TimeSpan UndoTimeLimit = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _hideTimer = new() { Interval = UndoTimeLimit };
    private Action? _action;

    public InstantToast()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.PreventActivation(new WindowInteropHelper(this).Handle);
    }

    public void ShowWorking(string message, Action stop)
    {
        _hideTimer.Stop();
        ShowCard(message, "Stop", stop);
        ShowNearCursor();
    }

    public void ShowFinished(string message, Action undo)
    {
        ShowCard(message, "Undo", undo);
        ShowNearCursor();
        _hideTimer.Start();
    }

    public new void Hide()
    {
        _hideTimer.Stop();
        _action = null;
        base.Hide();
    }

    private void ShowCard(string message, string actionLabel, Action action)
    {
        MessageText.Text = message;
        ActionButton.Content = actionLabel;
        _action = action;
    }

    private void ShowNearCursor()
    {
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        var cursor = NativeMethods.GetCursorPosition();
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var position = transform.Transform(new Point(cursor.X, cursor.Y));

        Left = Math.Clamp(
            position.X + 12,
            SystemParameters.VirtualScreenLeft + 8,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth - 8);
        Top = Math.Clamp(
            position.Y + 16,
            SystemParameters.VirtualScreenTop + 8,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight - 8);
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        var action = _action;
        Hide();
        action?.Invoke();
    }
}
