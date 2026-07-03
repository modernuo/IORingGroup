# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build everything
dotnet build IORingGroup.sln

# Run tests (platform-specific tests auto-skip on unsupported OSes)
dotnet test IORingGroup.Tests/IORingGroup.Tests.csproj

# Run a single test
dotnet test IORingGroup.Tests/IORingGroup.Tests.csproj --filter "FullyQualifiedName~TestMethodName"

# Run echo server (for benchmarking)
dotnet run --project TestServer -- --port 2594 --mode ioring
# Run benchmark client
dotnet run --project TestClient -- --host 127.0.0.1 --port 2594

# Linux backend echo integration test (epoll correctness, in Docker)
docker compose -f docker/docker-compose.yml up --build \
  --abort-on-container-exit --exit-code-from client
```

Backends that can't run on the dev host are exercised in containers — see
`docker/README.md` for the epoll and io_uring echo harness (ping-pong
correctness + pipelined throughput). macOS/kqueue is tested on real hardware.

## Project Overview

IORingGroup is a cross-platform zero-copy async socket I/O library for .NET 10+ (namespace: `System.Network`). It abstracts three platform-specific I/O backends behind a unified submission queue / completion queue interface (`IIORingGroup`).

## Architecture

**Factory**: `IORingGroup.Create()` returns the platform-appropriate implementation.

**Platform backends** (each implements `IIORingGroup`):
- `Windows/WindowsManagedRIOGroup` — Windows Registered I/O, pure C#
- `Linux/LinuxIORingGroup` — Linux io_uring via direct syscalls
- `EPoll/LinuxEpollGroup` — Linux epoll fallback (readiness-based, bridges to completion model). Used automatically when io_uring is unavailable (old kernels, seccomp-blocked syscalls); `IORingGroup.CreateLinuxEpoll()` selects it explicitly.
- `Darwin/DarwinIORingGroup` — macOS/BSD kqueue (readiness-based, bridges to completion model)

**Core types**:
- `IORingBuffer` — Double-mapped circular buffer (physical memory mapped twice in virtual address space to eliminate wrap-around). Platform-specific allocation (VirtualAlloc2 / memfd_create / shm_open).
- `IORingBufferPool` — Multi-slab buffer pool with on-demand allocation and pre-registration with the ring.
- `RingSocket` — Managed socket wrapping an OS handle with pre-registered send/recv buffers. Tracks in-flight operation flags and generation counter for stale completion detection.
- `RingSocketManager` — High-level manager providing O(1) slot allocation with generation tracking, flush queue for batched sends, graceful disconnect queue, and event-based API (`RingSocketEvent`: DataReceived, DataSent, Disconnected, Accept).

**User data encoding** (`IORingUserData`): 64-bit value packing `[8 opType][16 generation][8 reserved][32 socketId]` to detect stale completions after socket slot reuse.

## Key Conventions

- Warnings as errors, warning level 5, unsafe code enabled, C# preview language features
- Root namespace is `System.Network`, assembly name is `IORingGroup`
- SPDX license headers (`BSD-3-Clause`) on all source files
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on hot-path methods
- Negative return codes for operational errors (matching Linux errno convention)
- Tests use xUnit with `Xunit.SkippableFact` for platform-conditional tests (`Skip.IfNot(...)`)

## Critical Design Rules

- **Buffer safety**: Buffers must not be released while I/O is in-flight. Disconnect waits for pending recv/send completions before releasing buffers.
- **Generation tracking**: Socket slots are reused; generation counters in user data prevent processing stale completions from a previous socket occupying the same slot.
- **Single-threaded ring access**: The submission/completion queues are accessed from a single thread. Cross-thread work is enqueued via `ConcurrentQueue` (send queue, disconnect queue).
