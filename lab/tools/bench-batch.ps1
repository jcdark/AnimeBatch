# Laboratorio AV1 hibrido - benchmark do MODO LOTE (Fase 5b / Etapa 2).
#
# 3 clipes x (lote OFF vs lote ON), p6 @500k 2-pass, threading de PRODUCAO
# (sem --lp, igual ao harness congelado). Tempo de parede por braco + ganho.
# Amostragem nvidia-smi durante um dos bracos ON (engajamento).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\bench-batch.ps1 [-Clips clip01,clip05,clip09]
param(
    [string[]]$Clips = @("clip01", "clip05", "clip09"),
    [string]$CudaExe = "E:\av1-refs\svt-av1\Bin\Release\SvtAv1EncApp.exe",
    [int]$Preset = 6,
    [int]$Kbps = 500
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg   = Join-Path $repoRoot "tools\ffmpeg.exe"
$tmp = Join-Path $repoRoot "lab\tmp\bench-batch"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Encode-Timed([string]$workDir, [string]$y4m, [string]$envSpec) {
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    if (-not (Test-Path (Join-Path $workDir "SvtAv1EncApp.exe"))) {
        Copy-Item (Join-Path (Split-Path $CudaExe) "*") $workDir -Force
    }
    $app = Join-Path $workDir "SvtAv1EncApp.exe"
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        Push-Location $workDir
        if ($envSpec -ne "") {
            foreach ($kv in $envSpec.Split(";")) {
                $k, $v = $kv.Split("=")
                Set-Item -Path "env:$k" -Value $v
            }
        }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        & $app -i $y4m -b out.ivf --keyint 240 --scd 0 --preset $Preset `
            --rc 1 --tbr $Kbps --passes 2 2> enc.log
        $sw.Stop()
        if ($envSpec -ne "") {
            foreach ($kv in $envSpec.Split(";")) {
                $k = $kv.Split("=")[0]
                Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue
            }
        }
        Pop-Location
    } finally {
        $ErrorActionPreference = $prev
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $workDir "out.ivf"))) {
        throw "encode falhou em $workDir"
    }
    return [math]::Round($sw.Elapsed.TotalSeconds, 1)
}

$rows = @()
foreach ($id in $Clips) {
    $clip = Join-Path $repoRoot "lab\clips\$id.mkv"
    if (-not (Test-Path $clip)) { throw "clipe ausente: $clip" }
    $y4m = Join-Path $tmp "$id.y4m"
    if (-not (Test-Path $y4m)) {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        & $ffmpeg -hide_banner -loglevel error -y -i $clip -f yuv4mpegpipe -strict -1 -- $y4m
        $ErrorActionPreference = $prev
        if ($LASTEXITCODE -ne 0) { throw "y4m falhou para $id" }
    }
    $off = Encode-Timed (Join-Path $tmp "$id`_off") $y4m ""
    $on  = Encode-Timed (Join-Path $tmp "$id`_on")  $y4m "SVA_CUDA=1;SVA_CUDA_BATCH=1"
    $gain = [math]::Round(($off / $on - 1) * 100, 1)
    $rows += [pscustomobject]@{ Clip = $id; OffS = $off; OnS = $on; GainPct = $gain }
    Write-Host ("{0}: OFF {1}s | ON {2}s | ganho {3}%" -f $id, $off, $on, $gain)
}

$avgGain = [math]::Round((($rows | Measure-Object -Property GainPct -Average).Average), 1)
Write-Host ("GANHO MEDIO: {0}%" -f $avgGain)

$report = Join-Path $repoRoot "lab\resultados\bench-batch-v1.md"
$lines = @(
    "# Benchmark do modo LOTE v1 (Fase 5b Etapa 2)",
    "",
    "Build CUDA do fork, p6 @${Kbps}k 2-pass, threading de producao (sem --lp)",
    "",
    "| Clipe | OFF (s) | ON lote (s) | Ganho (%) |",
    "|-------|---------|-------------|-----------|"
)
foreach ($r in $rows) {
    $lines += "| $($r.Clip) | $($r.OffS) | $($r.OnS) | $($r.GainPct) |"
}
$lines += ""
$lines += "Ganho medio: $avgGain%"
$lines -join "`n" | Out-File -FilePath $report -Encoding ascii
Write-Host "Relatorio: $report"
