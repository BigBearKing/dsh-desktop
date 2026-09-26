# Regression test for the About window, driven through real Win32 messages:
#   1. the title-bar system menu must contain the "关于" entry we appended,
#   2. sending that command id must actually open an About window in the same process,
#   3. closing it must leave the main window alive.
#
# Usage: pwsh -NoProfile -File tools\smoke-test-about.ps1 [-Exe <path>]
param(
    [string]$Exe
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'bin\Release\net10.0-windows\DshDesktop.exe' }

if (-not (Test-Path $Exe)) { throw "找不到 $Exe，请先 dotnet build -c Release" }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class AboutProbe
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int cchMax, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_CLOSE = 0x0010;
    public const uint MF_BYPOSITION = 0x400;

    public static List<IntPtr> Handles(uint pid, bool visibleOnly)
    {
        var result = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid && (!visibleOnly || IsWindowVisible(h))) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string Title(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<string> SystemMenuItems(IntPtr hWnd)
    {
        var items = new List<string>();
        var menu = GetSystemMenu(hWnd, false);
        if (menu == IntPtr.Zero) return items;
        int count = GetMenuItemCount(menu);
        for (uint i = 0; i < count; i++)
        {
            var sb = new StringBuilder(256);
            int n = GetMenuStringW(menu, i, sb, sb.Capacity, MF_BYPOSITION);
            items.Add(n > 0 ? sb.ToString() : "(separator)");
        }
        return items;
    }

    public static string FindTitle(uint pid, string contains)
    {
        foreach (var h in Handles(pid, true))
        {
            var title = Title(h);
            if (title.Contains(contains)) return title;
        }
        return null;
    }
}
'@

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds = 40, [string]$Label = 'condition')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "  [超时] $Label"
    return $null
}

foreach ($stale in @(Get-Process DshDesktop -ErrorAction SilentlyContinue)) {
    Write-Host "结束残留实例 pid=$($stale.Id)"
    Stop-Process -Id $stale.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 1

Write-Host '== 启动窗口 =='
$app = Start-Process -FilePath $Exe -PassThru
$main = Wait-Until -TimeoutSeconds 45 -Label '主窗口出现' -Condition {
    $h = [AboutProbe]::Handles([uint32]$app.Id, $true) | Where-Object { [AboutProbe]::Title($_) -eq 'DeepSeek Harness' } | Select-Object -First 1
    if ($h) { $h }
}
if (-not $main) {
    Write-Host '== 失败：没找到主窗口 =='
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "主窗口标题: $([AboutProbe]::Title($main))  (hwnd=$main)"

Write-Host ''
Write-Host '检查 1  标题栏系统菜单里有「关于」'
$menuItems = [AboutProbe]::SystemMenuItems($main)
Write-Host "  系统菜单项: $($menuItems -join ' | ')"
$hasAbout = ($menuItems | Where-Object { $_ -like '*关于*' }).Count -gt 0
Write-Host "  含「关于」: $hasAbout"

Write-Host ''
Write-Host '检查 2  发送 WM_SYSCOMMAND(0x1000) 能打开关于窗口'
$null = [AboutProbe]::PostMessageW($main, [AboutProbe]::WM_SYSCOMMAND, [IntPtr]0x1000, [IntPtr]::Zero)
$aboutTitle = Wait-Until -TimeoutSeconds 25 -Label '关于窗口出现' -Condition {
    [AboutProbe]::FindTitle([uint32]$app.Id, '关于')
}
if ($aboutTitle) {
    Write-Host "  关于窗口标题: $aboutTitle"
} else {
    Write-Host '  关于窗口没有出现'
}

Write-Host ''
Write-Host '检查 3  关闭关于窗口后主窗口仍然存活'
if ($aboutTitle) {
    $aboutHwnd = [AboutProbe]::Handles([uint32]$app.Id, $true) |
        Where-Object { [AboutProbe]::Title($_) -like '*关于*' } | Select-Object -First 1
    if ($aboutHwnd) { $null = [AboutProbe]::PostMessageW($aboutHwnd, [AboutProbe]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) }
    Start-Sleep -Seconds 2
}
$stillThere = [bool](Get-Process -Id $app.Id -ErrorAction SilentlyContinue)
$mainAlive = [AboutProbe]::FindTitle([uint32]$app.Id, 'DeepSeek Harness') -ne $null
Write-Host "  进程存活=$stillThere  主窗口还在=$mainAlive"

Write-Host ''
Write-Host '== 收尾：关闭窗口 =='
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue

$pass = $hasAbout -and ($null -ne $aboutTitle) -and $stillThere -and $mainAlive
if ($pass) { Write-Host '== 通过：关于入口和窗口都正常 =='; exit 0 }
Write-Host '== 失败：关于功能不符合预期 =='
exit 1
