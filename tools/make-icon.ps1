# Builds app.ico from an SVG logo.
#
# The logo path uses elliptical arcs, which GDI+ cannot represent faithfully, so instead of
# hand-rolling a path parser we let the Edge that is already on the machine rasterise it:
# headless Edge renders the SVG natively at every icon size (crisper than downscaling one
# big render), then we pack those bitmaps into a multi-size .ico.
#
# Note on layout: each size gets its own page with the glyph absolutely positioned in whole
# pixels, and the <svg> carries explicit pixel width/height. Sizing the glyph with CSS
# percentages inside a flex container made headless Edge mis-lay-out the big sizes (at 128 px
# the glyph landed entirely off-canvas, at 256 px it was shifted and clipped), so no
# percentage/flex sizing is used here on purpose.
#
# Usage:
#   pwsh -NoProfile -File tools\make-icon.ps1                    # transparent background (default)
#   pwsh -NoProfile -File tools\make-icon.ps1 -Plate white       # white rounded plate
#   pwsh -NoProfile -File tools\make-icon.ps1 -Plate dark        # dark rounded plate
param(
    [ValidateSet('none', 'white', 'dark')][string]$Plate = 'none',
    [double]$Fill = 0.88,
    [string]$SvgPath,
    [string]$OutputIco,
    [string]$EdgePath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $SvgPath) { $SvgPath = Join-Path $PSScriptRoot 'icon-source.svg' }
if (-not $OutputIco) { $OutputIco = Join-Path $root 'app.ico' }
if (-not $EdgePath) {
    $EdgePath = @(
        'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $EdgePath -or -not (Test-Path $EdgePath)) {
    throw '找不到 msedge.exe；本脚本用无头 Edge 栅格化 SVG。'
}
if (-not (Test-Path $SvgPath)) { throw "找不到 SVG 源文件：$SvgPath" }

# Drop the logo's em-based sizing so we can set exact pixels per render size.
$svgBase = (Get-Content $SvgPath -Raw).Replace('width="1em"', '').Replace('height="1em"', '')

function Get-IconHtml([int]$Size) {
    $glyph = [int][Math]::Round($Size * $(if ($Plate -eq 'none') { $Fill } else { 0.62 }))
    $inset = [int][Math]::Round(($Size - $glyph) / 2)
    $svg = $svgBase.Replace('<svg', "<svg width=`"$glyph`" height=`"$glyph`"")
    $plateStyle = switch ($Plate) {
        'white' { 'background:#ffffff;border-radius:22%;' }
        'dark' { 'background:#0B0F14;border-radius:22%;' }
        default { 'background:transparent;' }
    }
    return "<!doctype html><html><head><meta charset=`"utf-8`"></head>" +
    "<body style=`"margin:0;padding:0;background:transparent`">" +
    "<div style=`"position:relative;width:${Size}px;height:${Size}px;overflow:hidden;$plateStyle`">" +
    "<div style=`"position:absolute;left:${inset}px;top:${inset}px`">$svg</div></div></body></html>"
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$work = Join-Path $env:TEMP ('dsh-icon-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    Write-Host "== 用无头 Edge 逐尺寸渲染（底板=$Plate）=="
    $rendered = @{}
    foreach ($size in $sizes) {
        $htmlPath = Join-Path $work "icon-$size.html"
        Set-Content $htmlPath (Get-IconHtml -Size $size) -Encoding UTF8
        $url = 'file:///' + ($htmlPath -replace '\\', '/')
        $png = Join-Path $work "render-$size.png"

        & $EdgePath --headless=new --disable-gpu --hide-scrollbars --no-first-run --no-default-browser-check `
            --force-device-scale-factor=1 --default-background-color=00000000 `
            --user-data-dir="$work\profile-$size" --window-size="$size,$size" --screenshot="$png" $url 2>$null | Out-Null

        if (-not (Test-Path $png)) { throw "尺寸 $size 渲染失败：没有产出 PNG" }

        # A near-empty render means the glyph fell outside the canvas; fail loudly instead of
        # silently shipping a blank icon entry.
        $probe = [System.Drawing.Bitmap]::new($png)
        $opaque = 0
        for ($y = 0; $y -lt $probe.Height; $y++) {
            for ($x = 0; $x -lt $probe.Width; $x++) {
                if ($probe.GetPixel($x, $y).A -gt 100) { $opaque++ }
            }
        }
        $coverage = 100 * $opaque / ($probe.Width * $probe.Height)
        $probe.Dispose()
        if ($coverage -lt 5) { throw "尺寸 $size 渲染异常：不透明像素仅 $([Math]::Round($coverage,1))%，疑似空白" }

        $rendered[$size] = $png
        Write-Host ("   {0,3}x{0,-3} 覆盖 {1,5}%" -f $size, [Math]::Round($coverage, 1))
    }

    # ── pack the renders into the .ico ──────────────────────────────────────────────────────
    $pngs = @()
    foreach ($size in $sizes) {
        $source = [System.Drawing.Bitmap]::new($rendered[$size])
        # Normalise to exactly the target size (headless screenshots can differ by a pixel).
        $canvas = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
        $g.Dispose()
        $source.Dispose()

        $ms = [System.IO.MemoryStream]::new()
        $canvas.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $ms.Dispose()

        if ($size -in @(16, 32, 48, 256)) {
            $canvas.Save((Join-Path $PSScriptRoot "icon-preview-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        $canvas.Dispose()
    }

    $out = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($out)
    $writer.Write([UInt16]0)                # reserved
    $writer.Write([UInt16]1)                # type: icon
    $writer.Write([UInt16]$sizes.Count)     # image count

    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $data = $pngs[$i]
        $dim = if ($size -ge 256) { 0 } else { $size }   # 0 means 256 in the ICO header
        $writer.Write([Byte]$dim)
        $writer.Write([Byte]$dim)
        $writer.Write([Byte]0)               # palette count
        $writer.Write([Byte]0)               # reserved
        $writer.Write([UInt16]1)             # colour planes
        $writer.Write([UInt16]32)            # bits per pixel
        $writer.Write([UInt32]$data.Length)
        $writer.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($data in $pngs) { $writer.Write($data) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($OutputIco, $out.ToArray())
    $writer.Dispose()
    $out.Dispose()

    Write-Host "== 写出 $OutputIco ($((Get-Item $OutputIco).Length) bytes, $($sizes.Count) 个尺寸) =="
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
