using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Talkty.App.Converters;
using Talkty.App.ViewModels;
using Xunit;

namespace Talkty.Tests;

public partial class TranscriptionFlowTests
{
    [Fact]
    public Task HistorySnapshotsStayOrderedWhenDiskIsSlow() => ui.Run(async () =>
    {
        using var context = new Context();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string[]>();
        context.Settings.OnSaveHistory = entries =>
        {
            if (writes.Count == 0)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
            }
            writes.Add(entries.Select(e => e.Text).ToArray());
        };
        var first = new TranscriptionHistoryItem { Text = "First" };
        var second = new TranscriptionHistoryItem { Text = "Second" };
        context.ViewModel.History.Add(first);
        context.ViewModel.History.Add(second);
        context.ViewModel.DeleteHistoryItem(first);
        try
        {
            await entered.Task;
            context.ViewModel.ClearHistory();
        }
        finally { release.Set(); }
        await context.ViewModel.PendingHistorySave;
        Assert.Equal(2, writes.Count);
        Assert.Equal(new[] { "Second" }, writes[0]);
        Assert.Empty(writes[1]);
    });

    [Fact]
    public Task BusyDispatcherOnlyPublishesLatestMeterReadingAndResetsAfterStop() => ui.Run(async () =>
    {
        using var context = new Context();
        await context.ViewModel.StartListeningAsync();
        var updates = 0;
        context.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AudioLevel)) updates++;
        };
        for (var i = 1; i <= 1000; i++) context.Audio.EmitLevel(i / 1000f);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, updates);
        Assert.Equal(1f, context.ViewModel.AudioLevel);
        context.Audio.EmitLevel(0.75f);
        context.ViewModel.CancelRecording();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, context.ViewModel.AudioLevel);
        context.Audio.EmitLevel(0.8f);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, context.ViewModel.AudioLevel);
    });

    [Fact]
    public Task MissingModelOpensSettingsAndActiveRecordingProtectsMicrophone() => ui.Run(async () =>
    {
        using var context = new Context();
        var opened = 0;
        context.ViewModel.RequestShowSettings += (_, _) => opened++;
        context.ViewModel.ToggleListening();
        Assert.Equal(1, opened);
        await context.ViewModel.StartListeningAsync();
        context.ViewModel.OpenSettings();
        Assert.Equal(1, opened);
        Assert.True(context.Audio.IsRecording);
        Assert.Contains(context.Warnings, s => s.Contains("Finish or cancel"));
        context.ViewModel.CancelRecording();
    });

    [Fact]
    public Task MicrophoneTestCannotBeReusedAsDictation() => ui.Run(() =>
    {
        using var context = new Context();
        context.ViewModel.IsModelLoaded = true;
        context.Audio.StartRecording();
        context.ViewModel.ToggleListening();
        Assert.False(context.ViewModel.IsListening);
        Assert.True(context.Audio.IsRecording);
        Assert.Contains(context.Warnings, s => s.Contains("microphone test"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task OverlayClockResetsImmediatelyAndReusesOneTimer() => ui.Run(() =>
    {
        var vm = new OverlayViewModel { ElapsedTime = "03:28", AudioLevel = 0.8f };
        vm.StartTimer();
        try
        {
            var field = typeof(OverlayViewModel).GetField("_timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var timer = Assert.IsType<DispatcherTimer>(field.GetValue(vm));
            vm.StartTimer();
            Assert.Same(timer, field.GetValue(vm));
            Assert.Equal("00:00", vm.ElapsedTime);
            Assert.Equal(0, vm.AudioLevel);
            vm.StopTimer();
            Assert.False(timer.IsEnabled);
            Assert.Equal("1:02:03", OverlayViewModel.FormatElapsed(TimeSpan.FromSeconds(3723)));
        }
        finally { vm.StopTimer(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(420, 520)]
    [InlineData(380, 420)]
    [InlineData(720, 640)]
    public Task HistoryLayoutVirtualizesAndRecordingControlIsAButton(int width, int height) => ui.Run(async () =>
    {
        using var context = new Context();
        context.ViewModel.IsModelLoaded = true;
        context.ViewModel.StatusText = "Ready";
        context.ViewModel.ModelProfileDisplay = "Large Turbo Lite";
        for (var i = 0; i < 100; i++)
            context.ViewModel.History.Add(new TranscriptionHistoryItem
            {
                Text = i == 0 ? "Build a keyboard-accessible settings panel with clear recording feedback." : $"Recording {i + 1}: notes for the next design review and development pass.",
                RawTranscription = i == 0 ? "Make the settings easier to use with a keyboard." : null,
                Timestamp = new DateTime(2026, 9, 5, 10, 30, 0).AddMinutes(-i)
            });
        var surface = LoadPreview("Talkty.App/MainWindow.xaml");
        surface.DataContext = context.ViewModel;
        Layout(surface, width, height);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(surface, width, height);
        var list = Assert.IsType<ListBox>(surface.FindName("HistoryList"));
        var realized = Enumerable.Range(0, 100).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
        Assert.InRange(realized, 1, 25);
        Assert.True(list.ActualHeight > 90);
        var record = Assert.IsType<Button>(surface.FindName("RecordButton"));
        Assert.True(record.Focusable);
        Assert.Same(context.ViewModel.ToggleListeningCommand, record.Command);
        Assert.True(record.ActualWidth > 250);
        SavePreview(surface, width, height, $"main-{width}x{height}.png");
        File.WriteAllText(Path.Combine(RepoPath(), "Talkty.Tests", "bin", "ui-evidence", $"layout-{width}.txt"),
            $"{width}x{height}: {realized} realized cards of 100; history viewport height {list.ActualHeight:F0}px");
        list.ScrollIntoView(context.ViewModel.History[^1]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(surface, width, height);
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(99));
        context.ViewModel.IsTranscribing = true;
        Layout(surface, width, height);
        Assert.False(record.IsEnabled);
        context.ViewModel.IsTranscribing = false;
        context.ViewModel.IsListening = true;
        context.ViewModel.AudioLevel = 1;
        Layout(surface, width, height);
        Assert.True(record.IsEnabled);
        var track = Assert.IsType<Border>(surface.FindName("AudioTrack"));
        Assert.Equal(track.ActualWidth, ((Border)track.Child).ActualWidth, precision: 2);
        context.ViewModel.IsListening = false;
    });

    [Fact]
    public Task OverlayShowsProcessingAndCancellationInsteadOfStaleTimer() => ui.Run(() =>
    {
        var surface = LoadPreview("Talkty.App/Views/OverlayWindow.xaml");
        var vm = new OverlayViewModel { IsListening = false, IsTranscribing = true, StatusText = "Transcribing...", ElapsedTime = "00:24" };
        surface.DataContext = vm;
        Layout(surface, 380, 64);
        Assert.Contains(Descendants<TextBlock>(surface), t => t.Text == "Transcribing...");
        Assert.DoesNotContain(Descendants<TextBlock>(surface), t => t.Text == "...");
        SavePreview(surface, 380, 64, "overlay-transcribing.png");
        vm.StatusText = "Cancelled";
        Layout(surface, 380, 64);
        Assert.Contains(Descendants<TextBlock>(surface), t => t.Text == "Cancelled");
        return Task.CompletedTask;
    });

    [Fact]
    public Task OverlayCarriesTheSpokenCommandAndTheAnswerToIt() => ui.Run(() =>
    {
        var surface = LoadPreview("Talkty.App/Views/OverlayWindow.xaml");
        var vm = new OverlayViewModel
        {
            IsListening = false,
            IsTranscribing = false,
            StatusText = "Sending command...",
            CommandText = "Search this page for Kenshi Yonezu.",
            CommandDetail = "Sending…",
            CommandStage = CommandStage.Sending,
        };
        surface.DataContext = vm;
        Layout(surface, 520, 80);

        // His words are on the pill, and the one-word status has stepped aside.
        Assert.Contains(Descendants<TextBlock>(surface), t => t.Text == "Search this page for Kenshi Yonezu." && t.Visibility == Visibility.Visible);
        Assert.DoesNotContain(Descendants<TextBlock>(surface), t => t.Text == "Sending command..." && t.Visibility == Visibility.Visible);
        SavePreview(surface, 520, 80, "overlay-command-sending.png");

        vm.CommandStage = CommandStage.Working;
        vm.CommandDetail = "Working… 2 steps";
        Layout(surface, 520, 80);
        Assert.Contains(Descendants<TextBlock>(surface), t => t.Text == "Working… 2 steps" && t.Visibility == Visibility.Visible);
        SavePreview(surface, 520, 80, "overlay-command-working.png");

        var green = Application.Current.Resources["StatusGreenBrush"];
        vm.CommandStage = CommandStage.Succeeded;
        vm.CommandDetail = "Searched youtube.com for Kenshi Yonezu in the tab you had open.";
        Layout(surface, 520, 80);
        var done = Descendants<TextBlock>(surface).Single(t => t.Text == vm.CommandDetail);
        Assert.Equal(Visibility.Visible, done.Visibility);
        Assert.Same(green, done.Foreground);
        SavePreview(surface, 520, 80, "overlay-command-done.png");

        var red = Application.Current.Resources["StatusRedBrush"];
        vm.CommandStage = CommandStage.Failed;
        vm.CommandDetail = "The window you were using is not open any more.";
        Layout(surface, 520, 80);
        var failed = Descendants<TextBlock>(surface).Single(t => t.Text == vm.CommandDetail);
        Assert.Same(red, failed.Foreground);
        SavePreview(surface, 520, 80, "overlay-command-failed.png");

        // An ordinary dictation is untouched by any of this.
        vm.ClearCommand();
        vm.StatusText = "Transcribing...";
        vm.IsTranscribing = true;
        Layout(surface, 380, 64);
        Assert.Contains(Descendants<TextBlock>(surface), t => t.Text == "Transcribing..." && t.Visibility == Visibility.Visible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task OverlayShowsTheWordsWhileHeIsStillSpeaking() => ui.Run(() =>
    {
        var surface = LoadPreview("Talkty.App/Views/OverlayWindow.xaml");
        var vm = new OverlayViewModel { IsListening = true, ElapsedTime = "00:03" };
        surface.DataContext = vm;
        Layout(surface, 520, 64);
        Assert.DoesNotContain(Descendants<TextBlock>(surface), t => t.Text == "" && t.Visibility == Visibility.Visible && t.ActualWidth > 0);

        vm.PreviewText = "search this page for kenshi";
        Layout(surface, 520, 64);
        var preview = Descendants<TextBlock>(surface).Single(t => t.Text == "search this page for kenshi");
        Assert.Equal(Visibility.Visible, preview.Visibility);
        // The timer stays: it is how he knows it is still listening.
        Assert.Contains(Descendants<TextBlock>(surface), t => t.Text == "00:03" && t.Visibility == Visibility.Visible);
        SavePreview(surface, 520, 64, "overlay-live-words.png");

        // Nothing on the pill configures anything any more.
        Assert.Empty(Descendants<System.Windows.Controls.Primitives.ToggleButton>(surface));

        // Once a command owns the pill, the live words step aside for it.
        vm.CommandText = "Search this page for Kenshi.";
        vm.CommandStage = CommandStage.Sending;
        Layout(surface, 520, 80);
        Assert.Equal(Visibility.Collapsed, preview.Visibility);
        return Task.CompletedTask;
    });

    // Render the production XAML without opening a window, tray icon, or real services.
    // Event handlers are compiled by the app build; these tests exercise layout/bindings.
    private static FrameworkElement LoadPreview(string relativePath)
    {
        var resources = Application.Current.Resources;
        if (!resources.Contains("Zinc950Brush"))
        {
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Talkty.App;component/Resources/Styles.xaml", UriKind.Relative) });
            resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            resources["CountToVisibilityConverter"] = new CountToVisibilityConverter();
        }
        var source = XDocument.Load(Path.Combine(RepoPath(), relativePath)).Root!;
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var grid = new XElement(ns + "Grid", source.Attributes().Where(a => a.IsNamespaceDeclaration), source.Elements());
        foreach (var item in grid.Descendants().Where(e => e.Name.Namespace != ns).ToList()) item.Remove();
        string[] handlers = ["Click", "MouseLeftButtonUp", "MouseLeftButtonDown", "PreviewKeyDown"];
        foreach (var attribute in grid.Descendants().Attributes().Where(a => handlers.Contains(a.Name.LocalName)).ToList()) attribute.Remove();
        return (FrameworkElement)XamlReader.Parse(grid.ToString());
    }

    private static void Layout(FrameworkElement surface, double width, double height)
    {
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static string RepoPath([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));

    private static void SavePreview(FrameworkElement surface, int width, int height, string name)
    {
        var folder = Path.Combine(RepoPath(), "Talkty.Tests", "bin", "ui-evidence");
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }
}
