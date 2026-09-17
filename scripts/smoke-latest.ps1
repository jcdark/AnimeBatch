$exe = "E:\AnimeBatch\dist\AnimeBatchV0.15\AnimeBatchV0.15.exe"
Start-Process $exe
$elapsed = 0
foreach ($t in @(15, 40)) {
    Start-Sleep -Seconds ($t - $elapsed)
    $elapsed = $t
    $p = Get-Process AnimeBatchV0.15 -ErrorAction SilentlyContinue
    if ($p) {
        Write-Host ("VIVO apos {0}s (respondendo: {1})" -f $t, $p.Responding)
    } else {
        Write-Host "MORREU antes de ${t}s"
        Get-Content "E:\AnimeBatch\dist\AnimeBatchV0.15\data\crash.log" -ErrorAction SilentlyContinue | Select-Object -First 12
        exit 1
    }
}
Write-Host "App aberto e estavel - deixando rodando pra voce ver"
