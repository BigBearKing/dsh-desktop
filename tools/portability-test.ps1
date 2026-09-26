# Portability probe for a "clean machine" bundle: does dsh boot from a copied node + dsh tree,
# with a brand-new DSH_HOME (profile stub only, no node_modules junctions) and a stripped PATH?
#
# Usage: pwsh -NoProfile -File tools\portability-test.ps1 [-Keep]
param([switch]$Keep)

$ErrorActionPreference = 'Continue'
$bundle = Join-Path $env:TEMP 'dsh-bundle-test'
$testHome = Join-Path $env:TEMP 'dsh-home-test'
$nodeSrc = 'C:\Program Files\nodejs\node.exe'
$dshSrc = Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh'
$profileSrc = Join-Path $env:USERPROFILE '.dsh\profiles\web'

Write-Host '== 组装模拟 bundle =='
Remove-Item $bundle, $testHome -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'node'), (Join-Path $bundle 'harness\dsh'), (Join-Path $testHome 'profiles\web') | Out-Null

Copy-Item $nodeSrc (Join-Path $bundle 'node\node.exe') -Force
robocopy $dshSrc (Join-Path $bundle 'harness\dsh') /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
# Only the tiny profile stub: package.json / cordis.yml / cordis.patch.yml / pnpm-workspace.yaml.
Copy-Item (Join-Path $profileSrc '*') (Join-Path $testHome 'profiles\web') -Recurse -Force

$bundleSize = (Get-ChildItem $bundle -Recurse -File -Force | Measure-Object -Sum Length).Sum / 1MB
Write-Host ("   bundle = {0:N1} MB (node + dsh 树)" -f $bundleSize)
Write-Host "   DSH_HOME = $testHome （只有 profile 存根，没有 node_modules 软链）"

Write-Host '== 用剥离的 PATH 启动（模拟干净电脑：没有全局 npm、没有系统 node 可用）=='
$outFile = Join-Path $env:TEMP 'portability-test.out'
$errFile = Join-Path $env:TEMP 'portability-test.err'
Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = Join-Path $bundle 'node\node.exe'
$psi.ArgumentList.Add((Join-Path $bundle 'harness\dsh\lib\bin.js'))
$psi.ArgumentList.Add('web')
$psi.ArgumentList.Add('--no-open')
$psi.ArgumentList.Add('--port')
$psi.ArgumentList.Add('0')
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.WorkingDirectory = $testHome
$psi.Environment['DSH_HOME'] = $testHome
$psi.Environment['PATH'] = 'C:\Windows\System32'   # nothing else: no node, no npm, no pnpm
$psi.Environment['NO_COLOR'] = '1'

$proc = [System.Diagnostics.Process]::new()
$proc.StartInfo = $psi
$null = $proc.Start()
$stdout = $proc.StandardOutput.ReadToEndAsync()
$stderr = $proc.StandardError.ReadToEndAsync()

$url = $null
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and -not $url) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { break }
    $listening = Get-NetTCPConnection -OwningProcess $proc.Id -State Listen -ErrorAction SilentlyContinue
    if ($listening) { $url = "http://127.0.0.1:$(($listening | Select-Object -First 1).LocalPort)/" }
}

Write-Host "== 进程存活: $(-not $proc.HasExited) =="
if ($url) {
    Write-Host "== 已监听并自举成功: $url =="
    try {
        $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10
        Write-Host "== HTTP $($r.StatusCode), 含 DSH 标记 = $($r.Content.Contains('__ModuleLoader__')) =="
    } catch { Write-Host "== HTTP 探测失败: $($_.Exception.Message) ==" }

    Write-Host '== 自举后 DSH_HOME 里被创建了什么 =='
    Get-ChildItem $testHome -Force | Select-Object Name | Format-Table -AutoSize
    $createdNested = Test-Path (Join-Path $testHome 'profiles\node_modules')
    Write-Host "== profiles\node_modules 是否被重新创建（说明它需要 pnpm install）: $createdNested =="
    if ($createdNested) {
        Write-Host '   内容取样:'
        Get-ChildItem (Join-Path $testHome 'profiles\node_modules') -Force -ErrorAction SilentlyContinue |
            Select-Object -First 6 Name, LinkType | Format-Table -AutoSize
    }
} else {
    Write-Host '== 未能自举 =='
}

if (-not $proc.HasExited) { $proc.Kill($true) }
$proc.WaitForExit(8000)
Write-Host "`n== STDOUT =="
$o = $stdout.Result; if ($o) { $o.Substring(0, [Math]::Min(3000, $o.Length)) } else { '(空)' }
Write-Host "`n== STDERR =="
$e = $stderr.Result; if ($e) { $e.Substring(0, [Math]::Min(3000, $e.Length)) } else { '(空)' }

if (-not $Keep) {
    Remove-Item $bundle, $testHome -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    Write-Host '== 已清理临时目录 =='
} else {
    Write-Host "== 保留临时目录：$bundle / $testHome =="
}

