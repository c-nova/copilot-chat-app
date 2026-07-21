#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Restarts the installed copilot-chat-app server on Windows.

.DESCRIPTION
    Restarts the existing CopilotChatServer Scheduled Task, or the Startup-folder fallback when
    Task Scheduler is unavailable. It also stops a child node.exe process left behind when Task
    Scheduler terminates only the parent PowerShell process.
#>

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serverDir = Join-Path $root "server"
$serverEntryPoint = Join-Path $serverDir "dist\index.js"
$taskName = "CopilotChatServer"
$startupShortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "$taskName.lnk"
$logFile = Join-Path $env:LOCALAPPDATA "$taskName\server.log"

if (-not (Test-Path $serverEntryPoint)) {
    throw "server/dist/index.js not found. Build the server first with .\scripts\build-windows.ps1."
}
if (-not (Test-Path (Join-Path $serverDir ".env"))) {
    throw "server/.env not found. Configure the server before restarting it."
}

function Get-ConfiguredPort {
    $portLine = Get-Content (Join-Path $serverDir ".env") -ErrorAction SilentlyContinue |
        Where-Object { $_ -match '^PORT=(\d+)' } |
        Select-Object -First 1
    $portMatch = [regex]::Match($portLine, '^PORT=(\d+)')
    if ($portMatch.Success) {
        return [int]$portMatch.Groups[1].Value
    }
    return 5219
}

function Get-RunningServerProcesses {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" -ErrorAction SilentlyContinue)
    $matchingProcesses = @($processes | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains($serverEntryPoint, [StringComparison]::OrdinalIgnoreCase)
    })

    $ownerIds = @(Get-NetTCPConnection -State Listen -LocalPort (Get-ConfiguredPort) -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    $matchingProcesses += $processes | Where-Object { $_.ProcessId -in $ownerIds }
    $matchingProcesses | Sort-Object ProcessId -Unique
}

function Wait-ForScheduledTaskToStop {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue).State -eq "Running") {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Scheduled Task '$taskName' did not stop within 10 seconds."
        }
        [Threading.Thread]::Sleep(100)
    }
}

function Wait-ForServerToListen {
    $port = Get-ConfiguredPort
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($listener) {
            return
        }
        [Threading.Thread]::Sleep(100)
    } while ([DateTime]::UtcNow -lt $deadline)

    $taskResult = if ($task) {
        (Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue).LastTaskResult
    }
    throw "Server did not start listening on port $port within 10 seconds. Scheduled Task result: $taskResult. Check $logFile."
}

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$hasStartupShortcut = Test-Path $startupShortcut
if (-not $task -and -not $hasStartupShortcut) {
    throw "Server auto-start is not installed. Run .\scripts\install-server-startup-windows.ps1 first."
}

Write-Host "==> Stopping Copilot chat server..." -ForegroundColor Cyan
if ($task) {
    $task | Stop-ScheduledTask -ErrorAction SilentlyContinue
}
$runningProcesses = @(Get-RunningServerProcesses)
foreach ($process in $runningProcesses) {
    Write-Host "  Stopping node.exe (PID $($process.ProcessId))..."
    Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
}
foreach ($process in $runningProcesses) {
    Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
}
if ($task) {
    Wait-ForScheduledTaskToStop
}

Write-Host "==> Starting Copilot chat server..." -ForegroundColor Cyan
if ($task) {
    Start-ScheduledTask -TaskName $taskName
    Write-Host "Restarted Scheduled Task '$taskName'." -ForegroundColor Green
}
else {
    Start-Process $startupShortcut
    Write-Host "Restarted from Startup shortcut '$startupShortcut'." -ForegroundColor Green
}

Wait-ForServerToListen
Write-Host "Server is listening on port $(Get-ConfiguredPort)." -ForegroundColor Green
Write-Host "Log: $logFile"
