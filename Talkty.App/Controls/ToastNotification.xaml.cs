using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Talkty.App.Controls;

public partial class ToastNotification : UserControl
{
    private readonly DispatcherTimer _autoHideTimer;
    private Action? _actionCallback;

    public enum ToastType
    {
        Info,
        Success,
        Warning,
        Tip
    }

    public ToastNotification()
    {
        InitializeComponent();
        _autoHideTimer = new DispatcherTimer();
        _autoHideTimer.Tick += (s, e) =>
        {
            _autoHideTimer.Stop();
            Hide();
        };
    }

    public void Show(string message, ToastType type = ToastType.Info, int durationMs = 3000, string? actionText = null, Action? actionCallback = null)
    {
        MessageText.Text = message;
        _actionCallback = actionCallback;

        // Configure icon based on type
        ConfigureIcon(type);

        // Configure action button
        if (!string.IsNullOrEmpty(actionText) && actionCallback != null)
        {
            ActionButton.Content = actionText;
            ActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            ActionButton.Visibility = Visibility.Collapsed;
        }

        // Show with animation
        IsHitTestVisible = true;
        var showStoryboard = (Storyboard)FindResource("ShowAnimation");
        showStoryboard.Begin(this);

        // Auto-hide timer
        if (durationMs > 0)
        {
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _autoHideTimer.Start();
        }
    }

    public void Hide()
    {
        _autoHideTimer.Stop();
        var hideStoryboard = (Storyboard)FindResource("HideAnimation");
        hideStoryboard.Completed += (s, e) => IsHitTestVisible = false;
        hideStoryboard.Begin(this);
    }

    private void ConfigureIcon(ToastType type)
    {
        IconBorder.Visibility = Visibility.Visible;

        switch (type)
        {
            case ToastType.Success:
                IconBorder.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                IconPath.Data = Geometry.Parse("M9 12l2 2 4-4"); // Checkmark
                IconPath.Stroke = Brushes.White;
                IconPath.StrokeThickness = 2;
                IconPath.Fill = null;
                break;

            case ToastType.Warning:
                IconBorder.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Yellow
                IconPath.Data = Geometry.Parse("M12 9v2m0 4h.01"); // Exclamation
                IconPath.Stroke = Brushes.White;
                IconPath.StrokeThickness = 2;
                IconPath.Fill = null;
                break;

            case ToastType.Tip:
                IconBorder.Background = new SolidColorBrush(Color.FromRgb(168, 85, 247)); // Purple
                IconPath.Data = Geometry.Parse("M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z"); // Lightbulb
                IconPath.Stroke = Brushes.White;
                IconPath.StrokeThickness = 1.5;
                IconPath.Fill = null;
                break;

            default: // Info
                IconBorder.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
                IconPath.Data = Geometry.Parse("M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z");
                IconPath.Stroke = Brushes.White;
                IconPath.StrokeThickness = 1.5;
                IconPath.Fill = null;
                break;
        }
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        _actionCallback?.Invoke();
        Hide();
    }
}
