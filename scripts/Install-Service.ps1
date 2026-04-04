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

    For domain accounts, the script automatically grants the
    SeServiceLogonRight ("Log on as a service") privilege.

    Optionally creates a Windows Firewall inbound rule for the service port.

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

.PARAMETER Port
    TCP port the service listens on. Used for firewall rule creation. Default: 58432.

.PARAMETER CreateFirewallRule
    If specified, creates an inbound Windows Firewall rule for the service port.

.EXAMPLE
    .\Install-Service.ps1
    .\Install-Service.ps1 -ServiceAccount "DOMAIN\svc_agmonitor" -ServicePassword "P@ssw0rd"
    .\Install-Service.ps1 -CreateFirewallRule -Port 58432
#>
[CmdletBinding()]
param(
    [string]$InstallPath = "C:\Program Files\SqlAgMonitor",
    [string]$ServiceName = "SqlAgMonitorService",
    [string]$DisplayName = "SQL Server AG Monitor Service",
    [string]$ServiceAccount = "NT AUTHORITY\LOCAL SERVICE",
    [string]$ServicePassword = "",
    [int]$Port = 58432,
    [switch]$CreateFirewallRule
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

# Grant SeServiceLogonRight for domain accounts
$builtInAccounts = @("NT AUTHORITY\LOCAL SERVICE", "NT AUTHORITY\NETWORK SERVICE", "LocalSystem")
if ($ServiceAccount -notin $builtInAccounts) {
    Write-Host "Granting 'Log on as a service' right to $ServiceAccount..." -ForegroundColor Cyan
    $sid = (New-Object System.Security.Principal.NTAccount($ServiceAccount)).Translate(
        [System.Security.Principal.SecurityIdentifier]).Value
    $tempCfg = [System.IO.Path]::GetTempFileName()
    secedit /export /cfg $tempCfg /areas USER_RIGHTS | Out-Null
    $content = Get-Content $tempCfg -Raw
    if ($content -match "SeServiceLogonRight\s*=\s*(.*)") {
        $existing = $Matches[1]
        if ($existing -notmatch [regex]::Escape($sid)) {
            $content = $content -replace "(SeServiceLogonRight\s*=\s*.*)", "`$1,*$sid"
        }
    } else {
        $content = $content -replace "(\[Privilege Rights\])", "`$1`r`nSeServiceLogonRight = *$sid"
    }
    Set-Content $tempCfg $content
    secedit /configure /db ([System.IO.Path]::GetTempFileName()) /cfg $tempCfg /areas USER_RIGHTS | Out-Null
    Remove-Item $tempCfg -ErrorAction SilentlyContinue
    Write-Host "  Granted." -ForegroundColor Green
}

# Create firewall rule if requested
if ($CreateFirewallRule) {
    $ruleName = "SqlAgMonitor Service (TCP $Port)"
    Write-Host "Creating firewall rule '$ruleName'..." -ForegroundColor Cyan
    netsh advfirewall firewall delete rule name="$ruleName" 2>$null | Out-Null
    netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=tcp localport=$Port profile=any
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to create firewall rule. You may need to create it manually."
    } else {
        Write-Host "  Firewall rule created for TCP port $Port." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Service installed successfully." -ForegroundColor Green
Write-Host ""
Write-Host "Before starting the service:" -ForegroundColor Yellow
Write-Host "  1. Ensure config exists at: $env:ProgramData\SqlAgMonitor\"
Write-Host "  2. Create the initial admin user by running:"
Write-Host "     Invoke-RestMethod -Method POST -Uri http://localhost:${Port}/api/auth/setup ``"
Write-Host "       -ContentType 'application/json' ``"
Write-Host "       -Body '{`"username`":`"admin`",`"password`":`"YourPassword`"}'"
Write-Host ""
Write-Host "To start the service:"
Write-Host "  Start-Service $ServiceName"
