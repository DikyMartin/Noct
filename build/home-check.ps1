# Exercises Home on an already running Noct: Refresh, Shuffle all, open an album from Home.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$p = Get-Process Noct | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
$lines = New-Object System.Collections.Generic.List[string]
function Kid($el, $n) { $c = $walker.GetFirstChild($el); while ($c) { if ($c.Current.Name -eq $n -or $c.Current.AutomationId -eq $n) { return $c }; $c = $walker.GetNextSibling($c) }; return $null }
function Home-Page { $c = $walker.GetFirstChild($root); while ($c) { if ($c.Current.Name -eq 'Home' -and $c.Current.ControlType.ProgrammaticName -eq 'ControlType.Custom') { return $c }; $c = $walker.GetNextSibling($c) }; return $null }
function Buttons { @((Home-Page).FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | ForEach-Object { $_.Current.Name }) }
function Deep($el, $n) { $el.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n))) }
function Invoke-It($el) { $pat = $null; if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke() } elseif ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() } else { throw "no click pattern on $($el.Current.Name)" } }
function PlayName { $t = Kid $root 'Playback controls'; (Kid $t 'PlayButton').Current.Name }

$before = (Buttons | Where-Object { $_ -match '· \d+ tracks?$' }) -replace ',.*', ''
$songsBefore = (Buttons | Where-Object { $_ -like 'silence*' -or $_ -like '*Quiet*' }) -join '|'
$lines.Add('albums before Refresh: ' + ($before -join ', '))
Invoke-It (Deep (Home-Page) 'Refresh picks')
Start-Sleep -Seconds 1
$after = (Buttons | Where-Object { $_ -match '· \d+ tracks?$' }) -replace ',.*', ''
$songsAfter = (Buttons | Where-Object { $_ -like 'silence*' -or $_ -like '*Quiet*' }) -join '|'
$lines.Add('albums after Refresh:  ' + ($after -join ', '))
$lines.Add("Refresh kept 6 albums: $($after.Count -eq 6)  order changed: $(($before -join ',') -ne ($after -join ','))  songs changed: $($songsBefore -ne $songsAfter)")

$lines.Add("play button before Shuffle all: $(PlayName)")
Invoke-It (Deep (Home-Page) 'Shuffle all')
Start-Sleep -Seconds 1.5
$lines.Add("play button 1.5 s after Shuffle all: $(PlayName)")
$q = Kid $root 'Queue'
Invoke-It $q
Start-Sleep -Seconds 1
$queuePage = $null
$c = $walker.GetFirstChild($root); while ($c) { if ($c.Current.Name -eq 'Queue' -and $c.Current.ControlType.ProgrammaticName -eq 'ControlType.Custom') { $queuePage = $c }; $c = $walker.GetNextSibling($c) }
$items = Kid $queuePage 'Queue items'
$n = 0; if ($items) { $c = $walker.GetFirstChild($items); while ($c) { $n++; $c = $walker.GetNextSibling($c) } }
$lines.Add("queue after Shuffle all holds $n tracks (library has 10 + 1 + 2 + 2 + ... see below)")
Invoke-It (Kid $root 'Home')
Start-Sleep -Seconds 1
$lines.Add("play button after leaving to Queue and back to Home: $(PlayName)")
$lines | Set-Content (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\artifacts\home-check.txt')
