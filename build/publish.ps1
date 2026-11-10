<#
Publishes a fresh, self-contained Windows x64 application into dist\Noct.
The .NET SDK restores the runtime; end users do not need to install .NET.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$publishDir = [IO.Path]::GetFullPath((Join-Path $root 'dist\Noct'))
$project = Join-Path $root 'src\Noct.App\Noct.App.csproj'

# Only replace this script's generated output, never a caller-supplied directory.
if (-not $publishDir.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish output must be inside the repository.'
}
$distDir = Join-Path $root 'dist'
if ((Test-Path -LiteralPath $distDir) -and ((Get-Item -LiteralPath $distDir).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing to publish through a linked dist directory.'
}
if (Test-Path -LiteralPath $publishDir) {
    if ((Get-Item -LiteralPath $publishDir).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Refusing to clear a linked publish directory.'
    }
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

foreach ($required in 'Noct.exe', 'Noct.dll', 'Noct.Core.dll', 'NAudio.Wasapi.dll', 'NAudio.Asio.dll', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $required) -PathType Leaf)) {
        throw "Publish output is incomplete: $required is missing."
    }
}

foreach ($document in 'README.md', 'THIRD_PARTY_NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $root $document) -Destination $publishDir
}
$docs = Join-Path $publishDir 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' -File | Copy-Item -Destination $docs
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $publishDir -Recurse

# The SDK does not copy runtime redistribution notices into publish output.
# Use the exact runtime versions embedded in this publish, not the latest cache entry.
$runtime = Get-Content -LiteralPath (Join-Path $publishDir 'Noct.runtimeconfig.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath (Join-Path $root 'src\Noct.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $packageId = ($framework.name + '.Runtime.win-x64').ToLowerInvariant()
    $packageDir = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot ($packageId + '\' + $framework.version)
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDir = $candidate; break }
    }
    if (-not $packageDir) { throw "Cannot find restored runtime notices for $packageId $($framework.version)." }

    $licenseDir = Join-Path $publishDir ('licenses\' + $framework.name)
    New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    $notices = @(Get-ChildItem -LiteralPath $packageDir -File | Where-Object { $_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' })
    if (-not ($notices | Where-Object { $_.Name -match '^LICENSE' })) {
        throw "Runtime license is missing from $packageId $($framework.version)."
    }
    $notices | Copy-Item -Destination $licenseDir
}

$files = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
Write-Output ("publish: {0} files, {1:N1} MB in {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $publishDir)
