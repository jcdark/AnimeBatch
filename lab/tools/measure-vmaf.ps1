# Laboratorio AV1 hibrido - mede VMAF/SSIM/PSNR (libvmaf) entre referencia e distorcido.
# Saida: JSON com as medias pooled (vmaf/float_ssim/psnr) + tempo de medicao.
# NOTA: 100% ASCII - PowerShell 5.1 le .ps1 sem BOM como ANSI (mesma licao do make-icon.ps1).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\measure-vmaf.ps1 -Ref <video> -Dist <video> [-OutJson resultado.json]
param(
    [Parameter(Mandatory = $true)][string]$Ref,
    [Parameter(Mandatory = $true)][string]$Dist,
    [string]$OutJson = "",
    [int]$NThreads = 0   # 0 = usa todos os cores logicos
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg = Join-Path $repoRoot "tools\ffmpeg.exe"
$ffprobe = Join-Path $repoRoot "tools\ffprobe.exe"

if (-not $NThreads -or $NThreads -le 0) { $NThreads = [Environment]::ProcessorCount }

function Probe([string]$file, [string]$what) {
    return (& $ffprobe -v error -select_streams v:0 -show_entries "stream=$what" -of csv=p=0 -- $file | Select-Object -First 1)
}

# MKV/IVF frequentemente tem stream=nb_frames = N/A -> decodifica e conta
function ProbeFrames([string]$file) {
    $v = Probe $file "nb_frames"
    $n = 0L
    if ($v -and [int64]::TryParse($v, [ref]$n) -and $n -gt 0) { return $n }
    $v = & $ffprobe -v error -select_streams v:0 -count_frames -show_entries "stream=nb_read_frames" -of csv=p=0 -- $file
    if ([int64]::TryParse(($v | Select-Object -First 1), [ref]$n)) { return $n }
    return 0L
}

$refW = [int](Probe $Ref "width"); $refH = [int](Probe $Ref "height")
$refFps = Probe $Ref "avg_frame_rate"
$refFrames = ProbeFrames $Ref
$distFrames = ProbeFrames $Dist
if (-not $refFps -or $refFps -eq "0/0") { $refFps = "24000/1001" }
$frameCap = [Math]::Min($refFrames, $distFrames)
if ($frameCap -le 0) { throw "Contagem de frames invalida (ref=$refFrames dist=$distFrames)" }

$logDir = [System.IO.Path]::GetTempPath() + [Guid]::NewGuid().ToString("N")
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logJson = Join-Path $logDir "vmaf_log.json"

# Ambas as entradas normalizadas para o mesmo fps/tamanho/formato; corta no menor frame count.
$graph = "[0:v]fps=$refFps,format=yuv420p,trim=end_frame=$frameCap,setpts=PTS-STARTPTS[ref];" +
         "[1:v]fps=$refFps,scale=${refW}:${refH}:flags=bicubic,format=yuv420p,trim=end_frame=$frameCap,setpts=PTS-STARTPTS[dist];" +
         "[ref][dist]libvmaf=log_path=vmaf_log.json:log_fmt=json:n_threads=$NThreads" +
         ":feature='name=psnr':feature='name=float_ssim'[out]"

# GOTCHA PS 5.1: com EAP=Stop, stderr redirecionado de processo nativo vira
# NativeCommandError — guarda EAP e captura o stderr em log para diagnostico.
$ffmpegLog = [System.IO.Path]::GetTempFileName() + ".log"
$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # log_path relativo: o "C:" do caminho absoluto quebra o parser de opcoes do filtro
    Push-Location $logDir
    try {
        & $ffmpeg -hide_banner -loglevel error -y -i $Ref -i $Dist -filter_complex $graph -map "[out]" -f null NUL 2> $ffmpegLog
        $exit = $LASTEXITCODE
    } finally { Pop-Location }
    $sw.Stop()
} finally {
    $ErrorActionPreference = $prevEap
}
$swSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
if ($exit -ne 0) {
    $tail = if (Test-Path $ffmpegLog) { (Get-Content $ffmpegLog -Raw) } else { "" }
    throw "ffmpeg/libvmaf falhou (exit $exit): $tail"
}
if (-not (Test-Path $logJson) -or (Get-Item $logJson).Length -eq 0) { throw "libvmaf nao produziu log JSON" }

$log = Get-Content $logJson -Raw | ConvertFrom-Json
Remove-Item $logJson -ErrorAction SilentlyContinue

$pooled = $log.pooled_metrics
if (-not $pooled) { throw "Log sem pooled_metrics (frameCap=$frameCap)" }

# Algumas builds do libvmaf nao expõem "psnr" pooled (só psnr_y/psnr_hvs...): pega a 1a chave psnr*
$psnrKey = $pooled.PSObject.Properties.Name | Where-Object { $_ -match "^psnr" } | Select-Object -First 1
$psnrMean = if ($psnrKey) { $pooled.$psnrKey.mean } else { 0 }

$result = [ordered]@{
    ref             = (Resolve-Path $Ref).Path
    dist            = (Resolve-Path $Dist).Path
    frames          = $frameCap
    width           = $refW
    height          = $refH
    fps             = $refFps
    measureSeconds  = $swSeconds
    vmaf            = [math]::Round($pooled.vmaf.mean, 3)
    vmafHarmonic    = [math]::Round($pooled.vmaf.harmonic_mean, 3)
    vmafMin         = [math]::Round($pooled.vmaf.min, 3)
    ssim            = [math]::Round($pooled.float_ssim.mean, 5)
    psnr            = [math]::Round($psnrMean, 3)
}

if ($OutJson) {
    $result | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 $OutJson
    Write-Host "vmaf=$($result.vmaf) ssim=$($result.ssim) psnr=$($result.psnr) -> $OutJson"
} else {
    $result | ConvertTo-Json -Depth 3 -Compress | Write-Output
}