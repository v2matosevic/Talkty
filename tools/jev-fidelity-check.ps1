<#
.SYNOPSIS
Live qualification for Talkty's prompt-fidelity check: runs the labeled corpus through the real
Jev decision path and writes an evidence file.

.DESCRIPTION
Loads the built Talkty.App.dll, decrypts the saved OpenRouter key with the app's own ApiKeyProtector
(the key is never printed or written), and for every corpus case runs the production code path:
PromptFidelityAnalyzer clause extraction -> PromptFidelityService question profile and state ->
JevDecisionClient transport and strict validation -> PromptFidelityPolicy thresholds. The exact
code layer and the existing PromptRefinementService.IsSuspectedSummary length guard are recorded
alongside, so the comparison is measured rather than asserted.

SPENDS REAL MONEY on the saved OpenRouter key. Every attempt reserves $0.002 against the ceiling
and the reservation is replaced by the cost the provider actually reports; an unreported cost keeps
its reservation rather than counting as free. The run stops before an attempt that would breach the
ceiling. Failures are kept in the evidence file, not discarded.

A bare invocation is a DRY RUN: it prints the plan and the announced spend and sends nothing.
Add -Run to actually spend.

.EXAMPLE
pwsh tools/jev-fidelity-check.ps1
pwsh tools/jev-fidelity-check.ps1 -Run -Split development
pwsh tools/jev-fidelity-check.ps1 -Run -Ceiling 0.05
#>
param(
    # Without this the script only prints the plan.
    [switch]$Run,
    [ValidateSet('all', 'development', 'holdout')][string]$Split = 'all',
    # Restrict to specific corpus case ids, e.g. -Case dev-01,dev-02
    [string[]]$Case = @(),
    # Hard ceiling for this qualification pass, in US dollars.
    [double]$Ceiling = 0.05,
    [string]$Corpus = "$PSScriptRoot\jev-fidelity-corpus.json",
    [string]$Out = "$PSScriptRoot\..\docs\evidence\jev-fidelity-$(Get-Date -Format 'yyyy-MM-dd')",
    [string]$DllRoot = "$PSScriptRoot\..\Talkty.App\bin"
)
$ErrorActionPreference = 'Stop'

# Reservation per attempt, mirroring Constants.JevReservationMicroUsd.
$reservationUsd = 0.002

$dll = Get-ChildItem $DllRoot -Recurse -Filter 'Talkty.App.dll' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "Talkty.App.dll not found under $DllRoot - build first (dotnet build)." }
"assembly:  $($dll.FullName) ($($dll.LastWriteTime))"
Add-Type -Path $dll.FullName

$doc = Get-Content $Corpus -Raw -Encoding UTF8 | ConvertFrom-Json
$cases = $doc.cases | Where-Object { $Split -eq 'all' -or $_.split -eq $Split }
if ($Case.Count) { $cases = $cases | Where-Object { $Case -contains $_.id } }
if (-not $cases) { throw "No corpus cases matched (split=$Split, case=$($Case -join ','))." }

$maxAttempts = [math]::Floor($Ceiling / $reservationUsd)
"corpus:    $Corpus (version $($doc.version)), $($cases.Count) case(s), split=$Split"
"ceiling:   `$$Ceiling  |  reservation `$$reservationUsd per attempt  |  at most $maxAttempts attempt(s)"
"announced: up to `$$([math]::Round([math]::Min($Ceiling, $cases.Count * $reservationUsd), 4)) of Jev input charges for this pass"
"model:     $([Talkty.App.Services.JevDecisionClient]::Model) via $([Talkty.App.Services.JevDecisionClient]::Endpoint)"

if (-not $Run) {
    ""
    "DRY RUN - nothing was sent. Re-run with -Run to spend up to the ceiling above."
    return
}

if ($cases.Count -gt $maxAttempts) {
    throw "The ceiling of `$$Ceiling allows only $maxAttempts attempts but $($cases.Count) cases were selected. Raise -Ceiling deliberately or narrow -Split/-Case."
}

if ((Test-Path $Out) -and (Get-ChildItem $Out -File).Count) {
    throw "Evidence directory $Out already holds files. Point -Out at a fresh directory so an earlier run is never overwritten."
}
New-Item -ItemType Directory -Force $Out | Out-Null

$settingsPath = "$env:APPDATA\Talkty\settings.json"
if (-not (Test-Path $settingsPath)) { throw "No settings.json at $settingsPath." }
$settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$key = [Talkty.App.Services.ApiKeyProtector]::Unprotect($settings.openRouterApiKeyEncrypted)
if (-not $key) { throw 'No OpenRouter key saved in settings.json - add one in Settings first.' }
"key:       present and decrypted from settings.json (not shown)"
""

