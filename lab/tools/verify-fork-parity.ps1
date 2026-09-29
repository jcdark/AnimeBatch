# Laboratorio AV1 hibrido - gate de paridade do fork instrumentado.
#
# Compara 3 builds MSVC no MESMO clip com --lp 1 (single MD thread = determinismo;
# com lp>1 o SVT-AV1 4.2.0 e nao-deterministico ate run-a-run, ver lab README):
#   1. pristine v4.2.0 (worktree sem patch)
#   2. fork com SVA_DUMP ausente
#   3. fork com SVA_DUMP=1
# Os tres streams precisam ser byte-a-byte identicos - prova de que o dump nao
# muda decisao nenhuma do encoder (regra do plano: algoritmo intocado).
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File lab\tools\verify-fork-parity.ps1
param(
    [string]$Clip = "",
    [string]$PristineExe = "E:\av1-refs\svt-av1-pristine\Bin\Release\SvtAv1EncApp.exe",
    [string]$ForkExe     = "E:\av1-refs\svt-av1\Bin\Release\SvtAv1EncApp.exe",
    [int]$Preset = 6,
    [int]$Kbps = 500
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$ffmpeg   = Join-Path $repoRoot "tools\ffmpeg.exe"
$stock    = Join-Path $repoRoot "tools\svt-av1\SvtAv1EncApp.exe"
if (-not $Clip) { $Clip = Join-Path $repoRoot "lab\clips\clip01.mkv" }
$tmp = Join-Path $repoRoot "lab\tmp\parity"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$y4m = Join-Path $tmp "input.y4m"
& $ffmpeg -hide_banner -loglevel error -y -i $Clip -f yuv4mpegpipe -strict -1 -- $y4m
if ($LASTEXITCODE -ne 0) { throw "y4m falhou" }

function Encode-Ivf([string]$workDir, [string]$exeDir, [bool]$dump) {
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null
    Copy-Item (Join-Path $exeDir "*") $workDir -Force
    $app = Join-Path $exeDir "SvtAv1EncApp.exe"
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        Push-Location $workDir   # stat file do 2-pass fica aqui
        if ($dump) { $env:SVA_DUMP = "1"; $env:SVA_DUMP_DIR = $workDir }
        & $app -i $y4m -b out.ivf --keyint 240 --scd 0 --preset $Preset `
            --rc 1 --tbr $Kbps --passes 2 --lp 1 2> enc.log
        if ($dump) { Remove-Item env:SVA_DUMP, env:SVA_DUMP_DIR -ErrorAction SilentlyContinue }
        Pop-Location
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $workDir "out.ivf"))) {
        throw "encode falhou em $workDir (veja enc.log)"
    }
    return (Get-FileHash -Algorithm SHA256 (Join-Path $workDir "out.ivf")).Hash.ToLowerInvariant()
}

$h1 = Encode-Ivf (Join-Path $tmp "pristine")  (Split-Path $PristineExe) $false
$h2 = Encode-Ivf (Join-Path $tmp "fork_off")  (Split-Path $ForkExe)     $false
$h3 = Encode-Ivf (Join-Path $tmp "fork_on")   (Split-Path $ForkExe)     $true
$dumpFiles = Get-ChildItem (Join-Path $tmp "fork_on") -Filter "sva_dump_*.bin"
$dumpMB = [math]::Round(($dumpFiles | Measure-Object Length -Sum).Sum / 1MB, 1)

Write-Host ("pristine 4.2.0 MSVC : " + $h1)
Write-Host ("fork dump=0         : " + $h2)
Write-Host ("fork dump=1         : " + $h3)
Write-Host ("dump gerado         : " + ($dumpFiles | Measure-Object).Count + " arquivos / " + $dumpMB + " MB")

if ($h1 -ne $h2) { Write-Host "FALHOU: o patch altera o stream mesmo SEM dump" -ForegroundColor Red; exit 1 }
if ($h2 -ne $h3) { Write-Host "FALHOU: o dump altera o stream" -ForegroundColor Red; exit 1 }
Write-Host "PARIDADE OK: fork + dump produzem o mesmo stream byte a byte do pristine" -ForegroundColor Green
Write-Host "(nota: o stock de tools\ e build MSYS2/gcc - streams entre compiladores diferentes nao se comparam)"
