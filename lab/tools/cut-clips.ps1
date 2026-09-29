# Laboratorio AV1 hibrido - corta o clipset de benchmark a partir dos originais.
# Os arquivos de origem ficam SOMENTE em clips.local.json (gitignored); o manifest
# publico (clips.json) guarda apenas metadados tecnicos (repo e publico no GitHub).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\cut-clips.ps1 [-SourceRoot F:\Dublados] [-Count 9] [-Seconds 45]
# NOTA: 100% ASCII - PowerShell 5.1 le .ps1 sem BOM como ANSI (mesma licao do make-icon.ps1).
param(
    [string]$SourceRoot = "F:\Dublados",
    [string]$OutDir = "",
    [int]$Count = 9,
    [int]$Seconds = 45,
    [int]$Seed = 42,
    [int]$MovieClips = 1
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg = Join-Path $repoRoot "tools\ffmpeg.exe"
$ffprobe = Join-Path $repoRoot "tools\ffprobe.exe"
if (-not $OutDir) { $OutDir = Join-Path $repoRoot "lab\clips" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Probe([string]$file, [string]$what) {
    $v = & $ffprobe -v error -select_streams v:0 -show_entries "stream=$what" -of csv=p=0 -- $file 2>$null
    return ($v | Select-Object -First 1)
}

# Duração real do CONTAINER (em MKV o stream=duration costuma ser N/A)
function ProbeDuration([string]$file) {
    return (& $ffprobe -v error -show_entries "format=duration" -of csv=p=0 -- $file | Select-Object -First 1)
}

# Contagem de frames; MKV/IVF frequentemente têm stream=nb_frames = N/A -> decodifica e conta
function ProbeFrames([string]$file) {
    $v = Probe $file "nb_frames"
    $n = 0L
    if ($v -and [int64]::TryParse($v, [ref]$n) -and $n -gt 0) { return $n }
    $v = & $ffprobe -v error -select_streams v:0 -count_frames -show_entries "stream=nb_read_frames" -of csv=p=0 -- $file
    if ([int64]::TryParse(($v | Select-Object -First 1), [ref]$n)) { return $n }
    return 0L
}

# 1. Levanta todos os vídeos agrupados por pasta (1 clipe por pasta = diversidade)
$all = Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -Include *.mkv,*.mp4,*.avi -ErrorAction SilentlyContinue
if (-not $all -or $all.Count -eq 0) { throw "Nenhum video encontrado em $SourceRoot" }
$byFolder = $all | Group-Object { $_.DirectoryName }

$rand = [System.Random]::new($Seed)
function Shuffle([object[]]$items) {
    $a = @($items)
    for ($i = $a.Count - 1; $i -gt 0; $i--) {
        $j = $rand.Next($i + 1)
        $tmp = $a[$i]; $a[$i] = $a[$j]; $a[$j] = $tmp
    }
    return $a
}

$movieFolders  = @(Shuffle ($byFolder | Where-Object { $_.Name -match "\\Filmes\\" }))
$seriesFolders = @(Shuffle ($byFolder | Where-Object { $_.Name -notmatch "\\Filmes\\" }))

$picks = @()
if ($MovieClips -gt 0 -and $movieFolders.Count -gt 0) {
    $picks += $movieFolders | Select-Object -First $MovieClips | ForEach-Object { $_.Group[0] }
}
$remaining = $Count - $picks.Count
if ($remaining -gt 0) {
    $picks += ($seriesFolders | ForEach-Object { $_.Group[0] } | Select-Object -First $remaining)
}

# 2. Corta cada clipe (perda zero, sem audio - o baseline mede so video)
$locals = @()
$public = @()
$idx = 0
foreach ($src in $picks) {
    $idx++
    $id = "clip{0:D2}" -f $idx
    $durStr = ProbeDuration $src.FullName
    $dur = 0.0
    if (-not [double]::TryParse($durStr, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$dur)) { continue }
    if ($dur -lt ($Seconds * 2 + 30)) { continue }  # arquivo curto demais para cortar depois do OP
    $start = [math]::Round($dur * 0.35, 1)
    $class = if ($src.DirectoryName -match "\\Filmes\\") { "filme" } else { "serie" }
    $outFile = Join-Path $OutDir "$id.mkv"

    & $ffmpeg -hide_banner -loglevel error -y `
        -ss $start -i $src.FullName -t $Seconds `
        -map 0:v:0 -an -sn -dn `
        -c:v libx264 -qp 0 -pix_fmt yuv420p -- $outFile
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outFile)) { Write-Warning "Falha ao cortar $id de $($src.Name)"; continue }

    $w = Probe $outFile "width"; $h = Probe $outFile "height"
    $fps = Probe $outFile "avg_frame_rate"
    $nb  = ProbeFrames $outFile
    if ($nb -le 0) { Write-Warning "Clipe $id sem frames validos"; Remove-Item $outFile -ErrorAction SilentlyContinue; continue }
    $hash = (Get-FileHash -Algorithm SHA256 $outFile).Hash.ToLowerInvariant()

    $locals += [ordered]@{ id = $id; source = $src.FullName; sourceTitle = $src.BaseName; startSeconds = $start; durationSeconds = $Seconds; class = $class }
    $public  += [ordered]@{ id = $id; file = "clips/$id.mkv"; class = $class; width = [int]$w; height = [int]$h; fps = $fps; frames = [int64]$nb; sha256 = $hash }
    Write-Host "ok $id <- $($src.Name) @ ${start}s ($($w)x$($h) @$fps, $nb frames)"
}

if ($public.Count -eq 0) { throw "Nenhum clipe foi gerado" }

# 3. Manifests: local (com caminhos, gitignored) e publico (só metadados)
$localPath = Join-Path $repoRoot "lab\clips.local.json"
$pubPath   = Join-Path $repoRoot "lab\clips.json"
$locals | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $localPath
[ordered]@{
    clipset   = "animebatch-lab-v1"
    seed      = $Seed
    sourceRootHint = "originais dublados (espelho local; caminhos em clips.local.json, nao versionado)"
    generatedAt    = (Get-Date).ToString("o")
    clips          = $public
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $pubPath

Write-Host "`nManifests: $localPath (local) e $pubPath (publico)"
Write-Host "$($public.Count) clipes em $OutDir"