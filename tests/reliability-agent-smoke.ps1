#requires -Version 7.2
# Exercises only processes started by this test, on an unused loopback port.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AgentPath,
    [Parameter(Mandatory)][string]$TailscaleFixturePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'This process-level fault injection test requires Windows' }
$agentExecutable = (Resolve-Path -LiteralPath $AgentPath).Path
$fixture = (Resolve-Path -LiteralPath $TailscaleFixturePath).Path
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
try { $probe.Start(); $port = $probe.LocalEndpoint.Port } finally { $probe.Stop() }
$endpoint = "http://127.0.0.1:$port/v1/snapshot"
$agent = $null
$workers = [Collections.Generic.List[Diagnostics.Process]]::new()
$originalFixture = $env:TAILWATCH_TAILSCALE
$previousBoot = $null
$lastSequence = [long]-1

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TailwatchWorkerFault {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr process);
}
'@

function Read-AdvancingSnapshot([int]$TimeoutSeconds = 15) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $agent.Refresh()
        if ($agent.HasExited) { throw "CPU/RAM agent exited with code $($agent.ExitCode)" }
        $sample = $null
        try { $sample = Invoke-RestMethod -Uri $endpoint -NoProxy -TimeoutSec 2 }
        catch [Net.Http.HttpRequestException] { }
        catch [Threading.Tasks.TaskCanceledException] { }
        if ($null -ne $sample -and $sample.sequence -gt $script:lastSequence) {
            if ($sample.node_id -cne 'test-self') { throw 'Unexpected fixture identity' }
            if ($sample.age_ms -gt 10000 -or $sample.cpu.status -cne 'ok' -or $sample.ram.status -cne 'ok') {
                throw 'CPU/RAM stopped being fresh during GPU worker fault'
            }
            if ($null -ne $script:previousBoot -and $sample.boot_id -cne $script:previousBoot) {
                throw 'Agent restarted instead of containing GPU failure'
            }
            $script:previousBoot = $sample.boot_id
            $script:lastSequence = $sample.sequence
            return $sample
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'Agent did not produce an advancing CPU/RAM sample before deadline'
}

function Get-OwnedWorker([int]$ExceptId = 0, [int]$TimeoutSeconds = 20) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($agent.Id)" |
            Where-Object { $_.ExecutablePath -ieq $agentExecutable -and $_.CommandLine -match '\bgpu-worker\b' -and $_.ProcessId -ne $ExceptId })
        if ($children.Count -gt 1) { throw 'Agent started duplicate GPU workers' }
        if ($children.Count -eq 1) {
            $worker = Get-Process -Id $children[0].ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $worker) { $workers.Add($worker); return $worker }
        }
        $agent.Refresh()
        if ($agent.HasExited) { throw 'Agent exited while awaiting its GPU worker' }
        Start-Sleep -Milliseconds 200
    }
    throw 'GPU worker was not started/replaced before deadline'
}

try {
    $env:TAILWATCH_TAILSCALE = $fixture
    $agent = Start-Process -FilePath $agentExecutable -ArgumentList @('run', '--local-only', '--port', $port) -WindowStyle Hidden -PassThru
    [void](Read-AdvancingSnapshot)
    $worker = Get-OwnedWorker
    $worker.Kill()
    if (!$worker.WaitForExit(5000)) { throw 'Injected GPU worker crash did not terminate' }
    [void](Read-AdvancingSnapshot)
    $replacement = Get-OwnedWorker -ExceptId $worker.Id
    Write-Output 'PASS GPU worker crash: CPU/RAM remained live and the agent replaced its worker.'

    if ([TailwatchWorkerFault]::NtSuspendProcess($replacement.Handle) -ne 0) { throw 'Could not suspend the owned GPU worker' }
    try {
        [void](Read-AdvancingSnapshot)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $unavailable = $false
        while ($clock.Elapsed.TotalSeconds -lt 15) {
            $sample = Read-AdvancingSnapshot
            if ($sample.gpu_status -eq 'worker_timeout' -and @($sample.gpus).Count -eq 0) { $unavailable = $true; break }
        }
        if (!$unavailable) { throw 'A hung worker did not become unavailable with a timeout reason' }
        if (!$replacement.WaitForExit(5000)) { throw 'Timed-out GPU worker was not terminated' }
    } finally {
        $replacement.Refresh()
        if (!$replacement.HasExited) { [void][TailwatchWorkerFault]::NtResumeProcess($replacement.Handle) }
    }
    $recovered = Get-OwnedWorker -ExceptId $replacement.Id
    [void](Read-AdvancingSnapshot)
    Write-Output 'PASS GPU worker hang: bounded timeout, unavailable GPU, uninterrupted CPU/RAM, and worker replacement.'

    # Kill the parent alone, not its tree: the child's EOF watchdog must stop it.
    $agent.Kill()
    if (!$agent.WaitForExit(5000)) { throw 'Agent did not exit' }
    if (!$recovered.WaitForExit(5000)) { throw 'GPU worker survived loss of its parent process' }
    Write-Output 'PASS abrupt parent exit: no orphan GPU worker.'
} finally {
    $env:TAILWATCH_TAILSCALE = $originalFixture
    if ($null -ne $agent) {
        try { if (!$agent.HasExited) { $agent.Kill($true); [void]$agent.WaitForExit(5000) } }
        finally { $agent.Dispose() }
    }
    foreach ($worker in $workers) {
        try { if (!$worker.HasExited) { $worker.Kill(); [void]$worker.WaitForExit(5000) } }
        finally { $worker.Dispose() }
    }
}
