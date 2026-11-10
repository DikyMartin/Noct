# Checks the custom caption buttons on the running Noct: hit-test results (drag vs click),
# then Maximize / Restore / Minimize via UI Automation, reporting window state after each.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Hit {
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
}
'@
[void][Hit]::SetProcessDPIAware()
$p = Get-Process Noct | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond) |
    Where-Object { $_.Current.Name -eq 'Noct' } | Select-Object -First 1
$h = [IntPtr]$win.Current.NativeWindowHandle
function Nc([int]$x, [int]$y) { [int][Hit]::SendMessage($h, 0x84, [IntPtr]::Zero, [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF))) }
function Btn([string]$n) { $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n))) }
function Facts { $r = New-Object Hit+R; [void][Hit]::GetWindowRect($h, [ref]$r); "zoomed=$([Hit]::IsZoomed($h)) visible=$([Hit]::IsWindowVisible($h)) rect=($($r.L),$($r.T))-($($r.Rt),$($r.B))" }

$r = New-Object Hit+R; [void][Hit]::GetWindowRect($h, [ref]$r)
# WM_NCHITTEST: 2 = HTCAPTION (drags the window), 1 = HTCLIENT (clickable content), 10 = HTTOP-ish resize edges
"hit empty caption strip : $(Nc ($r.L + 600) ($r.T + 20))  (2 = draggable caption)"
$m = (Btn 'Minimize').Current.BoundingRectangle
"hit Minimize button     : $(Nc ([int]($m.X + $m.Width/2)) ([int]($m.Y + $m.Height/2)))  (1 = client, clickable)"
$c = (Btn 'Close').Current.BoundingRectangle
"hit Close button        : $(Nc ([int]($c.X + $c.Width/2)) ([int]($c.Y + $c.Height/2)))"
"button sizes (px)       : min $($m.Width)x$($m.Height)  max $((Btn 'Maximize').Current.BoundingRectangle.Width)x$((Btn 'Maximize').Current.BoundingRectangle.Height)  close $($c.Width)x$($c.Height)"
"start                   : $(Facts)"

$inv = [System.Windows.Automation.InvokePattern]::Pattern
(Btn 'Maximize').GetCurrentPattern($inv).Invoke(); Start-Sleep -Milliseconds 700
"after Maximize          : $(Facts)   button now named '$((Btn 'Restore').Current.Name)'"
(Btn 'Restore').GetCurrentPattern($inv).Invoke(); Start-Sleep -Milliseconds 700
"after Restore           : $(Facts)   button now named '$((Btn 'Maximize').Current.Name)'"
