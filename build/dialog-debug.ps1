$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [System.IO.Path]::GetFullPath((Join-Path $here '..\src\Noct.App\bin\Release\net10.0-windows\win-x64\Noct.exe'))
$data = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts\featuredata-debug'))
$folder = [System.IO.Path]::GetFullPath((Join-Path $here '..\artifacts\featurelib\Blue Hour'))
$out = Join-Path $here '..\artifacts\dialog-debug.txt'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Fg { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid); [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, int flags, int extra); }
'@
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
$env:NOCT_DATA_DIR = $data
$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds 6
$lines = New-Object System.Collections.Generic.List[string]
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$btn = $null
$c = $walker.GetFirstChild($root)
while ($c) { if ($c.Current.Name -eq 'Add folder' -and $c.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') { $btn = $c }; $c = $walker.GetNextSibling($c) }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2.5
$dialog = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($w.Current.Name -eq 'Add a music folder') { $dialog = $w } }
$lines.Add("dialog found: $([bool]$dialog)")
$hwnd = [IntPtr]$dialog.Current.NativeWindowHandle
$lines.Add("dialog hwnd: $hwnd ; foreground before: $([Fg]::GetForegroundWindow())")
# Windows only lets the foreground process steal focus; a tapped Alt key lifts that restriction.
[Fg]::keybd_event(0x12, 0, 0, 0); [Fg]::keybd_event(0x12, 0, 2, 0)
$lines.Add("SetForegroundWindow: $([Fg]::SetForegroundWindow($hwnd)) ; foreground after: $([Fg]::GetForegroundWindow())")
Start-Sleep -Milliseconds 500
$edit = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
$lines.Add('edit box found: ' + [bool]$edit + ' ; name=' + $(if ($edit) { $edit.Current.Name } else { '' }))
try { $edit.SetFocus(); $lines.Add('SetFocus ok') } catch { $lines.Add('SetFocus failed: ' + $_.Exception.Message) }
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait($folder)
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 3
$still = $false
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($w.Current.Name -eq 'Add a music folder') { $still = $true } }
$lines.Add("dialog still open after typing: $still")
Start-Sleep -Seconds 2
$lines.Add('settings.json exists: ' + (Test-Path (Join-Path $data 'settings.json')))
if (Test-Path (Join-Path $data 'settings.json')) { $lines.Add((Get-Content (Join-Path $data 'settings.json') -Raw | Select-String 'libraryFolders' -Context 0,2 | Out-String).Trim()) }
foreach ($k in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) { if ($k.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $k.Current.Name -match 'album|track') { $lines.Add('count text: ' + $k.Current.Name) } }
Get-Process Noct -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item Env:\NOCT_DATA_DIR -ErrorAction SilentlyContinue
$lines | Set-Content $out
