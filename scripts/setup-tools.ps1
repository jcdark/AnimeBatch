# Popula a pasta tools\ com os binários externos que o app embute.
# Uso:  powershell -File scripts\setup-tools.ps1
# Origens: instalações locais conhecidas (MKVToolNix, HandBrake) e download do ffmpeg BtbN.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root "tools"
New-Item -ItemType Directory -Force -Path $tools | Out-Null

function Copy-IfFound($file, $candidates) {
    if (Test-Path (Join-Path $tools $file)) { Write-Host "ok (já existe): $file"; return }
    foreach ($c in $candidates) {
        if (Test-Path $c) { Copy-Item $c (Join-Path $tools $file); Write-Host "copiado: $file  <-  $c"; return }
    }
    Write-Warning "NÃO ENCONTRADO: $file (procurei em: $($candidates -join '; '))"
}

Copy-IfFound "mkvmerge.exe"     @("C:\Program Files\MKVToolNix\mkvmerge.exe")
Copy-IfFound "mkvextract.exe"   @("C:\Program Files\MKVToolNix\mkvextract.exe")
Copy-IfFound "HandBrakeCLI.exe" @("C:\Program Files\HandBrake\HandBrakeCLI.exe")

if (-not (Test-Path (Join-Path $tools "ffmpeg.exe"))) {
    Write-Host "Baixando ffmpeg (BtbN win64-gpl)…"
    $zip = Join-Path $env:TEMP "ffmpeg-btbn.zip"
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip" -OutFile $zip
    $dest = Join-Path $env:TEMP "ffmpeg-btbn"
    Expand-Archive $zip $dest -Force
    $exe = Get-ChildItem $dest -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    Copy-Item $exe.FullName (Join-Path $tools "ffmpeg.exe")
    Copy-Item (Join-Path $exe.Directory.FullName "ffprobe.exe") (Join-Path $tools "ffprobe.exe")
    Write-Host "copiado: ffmpeg.exe, ffprobe.exe"
} else { Write-Host "ok (já existe): ffmpeg.exe/ffprobe.exe" }

# Upscale (estágio M4): cada ferramenta vai pra subpasta própria porque precisa das
# pastas de modelos ao lado do exe. O ToolsLocator enxerga exes em subpastas de tools\.

function Install-GitHubTool($repo, $assetPattern, $destName) {
    $dest = Join-Path $tools $destName
    $existing = $null
    if (Test-Path $dest) {
        $existing = Get-ChildItem $dest -Recurse -Filter "$destName-ncnn-vulkan.exe" | Select-Object -First 1
    }
    if ($existing) { Write-Host "ok (já existe): $destName"; return }

    Write-Host "Baixando $destName ($repo)…"
    # Release "latest" pode não ter binários — varre todos os releases até achar o asset
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases?per_page=100"
    $asset = $null
    foreach ($rel in $releases) {
        $asset = $rel.assets | Where-Object { $_.name -like $assetPattern } | Select-Object -First 1
        if ($asset) { break }
    }
    if (-not $asset) { throw "Asset não encontrado em $repo (padrão: $assetPattern)" }
    $zip = Join-Path $env:TEMP $asset.name
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
    $extract = Join-Path $env:TEMP ([IO.Path]::GetFileNameWithoutExtension($asset.name))
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive $zip $extract -Force
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    # zip pode vir com pasta interna ou não — copia exe + pastas de modelos dos dois jeitos
    Get-ChildItem $extract -Recurse -Include "*.exe", "models*", "*.param", "*.bin" |
        ForEach-Object {
            $relPath = $_.FullName.Substring($extract.Length).TrimStart('\')
            $target = Join-Path $dest $relPath
            New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
            Copy-Item $_.FullName $target -Force
        }
    Write-Host "instalado: $destName"
}

Install-GitHubTool "nihui/realcugan-ncnn-vulkan"  "*windows.zip" "realcugan"
Install-GitHubTool "xinntao/Real-ESRGAN"          "realesrgan-ncnn-vulkan*windows.zip" "realesrgan"

Write-Host "`nConcluído. Ferramentas em: $tools"
