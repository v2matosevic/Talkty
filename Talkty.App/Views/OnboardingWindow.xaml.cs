using System.Windows;
using Talkty.App.Models;

namespace Talkty.App.Views;

public partial class OnboardingWindow : Window
{
    public bool OpenSettingsRequested { get; private set; }

    public OnboardingWindow()
    {
        InitializeComponent();
    }

    public void SetHotkey(HotkeyModifiers modifier, System.Windows.Input.Key key)
    {
        HotkeyDisplay.Text = $"{modifier} + {key}";
    }

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested = false;
        DialogResult = true;
        Close();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested = true;
        DialogResult = true;
        Close();
    }
}
