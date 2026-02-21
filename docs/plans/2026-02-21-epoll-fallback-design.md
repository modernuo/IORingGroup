# Epoll Fallback Backend for Linux

**Date:** 2026-02-21
**Status:** Approved

## Problem

IORingGroup on Linux requires io_uring, which may be blocked by seccomp policies (e.g., Docker default profiles, restricted cloud environments) or unavailable on older kernels (< 5.1). When io_uring is unavailable, the factory throws an `InvalidOperationException` instead of falling back gracefully.

## Solution

Add a `LinuxEpollGroup` backend that implements `IIORingGroup` using epoll, providing automatic fallback when io_uring is unavailable. Additionally, optimize the Darwin/kqueue backend to use fixed arrays instead of dictionaries for pending operation tracking.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Architecture | Peer backend (self-contained) | Avoids leaky abstractions; epoll/kqueue differ enough to warrant independence |
| Syscall bindings | Direct LibraryImport | Matches current io_uring and Darwin patterns; no arch abstraction interface |
| Pending op storage | Fixed arrays by connection ID | Zero-alloc hot path; maxConnections known upfront |
| Epoll mode | EPOLLET + EPOLLONESHOT | Edge-triggered + one-shot matches completion semantics; one Prepare = one completion |
| FD-to-slot mapping | Connection ID in epoll_data | RegisterSocket allocates dense slot; epoll carries slot ID, enabling O(1) array lookup |
| Darwin optimization | Fixed arrays (same pattern) | Eliminates dictionary allocations on hot path |

## Architecture

### File Structure

```
IORingGroup/
  EPoll/
    LinuxEpollGroup.cs            # Main implementation
    LinuxEpollGroup.Syscalls.cs   # P/Invoke declarations (partial class)
    Structs.cs                    # epoll_event, epoll_events, epoll_op enums
```

**Namespace:** `System.Network.EPoll`
**Class:** `sealed unsafe partial class LinuxEpollGroup : IIORingGroup`

### Readiness-to-Completion Bridge

Epoll is readiness-based (notifies when FDs are ready for I/O). IIORingGroup is completion-based (notifies when I/O has completed). The bridge works as follows:

1. `PrepareRecvBuffer(connId, ...)` stores a `PendingOp` in `_pendingRecvs[connId]`
2. `Submit()` calls `epoll_ctl(EPOLL_CTL_MOD, fd, EPOLLIN|EPOLLET|EPOLLONESHOT)` for each pending op, storing `connId` in `epoll_data`
3. `PeekCompletions()` calls `epoll_wait(timeout=0)`, and for each ready event:
   - Extracts `connId` from `epoll_data`
   - Looks up pending op in `_pendingRecvs[connId]` or `_pendingSends[connId]`
   - Executes the actual `recv()` / `send()` syscall
   - Adds the result to a user-space completion ring buffer
4. `SubmitAndWait(waitNr)` calls `Submit()` then loops `epoll_wait` (blocking) until `waitNr` completions

### Pending Operation Storage

```csharp
// Fixed arrays indexed by connection ID (0 to maxConnections-1)
PendingOp[] _pendingRecvs;
PendingOp[] _pendingSends;
bool[] _hasRecv;
bool[] _hasSend;

// Accept operations: small dictionary keyed by listener FD (typically 1-2 listeners)
Dictionary<int, PendingOp> _pendingAccepts;
```

### Connection ID Management

```csharp
// Dense slot allocation
int[] _connIdToFd;       // connId -> FD mapping
int[] _fdToConnId;       // FD -> connId mapping (sized to track registered FDs)
int[] _freeSlots;        // free stack for O(1) alloc/release
int _freeSlotCount;
```

`RegisterSocket(fd)` pops from free stack, stores mappings, calls `epoll_ctl(ADD)`.
`UnregisterSocket(connId)` calls `epoll_ctl(DEL)`, clears pending ops, pushes slot back.

### User-Space Completion Queue

```csharp
Completion[] _cqEntries;  // size = queueSize * 2
int _cqHead, _cqTail;
int _cqMask;              // queueSize * 2 - 1
```

### epoll_event Struct Handling

x64: 12 bytes packed `[4B events][8B data]`
ARM64: 16 bytes aligned `[4B events][4B pad][8B data]`

Two struct definitions with appropriate `StructLayout`. Runtime `ProcessArchitecture` check in constructor selects the right size for `epoll_wait` buffer stride.

### Socket Management

- `CreateListener`: `socket(SOCK_STREAM|SOCK_NONBLOCK)` + bind + listen + `epoll_ctl(ADD)`
- `ConfigureSocket`: `fcntl(O_NONBLOCK)` + TCP_NODELAY + SO_LINGER disabled
- `RegisterSocket`: allocate slot, store FD mapping, `epoll_ctl(ADD)` with no initial events
- `UnregisterSocket`: `epoll_ctl(DEL)`, clear pending ops, return slot
- `CloseSocket`: `close(fd)` (epoll auto-removes closed FDs)

### Buffer Registration

External buffer array tracking (identical to Darwin and io_uring backends):

```csharp
nint[] _externalBufferPtrs;      // size = maxConnections * 2
int[]  _externalBufferLengths;
int    _externalBufferCount;
```

No kernel-level buffer registration (epoll doesn't support it).

### Factory Integration

```csharp
private static IIORingGroup CreateLinuxRing(int queueSize, int maxConnections)
{
    if (IORing.LinuxIORingGroup.IsAvailable())
        return new IORing.LinuxIORingGroup(queueSize, maxConnections);
    return new EPoll.LinuxEpollGroup(queueSize, maxConnections);
}

public static IIORingGroup CreateLinuxEpoll(
    int queueSize = DefaultQueueSize,
    int maxConnections = DefaultMaxConnections)
{ ... }
```

## Darwin Optimization

Update `DarwinIORingGroup` to use fixed arrays indexed by connection ID instead of `Dictionary<nint, PendingOp>`:

- Add `RegisterSocket()` slot allocation (same pattern as epoll)
- Replace `_pendingRecvs`, `_pendingSends` dictionaries with fixed arrays
- Keep `_pendingAccepts` as dictionary (listener FDs are sparse, typically 1-2)
- Store `connId` in `kevent.udata` for O(1) lookup on event delivery

## Benchmarking Phase

After implementation, compare:
- Epoll backend vs io_uring backend vs PollGroup
- Metrics: throughput (msg/sec), latency (p50/p99), allocation rate
- Tools: TestServer/TestClient with `--mode epoll` flag, `dotnet-counters`, `dotnet-trace`
