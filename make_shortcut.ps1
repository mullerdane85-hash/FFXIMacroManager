# =============================================================================
# make_shortcut.ps1 — generates an FFXI-themed .ico + desktop .lnk for the
# FFXIMacroManager standalone executable. Run once after cloning the repo.
#
# The icon is drawn programmatically (no external image deps) at six
# resolutions (16, 32, 48, 64, 128, 256 px) and packed into a single .ico
# so Explorer / taskbar / file pickers all show a crisp version.
#
# Motif: dark-navy gradient background + cyan/azure faceted crystal
# (FFXI's signature visual) + gold "M" overlay for "Macro."
# =============================================================================

Add-Type -AssemblyName System.Drawing

$root    = "C:\Users\Jason\Desktop\My FFXI Add ons\FFXIMacroManager"
$exePath = Join-Path $root "FFXIMacroManager.exe"
$icoPath = Join-Path $root "FFXIMacroManager.ico"
$lnkPath = "C:\Users\Jason\Desktop\FFXI Macro Manager.lnk"

if (-not (Test-Path $exePath)) {
    Write-Error "FFXIMacroManager.exe not found at $exePath"
    exit 1
}

# -----------------------------------------------------------------------------
# Draw a single crystal-icon bitmap at the requested size.
# -----------------------------------------------------------------------------
function New-CrystalBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # --- background: dark navy vertical gradient (matches GSUI palette) ----
    $bgRect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $bgRect,
        ([System.Drawing.Color]::FromArgb(255, 10, 21, 48)),
        ([System.Drawing.Color]::FromArgb(255, 26, 48, 96)),
        ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical))
    $g.FillRectangle($bgBrush, $bgRect)
    $bgBrush.Dispose()

    # --- cyan outer border ------------------------------------------------
    $borderW = [Math]::Max(1, [int]($size / 32))
    $borderPen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(255, 95, 200, 255), $borderW)
    $g.DrawRectangle($borderPen, 0, 0, ($size - 1), ($size - 1))
    $borderPen.Dispose()

    # --- crystal (hexagonal facet) ---------------------------------------
    $cx = $size / 2.0
    $cy = $size / 2.0
    $r  = $size * 0.34

    $points = @(
        (New-Object System.Drawing.PointF($cx,                     ($cy - $r))),
        (New-Object System.Drawing.PointF(($cx + $r * 0.87),       ($cy - $r * 0.5))),
        (New-Object System.Drawing.PointF(($cx + $r * 0.87),       ($cy + $r * 0.5))),
        (New-Object System.Drawing.PointF($cx,                     ($cy + $r))),
        (New-Object System.Drawing.PointF(($cx - $r * 0.87),       ($cy + $r * 0.5))),
        (New-Object System.Drawing.PointF(($cx - $r * 0.87),       ($cy - $r * 0.5)))
    )

    # cyan-to-blue radial-ish gradient
    $crystalBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, ($cy - $r))),
        (New-Object System.Drawing.PointF(0, ($cy + $r))),
        ([System.Drawing.Color]::FromArgb(255, 220, 245, 255)),
        ([System.Drawing.Color]::FromArgb(255, 30, 100, 200)))
    $g.FillPolygon($crystalBrush, $points)
    $crystalBrush.Dispose()

    # facet lines + outline
    $facetW = [Math]::Max(1, [int]($size / 48))
    $facetPen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(200, 255, 255, 255), $facetW)
    # vertical seam top->bottom
    $g.DrawLine($facetPen, $cx, ($cy - $r), $cx, ($cy + $r))
    # diagonal seams connecting opposite verts
    $g.DrawLine($facetPen, $points[5].X, $points[5].Y, $points[1].X, $points[1].Y)
    $g.DrawLine($facetPen, $points[4].X, $points[4].Y, $points[2].X, $points[2].Y)
    # hex outline
    $g.DrawPolygon($facetPen, $points)
    $facetPen.Dispose()

    # --- gold "M" overlay (only at sizes large enough to read) -----------
    if ($size -ge 32) {
        $fontSize = [Math]::Max(8, [int]($size * 0.34))
        $font = New-Object System.Drawing.Font("Arial Black", $fontSize,
                                                [System.Drawing.FontStyle]::Bold,
                                                [System.Drawing.GraphicsUnit]::Pixel)
        # gold with subtle drop shadow for legibility on the cyan fill
        $shadowBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(160, 0, 0, 0))
        $textBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb(255, 255, 215, 0))
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment     = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $shadowOffset = [Math]::Max(1, [int]($size / 64))
        $g.DrawString("M", $font, $shadowBrush, ($cx + $shadowOffset), ($cy + $shadowOffset), $sf)
        $g.DrawString("M", $font, $textBrush,   $cx,                   $cy,                   $sf)
        $font.Dispose()
        $textBrush.Dispose()
        $shadowBrush.Dispose()
        $sf.Dispose()
    }

    $g.Dispose()
    return $bmp
}

