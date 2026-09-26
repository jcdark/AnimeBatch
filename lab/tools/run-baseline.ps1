# Laboratorio AV1 hibrido - roda a matriz de encodes sobre o clipset e mede
# tempo/fps/bitrate/tamanho + VMAF/SSIM/PSNR contra o clipe de referencia.
#
# Regra do projeto: nada avanca sem comparacao contra o baseline congelado em lab\baselines\.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\run-baseline.ps1 -Label baseline-v1 [-Freeze]
#   Engines: svt_p6_500k_2pass (padrao) | nvenc_p7_500k
# NOTA: 100% ASCII - PowerShell 5.1 le .ps1 sem BOM como ANSI (mesma licao do make-icon.ps1).
param(
    [string]$ClipsJson = "",
    [string]$ClipsDir = "",
    [string]$OutDir = "",
    [string]$ResultsDir = "",
    # Chamada externa pode chegar com espaco/virgula/ponto-e-virgula (quirk do -File)
    [string]$Engines = "svt_p6_500k_2pass",
    [Parameter(Mandatory = $true)][string]$Label,
    # Reusa encodes existentes em out\ (so mede de novo) - util para iterar metricas
    [switch]$Reuse,
    [switch]$Freeze
)

# GOTCHA: parametro tipado [string] COERCE array para string na atribuicao ($OFS junta
# com espaco) — por isso o resultado do split vai para uma variavel NOVA, nao para $Engines.
# Chamada externa pode chegar com espaco/virgula/ponto-e-virgula (quirk do -File).
$engineList = @($Engines -split "[,;\s]+" | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg   = Join-Path $repoRoot "tools\ffmpeg.exe"
$ffprobe  = Join-Path $repoRoot "tools\ffprobe.exe"
$svtApp   = Join-Path $repoRoot "tools\svt-av1\SvtAv1EncApp.exe"
if (-not $ClipsJson)  { $ClipsJson  = Join-Path $repoRoot "lab\clips.json" }
if (-not $ClipsDir)   { $ClipsDir   = Join-Path $repoRoot "lab\clips" }
if (-not $OutDir)     { $OutDir     = Join-Path $repoRoot "lab\out" }
if (-not $ResultsDir) { $ResultsDir = Join-Path $repoRoot "lab\resultados" }
New-Item -ItemType Directory -Force -Path $OutDir, $ResultsDir | Out-Null
$tmpDir = Join-Path $repoRoot "lab\tmp"
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null

$manifest = Get-Content $ClipsJson -Raw | ConvertFrom-Json

function Probe([string]$file, [string]$what) {
    return (& $ffprobe -v error -select_streams v:0 -show_entries "stream=$what" -of csv=p=0 -- $file | Select-Object -First 1)
}

# Kbps do CONTAINER: format=bit_rate; IVF/MKV às vezes dão N/A -> cai para tamanho/duração
function Get-Kbps([string]$file) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $br = & $ffprobe -v error -show_entries "format=bit_rate" -of csv=p=0 -- $file | Select-Object -First 1
    $v = 0.0
    if ($br -and [double]::TryParse($br, [Globalization.NumberStyles]::Float, $inv, [ref]$v) -and $v -gt 0) {
        return [math]::Round($v / 1000.0, 1)
    }
    $dur = & $ffprobe -v error -show_entries "format=duration" -of csv=p=0 -- $file | Select-Object -First 1
    $d = 0.0
    if ($dur -and [double]::TryParse($dur, [Globalization.NumberStyles]::Float, $inv, [ref]$d) -and $d -gt 0) {
        return [math]::Round((Get-Item $file).Length * 8.0 / $d / 1000.0, 1)
    }
    return 0
}

function Get-EnvInfo {
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1)
    $gpus = @()
    $nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($nvidiaSmi) { $gpus = (& nvidia-smi --query-gpu=name --format=csv,noheader) }
    $svtVer = (& $svtApp --version 2>&1 | Select-Object -First 1)
    $ffVer  = (& $ffmpeg -hide_banner -version | Select-Object -First 1)
    return [ordered]@{
        cpu        = $cpu.Name.Trim()
        cores      = "$($cpu.NumberOfCores)C/$($cpu.NumberOfLogicalProcessors)T"
        gpus       = ($gpus -join "; ")
        svt        = $svtVer
        ffmpeg     = $ffVer
        os         = [System.Environment]::OSVersion.VersionString
        recordedAt = (Get-Date).ToString("o")
    }
}

