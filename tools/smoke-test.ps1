# End-to-end smoke test for the DSH desktop shell.
#  1. forces the "spawn my own dsh web" path (ephemeral port),
#  2. checks the server answers and the WebView2 window actually navigated to it (via CDP),
#  3. hard-kills the window and checks the node child died with it.
# Usage: pwsh -NoProfile -File tools\smoke-test.ps1 [-Configuration Release]
param(
    [string]$Configuration = 'Release',
    [int]$CdpPort = 9223
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "bin\$Configuration\net10.0-windows\DshDesktop.exe"
$cfgDir = Join-Path $env:LOCALAPPDATA 'DeepSeekHarness'

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds = 60, [string]$Label = 'condition')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 700
    }
    Write-Host "  [超时] $Label"
    return $null
}

if (-not (Test-Path $exe)) { throw "找不到 $exe，请先 dotnet build -c $Configuration" }

Write-Host "== 准备干净的首次启动状态 =="
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
Remove-Item (Join-Path $cfgDir 'state.json') -Force -ErrorAction SilentlyContinue
# ReuseExistingServer=false so the shell must spawn and own its own server.
# The workspace is derived, not hardcoded, so this script stays machine-independent.
$testWorkspace = $root
$testConfig = [ordered]@{
    WorkspaceDirectory   = $testWorkspace
    Port                 = 0
    ReuseExistingServer  = $false
    ExistingServerPort   = 3080
} | ConvertTo-Json -Compress
Set-Content (Join-Path $cfgDir 'config.json') $testConfig -Encoding UTF8

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"
$app = Start-Process -FilePath $exe -PassThru
Write-Host "== 启动窗口 pid=$($app.Id) =="

$node = Wait-Until -TimeoutSeconds 75 -Label 'node 子进程出现' -Condition {
    $kids = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($app.Id)"
    $kids | Where-Object { $_.Name -eq 'node.exe' } | Select-Object -First 1
}

if (-not $node) {
    Write-Host "== 失败：窗口没有拉起 dsh 服务 =="
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    exit 1
}
Write-Host "== node 子进程 pid=$($node.ProcessId) =="
Write-Host "   $($node.CommandLine)"

$port = Wait-Until -TimeoutSeconds 45 -Label '监听端口出现' -Condition {
    (Get-NetTCPConnection -OwningProcess $node.ProcessId -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1).LocalPort
}
if (-not $port) {
    Write-Host "== 失败：子进程没有监听端口 =="
    Stop-Process -Id $app.Id -Force
    exit 1
}
Write-Host "== 私有服务端口 = $port （启动参数是 --port 0，说明端口是自动分配的）=="

$served = Wait-Until -TimeoutSeconds 30 -Label 'HTTP 200 + DSH 标记' -Condition {
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 6
        if ($r.StatusCode -eq 200 -and $r.Content.Contains('__ModuleLoader__')) { $true } else { $false }
    } catch { $false }
}
Write-Host "== HTTP 就绪: $([bool]$served) =="

$page = Wait-Until -TimeoutSeconds 45 -Label 'WebView2 页面目标出现' -Condition {
    try {
        $json = Invoke-WebRequest "http://127.0.0.1:$CdpPort/json" -UseBasicParsing -TimeoutSec 6 |
            Select-Object -ExpandProperty Content | ConvertFrom-Json
        $json | Where-Object { $_.type -eq 'page' -and $_.url -like "*:$port*" } | Select-Object -First 1
    } catch { $null }
}
if ($page) {
    Write-Host "== WebView2 已加载窗口页面 =="
    Write-Host "   title: $($page.title)"
    Write-Host "   url  : $($page.url)"
} else {
    Write-Host "== 警告：没有在 WebView2 里看到指向 $port 的页面 =="
}

Write-Host "== WebView2 渲染进程数: $((Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Measure-Object).Count) =="
$geometry = Get-Content (Join-Path $cfgDir 'state.json') -Raw -ErrorAction SilentlyContinue
Write-Host "== state.json: $($geometry -replace '\s+', ' ') =="

Write-Host "== 硬杀窗口进程（模拟任务管理器结束进程），检查子进程是否被连带清理 =="
$nodePid = $node.ProcessId
Stop-Process -Id $app.Id -Force
Start-Sleep -Seconds 8
if (Get-Process -Id $nodePid -ErrorAction SilentlyContinue) {
    Write-Host "== 失败：node $nodePid 仍在运行（出现孤儿进程）=="
    Stop-Process -Id $nodePid -Force
    exit 1
}
Write-Host "== OK：node $nodePid 已随窗口退出，无孤儿进程 =="

# Leave the machine as we found it: drop the test config so the app regenerates its
# commented default template on the next real launch.
Remove-Item (Join-Path $cfgDir 'config.json') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $cfgDir 'state.json') -Force -ErrorAction SilentlyContinue
Write-Host "== 已清理测试用 config.json / state.json =="