# -----------------------------------------------------------------------------
# Build a multi-resolution .ico from a list of bitmaps. The standard ICO
# container is a 6-byte ICONDIR header + N * 16-byte ICONDIRENTRY records,
# then the image payload (PNG-encoded for 32bpp icons on Vista+).
# -----------------------------------------------------------------------------
function Save-Ico([System.Drawing.Bitmap[]]$bitmaps, [string]$path) {
    # Convert each bitmap to PNG bytes first so we know the sizes for the
    # directory entries.
    $pngBlobs = @()
    foreach ($bmp in $bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBlobs += ,($ms.ToArray())
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter $fs

    # ICONDIR
    $bw.Write([uint16]0)                  # reserved
    $bw.Write([uint16]1)                  # type 1 = icon
    $bw.Write([uint16]$bitmaps.Count)

    # ICONDIRENTRY x N
    $headerSize = 6 + (16 * $bitmaps.Count)
    $dataOffset = $headerSize
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $bmp = $bitmaps[$i]
        $w = if ($bmp.Width  -ge 256) { 0 } else { [byte]$bmp.Width  }
        $h = if ($bmp.Height -ge 256) { 0 } else { [byte]$bmp.Height }
        $bw.Write([byte]$w)               # width  (0 means 256)
        $bw.Write([byte]$h)               # height (0 means 256)
        $bw.Write([byte]0)                # palette count (0 for 32bpp)
        $bw.Write([byte]0)                # reserved
        $bw.Write([uint16]1)              # color planes
        $bw.Write([uint16]32)             # bits per pixel
        $bw.Write([uint32]$pngBlobs[$i].Length)
        $bw.Write([uint32]$dataOffset)
        $dataOffset += $pngBlobs[$i].Length
    }

    # Image data
    foreach ($blob in $pngBlobs) { $bw.Write($blob) }

    $bw.Close()
    $fs.Close()
}

# -----------------------------------------------------------------------------
# Build the icon
# -----------------------------------------------------------------------------
$sizes   = @(16, 32, 48, 64, 128, 256)
$bitmaps = @()
foreach ($s in $sizes) { $bitmaps += (New-CrystalBitmap $s) }

Save-Ico $bitmaps $icoPath
foreach ($b in $bitmaps) { $b.Dispose() }
Write-Host "Wrote icon: $icoPath"

# -----------------------------------------------------------------------------
# Build the desktop shortcut
# -----------------------------------------------------------------------------
$WshShell = New-Object -ComObject WScript.Shell
$lnk = $WshShell.CreateShortcut($lnkPath)
$lnk.TargetPath       = $exePath
$lnk.WorkingDirectory = $root
$lnk.IconLocation     = "$icoPath,0"
$lnk.Description      = "FFXI Macro Manager - edit mcr*.dat macro files (standalone, no Windower required)"
$lnk.Save()
Write-Host "Wrote shortcut: $lnkPath"
