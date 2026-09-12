# IORingGroup

[![NuGet](https://img.shields.io/nuget/v/IORingGroup)](https://www.nuget.org/packages/IORingGroup)
[![License: BSD-3-Clause](https://img.shields.io/badge/License-BSD--3--Clause-blue.svg)](LICENSE)
[![.NET 10+](https://img.shields.io/badge/.NET-10%2B-purple)](https://dotnet.microsoft.com/)

Cross-platform zero-copy async socket I/O for .NET 10+. IORingGroup abstracts io_uring, Windows Registered I/O (RIO), and kqueue behind a unified submission queue / completion queue interface, enabling high-throughput networking with minimal allocations and no `async`/`await` overhead.

## Platform Backends

| Platform       | Backend                  | Mechanism                          |
|----------------|--------------------------|-------------------------------------|
| Windows        | `WindowsManagedRIOGroup` | Registered I/O, pure C#            |
| Linux          | `LinuxIORingGroup`       | io_uring via direct syscalls        |
| macOS / FreeBSD| `DarwinIORingGroup`      | kqueue (readiness-based, bridged to completion model) |

## Installation

```xml
<PackageReference Include="IORingGroup" Version="1.0.3" />
```

Or via the CLI:

```bash
dotnet add package IORingGroup
```

## Quick Start: Low-Level API

Use `IIORingGroup` directly for maximum control. This example shows a minimal single-threaded echo server:

```csharp
using System.Network;

// Create the platform-appropriate ring
using var ring = IORingGroup.Create(queueSize: 4096, maxConnections: 1024);

// Create a buffer pool for zero-copy I/O
using var bufferPool = new IORingBufferPool(
    ring, slabSize: 256, bufferSize: 4096, initialSlabs: 4, maxSlabs: 64
);

// Start listening
var listener = ring.CreateListener("0.0.0.0", 5000, backlog: 128);

// Queue initial accept
ring.PrepareAccept(listener, 0, 0, userData: OpAccept);
ring.Submit();

// Event loop
Span<Completion> completions = stackalloc Completion[256];
while (running)
{
    ring.Submit();
    int count = ring.PeekCompletions(completions);

    for (int i = 0; i < count; i++)
    {
        ref var cqe = ref completions[i];

        // Decode operation type from userData and dispatch
        switch (GetOpType(cqe.UserData))
        {
            case OpAccept:
                nint clientHandle = (nint)cqe.Result;
                ring.ConfigureSocket(clientHandle);
                int connId = ring.RegisterSocket(clientHandle);

                // Acquire a buffer and post recv
                bufferPool.TryAcquire(out var buffer);
                ring.PrepareRecvBuffer(connId, buffer.BufferId,
                    buffer.WriteOffset, buffer.WritableBytes, userData: OpRecv);

                // Re-arm accept
                ring.PrepareAccept(listener, 0, 0, userData: OpAccept);
                break;

            case OpRecv:
                buffer.CommitWrite(cqe.Result);
                ring.PrepareSendBuffer(connId, buffer.BufferId,
                    buffer.ReadOffset, buffer.ReadableBytes, userData: OpSend);
                break;

            case OpSend:
                buffer.CommitRead(cqe.Result);
                // Post next recv...
                break;
        }
    }

    ring.AdvanceCompletionQueue(count);
}

ring.CloseListener(listener);
```

## Quick Start: High-Level API

`RingSocketManager` handles buffer lifecycle, generation tracking, graceful disconnect, and batched sends:

```csharp
using System.Network;

using var ring = IORingGroup.Create();
using var manager = new RingSocketManager(ring, maxSockets: 4096);

// Set up listener
var listener = ring.CreateListener("0.0.0.0", 5000, backlog: 128);
ring.PrepareAccept(listener, 0, 0, userData: 0);

Span<RingSocketEvent> events = stackalloc RingSocketEvent[4096];

while (running)
{
    int eventCount = manager.ProcessCompletions(events);

    for (int i = 0; i < eventCount; i++)
    {
        switch (events[i].Type)
        {
            case RingSocketEventType.Accept:
                var socket = manager.CreateSocket(events[i].AcceptedSocketHandle);
                // Store app state: appState[socket.Id] = new MyState(socket);
                ring.PrepareAccept(listener, 0, 0, userData: 0);
                break;

            case RingSocketEventType.DataReceived:
                var s = events[i].Socket;
                // Echo: copy recv data to send buffer
                var data = s.RecvBuffer.GetReadSpan()[..events[i].BytesTransferred];
                data.CopyTo(s.SendBuffer.GetWriteSpan());
                s.SendBuffer.CommitWrite(data.Length);
                s.RecvBuffer.CommitRead(data.Length);
                s.QueueSend(); // Flush-and-forget
                break;

            case RingSocketEventType.DataSent:
                break; // Nothing to do — flush-and-forget

            case RingSocketEventType.Disconnected:
                // Clean up: appState[events[i].Socket.Id] = null;
                break;
        }
    }

    manager.Submit();
}
```

### Send buffer growth

Bursty sockets can outgrow the base send buffer without paying that cost for every idle connection. `RingSocketManager` supports optional growth through power-of-two tiers above the base `sendBufferSize`, bounded by a byte budget shared across all sockets:

```csharp
using var ring = IORingGroup.Create(
    maxConnections: 4096,
    maxRegisteredBuffers: RingSocketManager.RequiredRegisteredBuffers(
        maxSockets: 4096,
        sendBufferSize: 256 * 1024,
        maxSendBufferSize: 4 * 1024 * 1024,
        sendBufferGrowthBudget: 512 * 1024 * 1024
    )
);

using var manager = new RingSocketManager(
    ring,
    maxSockets: 4096,
    maxSendBufferSize: 4 * 1024 * 1024,       // largest a socket may grow to (0 disables growth)
    sendBufferGrowthBudget: 512 * 1024 * 1024 // bytes of tier-pool capacity shared across all sockets
);
```

`maxConnections` is not optional here: it defaults to 1024, and a ring built for 1024 connections cannot carry a manager built for 4096.

- `maxSendBufferSize` is the ceiling a socket can grow to; 0 (the default) means growth is disabled. It must be a power of two, no smaller than `sendBufferSize`, and no larger than 256 MiB.
- `sendBufferGrowthBudget` caps how many bytes the tier pools may hold in total; a positive value below `RingSocketManager.MinimumSendBufferGrowthBudget(sendBufferSize)` throws, since tier buffers are only ever handed out a slab at a time.
- `RequiredRegisteredBuffers(...)` computes the registration table size these settings need — pass it as `IORingGroup.Create(maxRegisteredBuffers:)` so the ring and the manager can't drift out of sync. The manager cross-checks the two in its constructor and throws when the ring's table is too small, so a mismatch surfaces at startup instead of at an accept or a growth.
- Call `manager.Maintain()` about once a minute from the ring thread. It rotates each tier pool's usage window, trims at most one idle slab per tier down to the recent peak, and returns a `SendBufferMaintenance` snapshot (buffers released, growth refusals, tier capacity/usage). Its `TierInUse` and `TierRetainFloor` are buffer *counts* summed across tiers of different sizes; use `manager.GetSendBufferTierStats(tier)` when you need one tier's real numbers.

#### Budget per tier, not just in total

Tier buffers are allocated a slab at a time, and a slab of tier size *S* holds `max(4, min(16, 8 MiB / S))` buffers — so a slab costs 8 MiB up to the point where the floor of 4 buffers takes over, and more above it. Clearing `MinimumSendBufferGrowthBudget` only guarantees the *first* tier is reachable. With the 256 KiB base and 4 MiB ceiling above:

| Tier size | Buffers per slab | Slab cost |
|-----------|------------------|-----------|
| 512 KiB   | 16               | 8 MiB     |
| 1 MiB     | 8                | 8 MiB     |
| 2 MiB     | 4                | 8 MiB     |
| 4 MiB     | 4                | 16 MiB    |

A growth is refused when the tier has no free buffer and its next slab would not fit in what the budget has left, so the budget must cover at least one slab of a tier for that tier to be usable at all — and in practice several, since the lower tiers allocate first and hold their capacity until `Maintain()` trims them. `SendBufferMaintenance.GrowthRefusals` is how you find out the budget is set too low.

#### What bounds memory, and what bounds connections

Worst-case tier memory is exactly `sendBufferGrowthBudget`. The base pools are bounded separately, and not by `maxSockets`: with `slabSize = max(64, maxSockets / maxBufferSlabs)`, the recv pool tops out at `maxBufferSlabs × slabSize` buffers and the base send pool at `maxBufferSlabs × (slabSize / 4)` — a quarter of the recv pool.

For large `maxSockets` that quarter, not `maxSockets`, is what actually bounds concurrent connections: the configuration above allows 4096 sockets but only `32 × 32 = 1024` base send buffers (256 MiB of them), and `CreateSocket` returns null once they are all handed out. Raise `maxBufferSlabs`, or size `maxSockets` against the send pool rather than the socket table, if every slot has to be usable at once.

## Threading Model

IORingGroup is designed for **single-threaded** event loops. The ring, the manager, and all socket operations must be called from the same thread:

- `ProcessCompletions()`, `Submit()`, `CreateSocket()`, `DisconnectImmediate()`
- `RingSocket.QueueSend()`, `RingSocket.Disconnect()`

There is no cross-thread synchronization — this is by design. Single-threaded access eliminates lock contention and enables zero-allocation hot paths. The internal send and disconnect queues are plain `Queue<T>`, not `ConcurrentQueue<T>`.

If you need multi-threaded I/O, run multiple rings on separate threads with separate socket sets.

## Buffer System

### IORingBuffer

A double-mapped circular buffer: the same physical memory is mapped twice contiguously in virtual address space. This eliminates wrap-around copies — a read or write that crosses the end of the buffer seamlessly continues at the beginning via the second mapping.

- `GetReadSpan()` / `GetWriteSpan()` — contiguous spans, even across the boundary
- `CommitRead(n)` / `CommitWrite(n)` — advance head/tail pointers
- Platform-specific allocation: `VirtualAlloc2` (Windows), `memfd_create` (Linux), `shm_open` (macOS)

### IORingBufferPool

Multi-slab pool with on-demand allocation. Buffers are pre-registered with the ring for zero-copy I/O:

```csharp
var pool = new IORingBufferPool(
    ring,
    slabSize: 256,      // Buffers per slab
    bufferSize: 4096,   // Bytes per buffer
    initialSlabs: 4,    // Pre-allocate 1024 buffers
    maxSlabs: 64        // Grow up to 16K buffers on demand
);

pool.TryAcquire(out var buffer); // O(1) allocation
pool.Release(buffer);            // O(1) return to pool
```

## Benchmarking

Run the echo server and client for performance testing:

```bash
# IORing server (default — uses RIO on Windows, io_uring on Linux, kqueue on macOS)
dotnet run --project TestServer -c Release -- --ioring --benchmark --duration 10

# PollGroup server (cross-platform baseline)
dotnet run --project TestServer -c Release -- --pollgroup --benchmark --duration 10

# Client (connect and blast echo traffic)
dotnet run --project TestClient -c Release -- --host 127.0.0.1 --port 5000
```

## License

[BSD-3-Clause](LICENSE)
