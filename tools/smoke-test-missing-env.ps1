# Regression test for the start-up gate: on a machine without Node/dsh the window must show the
# environment checklist and must NOT try to start a server or initialise WebView2.
#
# The "clean machine" is simulated with a stripped PATH plus --path-only (which disables the
# known-install-location search). Observable proof that the gate fired:
#   1. a failing report lands in env-check.txt,
#   2. no node child process is spawned,
#   3. the WebView2 user data folder is never created (we never reached WebView2 init).
#
# Every step is appended to tools\gate-test.log so a run stays diagnosable even if the console
# output never reaches the caller.
#
# Usage: pwsh -NoProfile -File tools\smoke-test-missing-env.ps1
param(
    [string]$Exe,
    [int]$SettleSeconds = 8
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'bin\Release\net10.0-windows\DshDesktop.exe' }
$cfgDir = Join-Path $env:LOCALAPPDATA 'DeepSeekHarness'
$reportPath = Join-Path $cfgDir 'env-check.txt'
$webViewDir = Join-Path $cfgDir 'WebView2'
$logPath = Join-Path $PSScriptRoot 'gate-test.log'

Remove-Item $logPath -Force -ErrorAction SilentlyContinue
function Write-Trace([string]$Message) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message
    Write-Host $line
    Add-Content -Path $logPath -Value $line -Encoding UTF8
}

if (-not (Test-Path $Exe)) { throw "找不到 $Exe，请先 dotnet build -c Release" }
Write-Trace "开始 Exe=$Exe"

# A leftover instance would hold the single-instance mutex, so the new window would just show
# "already running" and exit - which would look like a pass. Clear the field first.
foreach ($stale in @(Get-Process DshDesktop -ErrorAction SilentlyContinue)) {
    Write-Trace "发现残留实例 pid=$($stale.Id)，先结束它"
    Stop-Process -Id $stale.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 1

Remove-Item $reportPath -Force -ErrorAction SilentlyContinue
Remove-Item $webViewDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Trace "清理后 报告存在=$(Test-Path $reportPath) WebView2目录存在=$(Test-Path $webViewDir)"

$baselineWebView = (Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Trace "基线 msedgewebview2 进程数=$baselineWebView"

Write-Trace '以剥离的 PATH 启动窗口（cmd /c 才能给子进程改 PATH）'
$launcher = Start-Process cmd.exe -NoNewWindow -PassThru -ArgumentList '/c',
    "set `"PATH=C:\Windows\System32`" && `"$Exe`" --path-only"

$deadline = (Get-Date).AddSeconds(30)
$app = $null
while ((Get-Date) -lt $deadline -and -not $app) {
    Start-Sleep -Milliseconds 500
    $app = Get-Process DshDesktop -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $app) {
    Write-Trace '失败：窗口进程没有起来'
    if (-not $launcher.HasExited) { Stop-Process -Id $launcher.Id -Force }
    exit 1
}
Write-Trace "窗口进程已启动 pid=$($app.Id)"

Start-Sleep -Seconds $SettleSeconds

$nodeChildren = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($app.Id)" |
    Where-Object { $_.Name -eq 'node.exe' })
$webViewCount = (Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Measure-Object).Count
$appAlive = [bool](Get-Process -Id $app.Id -ErrorAction SilentlyContinue)
$reportExists = Test-Path $reportPath
$webViewDirExists = Test-Path $webViewDir

$check1 = $reportExists
$check2 = $nodeChildren.Count -eq 0
$check3 = -not $webViewDirExists
$check4 = $webViewCount -eq $baselineWebView

Write-Trace "检查1 失败报告已写出       = $check1"
Write-Trace "检查2 没有 node 子进程     = $check2 (子进程数 $($nodeChildren.Count))"
Write-Trace "检查3 WebView2 未初始化    = $check3 (数据目录存在=$webViewDirExists)"
Write-Trace "检查4 未新增 WebView2 进程 = $check4 (现在 $webViewCount 基线 $baselineWebView)"
Write-Trace "窗口仍存活                 = $appAlive"

if ($reportExists) {
    Write-Trace '--- 报告内容开始 ---'
    foreach ($line in (Get-Content $reportPath)) { Write-Trace "    $line" }
    Write-Trace '--- 报告内容结束 ---'
}

$pass = $check1 -and $check2 -and $check3 -and $check4 -and $appAlive

Write-Trace '关闭窗口'
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
if (-not $launcher.HasExited) { Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue }

if ($pass) {
    Write-Trace '通过：闸门按预期拦住，没有启动服务也没有初始化 WebView2'
    exit 0
}
Write-Trace '失败：闸门行为不符合预期'
exit 1
