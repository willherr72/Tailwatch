#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$semver = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($Version -cnotmatch $semver) { throw "Invalid semantic version: $Version (omit the v prefix)" }
$cargo = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../agent/Cargo.toml') -Raw
$cargoVersion = [regex]::Match($cargo, '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
$viewer = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../viewer/Tailwatch.App/Tailwatch.App.csproj') -Raw)
$viewerVersion = [string]$viewer.Project.PropertyGroup.Version
if ($Version -cne $cargoVersion -or $Version -cne $viewerVersion) { throw "Version $Version does not match agent ($cargoVersion) and viewer ($viewerVersion) source manifests" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output directory already exists: $output" }
if (!(Test-Path -LiteralPath $ArtifactRoot -PathType Container)) { throw "Missing artifact directory: $ArtifactRoot" }

$specs = @(
    @{ Platform = 'windows-x64'; Artifact = 'windows-x64-agent'; Names = @('tailwatch-agent.exe', 'install-windows.ps1', 'uninstall-windows.ps1', 'README.md', 'LICENSE') },
    @{ Platform = 'linux-x64'; Artifact = 'linux-x64-agent'; Names = @('tailwatch-agent', 'install-linux.sh', 'README.md', 'LICENSE') },
    @{ Platform = 'macos-arm64'; Artifact = 'macos-arm64-agent'; Names = @('tailwatch-agent', 'install-macos.sh', 'README.md', 'LICENSE') },
    @{ Platform = 'windows-x64'; Artifact = 'windows-x64-viewer'; Names = @('Tailwatch.exe'); Ignored = @('Tailwatch.pdb', 'Tailwatch.Core.pdb') }
)
$files = @{}
foreach ($spec in $specs) {
    $directory = Join-Path $ArtifactRoot $spec.Artifact
    foreach ($name in $spec.Names) {
        $path = Join-Path $directory $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing required file: $path" }
        if (!$files.ContainsKey($spec.Platform)) { $files[$spec.Platform] = [ordered]@{} }
        $files[$spec.Platform][$name] = $path
    }
    $allowed = @($spec.Names)
    if ($spec.ContainsKey('Ignored')) { $allowed += $spec.Ignored }
    foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
        if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $entry.Name -cnotin $allowed) { throw "Unexpected artifact entry: $($entry.FullName)" }
    }
}

function Assert-Architecture([string]$Path, [string]$Platform) {
    # Read just the headers, including a PE header at its declared offset.
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($Platform -eq 'windows-x64') {
            if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Expected PE x64: $Path" }
            $stream.Position = 0x3c; $offset = $reader.ReadUInt32()
            if ($offset -lt 64 -or ([long]$offset + 26) -gt $stream.Length) { throw "Expected PE x64: $Path" }
            $stream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "Expected PE x64: $Path" }
            $stream.Position = [long]$offset + 24
            if ($reader.ReadUInt16() -ne 0x20b) { throw "Expected PE x64: $Path" }
        } elseif ($Platform -eq 'linux-x64') {
            if ($stream.Length -lt 64 -or $reader.ReadUInt32() -ne 0x464c457f -or $reader.ReadByte() -ne 2 -or $reader.ReadByte() -ne 1) { throw "Expected little-endian ELF x64: $Path" }
            $stream.Position = 18
            if ($reader.ReadUInt16() -ne 62) { throw "Expected ELF x64: $Path" }
        } else {
            # Decimal UInt32 avoids PowerShell treating 0xfeedfacf as signed.
            if ($stream.Length -lt 32 -or $reader.ReadUInt32() -ne [uint32]4277009103 -or $reader.ReadUInt32() -ne [uint32]16777228) { throw "Expected Mach-O arm64: $Path" }
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
foreach ($platform in $files.Keys) {
    foreach ($name in $files[$platform].Keys) {
        if ($name -eq 'tailwatch-agent' -or $name.EndsWith('.exe')) { Assert-Architecture $files[$platform][$name] $platform }
    }
}

# All source validation happens before creating output. Never delete a caller's directory.
# A partial output is retained on I/O failure for inspection; retry with a fresh destination.
[void](New-Item -ItemType Directory -Path $output)
$checksums = @()
foreach ($platform in @('windows-x64', 'linux-x64', 'macos-arm64')) {
    $archiveName = "Tailwatch-v$Version-$platform.zip"
    $archivePath = Join-Path $output $archiveName
    $sourceHashes = @{}
    $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $files[$platform].Keys) {
            $path = $files[$platform][$name]
            $sourceHashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path, $name, [IO.Compression.CompressionLevel]::Optimal)
            $mode = if ($name -eq 'tailwatch-agent' -or $name.EndsWith('.sh')) { [uint32]33261 } else { [uint32]33188 }
            $entry.ExternalAttributes = [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]($mode -shl 16)), 0)
        }
    } finally { $zip.Dispose() }
    # Decompress every entry and compare it to the original file hash.
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($zip.Entries.Count -ne $sourceHashes.Count) { throw "Archive entry count mismatch: $archiveName" }
        foreach ($entry in $zip.Entries) {
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if (!$sourceHashes.ContainsKey($entry.FullName) -or $sourceHashes[$entry.FullName] -cne $hash) { throw "Archive hash mismatch: $archiveName/$($entry.FullName)" }
        }
    } finally { $zip.Dispose() }
    $checksums += (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $archiveName
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Created and verified three release archives in $output"
