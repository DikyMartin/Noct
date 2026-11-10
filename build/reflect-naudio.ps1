# Phase 2.1 spike helper: dump the real public API surface of the pinned NAudio
# assemblies so the implementation is written against verified members only.
param(
    [string[]]$Types = @(
        'NAudio.Wave.WasapiOut',
        'NAudio.CoreAudioApi.MMDeviceEnumerator',
        'NAudio.CoreAudioApi.MMDevice',
        'NAudio.CoreAudioApi.AudioClient',
        'NAudio.CoreAudioApi.AudioClientShareMode',
        'NAudio.Wave.AsioOut',
        'NAudio.Wave.WaveFormat',
        'NAudio.Wave.WaveFormatExtensible',
        'NAudio.Wave.IWaveProvider',
        'NAudio.Wave.PlaybackState',
        'NAudio.Wave.StoppedEventArgs'
    )
)

$root = "$env:USERPROFILE\.nuget\packages"
$asms = @(
    "$root\naudio.core\3.3.0\lib\net9.0\NAudio.Core.dll",
    "$root\naudio.wasapi\3.3.0\lib\net9.0\NAudio.Wasapi.dll",
    "$root\naudio.asio\3.3.0\lib\net9.0\NAudio.Asio.dll"
)

$loaded = @()
foreach ($a in $asms) {
    try { $loaded += [System.Reflection.Assembly]::LoadFrom($a) }
    catch { Write-Output "LOAD FAILED: $a -> $($_.Exception.Message)" }
}

function Resolve-Type([string]$name) {
    foreach ($asm in $loaded) {
        $t = $asm.GetType($name, $false)
        if ($t) { return $t }
    }
    return $null
}

foreach ($name in $Types) {
    $t = Resolve-Type $name
    if (-not $t) { Write-Output "`n### $name  -- MISSING"; continue }
    Write-Output "`n### $($t.FullName)  (kind=$(if($t.IsInterface){'interface'}elseif($t.IsEnum){'enum'}else{'class'}) assembly=$($t.Assembly.GetName().Name))"
    if ($t.IsEnum) {
        foreach ($v in [Enum]::GetValues($t)) { Write-Output ("    {0} = {1}" -f $v, [int]$v) }
        continue
    }
    foreach ($c in $t.GetConstructors()) {
        $ps = ($c.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
        Write-Output "    ctor($ps)"
    }
    foreach ($p in $t.GetProperties() | Sort-Object Name) {
        Write-Output "    prop $($p.PropertyType.Name) $($p.Name) { $(if($p.CanRead){'get;'})$(if($p.CanWrite){'set;'}) }"
    }
    foreach ($m in $t.GetMethods([System.Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly') | Where-Object { -not $_.IsSpecialName } | Sort-Object Name) {
        $ps = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
        Write-Output "    meth $($m.ReturnType.Name) $($m.Name)($ps)"
    }
    foreach ($e in $t.GetEvents()) { Write-Output "    event $($e.EventHandlerType.Name) $($e.Name)" }
}
