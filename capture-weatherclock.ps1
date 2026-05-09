$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$signature = @"
using System;
using System.Runtime.InteropServices;

public static class WinApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

Add-Type -TypeDefinition $signature

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "dist\Weather Clock.exe"
$shots = Join-Path $root "design-preview\captures"
New-Item -ItemType Directory -Force -Path $shots | Out-Null

if (!(Test-Path $exe)) {
    throw "Executable not found: $exe"
}

Get-Process | Where-Object { $_.ProcessName -eq "Weather Clock" } | Stop-Process -Force -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $exe -PassThru

try {
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 120
    }

    if ($proc.MainWindowHandle -eq 0) {
        throw "Could not find Weather Clock window handle."
    }

    [WinApi]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null

    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $targets = @(
        @{ Name = "small";  W = 900;  H = 520; X = $screen.Left + 40; Y = $screen.Top + 40 },
        @{ Name = "medium"; W = 1180; H = 700; X = $screen.Left + 70; Y = $screen.Top + 50 },
        @{ Name = "large";  W = [Math]::Min(1580, $screen.Width - 40); H = [Math]::Min(920, $screen.Height - 40); X = $screen.Left + 20; Y = $screen.Top + 20 }
    )

    foreach ($target in $targets) {
        [WinApi]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, $target.X, $target.Y, $target.W, $target.H, 0x0040) | Out-Null
        Start-Sleep -Milliseconds 260

        $rect = New-Object WinApi+RECT
        if (-not [WinApi]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)) {
            throw "Failed to read window bounds."
        }

        $width = [Math]::Max(1, $rect.Right - $rect.Left)
        $height = [Math]::Max(1, $rect.Bottom - $rect.Top)
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
        }
        finally {
            $graphics.Dispose()
        }

        $outFile = Join-Path $shots ("weatherclock_" + $target.Name + ".png")
        $bitmap.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Write-Host "Captured $outFile"
    }

    $secondary = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
    if ($secondary -ne $null) {
        $work = $secondary.WorkingArea
        [WinApi]::SetWindowPos($proc.MainWindowHandle, [IntPtr]::Zero, $work.Left + 40, $work.Top + 24, [Math]::Min(1100, $work.Width - 80), [Math]::Min(680, $work.Height - 60), 0x0040) | Out-Null
        Start-Sleep -Milliseconds 300
        [WinApi]::ShowWindow($proc.MainWindowHandle, 3) | Out-Null
        Start-Sleep -Milliseconds 380

        $rect = New-Object WinApi+RECT
        if (-not [WinApi]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)) {
            throw "Failed to read maximized window bounds."
        }

        $width = [Math]::Max(1, $rect.Right - $rect.Left)
        $height = [Math]::Max(1, $rect.Bottom - $rect.Top)
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
        }
        finally {
            $graphics.Dispose()
        }

        $outFile = Join-Path $shots "weatherclock_secondary_maximized.png"
        $bitmap.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Write-Host "Captured $outFile"
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 250
        if (-not $proc.HasExited) {
            $proc.Kill()
        }
    }
}
