using System.Windows;
using Version2.Capture;

namespace Talkty.App.Views;

public partial class CaptureHubWindow : Window
{
    private readonly CaptureClient _client;
    private bool _working;
    public CaptureDestination? Chosen { get; private set; }
    public bool ChoiceMade { get; private set; }
    public CaptureHubWindow(CaptureClient client)
    {
        _client = client; InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        if (_working) return; _working = true; UseButton.IsEnabled = RetryButton.IsEnabled = false;
        try
        {
            var pending = await Task.Run(_client.Pending); Saved.ItemsSource = pending;
            var destinations = await _client.DestinationsAsync(); Destinations.ItemsSource = destinations;
            Destinations.SelectedIndex = destinations.Count > 0 ? 0 : -1;
            Status.Text = destinations.Count == 0 ? "Open an agent tile in ADE, then refresh." : "The chosen destination stays fixed for each recording.";
            UseButton.IsEnabled = destinations.Count > 0; RetryButton.IsEnabled = pending.Count > 0;
        }
        catch (Exception ex) { Status.Text = ex.Message; RetryButton.IsEnabled = Saved.Items.Count > 0; }
        finally { _working = false; }
    }
    private void Use_Click(object sender, RoutedEventArgs e)
    { if (Destinations.SelectedItem is not CaptureDestination destination || _working) return; Chosen = destination; ChoiceMade = true; Close(); }
    private void Cursor_Click(object sender, RoutedEventArgs e)
    { if (_working) return; Chosen = null; ChoiceMade = true; Close(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_working || Saved.SelectedItem is not CaptureSubmission submission) return;
        _working = true; RetryButton.IsEnabled = false;
        try { Status.Text = (await _client.RetryAsync(submission)).Message; Saved.ItemsSource = await Task.Run(_client.Pending); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _working = false; RetryButton.IsEnabled = Saved.Items.Count > 0; }
    }
}
