# Gera uma nova versao do AnimeBatch em dist\<nome>.
# Uso:  powershell -File scripts\make-release.ps1            (usa a Version do csproj)
#       powershell -File scripts\make-release.ps1 0.2
# Antes de rodar, suba a <Version> no AnimeBatch.App.csproj (o exe sai como AnimeBatchV<versao>.exe).
# Nota: apenas ASCII neste arquivo - PowerShell 5.1 le .ps1 UTF-8 sem BOM como ANSI.

param([string]$ReleaseVersion = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\AnimeBatch.App\AnimeBatch.App.csproj"

if (-not $ReleaseVersion) {
    $matchLine = Select-String -Path $csproj -Pattern '<Version>(.*?)</Version>' | Select-Object -First 1
    $ReleaseVersion = $matchLine.Matches[0].Groups[1].Value
}

$out = Join-Path $root "dist\AnimeBatchV$ReleaseVersion"
Write-Host "Publicando AnimeBatchV$ReleaseVersion -> $out"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish -c Release -p:Platform=x64 (Join-Path $root "src\AnimeBatch.App") -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }

if (-not (Test-Path (Join-Path $out "AnimeBatchV$ReleaseVersion.pri"))) {
    throw "PRI do app nao foi gerado - o publish ficou sem o XAML compilado"
}

robocopy (Join-Path $root "tools") (Join-Path $out "tools") /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy tools falhou ($LASTEXITCODE)" }

Write-Host ""
Write-Host "OK: $out\AnimeBatchV$ReleaseVersion.exe"
Write-Host "Teste antes de distribuir: abra o exe e veja data\crash.log em caso de problema."
