# Talkty MSIX Build Script for Microsoft Store
# Requires: Windows SDK with makeappx.exe and signtool.exe

param(
    [switch]$SkipAssets,
    [switch]$SkipPublish,
    [string]$CertificatePath,
    [string]$CertificatePassword
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $ProjectRoot "Talkty.App\Talkty.App.csproj"
$ManifestPath = Join-Path $ProjectRoot "Talkty.App\Package.appxmanifest"
$AssetsDir = Join-Path $ProjectRoot "Talkty.App\Assets"
$DistDir = Join-Path $ProjectRoot "dist"
$MsixOutputDir = Join-Path $DistDir "msix"

Write-Host "================================" -ForegroundColor Cyan
Write-Host "  Talkty MSIX Build Script" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Generate Store assets
if (-not $SkipAssets) {
    Write-Host "[1/4] Generating Store assets..." -ForegroundColor Yellow

    $AssetScript = Join-Path $PSScriptRoot "generate_store_assets.py"
    if (Test-Path $AssetScript) {
        python $AssetScript
        if ($LASTEXITCODE -ne 0) {
            Write-Host "WARNING: Asset generation failed. You may need to create assets manually." -ForegroundColor Yellow
        }
    } else {
        Write-Host "WARNING: Asset generation script not found." -ForegroundColor Yellow
    }
    Write-Host ""
}

# Step 2: Publish the application
if (-not $SkipPublish) {
    Write-Host "[2/4] Publishing Talkty for MSIX..." -ForegroundColor Yellow

    $PublishDir = Join-Path $ProjectRoot "Talkty.App\bin\Release\net8.0-windows\win-x64\publish"
    if (Test-Path $PublishDir) {
        Remove-Item -Path $PublishDir -Recurse -Force
    }

    # Publish as self-contained for MSIX
    dotnet publish $AppProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Publish failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host ""
}

# Step 3: Create MSIX package directory structure
Write-Host "[3/4] Creating MSIX package structure..." -ForegroundColor Yellow

if (Test-Path $MsixOutputDir) {
    Remove-Item -Path $MsixOutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $MsixOutputDir | Out-Null

$PackageDir = Join-Path $MsixOutputDir "package"
New-Item -ItemType Directory -Path $PackageDir | Out-Null

# Copy published files
$PublishDir = Join-Path $ProjectRoot "Talkty.App\bin\Release\net8.0-windows\win-x64\publish"
Copy-Item -Path "$PublishDir\*" -Destination $PackageDir -Recurse

# Copy manifest
Copy-Item -Path $ManifestPath -Destination "$PackageDir\AppxManifest.xml"

# Copy assets
if (Test-Path $AssetsDir) {
    $PackageAssetsDir = Join-Path $PackageDir "Assets"
    if (-not (Test-Path $PackageAssetsDir)) {
        New-Item -ItemType Directory -Path $PackageAssetsDir | Out-Null
    }
    Copy-Item -Path "$AssetsDir\*" -Destination $PackageAssetsDir -Recurse
}

Write-Host "  Package directory: $PackageDir" -ForegroundColor Green
Write-Host ""

# Step 4: Create MSIX package
Write-Host "[4/4] Creating MSIX package..." -ForegroundColor Yellow

# Find makeappx.exe
$WindowsSdkPath = Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\bin" -Directory |
    Sort-Object Name -Descending |
    Select-Object -First 1

if ($WindowsSdkPath) {
    $MakeAppx = Join-Path $WindowsSdkPath.FullName "x64\makeappx.exe"
    $SignTool = Join-Path $WindowsSdkPath.FullName "x64\signtool.exe"
} else {
    $MakeAppx = $null
    $SignTool = $null
}

if ($MakeAppx -and (Test-Path $MakeAppx)) {
    $MsixFile = Join-Path $DistDir "Talkty-1.0.0.msix"

    & $MakeAppx pack /d $PackageDir /p $MsixFile /o

    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: MSIX creation failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "  MSIX created: $MsixFile" -ForegroundColor Green

    # Sign if certificate provided
    if ($CertificatePath -and (Test-Path $CertificatePath)) {
        Write-Host "  Signing MSIX..." -ForegroundColor Yellow

        if ($CertificatePassword) {
            & $SignTool sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $MsixFile
        } else {
            & $SignTool sign /fd SHA256 /f $CertificatePath $MsixFile
        }

        if ($LASTEXITCODE -eq 0) {
            Write-Host "  MSIX signed successfully" -ForegroundColor Green
        } else {
            Write-Host "  WARNING: Signing failed" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  NOTE: MSIX not signed. For Store submission, use Partner Center's signing." -ForegroundColor Yellow
    }
} else {
    Write-Host "WARNING: Windows SDK not found. MSIX package not created." -ForegroundColor Yellow
    Write-Host "Install Windows SDK from: https://developer.microsoft.com/windows/downloads/windows-sdk/" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Manual steps to create MSIX:" -ForegroundColor Cyan
    Write-Host "  1. Install Windows SDK" -ForegroundColor White
    Write-Host "  2. Run: makeappx pack /d `"$PackageDir`" /p `"$DistDir\Talkty-1.0.0.msix`"" -ForegroundColor White
}

Write-Host ""
Write-Host "================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Green
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "For Microsoft Store submission:" -ForegroundColor Yellow
Write-Host "  1. Create a Microsoft Partner Center account" -ForegroundColor White
Write-Host "  2. Reserve the app name 'Talkty'" -ForegroundColor White
Write-Host "  3. Update Package.appxmanifest with assigned Identity" -ForegroundColor White
Write-Host "  4. Upload the MSIX package to Partner Center" -ForegroundColor White
Write-Host "  5. Complete Store listing (screenshots, description, etc.)" -ForegroundColor White
Write-Host ""
