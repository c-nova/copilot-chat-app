#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Registers the copilot-chat-app server as a Windows Scheduled Task, so it starts
    automatically at logon and gets restarted automatically if it ever crashes/exits -
    "server-like" behavior using only built-in Windows tooling (no NSSM/node-windows/third-party
    service wrapper needed).

.DESCRIPTION
    A plain `node.exe` process can't implement the Windows Service Control Manager protocol on its
    own, so a bare `sc.exe create` pointing at node.exe wouldn't behave like a real service (it
    can't respond to Start/Stop/status queries correctly). Task Scheduler's "run at logon, restart
    on failure" mode is the simplest way to get equivalent persistent/self-healing behavior using
    only what's already built into Windows.

    This is scoped to the CURRENT user's logon session (mirrors the macOS LaunchAgent counterpart,
    which is also per-user, not a system-wide daemon), and does not require Administrator rights.

.PARAMETER Uninstall
    Stop and remove the scheduled task instead of installing it.

.EXAMPLE
    ./scripts/install-server-startup-windows.ps1
    Register the task and start the server right now.

.EXAMPLE
    ./scripts/install-server-startup-windows.ps1 -Uninstall
    Stop the server and remove the scheduled task.
#>
param(
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serverDir = Join-Path $root "server"
$taskName = "CopilotChatServer"
$logFile = Join-Path $env:LOCALAPPDATA "CopilotChatServer\server.log"

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

if ($Uninstall) {
    Write-Step "Stopping and removing the scheduled task ($taskName)..."
    Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Stop-ScheduledTask -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Done. (Log file left at $logFile if you want to keep it; delete it manually if not.)"
    exit 0
}

if (-not (Test-Path (Join-Path $serverDir "dist/index.js"))) {
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

Write-Step "Registering scheduled task '$taskName'..."

# Run node via powershell.exe so stdout/stderr can be redirected to a log file (Task Scheduler
# doesn't capture a plain process's console output on its own).
$psCommand = "Set-Location -LiteralPath '$serverDir'; & '$($node.Source)' dist/index.js *>> '$logFile'"
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -Command `"$psCommand`""

$trigger = New-ScheduledTaskTrigger -AtLogOn

# Keep it alive: restart up to once a minute, effectively indefinitely, and don't let Task
# Scheduler kill it for running "too long" (it's meant to run forever).
$settings = New-ScheduledTaskSettingsSet `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null

Write-Step "Starting it now..."
Start-ScheduledTask -TaskName $taskName

Write-Host ""
Write-Host "Installed and started. Useful commands:" -ForegroundColor Green
Write-Host "  Get-ScheduledTask -TaskName $taskName | Select State   # check it's running"
Write-Host "  Get-Content -Wait `"$logFile`"                          # follow server logs"
Write-Host "  ./scripts/install-server-startup-windows.ps1 -Uninstall  # stop + remove"
Write-Host ""
Write-Host "Note: this task runs at logon for the CURRENT user only (like the macOS LaunchAgent" -ForegroundColor DarkGray
Write-Host "counterpart) - it won't run before anyone logs in. It restarts automatically if it" -ForegroundColor DarkGray
Write-Host "crashes; use -Uninstall to actually stop it rather than just ending the process." -ForegroundColor DarkGray
