# Laboratorio AV1 hibrido - gate de paridade do MODO LOTE (Fase 5b / Etapa 2).
#
# Tres bracos com --lp 1 (determinismo) no mesmo clip, sha256 do .ivf tem que ser
# IDENTICO nos tres:
#   A: fork build CUDA, SVA_CUDA ausente          -> caminho CPU (nada instalado)
#   B: fork build CUDA, SVA_CUDA=1 SVA_CUDA_BATCH=1 -> HME por lotes na GPU
#   C: pristine upstream 4.2.0 (sem patch nenhum)  -> referencia absoluta
# Com o lote em apenas ALGUNS niveis (ex.: so L1 no Incremento 2), o restante da HME
# continua per-call; o gate cobre o encode inteiro de qualquer forma.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\verify-batch-parity.ps1 [-Clip <path>]
param(
    [string]$Clip = "",
    [string]$CudaExe = "E:\av1-refs\svt-av1\Bin\Release\SvtAv1EncApp.exe",
    [string]$PristineExe = "E:\av1-refs\svt-av1-pristine\Bin\Release\SvtAv1EncApp.exe",
    [int]$Preset = 6,
    [int]$Kbps = 500
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg   = Join-Path $repoRoot "tools\ffmpeg.exe"
if (-not $Clip) { $Clip = Join-Path $repoRoot "lab\clips\clip01.mkv" }
$tmp = Join-Path $repoRoot "lab\tmp\batch-parity"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$y4m = Join-Path $tmp "input.y4m"
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $ffmpeg -hide_banner -loglevel error -y -i $Clip -f yuv4mpegpipe -strict -1 -- $y4m
$ErrorActionPreference = $prevEap
if ($LASTEXITCODE -ne 0) { throw "y4m falhou" }

function Encode-Ivf([string]$workDir, [string]$exePath, [string]$envSpec) {
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    Copy-Item (Join-Path (Split-Path $exePath) "*") $workDir -Force
    $app = Join-Path (Split-Path $exePath) "SvtAv1EncApp.exe"
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
            --rc 1 --tbr $Kbps --passes 2 --lp 1 2> enc.log
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
        throw "encode falhou em $workDir (veja enc.log)"
    }
    [pscustomobject]@{
        Sha     = (Get-FileHash -Algorithm SHA256 (Join-Path $workDir "out.ivf")).Hash.ToLowerInvariant()
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }
}

Write-Host "GPU:" (& nvidia-smi --query-gpu=name --format=csv,noheader 2>$null)

$a = Encode-Ivf (Join-Path $tmp "cpu")  $CudaExe    ""
Write-Host ("A cpu (env off)      : {0}s sha={1}" -f $a.Seconds, $a.Sha.Substring(0, 16))
$b = Encode-Ivf (Join-Path $tmp "batch") $CudaExe    "SVA_CUDA=1;SVA_CUDA_BATCH=1"
Write-Host ("B lote (CUDA_BATCH=1): {0}s sha={1}" -f $b.Seconds, $b.Sha.Substring(0, 16))
$c = Encode-Ivf (Join-Path $tmp "pristine") $PristineExe ""
Write-Host ("C pristine           : {0}s sha={1}" -f $c.Seconds, $c.Sha.Substring(0, 16))

$gate = ($a.Sha -eq $b.Sha) -and ($b.Sha -eq $c.Sha)
Write-Host ("GATE A==B==C (lote byte-exato): {0}" -f $(if ($gate) { "OK" } else { "FALHOU" }))

$report = Join-Path $repoRoot "lab\resultados\batch-parity-v1.md"
New-Item -ItemType Directory -Force -Path (Split-Path $report) | Out-Null
@"
# Gate de paridade do modo LOTE v1 (Fase 5b Etapa 2)

Clip clip01.mkv (45s 1080p), p6 @${Kbps}k 2-pass, --lp 1

| Braco | Env | Tempo (s) | sha256 (16) |
|-------|-----|-----------|-------------|
| A cpu | - | $($a.Seconds) | $($a.Sha.Substring(0,16)) |
| B lote | SVA_CUDA=1 SVA_CUDA_BATCH=1 | $($b.Seconds) | $($b.Sha.Substring(0,16)) |
| C pristine | - | $($c.Seconds) | $($c.Sha.Substring(0,16)) |

Gate A==B==C: $(if ($gate) { "OK (byte-exato)" } else { "FALHOU" })
"@ | Out-File -FilePath $report -Encoding ascii

if (-not $gate) { exit 1 }
