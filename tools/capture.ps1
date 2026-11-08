# Saves a PNG of the clone's main window (by exe path) without focusing it.
# Usage: capture.ps1 -Exe <path to TaskManager.exe> -Out <png>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Cap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
'@
$p = Get-Process | Where-Object { $_.Path -eq $Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Error "window not found for $Exe"; exit 1 }
$r = New-Object Cap+RECT
[Cap]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp)
# WinUI draws through DirectComposition, which PrintWindow can't read; copy the on-screen pixels instead.
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
"pid $($p.Id) saved $Out"