function Encode-Svt([string]$clip, [string]$outIvf, [string]$workDir, [int]$preset, [int]$kbps) {
    # Mesma string de video params do motor av1an do app: --keyint 240 --scd 0 --preset N --rc 1 --tbr N (2-pass)
    # GOTCHA PS 5.1: com $ErrorActionPreference=Stop, stderr redirecionado de processo nativo
    # vira NativeCommandError na PRIMEIRA linha (o banner "Svt[info]" derrubaria o encode).
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $y4m = Join-Path $workDir "input.y4m"
        & $ffmpeg -hide_banner -loglevel error -y -i $clip -f yuv4mpegpipe -strict -1 -- $y4m 2> (Join-Path $workDir "y4m.log")
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg nao gerou y4m de $clip (veja y4m.log)" }

        $log = Join-Path $workDir "svt.log"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Push-Location $workDir   # stat file padrao do 2-pass fica no dir temporario do clipe
        try {
            & $svtApp -i $y4m -b $outIvf --keyint 240 --scd 0 --preset $preset --rc 1 --tbr $kbps --passes 2 2> $log
            $exit = $LASTEXITCODE
        } finally { Pop-Location }
        $sw.Stop()
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($exit -ne 0 -or -not (Test-Path $outIvf)) { throw "SvtAv1EncApp falhou (exit $exit); log: $log" }

    $avgFps = $null
    if (Test-Path $log) {
        $m = Select-String -Path $log -Pattern "Average speed:\s*([\d.]+)" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($m) { $avgFps = [double]$m.Matches[0].Groups[1].Value }
    }
    return @{ wallSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 2); svtAvgFps = $avgFps }
}

function Encode-Nvenc([string]$clip, [string]$outMp4, [int]$kbps) {
    # Mesma derivacao VBR do app (memoria 22/09): media + maxrate 2,4x + bufsize 4,8x
    $log = Join-Path $tmpDir "nvenc_$([System.IO.Path]::GetFileNameWithoutExtension($clip)).log"
    $maxrate = [int]($kbps * 2.4); $bufsize = [int]($kbps * 4.8)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        & $ffmpeg -hide_banner -loglevel error -y -i $clip `
            -c:v av1_nvenc -preset p7 -rc vbr -b:v "$($kbps)k" -maxrate "$($maxrate)k" -bufsize "$($bufsize)k" `
            -an -sn -dn -- $outMp4 2> $log
        $exit = $LASTEXITCODE
        $sw.Stop()
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($exit -ne 0 -or -not (Test-Path $outMp4)) {
        $err = if (Test-Path $log) { (Get-Content $log -Raw) } else { "" }
        if ($err -match "minimum required Nvidia driver|does not support the required nvenc") {
            return @{ skipped = "driver NVIDIA < 610 / API 13.1 (atualizar driver destrava)" }
        }
        throw "av1_nvenc falhou: $err"
    }
    return @{ wallSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 2) }
}

