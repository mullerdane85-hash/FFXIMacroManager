# Regenerates the 256px crystal bitmap as a flat PNG for preview purposes.
# Reuses the same drawing routine as make_shortcut.ps1 (kept in sync manually).
Add-Type -AssemblyName System.Drawing

. "C:\Users\Jason\Desktop\My FFXI Add ons\FFXIMacroManager\make_shortcut.ps1"

# After dot-sourcing, the icon + shortcut have already been (re)written.
# Now also drop a flat PNG preview so it's easy to inspect outside Explorer.
$bmp = New-CrystalBitmap 256
$bmp.Save("C:\Users\Jason\Desktop\My FFXI Add ons\FFXIMacroManager\FFXIMacroManager_preview.png",
          [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Wrote preview PNG"
