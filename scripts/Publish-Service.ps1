#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes the SqlAgMonitor Windows Service as a self-contained deployment.

.DESCRIPTION
    Runs dotnet publish for the SqlAgMonitor.Service project, producing a
    self-contained, single-file executable for the target runtime. The output
    is placed in the specified directory (default: C:\Program Files\SqlAgMonitor).

.PARAMETER OutputPath
    Directory where the published service files will be placed.

.PARAMETER Runtime
    Target runtime identifier. Default: win-x64.

.EXAMPLE
    .\Publish-Service.ps1
    .\Publish-Service.ps1 -OutputPath "D:\Services\SqlAgMonitor" -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string]$OutputPath = "C:\Program Files\SqlAgMonitor",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "..\src\SqlAgMonitor.Service\SqlAgMonitor.Service.csproj"
if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found at $projectPath. Run this script from the repository scripts\ directory."
    return
}

Write-Host "Publishing SqlAgMonitor.Service..." -ForegroundColor Cyan
Write-Host "  Runtime:    $Runtime"
Write-Host "  Output:     $OutputPath"
Write-Host ""

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $OutputPath

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
    return
}

Write-Host ""
Write-Host "Published successfully to: $OutputPath" -ForegroundColor Green
Write-Host "Next step: run Install-Service.ps1 to register the Windows Service."
