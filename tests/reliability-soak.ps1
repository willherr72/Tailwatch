#requires -Version 7.2
# Read-only observation of an already running local agent. No machine inventory is saved.
[CmdletBinding()]
param(
    [ValidateRange(1,1440)][int]$Minutes = 120,
    [ValidateRange(1,65535)][int]$Port = 48173,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output file; existing evidence is never overwritten' }
$parent = Split-Path $output -Parent
if (!(Test-Path -LiteralPath $parent -PathType Container)) { throw 'Output directory must already exist' }
$writer = [IO.StreamWriter]::new([IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read))
$writer.AutoFlush = $true
$clock = [Diagnostics.Stopwatch]::StartNew()
$observations = 0; $fresh = 0; $failures = 0; $restarts = 0
$boot = $null; $sequence = $null; $lastAdvance = 0.0; $lastFresh = 0.0; $maxGap = 0.0
try {
    while ($clock.Elapsed.TotalMinutes -lt $Minutes) {
        $sample = $null; $reason = $null
        try { $sample = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/v1/snapshot" -NoProxy -TimeoutSec 2 }
        catch [Net.Http.HttpRequestException] { $reason = 'endpoint_unavailable' }
        catch [Threading.Tasks.TaskCanceledException] { $reason = 'request_timeout' }
        $seconds = $clock.Elapsed.TotalSeconds
        $observations++
        $record = [ordered]@{ timeUtc=[DateTimeOffset]::UtcNow.ToString('O'); elapsedSeconds=[Math]::Round($seconds,2); fresh=$false; reason=$reason }
        if ($null -ne $sample) {
            if ($null -ne $boot -and $boot -cne $sample.boot_id) { $restarts++ }
            if ($boot -cne $sample.boot_id -or $sequence -ne $sample.sequence) { $lastAdvance = $seconds }
            $boot=$sample.boot_id; $sequence=$sample.sequence
            $record.fresh=$sample.age_ms -le 10000 -and ($seconds-$lastAdvance) -le 10 -and $sample.cpu.status -ceq 'ok' -and $sample.ram.status -ceq 'ok'
            if (!$record.fresh) { $record.reason='stale_or_unavailable_cpu_ram' }
            $record['agentVersion']=$sample.agent_version
            $record['sequence']=$sequence
            $record['sampleAgeMs']=$sample.age_ms
            $record['gpuStatus']=$sample.gpu_status
        }
        $maxGap=[Math]::Max($maxGap,$seconds-$lastFresh)
        if ($record.fresh) { $fresh++; $lastFresh=$seconds } else { $failures++ }
        $writer.WriteLine(($record | ConvertTo-Json -Compress))
        Start-Sleep -Seconds 2
    }
    $summary=[ordered]@{
        type='summary'; durationSeconds=[Math]::Round($clock.Elapsed.TotalSeconds,1)
        observations=$observations; fresh=$fresh; unavailable=$failures; agentRestarts=$restarts
        maximumGapSeconds=[Math]::Round($maxGap,2); passed=($fresh -gt 0 -and $maxGap -le 30)
    }
    $writer.WriteLine(($summary | ConvertTo-Json -Compress))
    $summary | ConvertTo-Json
    if (!$summary.passed) { throw 'Soak observed a CPU/RAM gap longer than 30 seconds or no fresh readings; inspect the saved evidence' }
} finally { $writer.Dispose() }
