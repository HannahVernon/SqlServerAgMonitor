#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the SqlAgMonitor Windows Service.

.DESCRIPTION
    Stops the service if running, then removes it using sc.exe delete.
    Does not delete the published files — remove those manually if desired.

.PARAMETER ServiceName
    Windows Service name to remove. Default: SqlAgMonitorService.

.EXAMPLE
    .\Uninstall-Service.ps1
    .\Uninstall-Service.ps1 -ServiceName "MyCustomServiceName"
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "SqlAgMonitorService"
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
Write-Host "Published files were not deleted. Remove them manually if no longer needed."
