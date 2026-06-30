# Re-render every Talkty social asset from the HTML sources.
# Requires Google Chrome. Run from anywhere: pwsh social-media/sources/render.ps1
$ErrorActionPreference = "Stop"
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
if (-not (Test-Path $chrome)) { $chrome = "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" }
$src = $PSScriptRoot
$out = Join-Path (Split-Path $src) "images"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Render($html, $png, $w, $h, [switch]$Transparent) {
  $a = @(
    "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
    "--no-default-browser-check", "--force-device-scale-factor=1",
    "--window-size=$w,$h", "--screenshot=$out\$png", "file:///$src/$html"
  )
  if ($Transparent) { $a += "--default-background-color=00000000" }
  & $chrome @a 2>$null
  Write-Host "  $png  ($w x $h)"
}

Write-Host "Rendering Talkty social assets..."
Render "hero-b.html"           "hero.png"              2400 1200
Render "hero-a.html"           "hero-minimal.png"     2400 1200
Render "brand-card.html"       "github-social.png"    1280 640
Render "brand-card.html"       "linkedin-card.png"    1200 627
Render "x-card.html"           "x-card.png"           1600 900
Render "pill-states.html"      "pill-states.png"      2000 620
Render "feature-prompting.html" "feature-prompting.png" 1600 1000
Render "wordmark.html"         "wordmark-light.png"   1400 420 -Transparent
Render "wordmark-dark.html"    "wordmark-dark.png"    1400 420 -Transparent
Render "icon.html"             "icon.png"             1024 1024 -Transparent
Write-Host "Done. Assets in $out"
