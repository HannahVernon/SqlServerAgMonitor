#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the SqlAgMonitor Windows Service.

.DESCRIPTION
    Registers SqlAgMonitor.Service.exe as a Windows Service using sc.exe.
    The service runs under the Local Service account by default, which is
    sufficient for network access to SQL Server instances. Override with
    -ServiceAccount for a domain account.

    The service starts automatically on boot and is configured for delayed
    start to reduce boot contention.

.PARAMETER InstallPath
    Directory containing the published SqlAgMonitor.Service.exe.

.PARAMETER ServiceName
    Windows Service name. Default: SqlAgMonitorService.

.PARAMETER DisplayName
    Display name shown in services.msc. Default: SQL Server AG Monitor Service.

.PARAMETER ServiceAccount
    Account to run the service as. Default: "NT AUTHORITY\LOCAL SERVICE".
    For SQL Server instances requiring Windows auth, use a domain service account.

.PARAMETER ServicePassword
    Password for the service account (required for domain accounts, omit for built-in accounts).

.EXAMPLE
    .\Install-Service.ps1
    .\Install-Service.ps1 -ServiceAccount "DOMAIN\svc_agmonitor" -ServicePassword "P@ssw0rd"
#>
[CmdletBinding()]
param(
    [string]$InstallPath = "C:\Program Files\SqlAgMonitor",
    [string]$ServiceName = "SqlAgMonitorService",
    [string]$DisplayName = "SQL Server AG Monitor Service",
    [string]$ServiceAccount = "NT AUTHORITY\LOCAL SERVICE",
    [string]$ServicePassword = ""
)

$ErrorActionPreference = "Stop"

$exePath = Join-Path $InstallPath "SqlAgMonitor.Service.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "Service executable not found at $exePath. Run Publish-Service.ps1 first."
    return
}

# Check if the service already exists
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Error "Service '$ServiceName' already exists (Status: $($existing.Status)). Run Uninstall-Service.ps1 first to remove it."
    return
}

Write-Host "Installing Windows Service..." -ForegroundColor Cyan
Write-Host "  Service Name: $ServiceName"
Write-Host "  Display Name: $DisplayName"
Write-Host "  Executable:   $exePath"
Write-Host "  Run As:       $ServiceAccount"
Write-Host ""

$binPath = "`"$exePath`""

if ($ServicePassword) {
    sc.exe create $ServiceName `
        binPath= $binPath `
        DisplayName= $DisplayName `
        start= delayed-auto `
        obj= $ServiceAccount `
        password= $ServicePassword
} else {
    sc.exe create $ServiceName `
        binPath= $binPath `
        DisplayName= $DisplayName `
        start= delayed-auto `
        obj= $ServiceAccount
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "sc.exe create failed with exit code $LASTEXITCODE."
    return
}

# Set the service description
sc.exe description $ServiceName "Monitors SQL Server Availability Groups and Distributed Availability Groups. Provides real-time status via SignalR to desktop clients."

# Configure recovery: restart on first and second failure, do nothing on third
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/120000//

Write-Host ""
Write-Host "Service installed successfully." -ForegroundColor Green
Write-Host ""
Write-Host "Before starting the service:" -ForegroundColor Yellow
Write-Host "  1. Copy your config.json to: $env:APPDATA\SqlAgMonitor\"
Write-Host "  2. Create the initial admin user by running:"
Write-Host "     Invoke-RestMethod -Method POST -Uri http://localhost:58432/api/auth/setup ``"
Write-Host "       -ContentType 'application/json' ``"
Write-Host "       -Body '{`"username`":`"admin`",`"password`":`"YourPassword`"}'"
Write-Host ""
Write-Host "To start the service:"
Write-Host "  Start-Service $ServiceName"
