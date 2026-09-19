<#
Renders the in-app concern toast off-screen so the thing the user actually sees gets looked at,
not just asserted for content. Same harness pattern as render-settings.ps1.
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

$styleXaml = Get-Content "$Repo\Talkty.App\Resources\Styles.xaml" -Raw -Encoding UTF8
$xmlReader = [System.Xml.XmlReader]::Create((New-Object System.IO.StringReader $styleXaml))
$app.Resources.MergedDictionaries.Add([System.Windows.Markup.XamlReader]::Load($xmlReader))

$appXaml = Get-Content "$Repo\Talkty.App\App.xaml" -Raw -Encoding UTF8
$converterXaml = @'
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:converters="clr-namespace:Talkty.App.Converters;assembly=Talkty.App">
CONVERTERS
</ResourceDictionary>
'@
$lines = @()
foreach ($line in ($appXaml -split "\r?\n")) {
    if ($line -match '<(converters:|BooleanToVisibilityConverter)') { $lines += $line }
}
$converterXaml = $converterXaml.Replace('CONVERTERS', ($lines -join "`n"))
$cReader = [System.Xml.XmlReader]::Create((New-Object System.IO.StringReader $converterXaml))
$app.Resources.MergedDictionaries.Add([System.Windows.Markup.XamlReader]::Load($cReader))

# Build the real message through the real code path, for the realistic worst case: two concerns.
$build = $asm.GetType('Talkty.App.ViewModels.MainViewModel').GetMethod(
    'BuildConcernMessage', [Reflection.BindingFlags]'NonPublic,Static')

$concernType = $asm.GetType('Talkty.App.Services.FidelityConcern')
$kind = $asm.GetType('Talkty.App.Services.FidelityConcernKind')
$analyzer = $asm.GetType('Talkty.App.Services.PromptFidelityAnalyzer')

function Concern($kindName, $labelField, $text, $clause) {
    return $concernType::new(
        [Enum]::Parse($kind, $kindName),
        $analyzer::"$labelField",
        $text, $clause, [System.Nullable[double]]0.97, [System.Nullable[double]]0.93)
}

$listType = [System.Collections.Generic.List[object]]
$cases = @{
    'toast-one-concern' = @(
        (Concern 'OmittedClause' 'LabelOmittedClause' 'Do not deploy this to production.' 'c4'))
    'toast-two-concerns' = @(
        (Concern 'MissingIdentifier' 'LabelMissingIdentifier' 'getUserById' 'c1'),
        (Concern 'DroppedProhibition' 'LabelDroppedProhibition' 'And whatever you do, do not touch the invoice numbering logic, that is audited and I do not want it moving.' 'c4'))
    'toast-long-clause' = @(
        (Concern 'OmittedClause' 'LabelOmittedClause' 'Oh and make sure the whole page still works on mobile, it is quite cramped at the moment and I keep forgetting to check it.' 'c5'))
}

foreach ($name in $cases.Keys | Sort-Object) {
    $listOfConcern = [System.Collections.Generic.List`1].MakeGenericType($concernType)
    $typed = [Activator]::CreateInstance($listOfConcern)
    foreach ($c in $cases[$name]) { $typed.Add($c) }

    # PowerShell unrolls a single-element array, so build the argument array explicitly.
    $callArgs = [object[]]::new(1)
    $callArgs[0] = $typed
    $message = $build.Invoke($null, $callArgs)
    "$name : $($message.Length) chars"
    "  $message"

    $toast = $asm.GetType('Talkty.App.Controls.ToastNotification')::new()
    $window = New-Object System.Windows.Window
    $window.Width = 420
    $window.SizeToContent = [System.Windows.SizeToContent]::Height
    $window.WindowStyle = [System.Windows.WindowStyle]::None
    $window.Background = (New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(9, 9, 11)))
    $window.Content = $toast
    $window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
    $window.Left = -32000; $window.Top = -32000
    $window.ShowActivated = $false; $window.ShowInTaskbar = $false
    $window.Show()

    $toastType = $asm.GetType('Talkty.App.Models.ToastType')
    $toast.Show($message, [Enum]::Parse($toastType, 'Warning'), 8000)

    for ($i = 0; $i -lt 12; $i++) {
        $frame = New-Object System.Windows.Threading.DispatcherFrame
        [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke(
            [System.Windows.Threading.DispatcherPriority]::Background, [action] { $frame.Continue = $false }) | Out-Null
        [System.Windows.Threading.Dispatcher]::PushFrame($frame)
    }
    $window.UpdateLayout()

    $w = [int]$window.ActualWidth; $h = [int]$window.ActualHeight
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($window)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [System.IO.File]::Create((Join-Path $Out "$name.png"))
    $enc.Save($fs); $fs.Close()
    "  captured ($w x $h)"
    $window.Close()
}

$app.Shutdown()
