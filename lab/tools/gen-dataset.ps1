# Laboratorio AV1 hibrido - gerador de dataset (Fase 2).
# Roda o FORK instrumentado do SVT-AV1 sobre os clipes do lab em pontos de taxa
# fixos e coleta os dumps de decisao por superbloco (sva_dump_*.bin).
#
# V1 usa CQ 1-pass (decisoes finais e reprodutiveis). VBR 2-pass do app pode ser
# adicionado depois; o dump distinguiria as passadas pela ordem dos frames.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\gen-dataset.ps1 [-ClipId clip01] [-ListEvery 8]
# NOTA: 100% ASCII. Volumes: ~10-40 MB por clipe/ponto com ListEvery=8.
param(
    [string]$ForkExe = "E:\av1-refs\svt-av1\Bin\Release\SvtAv1EncApp.exe",
    [string]$ClipsJson = "",
    [string]$ClipsDir = "",
    [string]$OutDir = "",
    [string]$WorkDir = "",
    [string[]]$RatePoints = @("cq20", "cq28", "cq36"),
    [int]$Preset = 6,
    [int]$ListEvery = 8,
    [string]$ClipId = ""   # vazio = todos os clipes do manifest
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg   = Join-Path $repoRoot "tools\ffmpeg.exe"
if (-not $ClipsJson) { $ClipsJson = Join-Path $repoRoot "lab\clips.json" }
if (-not $ClipsDir)  { $ClipsDir  = Join-Path $repoRoot "lab\clips" }
if (-not $OutDir)    { $OutDir    = Join-Path $repoRoot "lab\dataset" }
if (-not $WorkDir)   { $WorkDir   = Join-Path $repoRoot "lab\tmp\dataset" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$manifest = Get-Content $ClipsJson -Raw | ConvertFrom-Json

function Rate-Args([string]$point) {
    if ($point -like "cq*")  { return @("--rc", "0", "--crf", $point.Substring(2)) }
    if ($point -like "vbr*") { return @("--rc", "1", "--tbr", $point.Substring(3)) }
    throw "Ponto de taxa desconhecido: $point (use cqN ou vbrN)"
}

$runs = @()
foreach ($clipInfo in $manifest.clips) {
    if ($ClipId -and $clipInfo.id -ne $ClipId) { continue }
    $clip = Join-Path $ClipsDir (Split-Path $clipInfo.file -Leaf)
    if (-not (Test-Path $clip)) { Write-Warning "Clipe ausente: $clip"; continue }

    # y4m gerado uma vez por clipe (reusado entre os pontos de taxa)
    $y4m = Join-Path $WorkDir "$($clipInfo.id).y4m"
    if (-not (Test-Path $y4m)) {
        New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
        & $ffmpeg -hide_banner -loglevel error -y -i $clip -f yuv4mpegpipe -strict -1 -- $y4m
        if ($LASTEXITCODE -ne 0) { throw "y4m falhou para $($clipInfo.id)" }
    }

    foreach ($point in $RatePoints) {
        $runId  = "$($clipInfo.id)_p$Preset" + "_" + $point
        $runDir = Join-Path $OutDir $runId
        Remove-Item -Recurse -Force $runDir -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $runDir | Out-Null

        $env:SVA_DUMP = "1"
        $env:SVA_DUMP_DIR = $runDir
        $env:SVA_DUMP_LIST_EVERY = [string]$ListEvery
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            Push-Location $runDir
            & $ForkExe -i $y4m -b out.ivf --keyint 240 --scd 0 --preset $Preset `
                (Rate-Args $point) 2> enc.log
            Pop-Location
        } finally {
            $ErrorActionPreference = $prevEap
            Remove-Item env:SVA_DUMP, env:SVA_DUMP_DIR, env:SVA_DUMP_LIST_EVERY -ErrorAction SilentlyContinue
        }
        $sw.Stop()
        if ($LASTEXITCODE -ne 0) { Write-Warning "run $runId falhou (veja enc.log)"; continue }

        Remove-Item (Join-Path $runDir "out.ivf") -ErrorAction SilentlyContinue  # so o dump interessa
        $dumps = Get-ChildItem $runDir -Filter "sva_dump_*.bin"
        $mb = [math]::Round(($dumps | Measure-Object Length -Sum).Sum / 1MB, 1)
        $runs += [ordered]@{
            run = $runId; clip = $clipInfo.id; preset = $Preset; rate = $point
            list_every = $ListEvery; seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
            dump_files = ($dumps | Measure-Object).Count; dump_mb = $mb
        }
        Write-Host "ok $runId ($($dumps.Count) arquivos, $mb MB, $([math]::Round($sw.Elapsed.TotalSeconds,1))s)" -ForegroundColor Green
    }
}

$stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
[ordered]@{
    dataset    = "sva-dump-v2"
    fork       = $ForkExe
    preset     = $Preset
    list_every = $ListEvery
    runs       = $runs
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $OutDir "manifest-$stamp.json")
Write-Host "`nDataset pronto em $OutDir (manifest-$stamp.json)"