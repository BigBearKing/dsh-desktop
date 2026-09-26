# Verifies the "attach to an already-running dsh web" path and, critically, that a graceful
# window close does NOT kill a server the shell does not own.
# Usage: pwsh -NoProfile -File tools\smoke-test-attach.ps1 -Exe <path> [-ServerPort 3080]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$ServerPort = 3080,
    [int]$CdpPort = 9224
)

$ErrorActionPreference = 'Continue'
$cfgDir = Join-Path $env:LOCALAPPDATA 'DeepSeekHarness'
$cfgPath = Join-Path $cfgDir 'config.json'
$statePath = Join-Path $cfgDir 'state.json'

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

$foreign = (Get-NetTCPConnection -LocalPort $ServerPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1).OwningProcess
if (-not $foreign) {
    Write-Host "!! 端口 $ServerPort 上没有运行中的 dsh，跳过复用路径测试"
    exit 2
}
$foreignProc = Get-Process -Id $foreign
Write-Host "== 外部服务：pid=$foreign ($($foreignProc.ProcessName)) on :$ServerPort =="

Write-Host "== 保证是首次启动（默认 ReuseExistingServer=true）=="
Remove-Item $cfgPath, $statePath -Force -ErrorAction SilentlyContinue

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$CdpPort"
$app = Start-Process -FilePath $Exe -PassThru
Write-Host "== 启动窗口 pid=$($app.Id) =="

$page = Wait-Until -TimeoutSeconds 75 -Label "WebView2 加载 :$ServerPort 的页面" -Condition {
    try {
        $json = Invoke-WebRequest "http://127.0.0.1:$CdpPort/json" -UseBasicParsing -TimeoutSec 6 |
            Select-Object -ExpandProperty Content | ConvertFrom-Json
        $json | Where-Object { $_.type -eq 'page' -and $_.url -like "*:$ServerPort*" } | Select-Object -First 1
    } catch { $null }
}

if ($page) {
    Write-Host "== 已复用现有实例并渲染成功 =="
    Write-Host "   title: $($page.title)"
    Write-Host "   url  : $($page.url)"
} else {
    Write-Host "== 失败：没有加载 :$ServerPort 的页面 =="
}

$spawned = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($app.Id)" |
    Where-Object { $_.Name -eq 'node.exe' }
if ($spawned) {
    Write-Host "== 失败：复用模式下仍然自起了服务进程：$($spawned.ProcessId -join ', ')"
} else {
    Write-Host "== OK：复用模式下没有另起服务进程（没有重复占用 ~/.dsh 存储）=="
}

Write-Host "== 生成的 config.json（应带注释的默认模板）=="
Get-Content $cfgPath -Raw -ErrorAction SilentlyContinue

Write-Host "== 优雅关窗（发 WM_CLOSE），验证生命周期 =="
$closed = $app.CloseMainWindow()
Write-Host "   CloseMainWindow 返回 $closed"
$exited = Wait-Until -TimeoutSeconds 40 -Label '窗口进程退出' -Condition { $app.HasExited }
Write-Host "== 窗口已退出: $([bool]$exited) =="

Start-Sleep -Seconds 4
if (Get-Process -Id $foreign -ErrorAction SilentlyContinue) {
    Write-Host "== OK：外部服务 pid=$foreign 仍然健在，关窗没有误杀别人的服务 =="
} else {
    Write-Host "== 失败：外部服务 pid=$foreign 被误杀了 =="
}

# Keep the generated default config (that is what a real first launch leaves behind),
# but drop the test window geometry so the first launch opens centred at the default size.
Remove-Item $statePath -Force -ErrorAction SilentlyContinue
Write-Host "== 已清掉测试留下的 state.json =="
