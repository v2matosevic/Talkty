<#
Builds the Windows installer into installer/output with an explicit payload manifest.
Requires .NET 8 SDK and Inno Setup 6. Existing publish directories are never deleted.
#>
param(
    [switch]$SkipPublish,
    [switch]$SkipInstaller,
    [switch]$SkipChecks,
    [string]$PublishDirectory = '',
    [string]$InnoSetupPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = (Get-Content -LiteralPath (Join-Path $repoRoot 'version.txt') -Raw).Trim()
$outputRoot = Join-Path $PSScriptRoot 'output'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $outputRoot "release-$version" }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$solution = Join-Path $repoRoot 'Talkty.sln'
$project = Join-Path $repoRoot 'Talkty.App/Talkty.App.csproj'
if (-not $SkipInstaller -and -not (Test-Path -LiteralPath $InnoSetupPath)) { throw 'Inno Setup 6 is required to build the installer.' }
if (-not $SkipPublish -and (Test-Path -LiteralPath $PublishDirectory)) { throw 'Publish directory already exists. Choose a new, empty -PublishDirectory; existing artifacts are preserved.' }
if (-not $SkipChecks) {
    dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build $solution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet test $solution -c Release --no-build --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if (-not $SkipPublish) {
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:BundleCuda=false -p:PublishSingleFile=false -o $PublishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
foreach ($required in @('Talkty.App.exe', 'Talkty.App.dll', 'THIRD_PARTY_LICENSES', 'runtimes/win-x64/whisper.dll', 'runtimes/vulkan/win-x64/whisper.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $required))) { throw "Missing publish payload: $required" }
}
if (-not ((Test-Path -LiteralPath (Join-Path $PublishDirectory 'opus.dll')) -or (Test-Path -LiteralPath (Join-Path $PublishDirectory 'runtimes/win-x64/native/opus.dll')))) { throw 'Missing native Opus encoder.' }
$productVersion = (Get-Item -LiteralPath (Join-Path $PublishDirectory 'Talkty.App.dll')).VersionInfo.ProductVersion
if ($productVersion -notmatch ('^' + [regex]::Escape($version) + '(\+|$)')) { throw "Payload version $productVersion does not match $version." }
$payload = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($PublishDirectory, $_.FullName).Replace('\','/')
    # Match TalktySetup.iss exclusions exactly.
    if ($relative -notlike '*.pdb' -and $relative -notlike '*linux*' -and $relative -notlike '*.so') {
        if ($relative -match '(^|/)(cuda|win-arm64|win-x86)(/|$)|(^|/)(cublas|cudart)') { throw "Unsupported/bundled CUDA payload: $relative" }
        [pscustomobject]@{ Path = $relative; Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
} | Sort-Object Path)
$manifest = Join-Path $outputRoot "payload-$version.csv"
$payload | Export-Csv -LiteralPath $manifest -NoTypeInformation -Encoding utf8
if (-not $SkipInstaller) {
    & $InnoSetupPath "/DMyAppSourcePath=$PublishDirectory" (Join-Path $PSScriptRoot 'TalktySetup.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $outputRoot "TalktySetup-$version.exe"
    if (-not (Test-Path -LiteralPath $installer)) { throw 'Expected installer was not produced.' }
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath (Join-Path $outputRoot "TalktySetup-$version.sha256") -Encoding ascii
    Write-Output "Installer: $installer"
    Write-Output "SHA256: $hash"
}
Write-Output "Payload: $PublishDirectory ($($payload.Count) files); manifest: $manifest"
