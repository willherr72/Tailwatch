#requires -Version 7.2
# Normal packaged application startup; run only on an isolated Windows CI runner.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$TailscaleFixturePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This startup test requires an isolated GitHub-hosted Windows runner'
}
$viewerPath = (Resolve-Path -LiteralPath (Join-Path $PackageDirectory 'Tailwatch.exe')).Path
$fixture = (Resolve-Path -LiteralPath $TailscaleFixturePath).Path
if (!(Test-Path -LiteralPath (Join-Path $PackageDirectory 'tailwatch-agent.exe') -PathType Leaf)) { throw 'Packaged agent is missing' }
# Fail before launch if the test's loopback port is already occupied.
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 48173)
try { $probe.Start() } finally { $probe.Stop() }
$previousFixture = $env:TAILWATCH_TAILSCALE
$viewer = $null
try {
    # Both the viewer and its owned local-only agent inherit this CLI fixture.
    # The fixture supplies a synthetic identity; no real Tailscale commands run.
    $env:TAILWATCH_TAILSCALE = $fixture
    $viewer = Start-Process -FilePath $viewerPath -WindowStyle Hidden -PassThru
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $firstSequence = $null
    $ready = $false
    while ($clock.Elapsed.TotalSeconds -lt 45) {
        $viewer.Refresh()
        if ($viewer.HasExited) { throw "Packaged viewer exited during startup (exit $($viewer.ExitCode))" }
        $snapshot = $null
        try { $snapshot = Invoke-RestMethod -Uri 'http://127.0.0.1:48173/v1/snapshot' -NoProxy -TimeoutSec 2 }
        catch [Net.Http.HttpRequestException] { }
        catch [Threading.Tasks.TaskCanceledException] { }
        if ($null -ne $snapshot) {
            if ($snapshot.node_id -cne 'test-self') { throw 'Unexpected local agent identity' }
            if ($snapshot.age_ms -gt 10000) { throw 'Local agent returned stale startup metrics' }
            if ($null -eq $firstSequence) { $firstSequence = $snapshot.sequence }
            elseif ($snapshot.sequence -gt $firstSequence) { $ready = $true; break }
        }
        Start-Sleep -Milliseconds 250
    }
    if (!$ready) { throw 'Packaged viewer did not start its local agent with advancing snapshots within 45 seconds' }
    $viewer.Refresh()
    if ($viewer.HasExited) { throw 'Packaged viewer exited after local agent startup' }
    Write-Host 'Packaged Windows viewer stayed alive and started its local-only agent with fresh advancing snapshots.'
} finally {
    $env:TAILWATCH_TAILSCALE = $previousFixture
    if ($null -ne $viewer) {
        try { if (!$viewer.HasExited) { $viewer.Kill($true); [void]$viewer.WaitForExit(10000) } }
        finally { $viewer.Dispose() }
    }
}
