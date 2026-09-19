<#
.SYNOPSIS
Live qualification for the pre-refinement prompt classifier.

.DESCRIPTION
Runs the labeled planning corpus through the real PromptClassifier: the real question profile with
structured criteria, the real transport, the real thresholds. Reports the decision that actually
matters, which is not overall accuracy but FALSE SKIPS: a dictation that needed structuring and
would instead have been handed back as raw text. That is the error the speaker would feel.

SPENDS REAL MONEY on the saved OpenRouter key, about three hundredths of a cent for the whole
corpus. Synthetic text only. A bare invocation is a DRY RUN; add -Run to spend.

.EXAMPLE
pwsh tools/jev-planning-check.ps1
pwsh tools/jev-planning-check.ps1 -Run -Split development
#>
param(
    [switch]$Run,
    [ValidateSet('all', 'development', 'holdout')][string]$Split = 'all',
    [double]$Ceiling = 0.05,
    [string]$Corpus = "$PSScriptRoot\jev-planning-corpus.json",
    [string]$Out = "$PSScriptRoot\..\docs\evidence\jev-planning-$(Get-Date -Format 'yyyy-MM-dd')",
    [string]$DllRoot = "$PSScriptRoot\..\Talkty.App\bin"
)
$ErrorActionPreference = 'Stop'
$reservationUsd = 0.002

$dll = Get-ChildItem $DllRoot -Recurse -Filter 'Talkty.App.dll' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "Talkty.App.dll not found under $DllRoot - build first." }
"assembly:  $($dll.FullName) ($($dll.LastWriteTime))"
Add-Type -Path $dll.FullName

$doc = Get-Content $Corpus -Raw -Encoding UTF8 | ConvertFrom-Json
$cases = $doc.cases | Where-Object { $Split -eq 'all' -or $_.split -eq $Split }
if (-not $cases) { throw "No cases matched split=$Split." }

"corpus:    $Corpus (version $($doc.version)), $($cases.Count) case(s), split=$Split"
"ceiling:   `$$Ceiling  |  announced up to `$$([math]::Round([math]::Min($Ceiling, $cases.Count * $reservationUsd), 4))"
"thresholds: needsStructure<=$([Talkty.App.Constants]::PromptSkipMaxNeedsStructure) complexity<=$([Talkty.App.Constants]::PromptSkipMaxComplexity)"

if (-not $Run) { ""; "DRY RUN - nothing sent. Re-run with -Run."; return }

if ((Test-Path $Out) -and (Get-ChildItem $Out -File).Count) {
    throw "Evidence directory $Out already holds files. Point -Out at a fresh directory."
}
New-Item -ItemType Directory -Force $Out | Out-Null

$settings = Get-Content "$env:APPDATA\Talkty\settings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$key = [Talkty.App.Services.ApiKeyProtector]::Unprotect($settings.openRouterApiKeyEncrypted)
if (-not $key) { throw 'No OpenRouter key saved in settings.json.' }
"key:       present and decrypted (not shown)"
""

$classifier = [Talkty.App.Services.PromptClassifier]::new($null)
$classifier.SetApiKey($key)
$none = [Threading.CancellationToken]::None

$results = @()
$allocatedUsd = 0.0

foreach ($c in $cases) {
    if (($allocatedUsd + $reservationUsd) -gt $Ceiling) { "STOPPED before $($c.id): ceiling."; break }
    $allocatedUsd += $reservationUsd

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $plan = $classifier.ClassifyAsync($c.transcript, $none).GetAwaiter().GetResult()
    $sw.Stop()

    $evaluation = $plan.Evaluation
    if ($evaluation -and $null -ne $evaluation.CostUsd) {
        $allocatedUsd = $allocatedUsd - $reservationUsd + ([math]::Ceiling($evaluation.CostUsd * 1e6) / 1e6)
    }

    $skipped = "$($plan.Decision)" -eq 'SkipRefinement'
    $kindOk = if ($plan.Kind -eq 'Unknown') { $null } else { ("$($plan.Kind)".ToLower() -eq $c.expectedKind) }

    # The asymmetric errors. A false skip is felt by the speaker; a missed skip only costs a call.
    $falseSkip = ($skipped -and -not $c.expectSkip)
    $missedSkip = ((-not $skipped) -and $c.expectSkip)

    $results += [ordered]@{
        id = $c.id; split = $c.split; language = $c.language; label = $c.label
        transcriptChars = $c.transcript.Length
        expectSkip = [bool]$c.expectSkip; expectedKind = $c.expectedKind
        decision = "$($plan.Decision)"; reason = $plan.Reason
        kind = "$($plan.Kind)"; kindCorrect = $kindOk
        needsStructure = $plan.NeedsStructure; complexity = $plan.Complexity
        complexityConfidence = $plan.ComplexityConfidence
        wantsQualityModel = $plan.WantsQualityModel
        falseSkip = $falseSkip; missedSkip = $missedSkip
        correct = ($skipped -eq [bool]$c.expectSkip)
        inputTokens = if ($evaluation) { $evaluation.InputTokens } else { $null }
        costUsd = if ($evaluation) { $evaluation.CostUsd } else { $null }
        evaluationMs = if ($evaluation) { $evaluation.ElapsedMs } else { $null }
        roundTripMs = $sw.ElapsedMilliseconds
        partial = if ($evaluation) { [bool]$evaluation.IsPartial } else { $null }
        rejectedAnswers = if ($evaluation -and $evaluation.RejectedAnswers) { @($evaluation.RejectedAnswers) } else { @() }
    }

    $mark = if ($falseSkip) { 'FALSE-SKIP' } elseif ($missedSkip) { 'missed    ' } else { 'ok        ' }
    "$mark $($c.id.PadRight(10)) $($c.language)  want=$(if ($c.expectSkip) { 'skip  ' } else { 'refine' })  got=$("$($plan.Decision)".PadRight(15))  kind=$("$($plan.Kind)".PadRight(9)) ns=$([math]::Round($plan.NeedsStructure,2)) cx=$([math]::Round($plan.Complexity,2)) $($sw.ElapsedMilliseconds)ms"
}

