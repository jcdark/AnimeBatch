# Laboratorio AV1 hibrido - gate de paridade CUDA (Fase 5).
#
# Um unico binario (build com SVA_ENABLE_CUDA), tres rodadas com --lp 1
# (single MD thread = determinismo; com lp>1 o SVT 4.2.0 e nao-deterministico):
#   A : SVA_CUDA ausente -> caminho CPU (hook nao instalado)
#   B : SVA_CUDA=1       -> busca SAD full-pel na GPU
#   B2: SVA_CUDA=1 again -> determinismo run-a-run da GPU
# Gate: A == B e B == B2 byte-a-byte (sha256 do .ivf). Durante B, nvidia-smi
# e amostrado a cada segundo para provar que a GPU foi usada de fato
# (fallback silencioso para CPU produziria A==B trivialmente).
# Os segundos de cada rodada sao o benchmark end-to-end CPU vs GPU.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\verify-cuda-parity.ps1 [-Clip <path>]
param(
    [string]$Clip = "",
    [string]$CudaExe = "E:\av1-refs\svt-av1\Bin\Release\SvtAv1EncApp.exe",
    [int]$Preset = 6,
    [int]$Kbps = 500
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg   = Join-Path $repoRoot "tools\ffmpeg.exe"
if (-not $Clip) { $Clip = Join-Path $repoRoot "lab\clips\clip01.mkv" }
$tmp = Join-Path $repoRoot "lab\tmp\cuda-parity"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$y4m = Join-Path $tmp "input.y4m"
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $ffmpeg -hide_banner -loglevel error -y -i $Clip -f yuv4mpegpipe -strict -1 -- $y4m
$ErrorActionPreference = $prevEap
if ($LASTEXITCODE -ne 0) { throw "y4m falhou" }

function Encode-Ivf([string]$workDir, [int]$cuda, [bool]$sampleGpu) {
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    Copy-Item (Join-Path (Split-Path $CudaExe) "*") $workDir -Force
    $app = Join-Path (Split-Path $CudaExe) "SvtAv1EncApp.exe"
    $job = $null
    if ($sampleGpu) {
        $utilFile = Join-Path $workDir "gpu_util.txt"
        $job = Start-Job -ScriptBlock {
            param($out)
            for ($i = 0; $i -lt 900; $i++) {
                $u = & nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits 2>$null
                if ($LASTEXITCODE -eq 0 -and $u) { Add-Content -Path $out -Value $u }
                Start-Sleep -Seconds 1
            }
        } -ArgumentList $utilFile
    }
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        Push-Location $workDir   # stat file do 2-pass fica aqui
        if ($cuda -eq 1) { $env:SVA_CUDA = "1" }
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        & $app -i $y4m -b out.ivf --keyint 240 --scd 0 --preset $Preset `
            --rc 1 --tbr $Kbps --passes 2 --lp 1 2> enc.log
        $sw.Stop()
        if ($cuda -eq 1) { Remove-Item env:SVA_CUDA -ErrorAction SilentlyContinue }
        Pop-Location
    } finally {
        $ErrorActionPreference = $prev
    }
    if ($job) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $workDir "out.ivf"))) {
        throw "encode falhou em $workDir (veja enc.log)"
    }
    $maxUtil = ""
    $utilFile = Join-Path $workDir "gpu_util.txt"
    if (Test-Path $utilFile) {
        $vals = Get-Content $utilFile | ForEach-Object { [int]$_ }
        if ($vals.Count -gt 0) { $maxUtil = ($vals | Measure-Object -Maximum).Maximum }
    }
    [pscustomobject]@{
        Sha     = (Get-FileHash -Algorithm SHA256 (Join-Path $workDir "out.ivf")).Hash.ToLowerInvariant()
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        MaxGpu  = $maxUtil
    }
}

Write-Host "GPU:" (& nvidia-smi --query-gpu=name --format=csv,noheader 2>$null)

$a  = Encode-Ivf (Join-Path $tmp "cpu")  0 $false
Write-Host ("A cpu : {0}s sha={1}" -f $a.Seconds, $a.Sha.Substring(0, 16))
$b  = Encode-Ivf (Join-Path $tmp "gpu")  1 $true
Write-Host ("B gpu : {0}s sha={1} maxGpuUtil={2}%" -f $b.Seconds, $b.Sha.Substring(0, 16), $b.MaxGpu)
$b2 = Encode-Ivf (Join-Path $tmp "gpu2") 1 $false
Write-Host ("B2 gpu: {0}s sha={1}" -f $b2.Seconds, $b2.Sha.Substring(0, 16))

$gate1 = ($a.Sha -eq $b.Sha)
$gate2 = ($b.Sha -eq $b2.Sha)
Write-Host ("GATE A==B (GPU nao mudou a saida): {0}" -f $(if ($gate1) { "OK" } else { "FALHOU" }))
Write-Host ("GATE B==B2 (determinismo GPU)   : {0}" -f $(if ($gate2) { "OK" } else { "FALHOU" }))
if ($b.MaxGpu -ne "") {
    Write-Host ("Engajamento GPU: util maxima {0}% (amostragem 1s durante B)" -f $b.MaxGpu)
}
Write-Host ("Tempo CPU {0}s vs GPU {1}s ({2})" -f $a.Seconds, $b.Seconds, $(if ($b.Seconds -gt 0) { [math]::Round(($a.Seconds / $b.Seconds - 1) * 100, 1) } else { 0 }) + "% de ganho")

$report = Join-Path $repoRoot "lab\resultados\cuda-parity-v1.md"
New-Item -ItemType Directory -Force -Path (Split-Path $report) | Out-Null
@"
# Gate de paridade CUDA v1 (Fase 5)

Build: SVA_ENABLE_CUDA (build-hybrid-cuda), clip clip01.mkv (45s 1080p), p6 @${Kbps}k 2-pass, --lp 1

| Rodada | SVA_CUDA | Tempo (s) | sha256 (16) | GPU util max |
|--------|----------|-----------|-------------|--------------|
| A cpu  | -        | $($a.Seconds) | $($a.Sha.Substring(0,16)) | - |
| B gpu  | 1        | $($b.Seconds) | $($b.Sha.Substring(0,16)) | $($b.MaxGpu)% |
| B2 gpu | 1        | $($b2.Seconds) | $($b2.Sha.Substring(0,16)) | - |

Gate A==B: $(if ($gate1) { "OK (saida byte-exata)" } else { "FALHOU" })
Gate B==B2: $(if ($gate2) { "OK (determinismo)" } else { "FALHOU" })
"@ | Out-File -FilePath $report -Encoding ascii

if (-not ($gate1 -and $gate2)) { exit 1 }
