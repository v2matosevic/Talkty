using System.Windows;
using System.Windows.Input;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;

namespace Talkty.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public event EventHandler<AppSettings>? SettingsSaved;

    public SettingsWindow(ISettingsService settingsService, IAudioCaptureService audioCaptureService)
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel(settingsService, audioCaptureService);
        _viewModel.SettingsSaved += OnSettingsSaved;
        _viewModel.CloseRequested += OnCloseRequested;

        DataContext = _viewModel;

        // Handle key events for hotkey recording
        PreviewKeyDown += OnPreviewKeyDown;

        // Dispose ViewModel when window closes
        Closed += (s, e) => _viewModel.Dispose();
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

    // Model profile selection
    private void TinyProfile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.Tiny;
    }

    private void BaseProfile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.Base;
    }

    private void SmallProfile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.Small;
    }

    private void MediumProfile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.Medium;
    }

    private void LargeProfile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.Large;
    }

    private void LargeTurboProfile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.LargeTurbo;
    }

    private void DistilLargeV3Profile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.DistilLargeV3;
    }

    private void SenseVoiceProfile_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.SelectedProfile = ModelProfile.SenseVoice;
    }

    private void HotkeyBox_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.StartRecordingHotkeyCommand.Execute(null);
    }
}
