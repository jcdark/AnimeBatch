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

# Av1an (engine de encode opcional, codecs "AV1an"): o exe vem do release rolling
# "latest" (build MSVC nightly, precisa VapourSynth+Python na máquina) e o encoder
# SvtAv1EncApp vem do pacote MSYS2 do upstream (o repo oficial não publica Windows).
# Além disso a máquina precisa do VapourSynth + Python 3.13 (pip install do wheel que
# vem na pasta python\ do instalador do VS) — ver docs/arquitetura.md, seção Av1an.

if (-not (Test-Path (Join-Path $tools "av1an\av1an.exe"))) {
    Write-Host "Baixando av1an (rust-av/Av1an, release latest)…"
    $dest = Join-Path $tools "av1an"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri "https://github.com/rust-av/Av1an/releases/download/latest/av1an.exe" -OutFile (Join-Path $dest "av1an.exe")
    Write-Host "instalado: av1an\av1an.exe"
} else { Write-Host "ok (já existe): av1an\av1an.exe" }

if (-not (Test-Path (Join-Path $tools "svt-av1\SvtAv1EncApp.exe"))) {
    Write-Host "Baixando SvtAv1EncApp (pacote MSYS2 do SVT-AV1 upstream)…"
    $dest = Join-Path $tools "svt-av1"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $ProgressPreference = "SilentlyContinue"
    $pkg = Join-Path $env:TEMP "svt-av1-msys2.pkg.tar.zst"
    Invoke-WebRequest -Uri "https://repo.msys2.org/mingw/ucrt64/mingw-w64-ucrt-x86_64-svt-av1-4.2.0-1-any.pkg.tar.zst" -OutFile $pkg
    # .zst exige zstandard — usa o Python da máquina (o mesmo do VapourSynth)
    $tar = Join-Path $env:TEMP "svt-av1-msys2.tar"
    python -c "import sys, zstandard; d=zstandard.ZstdDecompressor(); d.copy_stream(open(sys.argv[1],'rb'), open(sys.argv[2],'wb'))" $pkg $tar
    if ($LASTEXITCODE -ne 0) { throw "zstandard indisponível: instale com 'python -m pip install zstandard' e rode de novo" }
    $extract = Join-Path $env:TEMP "svt-av1-msys2"
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    tar -xf $tar -C $extract
    Copy-Item (Join-Path $extract "ucrt64\bin\*") $dest -Force
    Write-Host "instalado: svt-av1\SvtAv1EncApp.exe"
} else { Write-Host "ok (já existe): svt-av1\SvtAv1EncApp.exe" }

Write-Host "`nConcluído. Ferramentas em: $tools"
