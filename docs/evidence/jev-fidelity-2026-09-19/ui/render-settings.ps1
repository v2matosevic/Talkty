<#
Off-screen render of the Settings window's Cloud & Prompting page, to inspect the new PROMPT CHECK
panel without touching the user's screen. Same pattern as the v1.2.1 verification harness:
load Talkty.App.dll, parse Styles.xaml from SOURCE (pack URIs do not resolve for a LoadFrom
assembly), show the window at -32000 with ShowActivated=false, capture with RenderTargetBitmap.
#>
param(
    [string]$Repo = 'B:\Coding\Talkty',
    [string]$Out = "$PSScriptRoot\ui"
)
$ErrorActionPreference = 'Stop'

$dll = Get-ChildItem "$Repo\Talkty.App\bin" -Recurse -Filter 'Talkty.App.dll' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
"assembly: $($dll.FullName) ($($dll.LastWriteTime))"

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$asm = [Reflection.Assembly]::LoadFrom($dll.FullName)

New-Item -ItemType Directory -Force $Out | Out-Null

$app = New-Object System.Windows.Application
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown

# Styles.xaml from source; the pack URI inside App.xaml cannot resolve for a LoadFrom assembly.
$styleXaml = Get-Content "$Repo\Talkty.App\Resources\Styles.xaml" -Raw -Encoding UTF8
$reader = New-Object System.IO.StringReader $styleXaml
$xmlReader = [System.Xml.XmlReader]::Create($reader)
$styles = [System.Windows.Markup.XamlReader]::Load($xmlReader)
$app.Resources.MergedDictionaries.Add($styles)

# App.xaml also declares the converters the windows bind through. Rebuild that dictionary from the
# same source rather than hand-listing keys, so a new converter cannot silently go missing here.
$appXaml = Get-Content "$Repo\Talkty.App\App.xaml" -Raw -Encoding UTF8
$converterXaml = @'
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:converters="clr-namespace:Talkty.App.Converters;assembly=Talkty.App">
CONVERTERS
</ResourceDictionary>
'@
$lines = @()
foreach ($line in ($appXaml -split "
?
")) {
    if ($line -match '<(converters:|BooleanToVisibilityConverter)') { $lines += $line }
}
if (-not $lines) { throw 'No converter declarations found in App.xaml.' }
$converterXaml = $converterXaml.Replace('CONVERTERS', ($lines -join "`n"))
$cReader = [System.Xml.XmlReader]::Create((New-Object System.IO.StringReader $converterXaml))
$app.Resources.MergedDictionaries.Add([System.Windows.Markup.XamlReader]::Load($cReader))
"converters merged: $($lines.Count)"

# Real services: SettingsService only READS settings.json here (Save is never called) and
# AudioCaptureService just enumerates devices. This avoids compiling C# doubles against a net8
# assembly from a .NET 10 PowerShell host.
$settingsService = New-Object Talkty.App.Services.SettingsService
$settingsService.Load()
$audioService = New-Object Talkty.App.Services.AudioCaptureService

$window = $asm.GetType('Talkty.App.Views.SettingsWindow')::new($settingsService, $audioService)

# Never let the real key reach a PNG, even though the PasswordBox masks it.
$window.DataContext.OpenRouterApiKey = 'sk-or-REDACTED-FOR-RENDER'
$window.FindName('ApiKeyMasked').Password = 'sk-or-REDACTED-FOR-RENDER'

$window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
$window.Left = -32000
$window.Top = -32000
$window.ShowActivated = $false
$window.ShowInTaskbar = $false
$window.Show()

function Capture([string]$name) {
    $window.UpdateLayout()
    for ($i = 0; $i -lt 8; $i++) {
        $frame = New-Object System.Windows.Threading.DispatcherFrame
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke(
            [System.Windows.Threading.DispatcherPriority]::Background,
            [action] { $frame.Continue = $false }) | Out-Null
        [System.Windows.Threading.Dispatcher]::PushFrame($frame)
    }
    $w = [int]$window.ActualWidth; $h = [int]$window.ActualHeight
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($window)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $path = Join-Path $Out "$name.png"
    $fs = [System.IO.File]::Create($path)
    $enc.Save($fs); $fs.Close()
    "captured $name ($w x $h) -> $path"
}

# Click the Cloud & Prompting nav item, which is where the new panel lives.
$nav = $window.FindName('NavCloud')
$nav.IsChecked = $true
Capture 'settings-cloud-prompting'

# The new panel sits below the fold; scroll the visible page to the bottom.
function VisibleScroller($root) {
    $found = $null
    $queue = New-Object System.Collections.Queue
    $queue.Enqueue($root)
    while ($queue.Count) {
        $node = $queue.Dequeue()
        $count = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($node)
        for ($i = 0; $i -lt $count; $i++) {
            $child = [System.Windows.Media.VisualTreeHelper]::GetChild($node, $i)
            if ($child -is [System.Windows.Controls.ScrollViewer] -and
                $child.Visibility -eq [System.Windows.Visibility]::Visible -and
                $child.ScrollableHeight -gt 0) { $found = $child }
            $queue.Enqueue($child)
        }
    }
    return $found
}
$scroller = VisibleScroller $window
if ($scroller) { $scroller.ScrollToEnd() } else { 'WARNING: no scrollable page found' }
Capture 'settings-prompt-check'

# Show concerns selected, to confirm the note text swaps with the picker.
$vm = $window.DataContext
$vm.SelectedFidelityMode = ($vm.AvailableFidelityModes | Where-Object { "$($_.Mode)" -eq 'Review' })
Capture 'settings-cloud-prompting-review'
"selected mode note: $($vm.SelectedFidelityMode.Note)"
"available modes: $(($vm.AvailableFidelityModes | ForEach-Object { $_.Name }) -join ' | ')"
"default from AppSettings: $((New-Object Talkty.App.Models.AppSettings).PromptFidelity)"

$window.Close()
$app.Shutdown()
