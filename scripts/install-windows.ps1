# Run elevated on a Windows machine that should report to another viewer.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string[]]$AllowPeer,
    [string]$Agent = (Join-Path $PSScriptRoot 'tailwatch-agent.exe'),
    [ValidateRange(1,65535)][int]$Port = 48173
)
$ErrorActionPreference='Stop'
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this installer in an elevated PowerShell.'}
if(Get-Service -Name TailwatchAgent -ErrorAction SilentlyContinue){throw 'TailwatchAgent already exists. Stop it and replace its binary to update; configuration is preserved.'}
$destination=Join-Path $env:ProgramFiles 'Tailwatch'
$configDirectory=Join-Path $env:ProgramData 'Tailwatch'
$config=Join-Path $configDirectory 'agent.json'
if(Test-Path -LiteralPath $config){throw 'An existing agent.json was found. Keep it or move it aside explicitly before installing a new identity.'}
New-Item -ItemType Directory -Force -Path $destination,$configDirectory | Out-Null
$binary=Join-Path $destination 'tailwatch-agent.exe'
Copy-Item -LiteralPath $Agent -Destination $binary
$arguments=@('configure','--output',$config,'--port',"$Port")
foreach($peer in $AllowPeer){$arguments+=@('--allow-peer',$peer)}
& $binary @arguments
if($LASTEXITCODE -ne 0){throw 'Could not configure the agent. Check Tailscale and the peer addresses.'}
# Only administrators/SYSTEM may replace the executable or policy; LocalService can read.
icacls.exe $configDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not restrict configuration permissions'}
$serviceCommand='"'+$binary+'" service --config "'+$config+'"'
sc.exe create TailwatchAgent binPath= $serviceCommand start= delayed-auto obj= 'NT AUTHORITY\LocalService' DisplayName= 'Tailwatch Agent' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not create the service'}
$localAddresses=(& 'C:/Program Files/Tailscale/tailscale.exe' status --json | ConvertFrom-Json).Self.TailscaleIPs
New-NetFirewallRule -Name TailwatchAgent -DisplayName 'Tailwatch Agent (allowed Tailscale peers)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $binary -LocalAddress $localAddresses -RemoteAddress $AllowPeer -Profile Any | Out-Null
Start-Service TailwatchAgent
Write-Output 'TailwatchAgent installed. Add the same port in the viewer if you changed the default.'
