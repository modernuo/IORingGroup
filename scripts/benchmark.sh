#!/bin/bash
# SPDX-License-Identifier: BSD-3-Clause
# Copyright (c) 2025, ModernUO
#
# IORingGroup Linux Benchmark Script
#
# Usage:
#   ./scripts/benchmark.sh                    # Run both backends with default 60s duration
#   ./scripts/benchmark.sh iouring            # Run io_uring only
#   ./scripts/benchmark.sh epoll              # Run epoll only
#   ./scripts/benchmark.sh iouring 30         # Run io_uring for 30 seconds
#   ./scripts/benchmark.sh both 120           # Run both backends for 120 seconds each

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

# Default values
BACKEND="${1:-both}"
DURATION="${2:-60}"
CONNECTIONS="${CONNECTIONS:-100}"
MESSAGES="${MESSAGES:-100000}"

echo "========================================"
echo "IORingGroup Linux Benchmark"
echo "========================================"
echo "Backend: $BACKEND"
echo "Duration: ${DURATION}s per backend"
echo "Connections: $CONNECTIONS"
echo "Messages per connection: $MESSAGES"
echo "========================================"
echo ""

cd "$PROJECT_DIR"

run_benchmark() {
    local backend="$1"
    local profile="$2"

    echo ""
    echo "========================================"
    echo "Running $backend benchmark..."
    echo "========================================"
    echo ""

    # Clean up any existing containers
    docker compose --profile "$profile" down --remove-orphans 2>/dev/null || true

    # Build and start the benchmark
    DURATION="$DURATION" CONNECTIONS="$CONNECTIONS" MESSAGES="$MESSAGES" \
        docker compose --profile "$profile" up --build --abort-on-container-exit

    # Clean up
    docker compose --profile "$profile" down --remove-orphans 2>/dev/null || true

    echo ""
    echo "$backend benchmark complete!"
    echo ""
}

case "$BACKEND" in
    iouring|io_uring|io-uring)
        run_benchmark "io_uring" "iouring"
        ;;
    epoll|pollgroup)
        run_benchmark "epoll/PollGroup" "epoll"
        ;;
    both|all)
        run_benchmark "io_uring" "iouring"
        echo ""
        echo "========================================"
        echo "Waiting 5 seconds before next benchmark..."
        echo "========================================"
        sleep 5
        run_benchmark "epoll/PollGroup" "epoll"
        ;;
    *)
        echo "Unknown backend: $BACKEND"
        echo "Usage: $0 [iouring|epoll|both] [duration_seconds]"
        exit 1
        ;;
esac

echo ""
echo "========================================"
echo "All benchmarks complete!"
echo "========================================"
