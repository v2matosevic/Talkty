using System.Windows;
using System.Windows.Input;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;

namespace Talkty.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _syncingApiKey;

    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsWindow(ISettingsService settingsService, IAudioCaptureService audioCaptureService)
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel(settingsService, audioCaptureService);
        _viewModel.SettingsSaved += OnSettingsSaved;
        _viewModel.CloseRequested += OnCloseRequested;

        DataContext = _viewModel;

        // Seed the masked API key box (PasswordBox.Password is not bindable by design).
        _syncingApiKey = true;
        ApiKeyMasked.Password = _viewModel.OpenRouterApiKey ?? "";
        _syncingApiKey = false;

        // Handle key events for hotkey recording
        PreviewKeyDown += OnPreviewKeyDown;

        // Dispose ViewModel when window closes
        Closed += (s, e) => _viewModel.Dispose();
    }

    /// <summary>Masked key box → ViewModel (PasswordBox has no two-way binding).</summary>
    private void ApiKeyMasked_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingApiKey) return;
        _viewModel.OpenRouterApiKey = ApiKeyMasked.Password;
    }

    /// <summary>
    /// Eye toggle: swap between the masked PasswordBox and the plain TextBox.
    /// Whichever becomes visible is synced from the ViewModel first, so edits made
    /// in one always carry into the other.
    /// </summary>
    private void ApiKeyReveal_Toggled(object sender, RoutedEventArgs e)
    {
        var reveal = ApiKeyReveal.IsChecked == true;
        if (!reveal)
        {
            _syncingApiKey = true;
            ApiKeyMasked.Password = _viewModel.OpenRouterApiKey ?? "";
            _syncingApiKey = false;
        }
        ApiKeyPlain.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyMasked.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingHotkey)
            return;

        e.Handled = true;

        // Get the actual key (handle system key for Alt)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore standalone modifier keys
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        // Escape cancels recording
        if (key == Key.Escape)
        {
            _viewModel.CancelRecordingHotkeyCommand.Execute(null);
            return;
        }

        // Set the hotkey
        _viewModel.SetHotkey(key, Keyboard.Modifiers);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;
        DragMove();
    }

    private void OnSettingsSaved(object? sender, AppSettings settings)
    {
        SettingsSaved?.Invoke(this, settings);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelCommand.Execute(null);
    }

    // Single handler for all model profile selections via data-driven ItemsControl
    private void ModelItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ModelProfileViewModel model)
        {
            _viewModel.SelectedProfile = model.Profile;
        }
    }

    private void HotkeyBox_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.StartRecordingHotkeyCommand.Execute(null);
    }

    // Collapsible model section toggles
    private void ToggleLocal_Click(object sender, MouseButtonEventArgs e) =>
        ToggleSection(LocalList, LocalArrow);

    private void ToggleCloud_Click(object sender, MouseButtonEventArgs e) =>
        ToggleSection(CloudList, CloudArrow);

    private static void ToggleSection(UIElement list, System.Windows.Controls.TextBlock arrow)
    {
        if (list.Visibility == Visibility.Visible)
        {
            list.Visibility = Visibility.Collapsed;
            arrow.Text = "▸";
        }
        else
        {
            list.Visibility = Visibility.Visible;
            arrow.Text = "▾";
        }
    }
}
