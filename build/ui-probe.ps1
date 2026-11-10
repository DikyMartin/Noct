<#
Drives the running Noct UI from outside the process so window behaviour is checked on a real
desktop instead of assumed. Each verb prints plain facts (visible or not, rectangle, state) and,
where useful, writes a screenshot under artifacts/screens.

  launch              start Noct.exe fresh, wait for the main window
  state               print window/taskbar facts for the running process
  shot <name>         screenshot the whole primary screen
  minimize            send SC_MINIMIZE to the main window (the taskbar-button route)
  restore             send the tray "open" route by relaunching (single-instance activation)
  kill                stop Noct
#>
param(
    [Parameter(Mandatory = $true)][string]$Verb,
    [string]$Arg = ''
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\src\Noct.App\bin\Debug\net10.0-windows\win-x64\Noct.exe'
$exe = [System.IO.Path]::GetFullPath($exe)
if ($env:NOCT_EXE) { $exe = $env:NOCT_EXE }
$shots = Join-Path $PSScriptRoot '..\artifacts\screens'
New-Item -ItemType Directory -Force -Path $shots | Out-Null
$shots = (Resolve-Path $shots).Path

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class W32 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern long GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static List<IntPtr> ForProcess(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
'@
[void][W32]::SetProcessDPIAware()

function Get-Noct { Get-Process Noct -ErrorAction SilentlyContinue | Select-Object -First 1 }

function Show-State {
    $p = Get-Noct
    if (-not $p) { Write-Output 'process: not running'; return }
    Write-Output "process: pid=$($p.Id) responding=$($p.Responding) workingSet=$([int]($p.WorkingSet64/1MB))MB threads=$($p.Threads.Count)"
    foreach ($h in [W32]::ForProcess([uint32]$p.Id)) {
        $title = New-Object System.Text.StringBuilder 256
        $cls = New-Object System.Text.StringBuilder 256
        [void][W32]::GetWindowText($h, $title, 256)
        [void][W32]::GetClassName($h, $cls, 256)
        $r = New-Object W32+RECT
        [void][W32]::GetWindowRect($h, [ref]$r)
        $ex = [W32]::GetWindowLongPtrW($h, -20)
        $vis = [W32]::IsWindowVisible($h)
        if ($cls.ToString() -like 'HwndWrapper*' -or $title.Length -gt 0) {
            if ($vis -or $title.ToString() -match 'Noct') {
                "window: visible=$vis iconic=$([W32]::IsIconic($h)) title='$title' rect=($($r.L),$($r.T))-($($r.R),$($r.B)) size=$($r.R-$r.L)x$($r.B-$r.T) toolwindow=$((($ex -band 0x80) -ne 0)) noactivate=$((($ex -band 0x8000000) -ne 0))"
            }
        }
    }
}

function Save-Shot([string]$name, [int]$x = 0, [int]$y = 0, [int]$w = 0, [int]$h = 0) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen
    if ($w -le 0) { $w = $screen.Bounds.Width }
    if ($h -le 0) { $h = $screen.Bounds.Height }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $path = Join-Path $shots "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "screenshot: $path ($w x $h at $x,$y)"
}
Add-Type -AssemblyName System.Windows.Forms

switch ($Verb) {
    'launch' {
        Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 400
        $null = Start-Process $exe
        $deadline = (Get-Date).AddSeconds(15)
        do { Start-Sleep -Milliseconds 300; $p = Get-Noct } while (-not $p -and (Get-Date) -lt $deadline)
        Start-Sleep -Seconds 2
        Show-State
    }
    'state' { Show-State }
    'shot' { Save-Shot $Arg }
    'windowshot' {
        # Renders only Noct's own window (PrintWindow), so nothing else on the desktop is captured
        # and the window does not have to be in front. Native backdrop material may not appear in
        # this capture; use shotrect on the island region for that.
        $p = Get-Noct
        $main = [W32]::ForProcess([uint32]$p.Id) | Where-Object {
            $t = New-Object System.Text.StringBuilder 256; [void][W32]::GetWindowText($_, $t, 256); $t.ToString() -eq 'Noct' -and [W32]::IsWindowVisible($_) } | Select-Object -First 1
        if (-not $main) { Write-Output 'no visible main window'; return }
        $r = New-Object W32+RECT
        [void][W32]::GetWindowRect($main, [ref]$r)
        $w = $r.R - $r.L; $h = $r.B - $r.T
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [void][W32]::PrintWindow($main, $hdc, 2)
        $g.ReleaseHdc($hdc)
        $path = Join-Path $shots "$Arg.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output "window screenshot: $path ($w x $h)"
    }
    'shotrect' {
        $parts = $Arg.Split(',')
        Save-Shot $parts[0] ([int]$parts[1]) ([int]$parts[2]) ([int]$parts[3]) ([int]$parts[4])
    }
    'minimize' {
        $p = Get-Noct
        $main = [W32]::ForProcess([uint32]$p.Id) | Where-Object {
            $t = New-Object System.Text.StringBuilder 256; [void][W32]::GetWindowText($_, $t, 256); $t.ToString() -eq 'Noct' -and [W32]::IsWindowVisible($_) } | Select-Object -First 1
        if (-not $main) { Write-Output 'no visible main window'; return }
        [void][W32]::SendMessage($main, 0x0112, [IntPtr]0xF020, [IntPtr]::Zero)
        Write-Output 'sent WM_SYSCOMMAND SC_MINIMIZE'
    }
    'activate' {
        $null = Start-Process $exe
        Write-Output 'launched second instance (asks the first to come forward)'
    }
    'has' {
        # True when an element with this name exists inside the island window. Collapsed elements
        # are not exposed to UI Automation, so this tells expanded from compact.
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $p = Get-Noct
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
        $found = $false
        foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
            if ($w.Current.Name -ne 'Noct island') { continue }
            $n = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Arg)
            if ($w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $n)) { $found = $true }
        }
        Write-Output "expanded-content-present=$found"
    }
    'move' {
        Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class Cur { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }'
        $xy = $Arg.Split(',')
        [void][Cur]::SetCursorPos([int]$xy[0], [int]$xy[1])
        Write-Output "cursor moved to $($xy[0]),$($xy[1])"
    }
    'close' {
        # The close-button route: WM_SYSCOMMAND SC_CLOSE, so the app's own clean exit runs.
        $p = Get-Noct
        $main = [W32]::ForProcess([uint32]$p.Id) | Where-Object {
            $t = New-Object System.Text.StringBuilder 256; [void][W32]::GetWindowText($_, $t, 256); $t.ToString() -eq 'Noct' -and [W32]::IsWindowVisible($_) } | Select-Object -First 1
        [void][W32]::SendMessage($main, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero)
        $deadline = (Get-Date).AddSeconds(8)
        while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100; $p.Refresh() }
        Write-Output "closed cleanly: $($p.HasExited)  exit code: $(if ($p.HasExited) { $p.ExitCode } else { 'n/a' })"
    }
    'syscmd' {
        # Arg = hex SC_ command (F030 maximize, F120 restore) sent to the main window.
        $p = Get-Noct
        $main = [W32]::ForProcess([uint32]$p.Id) | Where-Object {
            $t = New-Object System.Text.StringBuilder 256; [void][W32]::GetWindowText($_, $t, 256); $t.ToString() -eq 'Noct' -and [W32]::IsWindowVisible($_) } | Select-Object -First 1
        [void][W32]::SendMessage($main, 0x0112, [IntPtr][Convert]::ToInt32($Arg, 16), [IntPtr]::Zero)
        Write-Output "sent SC_$Arg"
    }
    'kill' { Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force; Write-Output 'stopped' }
    default { throw "unknown verb: $Verb" }
}
