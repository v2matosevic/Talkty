<#
Capability probe: does OUR OpenRouter decisions route actually accept the things the current
OpenAPI schema advertises but the 18 September survey concluded it did not?

  1. structured (JSON object) instructions
  2. structured (JSON object) Choice criteria
  3. a Score question, with structured level descriptions
  4. session_id

Costs a few hundredths of a cent. Synthetic text only.
#>
param(
    [string]$Repo = 'B:\Coding\Talkty',
    [string]$Out = "$PSScriptRoot\capability-probe.json"
)
$ErrorActionPreference = 'Stop'

$dll = Get-ChildItem "$Repo\Talkty.App\bin" -Recurse -Filter 'Talkty.App.dll' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Add-Type -Path $dll.FullName
$settings = Get-Content "$env:APPDATA\Talkty\settings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$key = [Talkty.App.Services.ApiKeyProtector]::Unprotect($settings.openRouterApiKeyEncrypted)
if (-not $key) { throw 'No OpenRouter key saved.' }

$dictation = 'In src/lib/users.ts rename getUserById to loadUser. Do not deploy this to production.'
$rewrite   = '**Task**' + "`n" + 'In src/lib/users.ts, rename getUserByID to loadUser.'

$body = [ordered]@{
    model = 'typesafe/jev-1.13'
    session_id = 'talkty-capability-probe-' + (Get-Random)
    state = [ordered]@{ dictation = $dictation; prompt = $rewrite }
    questions = [ordered]@{
        # 1 + 2: structured instructions AND structured Choice option descriptions
        c1 = [ordered]@{
            type = 'choice'
            instructions = [ordered]@{
                question = 'How does `prompt` treat the instruction in `dictation`?'
                inspect  = 'dictation'
                focus    = 'Judge meaning, not wording. A restated instruction is preserved.'
            }
            criteria = [ordered]@{
                preserved = [ordered]@{
                    what = 'The prompt carries the instruction in some wording'
                    examples = @('renamed the same function', 'same value, different phrasing')
                }
                omitted = [ordered]@{
                    what = 'The prompt does not carry the instruction at all'
                    not_for = 'A instruction that was reworded'
                }
                contradicted = [ordered]@{
                    what = 'The prompt states something incompatible'
                    examples = @('a different identifier', 'the opposite instruction')
                }
                unclear = [ordered]@{ what = 'The supplied text is not enough to tell' }
            }
        }
        # 3: a Score question with structured levels
        fidelity = [ordered]@{
            type = 'score'
            instructions = [ordered]@{
                question = 'How much of what the speaker asked for survives in `prompt`?'
                note = 'Judge content carried, not length or formatting.'
            }
            criteria = @(
                [ordered]@{ summary = 'Most of the request is missing'; signals = @('several instructions gone') },
                [ordered]@{ summary = 'A load-bearing detail is missing or changed'; signals = @('one identifier or value wrong') },
                [ordered]@{ summary = 'Everything the speaker asked for is present'; signals = @('only filler removed') }
            )
        }
    }
    provider = [ordered]@{ data_collection = 'deny'; zdr = $true; allow_fallbacks = $false }
}

$json = $body | ConvertTo-Json -Depth 12 -Compress
"request bytes: $([Text.Encoding]::UTF8.GetByteCount($json))"

$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    $response = Invoke-WebRequest -Uri 'https://openrouter.ai/api/alpha/decisions' -Method Post `
        -Headers @{ Authorization = "Bearer $key"; 'X-Title' = 'Talkty capability probe' } `
        -ContentType 'application/json' -Body $json -SkipHttpErrorCheck
    $sw.Stop()
    "status: $($response.StatusCode) in $($sw.ElapsedMilliseconds)ms"
    $text = $response.Content
} catch {
    $sw.Stop()
    "request failed after $($sw.ElapsedMilliseconds)ms: $($_.Exception.Message)"
    throw
}

$text | Set-Content $Out -Encoding UTF8
$parsed = $text | ConvertFrom-Json
"model: $($parsed.model)"
"provider: $($parsed.provider)"
"usage: input=$($parsed.usage.input_tokens) output=$($parsed.usage.output_tokens) cost=$($parsed.usage.cost)"
""
"c1 (structured instructions + structured criteria):"
"  type=$($parsed.answers.c1.type) choice=$($parsed.answers.c1.choice) confidence=$($parsed.answers.c1.confidence)"
"  probabilities: $($parsed.answers.c1.probabilities | ConvertTo-Json -Compress)"
""
"fidelity (Score with structured levels):"
"  type=$($parsed.answers.fidelity.type) score=$($parsed.answers.fidelity.score) confidence=$($parsed.answers.fidelity.confidence)"
"  probabilities: $($parsed.answers.fidelity.probabilities | ConvertTo-Json -Compress)"
"  legend: $($parsed.answers.fidelity.legend | ConvertTo-Json -Compress -Depth 6)"
""
"saved: $Out"
