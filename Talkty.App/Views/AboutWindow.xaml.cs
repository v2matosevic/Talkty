using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace Talkty.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Set version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
        {
            VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore if browser can't be opened
        }
        e.Handled = true;
    }
}
