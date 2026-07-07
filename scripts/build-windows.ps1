#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the copilot-chat-app server and the Windows client in one go.

.DESCRIPTION
    - Installs server dependencies and builds the TypeScript server (server/dist/*.js).
    - Creates server/.env from server/.env.example on first run (you still need to edit
      AUTH_TOKEN yourself before starting the server for real).
    - Builds the Windows (net10.0-windows10.0.19041.0) client.

.PARAMETER Run
    Also start the server (in a new window) and launch the Windows client (dotnet run) after
    building. Without this switch, the script only builds - nothing is started.

.PARAMETER Package
    Instead of a Debug build, publish a self-contained, standalone folder (dotnet publish,
    Release, unpackaged/no MSIX) that can be copied to any Windows PC and run without .NET or
    this repo installed. Can't be combined with -Run.

.EXAMPLE
    ./scripts/build-windows.ps1
    Build the server and the Windows client, but don't start/run anything.

.EXAMPLE
    ./scripts/build-windows.ps1 -Run
    Build everything, start the server in its own window, then run the Windows client.

.EXAMPLE
    ./scripts/build-windows.ps1 -Package
    Build the server, then publish a standalone, self-contained Windows client folder.
#>
param(
    [switch]$Run,
    [switch]$Package
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ($Run -and $Package) {
    Write-Error "-Run and -Package can't be used together - pick one."
    exit 1
}

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

Write-Step "Installing server dependencies..."
Push-Location "$root/server"
try {
    npm install
    if (-not (Test-Path ".env")) {
        Copy-Item ".env.example" ".env"
        Write-Host "Created server/.env from .env.example - edit AUTH_TOKEN (and BROWSE_ROOTS/WORK_DIR if needed) before starting the server for real!" -ForegroundColor Yellow
    }

    Write-Step "Building server (TypeScript -> server/dist)..."
    npm run build
}
finally {
    Pop-Location
}

if ($Package) {
    Write-Step "Publishing Windows client as a standalone, self-contained folder (Release)..."
    Push-Location "$root/client/CopilotChatApp"
    try {
        dotnet publish -f net10.0-windows10.0.19041.0 -c Release `
            -p:RuntimeIdentifierOverride=win-x64 `
            -p:WindowsPackageType=None `
            -p:WindowsAppSDKSelfContained=true
        $publishDir = "bin/Release/net10.0-windows10.0.19041.0/win-x64/publish"
        Write-Host ""
        Write-Host "Published: $root/client/CopilotChatApp/$publishDir" -ForegroundColor Green
        Write-Host "Copy that whole folder to wherever you want to run it from - CopilotChatApp.exe" -ForegroundColor DarkGray
        Write-Host "inside it needs nothing else installed (no .NET, no MSIX, no installer)." -ForegroundColor DarkGray
    }
    finally {
        Pop-Location
    }
    exit 0
}

Write-Step "Building Windows client (net10.0-windows10.0.19041.0)..."
Push-Location "$root/client/CopilotChatApp"
try {
    dotnet build -f net10.0-windows10.0.19041.0
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green

if ($Run) {
    Write-Step "Starting server in a new window..."
    Start-Process pwsh -ArgumentList "-NoExit", "-Command", "cd '$root/server'; npm start"

    Write-Step "Running Windows client..."
    Push-Location "$root/client/CopilotChatApp"
    try {
        dotnet run -f net10.0-windows10.0.19041.0
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "Run with -Run to also start the server and launch the client, or -Package to build a" -ForegroundColor DarkGray
    Write-Host "standalone installable folder instead, e.g.:" -ForegroundColor DarkGray
    Write-Host "  ./scripts/build-windows.ps1 -Run" -ForegroundColor DarkGray
    Write-Host "  ./scripts/build-windows.ps1 -Package" -ForegroundColor DarkGray
}
