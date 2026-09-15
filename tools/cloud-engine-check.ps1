<#
.SYNOPSIS
Drives Talkty's real OpenRouterEngine against a WAV file, without the UI or a microphone.

.DESCRIPTION
Loads the built Talkty.App.dll, decrypts the saved OpenRouter key with the app's own
ApiKeyProtector, and runs one transcription through the production code path: MP3 encoding,
payload building, HTTP, parsing. Use it to check a cloud model end to end after changing the
engine, or to compare models, languages and vocabulary hints on the same audio.

COSTS REAL MONEY on the saved OpenRouter key (MAI-Transcribe 2 is $0.10 per hour of audio,
billed per request rounded up to the second). Prefer short clips; send a long one once.

.EXAMPLE
pwsh tools/cloud-engine-check.ps1 -Wav C:\clips\probe.wav -Prewarm
pwsh tools/cloud-engine-check.ps1 -Wav C:\clips\probe.wav -Profile CloudGpt4oMiniTranscribe
pwsh tools/cloud-engine-check.ps1 -Wav C:\clips\probe.wav -Terms 'Revori|Athena Agent'
#>
param(
    [Parameter(Mandatory)][string]$Wav,
    [string]$Profile = 'CloudMaiTranscribe2',
    [string]$Language = 'en',
    # Vocabulary hints, pipe-separated. Omit to send none; MAI accepts at most 50.
    [string]$Terms = '',
    # Run the recording-start warm-up (encoder + HTTPS connection) before transcribing.
    [switch]$Prewarm,
    [string]$DllRoot = "$PSScriptRoot\..\Talkty.App\bin\Release"
)
$ErrorActionPreference = 'Stop'

$dll = Get-ChildItem $DllRoot -Recurse -Filter 'Talkty.App.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "Talkty.App.dll not found under $DllRoot — build first (dotnet build -c Release)." }
"assembly: $($dll.FullName) ($($dll.LastWriteTime))"
Add-Type -Path $dll.FullName

# 16-bit PCM WAV -> float samples. Finds the data chunk instead of assuming a 44-byte header.
$bytes = [IO.File]::ReadAllBytes((Resolve-Path $Wav))
$pos = 12
while ([Text.Encoding]::ASCII.GetString($bytes, $pos, 4) -ne 'data') { $pos += 8 + [BitConverter]::ToInt32($bytes, $pos + 4) }
$len = [BitConverter]::ToInt32($bytes, $pos + 4); $start = $pos + 8
$samples = New-Object float[] ($len / 2)
for ($i = 0; $i -lt $samples.Length; $i++) { $samples[$i] = [BitConverter]::ToInt16($bytes, $start + 2 * $i) / 32768.0 }
"audio: $($samples.Length) samples ($([math]::Round($samples.Length / 16000.0, 1))s)"

$settings = Get-Content "$env:APPDATA\Talkty\settings.json" -Raw | ConvertFrom-Json
$key = [Talkty.App.Services.ApiKeyProtector]::Unprotect($settings.openRouterApiKeyEncrypted)
if (-not $key) { throw 'No OpenRouter key saved in settings.json — add one in Settings first.' }

$engine = [Talkty.App.Services.Engines.OpenRouterEngine]::new()
$engine.SetApiKey($key)
$p = [Enum]::Parse([Talkty.App.Models.ModelProfile], $Profile)
$loaded = $engine.LoadModelAsync($p, '', $false, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
"loaded: $loaded / $($engine.BackendInfo)"

$props = @{ Language = $Language }
if ($Terms) {
    $list = [System.Collections.Generic.List[string]]::new()
    foreach ($t in ($Terms -split '\|')) { $list.Add($t) }
    $props.VocabularyTerms = $list
}
$opts = [Talkty.App.Services.TranscriptionOptions]$props
"vocabulary hints: $(if ($opts.VocabularyTerms) { $opts.VocabularyTerms.Count } else { 0 })"

if ($Prewarm) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $engine.PrewarmAsync().GetAwaiter().GetResult() | Out-Null
    "prewarm: $($sw.ElapsedMilliseconds)ms"
}

$result = $engine.TranscribeAsync($samples, $opts, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
"success: $($result.Success) in $([int]$result.Duration.TotalMilliseconds)ms"
"text: $($result.Text)"
if ($result.ErrorMessage) { "error: $($result.ErrorMessage)" }
