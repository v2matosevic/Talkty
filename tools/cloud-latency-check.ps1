<#
.SYNOPSIS
Bounded, paid MAI transport comparison. Reports timings, sizes, text and actual request cost.
.DESCRIPTION
Uses the saved DPAPI key without printing it. Requires a 16 kHz mono PCM16 WAV.
Compares the former MP3 JSON body, compact MP3 JSON and compact Opus JSON in alternating order.
No parallel requests, automatic retries, or unbounded loops. Default: six requests, max 180 seconds
of billable audio. A multipart probe deliberately sends an invalid provider option: success means
the provider option was silently ignored, so multipart must NOT replace the vocabulary-aware path.
#>
param(
    [Parameter(Mandatory)][string]$Wav,
    [ValidateRange(1,4)][int]$Rounds = 2,
    [ValidateSet('baseline','mp3','opus')][string[]]$Variants = @('baseline','mp3','opus'),
    [switch]$MultipartProbe,
    [string]$DllRoot = "$PSScriptRoot/../Talkty.App/bin/Release/net8.0-windows/win-x64",
    [string]$Output
)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $DllRoot 'Talkty.App.dll')
Add-Type -Path (Join-Path $DllRoot 'NAudio.Core.dll')
$reader = [NAudio.Wave.WaveFileReader]::new((Resolve-Path $Wav))
if ($reader.WaveFormat.SampleRate -ne 16000 -or $reader.WaveFormat.Channels -ne 1 -or $reader.WaveFormat.BitsPerSample -ne 16) {
    $reader.Dispose(); throw 'Requires PCM16 mono 16 kHz WAV'
}
$pcm = [byte[]]::new($reader.Length)
[void]$reader.Read($pcm, 0, $pcm.Length)
$reader.Dispose()
$samples = [float[]]::new($pcm.Length / 2)
for ($i = 0; $i -lt $samples.Length; $i++) { $samples[$i] = [BitConverter]::ToInt16($pcm, $i * 2) / 32768.0 }
$seconds = [Math]::Ceiling($samples.Length / 16000.0)
$calls = if ($MultipartProbe) { 2 } else { $Variants.Count * $Rounds }
if ($seconds * $calls -gt 180) { throw 'This run would exceed 180 seconds of billable audio; shorten the fixture.' }
Write-Host "Plan: $calls requests, at most $($seconds * $calls)s audio; estimated USD $([Math]::Round($seconds * $calls / 36000, 6))"
$settings = Get-Content "$env:APPDATA/Talkty/settings.json" -Raw | ConvertFrom-Json
$key = [Talkty.App.Services.ApiKeyProtector]::Unprotect($settings.openRouterApiKeyEncrypted)
if (!$key) { throw 'No saved OpenRouter key' }
$engineType = [Talkty.App.Services.Engines.OpenRouterEngine]
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$encode = $engineType.GetMethod('EncodeForUpload', $flags)
$build = $engineType.GetMethod('BuildPayload', $flags)
$serialize = $engineType.GetMethod('SerializePayload', $flags)
$profile = [Talkty.App.Models.ModelProfile]::CloudMaiTranscribe2
$terms = [Collections.Generic.List[string]]::new()
foreach ($term in @('Velora','C++','PostgreSQL','Kubernetes','Playwright')) { $terms.Add($term) }
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(35)
$client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$key)
$warm = $client.GetAsync('https://openrouter.ai/api/v1/key').GetAwaiter().GetResult()
$warm.Dispose()
$encodedVariants = @{}
foreach ($format in @('mp3','opus')) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $encoded = $encode.Invoke($null, @($samples,16000,($format -eq 'opus'),[Threading.CancellationToken]::None))
    $encodeMs = $sw.ElapsedMilliseconds
    $audio = [byte[]]$encoded.Item1
    $actualFormat = [string]$encoded.Item2
    $compact = [byte[]]$serialize.Invoke($null,@($profile,$audio,$actualFormat,'en',$terms))
    $encodedVariants[$format] = @{ audio=$audio; format=$actualFormat; body=$compact; encodeMs=$encodeMs }
    if ($format -eq 'mp3') {
        $payload = $build.Invoke($null,@($profile,[Convert]::ToBase64String($audio),$actualFormat,'en',$terms))
        $old = [Text.Json.JsonSerializer]::SerializeToUtf8Bytes($payload, $payload.GetType(), [Text.Json.JsonSerializerOptions]$null)
        $encodedVariants['baseline'] = @{ audio=$audio; format=$actualFormat; body=$old; encodeMs=$encodeMs }
    }
}
$results = [Collections.Generic.List[object]]::new()
$order = if ($MultipartProbe) { @('json-invalid','multipart-invalid') } else {
    for ($round = 0; $round -lt $Rounds; $round++) {
        if ($round % 2 -eq 0) { $Variants } else { $reversed = $Variants.Clone(); [Array]::Reverse($reversed); $reversed }
    }
}
try {
    foreach ($variant in $order) {
        $data = if ($MultipartProbe) { $encodedVariants['mp3'] } else { $encodedVariants[$variant] }
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,'https://openrouter.ai/api/v1/audio/transcriptions')
        $sw = [Diagnostics.Stopwatch]::StartNew()
        if ($variant -eq 'multipart-invalid') {
            $content = [Net.Http.MultipartFormDataContent]::new()
            # Some gateway parsers reject a quoted boundary, although .NET's form is valid MIME.
            foreach ($parameter in $content.Headers.ContentType.Parameters) {
                if ($parameter.Name -eq 'boundary') { $parameter.Value = $parameter.Value.Trim('"') }
            }
            $filePart = [Net.Http.ByteArrayContent]::new($data.audio)
            $filePart.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('audio/mpeg')
            $content.Add($filePart,'file','probe.mp3')
            $content.Add([Net.Http.StringContent]::new('microsoft/mai-transcribe-2'),'model')
            $content.Add([Net.Http.StringContent]::new('{"options":{"azure":{"enhancedMode":{"modelOptions":{"transcribeStyle":"INVALID_PROBE"}}}}}'),'provider')
        } else {
            $body = $data.body
            if ($variant -eq 'json-invalid') {
                $body = [Text.Encoding]::UTF8.GetBytes([Text.Encoding]::UTF8.GetString($body).Replace('"clean"','"INVALID_PROBE"'))
            }
            $contentType = $engineType.Assembly.GetType('Talkty.App.Services.Engines.CloudRequestContent')
            $content = [Activator]::CreateInstance($contentType, [Reflection.BindingFlags]'Instance,NonPublic', $null, @($body,$sw), $null)
        }
        $request.Content = $content
        $response = $client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $headersMs = $sw.ElapsedMilliseconds
        $bodyText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $totalMs = $sw.ElapsedMilliseconds
        $parsed = $bodyText | ConvertFrom-Json
        $bodyWritten = if (!$MultipartProbe) { $contentType.GetProperty('BodyWrittenMs',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($content) } else { $null }
        $row = [pscustomobject]@{ variant=$variant; seconds=$seconds; audioBytes=$data.audio.Length; bodyBytes=$content.Headers.ContentLength; encodeMs=$data.encodeMs; bodyWrittenMs=$bodyWritten; headersMs=$headersMs; totalMs=$totalMs; status=[int]$response.StatusCode; cost=$parsed.usage.cost; text=$parsed.text; error=$parsed.error }
        $results.Add($row)
        $row | ConvertTo-Json -Compress -Depth 8
        $response.Dispose(); $request.Dispose()
    }
} finally { $client.Dispose() }
if ($Output) { $results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Output }