$rows = @()
foreach ($clipInfo in $manifest.clips) {
    $clip = Join-Path $ClipsDir (Split-Path $clipInfo.file -Leaf)
    if (-not (Test-Path $clip)) { Write-Warning "Clipe ausente: $clip"; continue }
    $frames = [int64]$clipInfo.frames

    foreach ($engine in $engineList) {
        $base = "$($clipInfo.id)_$engine"
        Write-Host "== $engine / $($clipInfo.id) ==" -ForegroundColor Cyan
        $enc = $null
        $dist = $null
        try {
            switch -Wildcard ($engine) {
                "svt_p*_500k_2pass" {
                    $preset = [int]($engine -replace "^svt_p(\d+)_.*$", '$1')
                    $outIvf = Join-Path $OutDir "$base.ivf"
                    $work   = Join-Path $tmpDir $base
                    New-Item -ItemType Directory -Force -Path $work | Out-Null
                    if ($Reuse -and (Test-Path $outIvf)) {
                        $enc = @{ wallSeconds = 0; reused = $true }
                    } else {
                        $enc = Encode-Svt $clip $outIvf $work $preset 500
                    }
                    $dist = $outIvf
                }
                "nvenc_p*_500k" {
                    $outMp4 = Join-Path $OutDir "$base.mp4"
                    if ($Reuse -and (Test-Path $outMp4)) {
                        $enc = @{ wallSeconds = 0; reused = $true }
                    } else {
                        $enc = Encode-Nvenc $clip $outMp4 500
                    }
                    $dist = $outMp4
                }
                default { throw "Engine desconhecida: $engine" }
            }
        } catch {
            Write-Warning "falhou: $_"
            $rows += [ordered]@{ clip = $clipInfo.id; engine = $engine; error = "$_" }
            continue
        }
        if ($enc.skipped) {
            Write-Host "   PULADO: $($enc.skipped)" -ForegroundColor Yellow
            $rows += [ordered]@{ clip = $clipInfo.id; engine = $engine; skipped = $enc.skipped }
            continue
        }

        $vmafJson = Join-Path $OutDir "$base.vmaf.json"
        & (Join-Path $PSScriptRoot "measure-vmaf.ps1") -Ref $clip -Dist $dist -OutJson $vmafJson | Out-Null
        $vmaf = Get-Content $vmafJson -Raw | ConvertFrom-Json

        $row = [ordered]@{
            clip         = $clipInfo.id
            engine       = $engine
            wallSeconds  = if ($enc.wallSeconds -gt 0) { $enc.wallSeconds } else { $null }
            fpsEfetivo   = if ($enc.wallSeconds -gt 0) { [math]::Round($frames / $enc.wallSeconds, 2) } else { $null }
            kbps         = Get-Kbps $dist
            sizeBytes    = (Get-Item $dist).Length
            vmaf         = $vmaf.vmaf
            ssim         = $vmaf.ssim
            psnr         = $vmaf.psnr
        }
        if ($enc.reused) { $row.reused = $true }
        if ($enc.svtAvgFps) { $row.svtAvgFps = $enc.svtAvgFps }
        $rows += $row
        Write-Host ("   {0}s | {1} fps | {2} kbps | VMAF {3} | SSIM {4}" -f $row.wallSeconds, $row.fpsEfetivo, $row.kbps, $row.vmaf, $row.ssim) -ForegroundColor Green
    }
}

$stamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
$resultDoc = [ordered]@{
    label     = $Label
    clipset   = $manifest.clipset
    clipsetHash = (Get-FileHash -Algorithm SHA256 $ClipsJson).Hash.ToLowerInvariant()
    env       = Get-EnvInfo
    results   = $rows
}
$jsonPath = Join-Path $ResultsDir "$Label-$stamp.json"
$resultDoc | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $jsonPath

# Tabela MD legivel
$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# $Label - $stamp")
[void]$md.AppendLine()
[void]$md.AppendLine("| clipe | engine | tempo (s) | fps | kbps | VMAF | SSIM | PSNR |")
[void]$md.AppendLine("|---|---|---|---|---|---|---|---|")
foreach ($r in $rows) {
    if ($r.skipped) { [void]$md.AppendLine("| $($r.clip) | $($r.engine) | PULADO: $($r.skipped) | | | | | |"); continue }
    if ($r.error)   { [void]$md.AppendLine("| $($r.clip) | $($r.engine) | ERRO: $($r.error) | | | | | |"); continue }
    [void]$md.AppendLine("| $($r.clip) | $($r.engine) | $($r.wallSeconds) | $($r.fpsEfetivo) | $($r.kbps) | $($r.vmaf) | $($r.ssim) | $($r.psnr) |")
}
$mdPath = Join-Path $ResultsDir "$Label-$stamp.md"
$md.ToString() | Set-Content -Encoding UTF8 $mdPath

if ($Freeze) {
    New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot "lab\baselines") | Out-Null
    Copy-Item $jsonPath (Join-Path $repoRoot "lab\baselines\$Label.json") -Force
    Copy-Item $mdPath   (Join-Path $repoRoot "lab\baselines\$Label.md") -Force
    Write-Host "BASELINE CONGELADO em lab\baselines\$Label.{json,md}" -ForegroundColor Magenta
}

Write-Host "`nResultados: $jsonPath"
Get-Content $mdPath | Write-Output