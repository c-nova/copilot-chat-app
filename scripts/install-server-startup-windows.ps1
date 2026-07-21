#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Registers the copilot-chat-app server to start automatically at logon (and restart itself if
    it crashes) using a Windows Scheduled Task - falling back to a plain Startup folder shortcut if
    Task Scheduler registration is blocked (common on corporate-managed PCs via Group Policy).

.DESCRIPTION
    A plain `node.exe` process can't implement the Windows Service Control Manager protocol on its
    own, so a bare `sc.exe create` pointing at node.exe wouldn't behave like a real service (it
    can't respond to Start/Stop/status queries correctly). Task Scheduler's "run at logon, restart
    on failure" mode is the simplest way to get equivalent persistent/self-healing behavior using
    only what's already built into Windows - no NSSM/node-windows/third-party service wrapper.

    This is scoped to the CURRENT user's logon session (mirrors the macOS LaunchAgent counterpart,
    which is also per-user, not a system-wide daemon). Registering a Scheduled Task sometimes needs
    an elevated (Administrator) PowerShell session on corporate-managed PCs, even though a plain
    standard-user account can often do it directly - if it fails with an access-denied error, try
    re-running from "Run as Administrator" first. Some corporate Group Policies restrict Task
    Scheduler entirely, even for admins; in that case (or if you'd rather not elevate), this script
    automatically falls back to creating a shortcut in your Startup folder instead (`shell:startup`),
    which almost never needs special permissions -
    the tradeoff is no automatic restart-on-crash, just "starts when you log in".

.PARAMETER Uninstall
    Stop and remove the scheduled task (and/or Startup folder shortcut) instead of installing it.

.EXAMPLE
    ./scripts/install-server-startup-windows.ps1
    Register the task (or Startup shortcut fallback) and start the server right now.

.EXAMPLE
    ./scripts/install-server-startup-windows.ps1 -Uninstall
    Stop the server and remove whichever of the two was installed.
#>
param(
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serverDir = Join-Path $root "server"
$serverEntryPoint = Join-Path $serverDir "dist\index.js"
$taskName = "CopilotChatServer"
$logFile = Join-Path $env:LOCALAPPDATA "CopilotChatServer\server.log"

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

$startupShortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "$taskName.lnk"

# Finds the node.exe process for this server. New tasks use the absolute entry-point path, which is
# unambiguous. The configured listening port also catches an older task that launched the relative
# dist/index.js path and survived after Task Scheduler stopped only its parent PowerShell process.
function Get-RunningServerProcesses {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" -ErrorAction SilentlyContinue)
    $matchingProcesses = @($processes | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains($serverEntryPoint, [StringComparison]::OrdinalIgnoreCase)
    })

    $port = 5219
    $portLine = Get-Content (Join-Path $serverDir ".env") -ErrorAction SilentlyContinue |
        Where-Object { $_ -match '^PORT=(\d+)' } |
        Select-Object -First 1
    $portMatch = [regex]::Match($portLine, '^PORT=(\d+)')
    if ($portMatch.Success) {
        $port = [int]$portMatch.Groups[1].Value
    }

    $ownerIds = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    $matchingProcesses += $processes | Where-Object { $_.ProcessId -in $ownerIds }
    $matchingProcesses | Sort-Object ProcessId -Unique
}

function Stop-RunningServerProcesses {
    $running = Get-RunningServerProcesses
    if (-not $running) {
        Write-Host "  (nothing currently running)"
        return
    }

    foreach ($proc in $running) {
        Write-Host "  Stopping node.exe (PID $($proc.ProcessId))..."
        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

if ($Uninstall) {
    Write-Step "Stopping and removing the scheduled task ($taskName), if present..."
    Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path $startupShortcut) {
        Write-Step "Removing Startup folder shortcut fallback..."
        Remove-Item $startupShortcut -Force
    }

    Write-Step "Stopping the running server process, if any..."
    Stop-RunningServerProcesses

    Write-Host "Done. Log file left at $logFile if you want to keep it; delete it manually if not."
    exit 0
}


if (-not (Test-Path $serverEntryPoint)) {
    Write-Error "server/dist/index.js not found - build the server first, e.g.:`n  ./scripts/build-windows.ps1"
    exit 1
}
if (-not (Test-Path (Join-Path $serverDir ".env"))) {
    Write-Error "server/.env not found - set it up first (copy server/.env.example, set AUTH_TOKEN), e.g.:`n  ./scripts/build-windows.ps1"
    exit 1
}

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Error "node not found on PATH."
    exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logFile) | Out-Null

# Run node via powershell.exe so stdout/stderr can be redirected to a log file (Task Scheduler /
# a Startup shortcut don't capture a plain process's console output on their own).
$psCommand = "& '$($node.Source)' '$serverEntryPoint' *>> '$logFile'"
$psArgs = "-NoProfile -WindowStyle Hidden -Command `"$psCommand`""

function Install-StartupShortcutFallback {
    Write-Step "Falling back to a Startup folder shortcut instead (no admin/Task Scheduler rights needed)..."
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startupShortcut)
    $shortcut.TargetPath = "powershell.exe"
    $shortcut.Arguments = $psArgs
    $shortcut.WorkingDirectory = $serverDir
    $shortcut.WindowStyle = 7  # minimized
    $shortcut.Save()

    Write-Host ""
    Write-Host "Installed via Startup folder shortcut: $startupShortcut" -ForegroundColor Green
    Write-Host "This starts the server the next time you log in, but does NOT auto-restart it if it" -ForegroundColor Yellow
    Write-Host "crashes (unlike the Scheduled Task approach) - it's a simpler fallback for machines" -ForegroundColor Yellow
    Write-Host "where Task Scheduler is locked down (common on corporate-managed PCs)." -ForegroundColor Yellow
    Write-Step "Starting it now..."
    Start-Process "powershell.exe" -ArgumentList $psArgs -WindowStyle Hidden
}

Write-Step "Registering scheduled task '$taskName'..."
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
try {
    $existingTask | Stop-ScheduledTask -ErrorAction SilentlyContinue
    Stop-RunningServerProcesses

    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $psArgs
    $trigger = New-ScheduledTaskTrigger -AtLogOn

    # Keep it alive: restart up to once a minute, effectively indefinitely, and don't let Task
    # Scheduler kill it for running "too long" (it's meant to run forever).
    $settings = New-ScheduledTaskSettingsSet `
        -RestartCount 999 `
        -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries

    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Force -ErrorAction Stop | Out-Null
    if (Test-Path $startupShortcut) {
        Remove-Item $startupShortcut -Force
    }
    Write-Step "Starting it now..."
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop

    Write-Host ""
    Write-Host "Installed and started via Scheduled Task. Useful commands:" -ForegroundColor Green
    Write-Host "  Get-ScheduledTask -TaskName $taskName | Select State   # check it's running"
    Write-Host "  ./scripts/install-server-startup-windows.ps1 -Uninstall  # stop + remove"
    Write-Host ""
    Write-Host "Note: this task runs at logon for the CURRENT user only (like the macOS LaunchAgent" -ForegroundColor DarkGray
    Write-Host "counterpart) - it won't run before anyone logs in. It restarts automatically if it" -ForegroundColor DarkGray
    Write-Host "crashes; use -Uninstall to actually stop it rather than just ending the process." -ForegroundColor DarkGray
}
catch {
    Write-Host ""
    Write-Host "Couldn't register the Scheduled Task: $($_.Exception.Message)" -ForegroundColor Yellow
    if ($existingTask) {
        Write-Host "The existing task is still registered; restarting it instead of adding a duplicate Startup shortcut." -ForegroundColor Yellow
        if (Test-Path $startupShortcut) {
            Remove-Item $startupShortcut -Force
        }
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        Write-Host "Existing scheduled task restarted. Re-run this installer from an Administrator PowerShell to update its definition." -ForegroundColor Green
        Write-Host ""
        Write-Host "Follow server logs: Get-Content -Wait `"$logFile`""
        exit 0
    }
    Write-Host "Most commonly this just means this PowerShell session isn't elevated - try closing" -ForegroundColor Yellow
    Write-Host "this window and re-running the script from an Administrator PowerShell (right-click" -ForegroundColor Yellow
    Write-Host "PowerShell -> 'Run as Administrator'). Some corporate-managed PCs restrict Task" -ForegroundColor Yellow
    Write-Host "Scheduler via Group Policy even for admins, in which case falling back below is" -ForegroundColor Yellow
    Write-Host "expected - but it's worth trying elevated first." -ForegroundColor Yellow
    Install-StartupShortcutFallback
}

Write-Host ""
Write-Host "Follow server logs: Get-Content -Wait `"$logFile`""

