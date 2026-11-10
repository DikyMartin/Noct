# Launches Noct, captures the transport bar at normal and minimum width, and measures the
# vertical centre of each right-hand control from the real pixels.
param([string]$Tag = 'tb-after')
$ErrorActionPreference = 'Stop'
$env:NOCT_DATA_DIR = "$PSScriptRoot\..\artifacts\featuredata"
$env:NOCT_EXE = "$PSScriptRoot\..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe"
Set-Location $PSScriptRoot
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Win { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int cx, uint f); }
'@
.\ui-probe.ps1 launch | Out-Null
.\ui-probe.ps1 windowshot "$Tag-normal"
Add-Type -AssemblyName UIAutomationClient
$p = Get-Process Noct | Select-Object -First 1
$c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$w = [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Children', $c) | Where-Object { $_.Current.Name -eq 'Noct' } | Select-Object -First 1
[void][Win]::SetWindowPos([IntPtr]$w.Current.NativeWindowHandle, [IntPtr]::Zero, 200, 100, 1440, 900, 0x14)
Start-Sleep -Milliseconds 800
.\ui-probe.ps1 windowshot "$Tag-narrow"
.\ui-probe.ps1 kill
