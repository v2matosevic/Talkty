# Builds the optional CUDA pack zip that Talkty offers as an in-app download
# (Settings > Behavior) now that CUDA is no longer bundled in the installer.
#
# Upload the resulting zip as the asset of the dedicated GitHub release tag
#   cuda-pack-cu13
# named exactly:
#   TalktyCudaPack-cu13-win-x64.zip
# (CudaPackService.PackUrl points at that tag/asset; the tag is version-independent
# so app releases never need to re-upload ~250MB.)
#
# Zip layout mirrors the app install dir:
#   cublas64_13.dll / cublasLt64_13.dll / cudart64_13.dll   (app root)
#   runtimes/cuda/win-x64/*.dll                             (Whisper.net CUDA natives)
#
# Sources, in order of preference:
#   1) -SourceDir <fat publish or install dir> (e.g. a -p:BundleCuda=true publish, or B:\Talkty)
#   2) NuGet cache (whisper.net.runtime.cuda) + CUDA Toolkit v13.1 bin

param(
    [string]$SourceDir,
    [string]$OutDir = (Join-Path $PSScriptRoot 'output')
)

$ErrorActionPreference = 'Stop'

$cudaRootDlls = 'cublas64_13.dll', 'cublasLt64_13.dll', 'cudart64_13.dll'
$stage = Join-Path ([IO.Path]::GetTempPath()) "TalktyCudaPackStage_$([guid]::NewGuid().ToString('N'))"
$stageRuntimes = Join-Path $stage 'runtimes\cuda\win-x64'
New-Item -ItemType Directory -Force $stageRuntimes | Out-Null

try {
    if ($SourceDir) {
        Write-Host "Sourcing from: $SourceDir"
        foreach ($dll in $cudaRootDlls) {
            Copy-Item (Join-Path $SourceDir $dll) $stage
        }
        Copy-Item (Join-Path $SourceDir 'runtimes\cuda\win-x64\*.dll') $stageRuntimes
    }
    else {
        # NuGet cache: Whisper.net CUDA natives
        $pkgRoot = Join-Path $env:USERPROFILE '.nuget\packages\whisper.net.runtime.cuda'
        $ggmlCuda = Get-ChildItem $pkgRoot -Recurse -Filter 'ggml-cuda-whisper.dll' |
            Where-Object FullName -match 'win-x64' | Select-Object -First 1
        if (-not $ggmlCuda) { throw "ggml-cuda-whisper.dll (win-x64) not found under $pkgRoot — restore with -p:BundleCuda=true once, or pass -SourceDir." }
        Write-Host "Whisper CUDA natives: $($ggmlCuda.DirectoryName)"
        Copy-Item (Join-Path $ggmlCuda.DirectoryName '*.dll') $stageRuntimes

        # CUDA Toolkit: cublas/cudart
        $cudaBin = "$env:ProgramFiles\NVIDIA GPU Computing Toolkit\CUDA\v13.1\bin\x64"
        if (-not (Test-Path (Join-Path $cudaBin 'cublas64_13.dll'))) { throw "CUDA Toolkit v13.1 not found at $cudaBin — pass -SourceDir instead." }
        Write-Host "CUDA Toolkit DLLs: $cudaBin"
        foreach ($dll in $cudaRootDlls) {
            Copy-Item (Join-Path $cudaBin $dll) $stage
        }
    }

    # Sanity: every file CudaPackService requires must be present
    $required = $cudaRootDlls + 'runtimes\cuda\win-x64\ggml-cuda-whisper.dll'
    foreach ($rel in $required) {
        if (-not (Test-Path (Join-Path $stage $rel))) { throw "Pack is missing $rel" }
    }

    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $zipPath = Join-Path $OutDir 'TalktyCudaPack-cu13-win-x64.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath }

    Write-Host 'Compressing (this takes a minute — cublasLt is 458MB)...'
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $mb = [math]::Round((Get-Item $zipPath).Length / 1MB)
    Write-Host "Done: $zipPath ($mb MB)"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
