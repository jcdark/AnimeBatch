# Smoke test do release mais recente em dist\: abre o exe e confere que o processo
# continua vivo (e respondendo) apos 15s e 40s. Em caso de morte, mostra o crash.log.
# Uso:  powershell -File scripts\smoke-latest.ps1
# Nota: apenas ASCII neste arquivo - PowerShell 5.1 le .ps1 UTF-8 sem BOM como ANSI.

$ErrorActionPreference = "Stop"
$dist = Join-Path $PSScriptRoot "..\dist"

$latest = Get-ChildItem $dist -Directory -Filter "AnimeBatchV*" |
    Sort-Object Name -Descending |
    Select-Object -First 1
if (-not $latest) { throw "Nenhum release em dist\AnimeBatchV* - rode scripts\make-release.ps1 antes." }

$exeName = $latest.Name  # o exe tem o MESMO nome da pasta (AssemblyName versionado)
$exe = Join-Path $latest.FullName "$exeName.exe"
if (-not (Test-Path $exe)) { throw "Sem $exeName.exe em $($latest.FullName)" }

Write-Host "Smoke: $exe"
Start-Process $exe
$elapsed = 0
foreach ($t in @(15, 40)) {
    Start-Sleep -Seconds ($t - $elapsed)
    $elapsed = $t
    $p = Get-Process $exeName -ErrorAction SilentlyContinue
    if ($p) {
        Write-Host ("VIVO apos {0}s (respondendo: {1})" -f $t, $p.Responding)
    } else {
        Write-Host "MORREU antes de ${t}s"
        Get-Content (Join-Path $latest.FullName "data\crash.log") -ErrorAction SilentlyContinue |
            Select-Object -First 12
        exit 1
    }
}
Write-Host "App aberto e estavel - deixando rodando pra voce ver"
