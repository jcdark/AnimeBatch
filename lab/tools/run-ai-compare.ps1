# Laboratorio AV1 hibrido - Fase 4: compara o fork com a IA de prior em
# off / safe / aggressive contra o baseline congelado (mesmo clip, p6, 500k).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\run-ai-compare.ps1 [-ClipId clip01] [-Modes "off safe aggressive"]
# NOTA: 100% ASCII.
param(
    [string]$ForkExe = "E:\av1-refs\svt-av1\Bin\Release\SvtAv1EncApp.exe",
    [string]$PriorFile = "E:\AnimeBatch\lab\ai_prior.txt",
    [string]$ClipId = "clip01",
    [string]$Modes = "off safe aggressive",
    [int]$Preset = 6,
    [int]$Kbps = 500
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg   = Join-Path $repoRoot "tools\ffmpeg.exe"
$clipsDir = Join-Path $repoRoot "lab\clips"
$outRoot  = Join-Path $repoRoot "lab\tmp\ai-compare"
$clip     = Join-Path $clipsDir "$ClipId.mkv"
if (-not (Test-Path $clip)) { throw "clip nao encontrado: $clip" }
if (-not (Test-Path $PriorFile)) { throw "tabela de prior ausente: $PriorFile ( rode export_prior.py )" }
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

$y4m = Join-Path $outRoot "$ClipId.y4m"
if (-not (Test-Path $y4m)) {
    & $ffmpeg -hide_banner -loglevel error -y -i $clip -f yuv4mpegpipe -strict -1 -- $y4m
    if ($LASTEXITCODE -ne 0) { throw "y4m falhou" }
}

function Probe-Num([string]$file, [string]$what) {
    $v = & (Join-Path $repoRoot "tools\ffprobe.exe") -v error -show_entries "$what" -of csv=p=0 -- $file | Select-Object -First 1
    return $v
}

$modeList = @($Modes -split "[\s,]+" | Where-Object { $_ })
$rows = @()
foreach ($m in $modeList) {
    $work = Join-Path $outRoot "$ClipId`_$m"
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $env:SVA_AI_MODE = if ($m -eq "off") { "" } else { $m }
    $env:SVA_AI_PRIOR = if ($m -eq "off") { "" } else { $PriorFile }
    $env:SVA_AI_MIN_PRIOR = "0.002"

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        Push-Location $work
        & $ForkExe -i $y4m -b out.ivf --keyint 240 --scd 0 --preset $Preset `
            --rc 1 --tbr $Kbps --passes 2 2> enc.log
        Pop-Location
    } finally {
        $ErrorActionPreference = $prevEap
        Remove-Item env:SVA_AI_MODE, env:SVA_AI_PRIOR, env:SVA_AI_MIN_PRIOR -ErrorAction SilentlyContinue
    }
    $sw.Stop()
    if (-not (Test-Path (Join-Path $work "out.ivf"))) { Write-Warning "encode $m falhou"; continue }

    $outIvf = Join-Path $work "out.ivf"
    $kbps = [math]::Round([double](Probe-Num $outIvf "format=bit_rate") / 1000.0, 1)
    & (Join-Path $PSScriptRoot "measure-vmaf.ps1") -Ref $clip -Dist $outIvf -OutJson (Join-Path $work "vmaf.json") | Out-Null
    $vmaf = (Get-Content (Join-Path $work "vmaf.json") -Raw | ConvertFrom-Json).vmaf
    $rows += [ordered]@{ mode = $m; seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2); kbps = $kbps; vmaf = $vmaf }
    Write-Host ("{0,-11} {1,8}s  {2,7} kbps  VMAF {3}" -f $m, $rows[-1].seconds, $kbps, $vmaf) -ForegroundColor Green
}

Write-Host ""
$base = $rows | Where-Object { $_.mode -eq "off" } | Select-Object -First 1
foreach ($r in $rows) {
    if ($r.mode -eq "off" -or -not $base) { continue }
    $dT = [math]::Round((($base.seconds - $r.seconds) / $base.seconds) * 100, 1)
    $dV = [math]::Round($r.vmaf - $base.vmaf, 3)
    Write-Host ("{0,-11} tempo {1}% | dVMAF {2}" -f $r.mode, $dT, $dV)
}