$evaluated = @($results | Where-Object { $_.reason -ne 'no_api_key' -and $null -ne $_.inputTokens })
$shouldSkip = @($results | Where-Object expectSkip)
$shouldRefine = @($results | Where-Object { -not $_.expectSkip })
$kindChecked = @($results | Where-Object { $null -ne $_.kindCorrect })

$summary = [ordered]@{
    runUtc = (Get-Date).ToUniversalTime().ToString('o')
    corpusVersion = $doc.version
    split = $Split
    assembly = $dll.FullName
    model = [Talkty.App.Services.JevDecisionClient]::Model
    returnedModels = @($evaluated | ForEach-Object { $_.model } | Sort-Object -Unique)
    thresholds = [ordered]@{
        skipMaxNeedsStructure = [Talkty.App.Constants]::PromptSkipMaxNeedsStructure
        skipMaxComplexity     = [Talkty.App.Constants]::PromptSkipMaxComplexity
        kindMinProbability    = [Talkty.App.Constants]::PromptKindMinProbability
        qualityModelFloor     = [Talkty.App.Constants]::PromptComplexityQualityModelFloor
        timeoutMs             = [Talkty.App.Constants]::JevClassifierTimeoutMs
    }
    cases = $results.Count
    evaluated = $evaluated.Count
    unavailable = $results.Count - $evaluated.Count
    falseSkips = @($results | Where-Object falseSkip).Count
    missedSkips = @($results | Where-Object missedSkip).Count
    correctDecisions = @($results | Where-Object correct).Count
    shouldSkipCases = $shouldSkip.Count
    shouldRefineCases = $shouldRefine.Count
    kindChecked = $kindChecked.Count
    kindCorrect = @($kindChecked | Where-Object kindCorrect).Count
    kindAbstained = @($results | Where-Object { $_.kind -eq 'Unknown' }).Count
    partialResponses = @($results | Where-Object partial).Count
    inputTokens = ([long](@($evaluated).inputTokens | Measure-Object -Sum).Sum)
    reportedCostUsd = [math]::Round([double](@($evaluated).costUsd | Measure-Object -Sum).Sum, 9)
    allocatedUsd = [math]::Round($allocatedUsd, 9)
    ceilingUsd = $Ceiling
    medianEvaluationMs = if ($evaluated.Count) { (@($evaluated.evaluationMs | Sort-Object)[[int][math]::Floor($evaluated.Count / 2)]) } else { $null }
    maxEvaluationMs = if ($evaluated.Count) { (@($evaluated.evaluationMs | Measure-Object -Maximum).Maximum) } else { $null }
    note = 'Small labeled sample. A false skip is the consequential error: the speaker asked for a prompt and would receive raw dictation. Holdout cases were never used to choose a threshold.'
}

[ordered]@{ summary = $summary; results = $results } | ConvertTo-Json -Depth 10 |
    Set-Content "$Out\results.json" -Encoding UTF8

""
"decisions correct $($summary.correctDecisions)/$($summary.cases)  |  FALSE SKIPS $($summary.falseSkips)  |  missed skips $($summary.missedSkips)"
"should-skip $($summary.shouldSkipCases) | should-refine $($summary.shouldRefineCases)"
"request kind: $($summary.kindCorrect)/$($summary.kindChecked) correct where confident, abstained on $($summary.kindAbstained)"
"latency: median $($summary.medianEvaluationMs)ms, max $($summary.maxEvaluationMs)ms against a $([Talkty.App.Constants]::JevClassifierTimeoutMs)ms deadline"
"tokens $($summary.inputTokens) | cost `$$($summary.reportedCostUsd) | allocated `$$($summary.allocatedUsd) of `$$Ceiling | partial responses $($summary.partialResponses)"
"evidence:  $Out\results.json"
