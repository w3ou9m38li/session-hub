# L1 measurement: process start -> main window handle available.
# This is the launcher's own metric only. It says nothing about the official
# Claude client, which is measured separately and is NOT claimed to be faster.
param([int]$Runs = 6)

$exe = 'D:\ClaudeLauncher\artifacts\win-x64\ClaudeLauncher.App.exe'
if (-not (Test-Path $exe)) { throw "未找到产物：$exe" }

# The single-instance mutex means a stale process would turn the next run into an
# activation instead of a cold start, so clear it first.
Get-Process ClaudeLauncher.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$samples = @()
for ($r = 1; $r -le $Runs; $r++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $exe -PassThru
    $window = $null
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        Start-Sleep -Milliseconds 25
        $p.Refresh()
        if ($p.HasExited) { break }
        if ($p.MainWindowHandle -ne 0) { $window = $sw.Elapsed.TotalMilliseconds; break }
    }
    $sw.Stop()
    $ws = if ($p.HasExited) { 0 } else { $p.WorkingSet64 }
    $samples += [pscustomobject]@{
        run = $r
        first_window_ms = if ($null -ne $window) { [math]::Round($window) } else { $null }
        working_set_mb = [math]::Round($ws / 1MB, 1)
        result = if ($null -ne $window) { 'ok' } else { 'no-window' }
    }
    if (-not $p.HasExited) { $p.Kill() }
    Start-Sleep -Milliseconds 800   # let the mutex clear between cold starts
}

$samples | Format-Table -AutoSize
$ok = $samples | Where-Object result -eq 'ok' | Select-Object -ExpandProperty first_window_ms | Sort-Object
if ($ok.Count -gt 0) {
    $p50 = $ok[[math]::Floor(($ok.Count - 1) * 0.50)]
    $p95 = $ok[[math]::Floor(($ok.Count - 1) * 0.95)]
    ""
    "样本 {0} 次   p50 = {1} ms   p95 = {2} ms   目标 p95 <= 1500 ms" -f $ok.Count, $p50, $p95
    if ($p95 -le 1500) { "结论：达标" } else { "结论：未达标（如实记录，不调整目标）" }
    $samples | Export-Csv -Path 'D:\ClaudeLauncher\evidence\performance-l1.csv' -NoTypeInformation -Encoding UTF8
    "原始样本已写入 evidence\performance-l1.csv"
}