# The existing length-ratio baseline. Internal on purpose; reached by reflection so the comparison
# runs the real method rather than a copy of its arithmetic.
$guardMethod = [Talkty.App.Services.PromptRefinementService].GetMethod(
    'IsSuspectedSummary', [Reflection.BindingFlags]'NonPublic,Static')
if (-not $guardMethod) { throw 'PromptRefinementService.IsSuspectedSummary not found - was it renamed?' }

$client = [Talkty.App.Services.JevDecisionClient]::new()
$maxClauses = [Talkty.App.Constants]::JevMaxClauses
$none = [Threading.CancellationToken]::None

$results = @()
$allocatedUsd = 0.0
$attempts = 0

foreach ($c in $cases) {
    if (($allocatedUsd + $reservationUsd) -gt $Ceiling) {
        "STOPPED before $($c.id): the next reservation would breach the `$$Ceiling ceiling."
        break
    }

    $clauses = [Talkty.App.Services.PromptFidelityAnalyzer]::ExtractClauses($c.transcript, $maxClauses)
    $questions = [Talkty.App.Services.PromptFidelityService]::BuildQuestions($clauses)
    $state = [Talkty.App.Services.PromptFidelityService]::BuildState($clauses, $c.rewrite)
    $codeFindings = [Talkty.App.Services.PromptFidelityAnalyzer]::Compare($c.transcript, $c.rewrite)
    $guard = [bool]$guardMethod.Invoke($null, @($c.transcript, $c.rewrite))

    # Reserve first, exactly as the app's ledger does, so a crash mid-request cannot lose the spend.
    $allocatedUsd += $reservationUsd
    $attempts++

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $result = $client.EvaluateAsync($key, $state, $questions, $none).GetAwaiter().GetResult()
    $sw.Stop()

    $evaluation = $result.Evaluation
    $costKnown = $null -ne $evaluation -and $null -ne $evaluation.CostUsd
    if ($costKnown) {
        # The reported charge replaces the reservation; round up to whole microdollars.
        $allocatedUsd = $allocatedUsd - $reservationUsd + ([math]::Ceiling($evaluation.CostUsd * 1e6) / 1e6)
    }

    $modelConcerns = @()
    $answers = @()
    if ($result.Status -eq [Talkty.App.Services.JevStatus]::Evaluated) {
        $interpreted = [Talkty.App.Services.PromptFidelityPolicy]::Interpret($evaluation, $clauses)
        foreach ($m in $interpreted) {
            $modelConcerns += [ordered]@{
                kind = "$($m.Kind)"; clauseId = $m.ClauseId; sourceText = $m.SourceText
                probability = $m.Probability; confidence = $m.Confidence
            }
        }
        foreach ($id in $evaluation.Answers.Keys) {
            $a = $evaluation.Answers[$id]
            $answers += [ordered]@{
                id = $id; choice = $a.Choice; selectedProbability = $a.SelectedProbability
                confidence = $a.Confidence; noul = $a.Noul
            }
        }
    }

    $codeConcerns = @()
    foreach ($f in $codeFindings.Concerns) {
        $codeConcerns += [ordered]@{ kind = "$($f.Kind)"; clauseId = $f.ClauseId; sourceText = $f.SourceText }
    }

    $anyConcern = ($codeConcerns.Count + $modelConcerns.Count) -gt 0

    $results += [ordered]@{
        id                 = $c.id
        split              = $c.split
        language           = $c.language
        scenario           = $c.scenario
        label              = $c.label
        expectConcern      = [bool]$c.expectConcern
        expectedKinds      = @($c.expectedKinds)
        expectedClause     = $c.expectedClause
        codeShouldCatch    = [bool]$c.codeShouldCatch
        clauseCount        = $clauses.Count
        questionCount      = $questions.Count
        baselineGuard      = $guard
        codeConcerns       = $codeConcerns
        jevStatus          = "$($result.Status)"
        failureCode        = $result.FailureCode
        model              = if ($evaluation) { $evaluation.Model } else { $null }
        inputTokens        = if ($evaluation) { $evaluation.InputTokens } else { $null }
        outputTokens       = if ($evaluation) { $evaluation.OutputTokens } else { $null }
        costUsd            = if ($costKnown) { $evaluation.CostUsd } else { $null }
        costKnown          = $costKnown
        evaluationMs       = if ($evaluation) { $evaluation.ElapsedMs } else { $null }
        roundTripMs        = $sw.ElapsedMilliseconds
        answers            = $answers
        modelConcerns      = $modelConcerns
        anyConcern         = $anyConcern
        correct            = ($anyConcern -eq [bool]$c.expectConcern)
    }

    $verdict = if ($anyConcern) { 'concern' } else { 'clean  ' }
    $mark = if ($anyConcern -eq [bool]$c.expectConcern) { 'ok  ' } else { 'MISS' }
    "$mark $($c.id.PadRight(9)) $($c.split.PadRight(12)) $($c.language)  guard=$(([string]$guard).PadRight(5)) code=$($codeConcerns.Count) model=$($modelConcerns.Count) -> $verdict  $($result.Status) $($sw.ElapsedMilliseconds)ms"
}

