param(
    [string]$WindowTitle = "Folder Size",
    [string]$OutputPath = "$PSScriptRoot\screenshot.png"
)

Add-Type -AssemblyName System.Drawing

$signature = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class WinApi {
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
    }
}
"@

Add-Type -TypeDefinition $signature -ReferencedAssemblies System.Drawing

$foundHwnd = [IntPtr]::Zero
$proc = {
    param($hWnd, $lParam)
    if (-not [WinApi]::IsWindowVisible($hWnd)) { return $true }
    $len = [WinApi]::GetWindowTextLength($hWnd)
    if ($len -eq 0) { return $true }
    $sb = New-Object System.Text.StringBuilder ($len + 1)
    [void][WinApi]::GetWindowText($hWnd, $sb, $sb.Capacity)
    $title = $sb.ToString()
    if ($title -like "*$WindowTitle*") {
        $script:foundHwnd = $hWnd
        return $false
    }
    return $true
}

[void][WinApi]::EnumWindows($proc, [IntPtr]::Zero)

if ($foundHwnd -eq [IntPtr]::Zero) {
    Write-Error "Window '$WindowTitle' not found"
    exit 1
}

[void][WinApi]::ShowWindow($foundHwnd, 9)
$HWND_TOP = [IntPtr]0
$SWP_NOMOVE = 0x0002
$SWP_NOSIZE = 0x0001
$SWP_SHOWWINDOW = 0x0040
[void][WinApi]::SetWindowPos($foundHwnd, $HWND_TOP, 0, 0, 0, 0, $SWP_NOMOVE -bor $SWP_NOSIZE -bor $SWP_SHOWWINDOW)
[void][WinApi]::BringWindowToTop($foundHwnd)
[void][WinApi]::SetForegroundWindow($foundHwnd)
Start-Sleep -Milliseconds 700

$rect = New-Object WinApi+RECT
[void][WinApi]::GetWindowRect($foundHwnd, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

if ($width -le 0 -or $height -le 0) {
    Write-Error "Invalid window size: ${width}x${height}"
    exit 1
}

# Use PrintWindow for reliable capture regardless of Z-order
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()

# PW_RENDERFULLCONTENT = 2 (captures hardware-accelerated content like DirectX)
$ok = [WinApi]::PrintWindow($foundHwnd, $hdc, 2)
$graphics.ReleaseHdc($hdc)

if (-not $ok) {
    # Fallback to screen capture if PrintWindow failed
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
}

$bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

# Also save a downscaled version for preview
$maxW = 640
$scale = [Math]::Min(1.0, $maxW / $width)
$scaledW = [int]($width * $scale)
$scaledH = [int]($height * $scale)
$scaled = New-Object System.Drawing.Bitmap $scaledW, $scaledH
$sg = [System.Drawing.Graphics]::FromImage($scaled)
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$sg.DrawImage($bitmap, 0, 0, $scaledW, $scaledH)
$scaledPath = [System.IO.Path]::ChangeExtension($OutputPath, $null) + "small.png"
$scaled.Save($scaledPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sg.Dispose()
$scaled.Dispose()

$graphics.Dispose()
$bitmap.Dispose()

Write-Host "Saved: $OutputPath (${width}x${height}) PrintWindow=$ok"
Write-Host "Scaled: $scaledPath (${scaledW}x${scaledH})"
