# Talkty Build and Installer Script
# Requires: .NET 8 SDK, Inno Setup 6

param(
    [switch]$SkipPublish,
    [switch]$SkipInstaller,
    [string]$InnoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $ProjectRoot "Talkty.App\Talkty.App.csproj"
$InstallerScript = Join-Path $PSScriptRoot "setup.iss"
$DistDir = Join-Path $ProjectRoot "dist"

Write-Host "================================" -ForegroundColor Cyan
Write-Host "  Talkty Build Script" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Publish the application
if (-not $SkipPublish) {
    Write-Host "[1/3] Publishing Talkty..." -ForegroundColor Yellow

    # Clean previous publish
    $PublishDir = Join-Path $ProjectRoot "Talkty.App\bin\Release\net8.0-windows\win-x64\publish"
    if (Test-Path $PublishDir) {
        Remove-Item -Path $PublishDir -Recurse -Force
    }

    # Publish as self-contained single file
    dotnet publish $AppProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Publish failed!" -ForegroundColor Red
        exit 1
    }

    $ExePath = Join-Path $PublishDir "Talkty.App.exe"
    if (Test-Path $ExePath) {
        $FileInfo = Get-Item $ExePath
        Write-Host "  Published: $ExePath" -ForegroundColor Green
        Write-Host "  Size: $([math]::Round($FileInfo.Length / 1MB, 2)) MB" -ForegroundColor Green
    } else {
        Write-Host "ERROR: Published executable not found!" -ForegroundColor Red
        exit 1
    }

    Write-Host ""
}

# Step 2: Create dist directory
Write-Host "[2/3] Preparing distribution..." -ForegroundColor Yellow

if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

Write-Host "  Output directory: $DistDir" -ForegroundColor Green
Write-Host ""

# Step 3: Build installer
if (-not $SkipInstaller) {
    Write-Host "[3/3] Building installer..." -ForegroundColor Yellow

    if (-not (Test-Path $InnoSetupPath)) {
        Write-Host "WARNING: Inno Setup not found at $InnoSetupPath" -ForegroundColor Yellow
        Write-Host "Download from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "To build installer manually after installing Inno Setup:" -ForegroundColor Cyan
        Write-Host "  1. Open Inno Setup Compiler"
        Write-Host "  2. Load: $InstallerScript"
        Write-Host "  3. Press Ctrl+F9 to compile"
        Write-Host ""
    } else {
        & $InnoSetupPath $InstallerScript

        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Installer build failed!" -ForegroundColor Red
            exit 1
        }

        $InstallerPath = Join-Path $DistDir "TalktySetup-1.0.0.exe"
        if (Test-Path $InstallerPath) {
            $FileInfo = Get-Item $InstallerPath
            Write-Host "  Installer: $InstallerPath" -ForegroundColor Green
            Write-Host "  Size: $([math]::Round($FileInfo.Length / 1MB, 2)) MB" -ForegroundColor Green
        }
    }
    Write-Host ""
}

Write-Host "================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Green
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Test the installer on a clean machine" -ForegroundColor White
Write-Host "  2. Download Whisper models from:" -ForegroundColor White
Write-Host "     https://huggingface.co/ggerganov/whisper.cpp/tree/main" -ForegroundColor Cyan
Write-Host "  3. Place models in: %APPDATA%\Talkty\models\" -ForegroundColor White
Write-Host ""
