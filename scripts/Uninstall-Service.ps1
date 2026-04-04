#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the SqlAgMonitor Windows Service.

.DESCRIPTION
    Stops the service if running, then removes it using sc.exe delete.
    Optionally removes the Windows Firewall rule created during install.
    Does not delete the published files — remove those manually if desired.

.PARAMETER ServiceName
    Windows Service name to remove. Default: SqlAgMonitorService.

.PARAMETER RemoveFirewallRule
    If specified, removes the inbound firewall rule for the service port.

.PARAMETER Port
    TCP port used by the service. Used to identify the firewall rule. Default: 58432.

.EXAMPLE
    .\Uninstall-Service.ps1
    .\Uninstall-Service.ps1 -RemoveFirewallRule
    .\Uninstall-Service.ps1 -ServiceName "MyCustomServiceName" -RemoveFirewallRule -Port 9000
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "SqlAgMonitorService",
    [switch]$RemoveFirewallRule,
    [int]$Port = 58432
)

$ErrorActionPreference = "Stop"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not installed. Nothing to do." -ForegroundColor Yellow
    return
}

if ($existing.Status -eq "Running") {
    Write-Host "Stopping service '$ServiceName'..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
    Write-Host "Service stopped." -ForegroundColor Green
}

Write-Host "Removing service '$ServiceName'..." -ForegroundColor Cyan
sc.exe delete $ServiceName

if ($LASTEXITCODE -ne 0) {
    Write-Error "sc.exe delete failed with exit code $LASTEXITCODE."
    return
}

Write-Host ""
Write-Host "Service '$ServiceName' removed successfully." -ForegroundColor Green

if ($RemoveFirewallRule) {
    $ruleName = "SqlAgMonitor Service (TCP $Port)"
    Write-Host "Removing firewall rule '$ruleName'..." -ForegroundColor Cyan
    netsh advfirewall firewall delete rule name="$ruleName" 2>$null | Out-Null
    Write-Host "  Firewall rule removed." -ForegroundColor Green
}

Write-Host "Published files were not deleted. Remove them manually if no longer needed."
