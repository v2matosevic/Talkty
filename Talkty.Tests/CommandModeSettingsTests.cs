using Talkty.App;
using Talkty.App.Models;
using Talkty.App.Services;
using Talkty.App.ViewModels;
using Xunit;

namespace Talkty.Tests;

/// <summary>
/// The settings dialog rebuilds AppSettings from scratch when it saves, so a field
/// it does not carry silently reverts to its default on the next restart. These are
/// the guards for that: command mode must survive a round trip through the dialog
/// whether or not the user touched it.
/// </summary>
public class CommandModeSettingsTests
{
    [Fact]
    public void ADialogThatNeverTouchesCommandModeStillPreservesIt()
    {
        var stored = new TranscriptionFlowTests.FakeSettings();
        stored.Settings.CommandMode = true;
        stored.Settings.CommandEndpoint = "http://127.0.0.1:9100/command";
        stored.Settings.CommandToken = "stored-token";

        var saved = SaveThrough(stored);

        Assert.True(saved.CommandMode);
        Assert.Equal("http://127.0.0.1:9100/command", saved.CommandEndpoint);
        Assert.Equal("stored-token", saved.CommandToken);
    }

    [Fact]
    public void TurningCommandModeOnInTheDialogReachesTheSavedSettings()
    {
        var stored = new TranscriptionFlowTests.FakeSettings();
        var saved = SaveThrough(stored, vm =>
        {
            vm.CommandMode = true;
            vm.CommandToken = "  pasted-token  ";
            vm.CommandEndpoint = " http://127.0.0.1:8765/command ";
        });

        Assert.True(saved.CommandMode);
        // Pasting a token usually drags whitespace with it.
        Assert.Equal("pasted-token", saved.CommandToken);
        Assert.Equal("http://127.0.0.1:8765/command", saved.CommandEndpoint);
    }

    [Fact]
    public void TheCommandHotkeySurvivesEvenThoughTheDialogHasNoPickerForIt()
    {
        var stored = new TranscriptionFlowTests.FakeSettings();
        stored.Settings.CommandHotkeyKey = System.Windows.Input.Key.J;

        var saved = SaveThrough(stored);

        Assert.Equal(System.Windows.Input.Key.J, saved.CommandHotkeyKey);
        Assert.Equal(HotkeyModifiers.Alt, saved.CommandHotkeyModifier);
    }

    [Fact]
    public void TheDefaultEndpointIsTheLocalDaemon()
        => Assert.Equal(Constants.VoiceCommandDefaultEndpoint, new AppSettings().CommandEndpoint);

    [Fact]
    public void CommandModeIsOffUntilItIsTurnedOn()
    {
        var settings = new AppSettings();
        Assert.False(settings.CommandMode);
        Assert.Equal("", settings.CommandToken);
    }

    /// <summary>Open the dialog over these settings, optionally change something, save.</summary>
    private static AppSettings SaveThrough(ISettingsService stored, Action<SettingsViewModel>? edit = null)
    {
        var viewModel = new SettingsViewModel(stored, new TranscriptionFlowTests.FakeAudio());
        edit?.Invoke(viewModel);

        AppSettings? captured = null;
        viewModel.SettingsSaved += (_, s) => captured = s;
        viewModel.Save();

        Assert.NotNull(captured);
        return captured!;
    }
}
