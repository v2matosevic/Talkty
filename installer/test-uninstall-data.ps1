<#
Exercises the production uninstall guard against disposable fixtures under installer/output.
No app registration, shortcuts, microphone, user data or existing installation is touched.
#>
param([string]$InnoSetupPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'output'))
$fixtureRoot = Join-Path $outputRoot ('uninstall-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'TalktySetup.iss') -Raw
$code = ($source -split '(?m)^\[Code\]\s*', 2)[1]
if (-not $code -or -not $code.Contains("'{userappdata}\Talkty'")) { throw 'Production uninstall code was not found.' }
$results = @()
foreach ($choice in @('keep', 'remove')) {
    $caseRoot = Join-Path $fixtureRoot $choice
    $appPath = [IO.Path]::GetFullPath((Join-Path $caseRoot 'app'))
    $dataPath = [IO.Path]::GetFullPath((Join-Path $caseRoot 'data'))
    # The uninstaller can recursively remove only this new fixture data directory.
    foreach ($target in @($appPath, $dataPath)) {
        if (-not $target.StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture target escaped the test root.' }
    }
    New-Item -ItemType Directory -Path $caseRoot,$dataPath -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $caseRoot 'fixture.txt') -Value 'Installer fixture only'
    $sentinel = Join-Path $dataPath 'settings.txt'
    Set-Content -LiteralPath $sentinel -Value 'Keep this unless the simulated choice is Yes'
    $testCode = $code.Replace('{userappdata}\Talkty', $dataPath)
    if ($testCode.Contains('{userappdata}')) { throw 'Test must never target real user data.' }
    if ($choice -eq 'remove') {
        # Simulate the Yes answer without desktop input; only the test fixture's
        # suppressed-dialog return is changed. The production predicate is identical.
        if ([regex]::Matches($code, ', IDNO\);').Count -ne 1) { throw 'Expected one suppressed-message default.' }
        $testCode = $testCode.Replace(', IDNO);', ', IDYES);')
    }
    $fixture = @"
[Setup]
AppId=Talkty-DataGuard-$choice-$([guid]::NewGuid())
AppName=Talkty data guard fixture
AppVersion=1.0
DefaultDirName=$appPath
PrivilegesRequired=lowest
CreateUninstallRegKey=no
Uninstallable=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
OutputDir=$caseRoot
OutputBaseFilename=fixture-setup
Compression=none
[Files]
Source: "$caseRoot\fixture.txt"; DestDir: "{app}"
[Code]
$testCode
"@
    $script = Join-Path $caseRoot 'fixture.iss'
    [IO.File]::WriteAllText($script, $fixture)
    & $InnoSetupPath $script *> (Join-Path $caseRoot 'compile.log')
    if ($LASTEXITCODE -ne 0) { throw "Fixture compile failed: $caseRoot" }
    $setup = Start-Process -FilePath (Join-Path $caseRoot 'fixture-setup.exe') -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Fixture install exit $($setup.ExitCode)" }
    $uninstaller = Join-Path $appPath 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller)) { throw 'Fixture uninstaller missing.' }
    $uninstall = Start-Process -FilePath $uninstaller -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw "Fixture uninstall exit $($uninstall.ExitCode)" }
    $exists = Test-Path -LiteralPath $sentinel
    if ($exists -ne ($choice -eq 'keep')) { throw "Data guard failed for $choice (exists=$exists)." }
    $results += [pscustomobject]@{ Choice = $choice; DataPreserved = $exists; InstallExit = $setup.ExitCode; UninstallExit = $uninstall.ExitCode }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'results.json') -Encoding utf8
$results | Format-Table
Write-Output "Fixture evidence: $fixtureRoot"