# ── Summary ─────────────────────────────────────────────────────────────
$evaluated = @($results | Where-Object { $_.jevStatus -eq 'Evaluated' })
$lossCases = @($results | Where-Object { $_.expectConcern })
$faithful = @($results | Where-Object { -not $_.expectConcern })
$unknownCost = @($results | Where-Object { -not $_.costKnown })

$summary = [ordered]@{
    runUtc              = (Get-Date).ToUniversalTime().ToString('o')
    corpusVersion       = $doc.version
    split               = $Split
    assembly            = $dll.FullName
    assemblyWritten     = $dll.LastWriteTime.ToString('o')
    endpoint            = [Talkty.App.Services.JevDecisionClient]::Endpoint
    requestedModel      = [Talkty.App.Services.JevDecisionClient]::Model
    returnedModels      = @($evaluated.model | Sort-Object -Unique)
    thresholds          = [ordered]@{
        minProbability      = [Talkty.App.Constants]::JevFidelityMinProbability
        minConfidence       = [Talkty.App.Constants]::JevFidelityMinConfidence
        minAddedProbability = [Talkty.App.Constants]::JevFidelityMinAddedProbability
        maxClauses          = $maxClauses
    }
    attempts            = $attempts
    evaluated           = $evaluated.Count
    transportFailures   = $attempts - $evaluated.Count
    ceilingUsd          = $Ceiling
    allocatedUsd        = [math]::Round($allocatedUsd, 9)
    reportedCostUsd     = [math]::Round(([double](@($results | Where-Object costKnown).costUsd | Measure-Object -Sum).Sum), 9)
    attemptsWithUnknownCost = $unknownCost.Count
    inputTokens         = ([long](@($evaluated).inputTokens | Measure-Object -Sum).Sum)
    medianEvaluationMs  = if ($evaluated.Count) { (@($evaluated.evaluationMs | Sort-Object)[[int][math]::Floor($evaluated.Count / 2)]) } else { $null }
    lossCases           = $lossCases.Count
    lossCaught          = @($lossCases | Where-Object anyConcern).Count
    lossCaughtByCodeOnly = @($lossCases | Where-Object { $_.codeConcerns.Count -gt 0 }).Count
    lossCaughtByModel   = @($lossCases | Where-Object { $_.modelConcerns.Count -gt 0 }).Count
    lossCaughtByBaselineGuard = @($lossCases | Where-Object baselineGuard).Count
    faithfulCases       = $faithful.Count
    falseAlarms         = @($faithful | Where-Object anyConcern).Count
    falseAlarmsFromCode = @($faithful | Where-Object { $_.codeConcerns.Count -gt 0 }).Count
    falseAlarmsFromModel = @($faithful | Where-Object { $_.modelConcerns.Count -gt 0 }).Count
    note                = 'Small labeled sample. These counts do not establish a production error rate, and a Jev confidence is a distribution statistic, not a measured probability of correctness. Holdout cases were never used to choose a threshold.'
}

$payload = [ordered]@{ summary = $summary; results = $results }
$payload | ConvertTo-Json -Depth 12 | Set-Content "$Out\results.json" -Encoding UTF8

""
"attempts $($summary.attempts) | evaluated $($summary.evaluated) | transport failures $($summary.transportFailures)"
"fidelity-loss cases $($summary.lossCases): caught $($summary.lossCaught) (code $($summary.lossCaughtByCodeOnly), model $($summary.lossCaughtByModel), length guard $($summary.lossCaughtByBaselineGuard))"
"faithful cases $($summary.faithfulCases): false alarms $($summary.falseAlarms) (code $($summary.falseAlarmsFromCode), model $($summary.falseAlarmsFromModel))"
"input tokens $($summary.inputTokens) | reported cost `$$($summary.reportedCostUsd) | allocated `$$($summary.allocatedUsd) of `$$Ceiling | unknown-cost attempts $($summary.attemptsWithUnknownCost)"
"median evaluation $($summary.medianEvaluationMs)ms | returned model(s) $($summary.returnedModels -join ', ')"
"evidence:  $Out\results.json"
