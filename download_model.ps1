$modelsPath = Join-Path $env:APPDATA 'Talkty\Models'
New-Item -ItemType Directory -Force -Path $modelsPath | Out-Null

$modelFile = Join-Path $modelsPath 'ggml-tiny.en.bin'
Write-Host "Downloading Whisper tiny.en model to: $modelFile"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$webClient = New-Object Net.WebClient
$webClient.DownloadFile('https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin', $modelFile)

Write-Host "Download complete!"
Get-Item $modelFile | Select-Object Name, @{N='Size (MB)';E={[math]::Round($_.Length/1MB, 2)}}
