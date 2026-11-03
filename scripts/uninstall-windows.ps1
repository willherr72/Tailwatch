# Removes the service and its firewall rule. Preserves binaries and configuration.
$ErrorActionPreference='Stop'
if(Get-Service -Name TailwatchAgent -ErrorAction SilentlyContinue){Stop-Service TailwatchAgent;sc.exe delete TailwatchAgent | Out-Null;if($LASTEXITCODE -ne 0){throw 'Could not delete service'}}
Get-NetFirewallRule -Name TailwatchAgent -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Output 'Service removed. Configuration and binaries were preserved in ProgramData/Tailwatch and Program Files/Tailwatch.'
