# Run with PowerShell 7. Fixtures deliberately contain headers, never runnable binaries.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = Join-Path $PSScriptRoot '../scripts/package-release.ps1'
$sourceVersion = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot '../agent/Cargo.toml') -Raw), '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
$root = Join-Path ([IO.Path]::GetTempPath()) ('tailwatch-package-tests-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$passed = 0
function Assert($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function New-Fixture([string]$Name) {
    $inputPath = Join-Path $root "$Name/input"
    foreach ($name in @('windows-x64-agent', 'windows-x64-viewer', 'linux-x64-agent', 'macos-arm64-agent')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $inputPath $name))
    }
    $pe = [byte[]]::new(256); $pe[0] = 0x4d; $pe[1] = 0x5a; $pe[0x3c] = 0x80
    $pe[0x80] = 0x50; $pe[0x81] = 0x45; $pe[0x84] = 0x64; $pe[0x85] = 0x86
    $pe[0x98] = 0x0b; $pe[0x99] = 0x02
    [IO.File]::WriteAllBytes((Join-Path $inputPath 'windows-x64-agent/tailwatch-agent.exe'), $pe)
    [IO.File]::WriteAllBytes((Join-Path $inputPath 'windows-x64-viewer/Tailwatch.exe'), $pe)
    $elf = [byte[]]::new(64); $elf[0] = 0x7f; $elf[1] = 0x45; $elf[2] = 0x4c; $elf[3] = 0x46
    $elf[4] = 2; $elf[5] = 1; $elf[18] = 62
    [IO.File]::WriteAllBytes((Join-Path $inputPath 'linux-x64-agent/tailwatch-agent'), $elf)
    $mach = [byte[]]::new(32); $mach[0] = 0xcf; $mach[1] = 0xfa; $mach[2] = 0xed; $mach[3] = 0xfe
    $mach[4] = 0x0c; $mach[7] = 1
    [IO.File]::WriteAllBytes((Join-Path $inputPath 'macos-arm64-agent/tailwatch-agent'), $mach)
    foreach ($platform in @('windows-x64-agent', 'linux-x64-agent', 'macos-arm64-agent')) {
        foreach ($name in @('README.md', 'LICENSE')) { Set-Content -LiteralPath (Join-Path $inputPath "$platform/$name") -Value "$platform $name" }
    }
    foreach ($path in @('windows-x64-agent/install-windows.ps1', 'windows-x64-agent/uninstall-windows.ps1', 'linux-x64-agent/install-linux.sh', 'macos-arm64-agent/install-macos.sh', 'windows-x64-viewer/Tailwatch.pdb', 'windows-x64-viewer/Tailwatch.Core.pdb')) {
        Set-Content -LiteralPath (Join-Path $inputPath $path) -Value '# fixture'
    }
    return $inputPath
}
function Reject([string]$Name, [scriptblock]$Mutate, [string]$Version = $sourceVersion, [string]$Expected = '.') {
    $inputPath = New-Fixture $Name; $output = Join-Path $root "$Name/output"
    & $Mutate $inputPath $output
    $caught = $null
    try { & $package -ArtifactRoot $inputPath -OutputDirectory $output -Version $Version } catch { $caught = $_ }
    Assert ($null -ne $caught) "$Name unexpectedly succeeded"
    Assert ($caught.ToString() -match $Expected) "$Name returned wrong error: $caught"
    if ($Name -eq 'existing-output') {
        Assert ((Get-Content -LiteralPath (Join-Path $output 'keep.txt') -Raw).Trim() -eq 'keep') 'Existing output was modified'
    } else { Assert (!(Test-Path -LiteralPath $output)) "$Name left an output directory" }
    $script:passed++; Write-Host "PASS $Name"
}
try {
    Assert (Test-Path -LiteralPath $package) 'Packaging implementation is missing'
    $inputPath = New-Fixture 'success'; $output = Join-Path $root 'success/output'
    & $package -ArtifactRoot $inputPath -OutputDirectory $output -Version $sourceVersion
    $expected = @{
        'windows-x64' = @('Tailwatch.exe', 'tailwatch-agent.exe', 'install-windows.ps1', 'uninstall-windows.ps1', 'README.md', 'LICENSE')
        'linux-x64' = @('tailwatch-agent', 'install-linux.sh', 'README.md', 'LICENSE')
        'macos-arm64' = @('tailwatch-agent', 'install-macos.sh', 'README.md', 'LICENSE')
    }
    foreach ($platform in $expected.Keys) {
        $zipPath = Join-Path $output "Tailwatch-v$sourceVersion-$platform.zip"
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            Assert (!(Compare-Object @($expected[$platform] | Sort-Object) @($zip.Entries.FullName | Sort-Object))) "$platform contains unexpected files"
            foreach ($entry in $zip.Entries) {
                $source = if ($entry.Name -eq 'Tailwatch.exe') { Join-Path $inputPath 'windows-x64-viewer/Tailwatch.exe' } else { Join-Path $inputPath "$platform-agent/$($entry.Name)" }
                $stream = $entry.Open()
                try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
                Assert ($hash -eq (Get-FileHash -LiteralPath $source).Hash) "Archive changed bytes: $($entry.Name)"
                if ($entry.Name -eq 'tailwatch-agent' -or $entry.Name.EndsWith('.sh')) {
                    Assert ((($entry.ExternalAttributes -shr 16) -band 0x1ff) -eq 493) 'Unix entry lost executable mode'
                }
            }
        } finally { $zip.Dispose() }
    }
    $lines = @(Get-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt'))
    Assert ($lines.Count -eq 3) 'Checksum list must contain all three archives'
    foreach ($line in $lines) {
        Assert ($line -match ('^([a-f0-9]{64})  (Tailwatch-v' + [regex]::Escape($sourceVersion) + '-(?:windows-x64|linux-x64|macos-arm64)\.zip)$')) 'Bad checksum format'
        Assert ((Get-FileHash -LiteralPath (Join-Path $output $Matches[2])).Hash -eq $Matches[1]) 'Wrong archive checksum'
    }
    $passed++; Write-Host 'PASS exact contents, archive bytes, executable modes and checksums'
    Reject 'missing-binary' { param($inputPath) Remove-Item -LiteralPath (Join-Path $inputPath 'linux-x64-agent/tailwatch-agent') } -Expected 'Missing required file'
    Reject 'wrong-pe' { param($inputPath) $path = Join-Path $inputPath 'windows-x64-agent/tailwatch-agent.exe'; $bytes = [IO.File]::ReadAllBytes($path); $bytes[0x84] = 0x4c; [IO.File]::WriteAllBytes($path, $bytes) } -Expected 'PE x64'
    Reject 'wrong-viewer' { param($inputPath) $path = Join-Path $inputPath 'windows-x64-viewer/Tailwatch.exe'; $bytes = [IO.File]::ReadAllBytes($path); $bytes[0x84] = 0x4c; [IO.File]::WriteAllBytes($path, $bytes) } -Expected 'PE x64'
    Reject 'wrong-elf' { param($inputPath) $path = Join-Path $inputPath 'linux-x64-agent/tailwatch-agent'; $bytes = [IO.File]::ReadAllBytes($path); $bytes[18] = 183; [IO.File]::WriteAllBytes($path, $bytes) } -Expected 'ELF x64'
    Reject 'wrong-mach' { param($inputPath) $path = Join-Path $inputPath 'macos-arm64-agent/tailwatch-agent'; $bytes = [IO.File]::ReadAllBytes($path); $bytes[4] = 7; [IO.File]::WriteAllBytes($path, $bytes) } -Expected 'Mach-O arm64'
    Reject 'extra-file' { param($inputPath) Set-Content -LiteralPath (Join-Path $inputPath 'linux-x64-agent/secret.txt') -Value 'excluded' } -Expected 'Unexpected artifact entry'
    Reject 'existing-output' { param($inputPath, $output) [void][IO.Directory]::CreateDirectory($output); Set-Content -LiteralPath (Join-Path $output 'keep.txt') -Value 'keep' } -Expected 'already exists'
    foreach ($version in @('../bad', 'v0.1.0', '0.01.0', '0.1', '0.1.0-01', '0.1.0;echo bad')) {
        Reject ('invalid-' + $passed) {} -Version $version -Expected 'Invalid semantic version'
    }
    $otherVersion = if ($sourceVersion -eq '1.2.3') { '1.2.4' } else { '1.2.3' }
    Reject 'version-mismatch' {} -Version $otherVersion -Expected 'does not match'
    Reject 'valid-prerelease-mismatch' {} -Version "$otherVersion-alpha.1" -Expected 'does not match'
    Reject 'valid-build-mismatch' {} -Version "$otherVersion+build.123" -Expected 'does not match'
    Write-Host "$passed packaging tests passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('tailwatch-package-tests-')) { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
