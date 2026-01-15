# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2025, ModernUO
#
# IORingGroup Linux Benchmark Script (PowerShell for Windows/Docker Desktop)
#
# Usage:
#   .\scripts\benchmark.ps1                     # Run both backends with default 60s duration
#   .\scripts\benchmark.ps1 -Backend iouring    # Run io_uring only
#   .\scripts\benchmark.ps1 -Backend epoll      # Run epoll only
#   .\scripts\benchmark.ps1 -Backend iouring -Duration 30    # Run io_uring for 30 seconds
#   .\scripts\benchmark.ps1 -Backend both -Duration 120      # Run both backends for 120 seconds each
#
# Environment variables (optional):
#   $env:CONNECTIONS = 100       # Number of client connections
#   $env:MESSAGES = 10000       # Messages per connection
#   $env:MAX_CONCURRENT = 1000   # Max concurrent connections

param(
    [ValidateSet("iouring", "io_uring", "epoll", "pollgroup", "both", "all")]
    [string]$Backend = "both",

    [int]$Duration = 60,

    [int]$Connections = 100,

    [int]$Messages = 10000,

    [int]$MaxConcurrent = 1000
)

$ErrorActionPreference = "Stop"

# Get script and project directories
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $ScriptDir

# Use environment variables if set, otherwise use parameters
$Connections = if ($env:CONNECTIONS) { $env:CONNECTIONS } else { $Connections }
$Messages = if ($env:MESSAGES) { $env:MESSAGES } else { $Messages }
$MaxConcurrent = if ($env:MAX_CONCURRENT) { $env:MAX_CONCURRENT } else { $MaxConcurrent }

Write-Host "========================================"
Write-Host "IORingGroup Linux Benchmark (Docker)"
Write-Host "========================================"
Write-Host "Backend: $Backend"
Write-Host "Duration: ${Duration}s per backend"
Write-Host "Connections: $Connections"
Write-Host "Messages per connection: $Messages"
Write-Host "Max concurrent: $MaxConcurrent"
Write-Host "========================================"
Write-Host ""

Set-Location $ProjectDir

function Run-Benchmark {
    param(
        [string]$BackendName,
        [string]$Profile
    )

    Write-Host ""
    Write-Host "========================================"
    Write-Host "Running $BackendName benchmark..."
    Write-Host "========================================"
    Write-Host ""

    # Set environment variables for docker compose
    $env:DURATION = $Duration
    $env:CONNECTIONS = $Connections
    $env:MESSAGES = $Messages
    $env:MAX_CONCURRENT = $MaxConcurrent

    # Clean up any existing containers
    try {
        docker compose --profile $Profile down --remove-orphans 2>$null
    } catch {
        # Ignore errors during cleanup
    }

    # Build and start the benchmark
    try {
        docker compose --profile $Profile up --build --abort-on-container-exit --force-recreate
    } finally {
        # Clean up
        try {
            docker compose --profile $Profile down --remove-orphans 2>$null
        } catch {
            # Ignore errors during cleanup
        }
    }

    Write-Host ""
    Write-Host "$BackendName benchmark complete!"
    Write-Host ""
}

# Normalize backend name
$Backend = $Backend.ToLower()

switch -Regex ($Backend) {
    "^(iouring|io_uring|io-uring)$" {
        Run-Benchmark -BackendName "io_uring" -Profile "iouring"
    }
    "^(epoll|pollgroup)$" {
        Run-Benchmark -BackendName "epoll/PollGroup" -Profile "epoll"
    }
    "^(both|all)$" {
        Run-Benchmark -BackendName "io_uring" -Profile "iouring"

        Write-Host ""
        Write-Host "========================================"
        Write-Host "Waiting 5 seconds before next benchmark..."
        Write-Host "========================================"
        Start-Sleep -Seconds 5

        Run-Benchmark -BackendName "epoll/PollGroup" -Profile "epoll"
    }
    default {
        Write-Host "Unknown backend: $Backend" -ForegroundColor Red
        Write-Host "Usage: .\benchmark.ps1 -Backend [iouring|epoll|both] -Duration [seconds]"
        exit 1
    }
}

Write-Host ""
Write-Host "========================================"
Write-Host "All benchmarks complete!"
Write-Host "========================================"
