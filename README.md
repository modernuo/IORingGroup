# IORingGroup

[![NuGet](https://img.shields.io/nuget/v/IORingGroup)](https://www.nuget.org/packages/IORingGroup)
[![License: BSD-3-Clause](https://img.shields.io/badge/License-BSD--3--Clause-blue.svg)](LICENSE)
[![.NET 10+](https://img.shields.io/badge/.NET-10%2B-purple)](https://dotnet.microsoft.com/)

Cross-platform zero-copy async socket I/O for .NET 10+. IORingGroup abstracts io_uring, Windows Registered I/O (RIO), and kqueue behind a unified submission queue / completion queue interface, enabling high-throughput networking with minimal allocations and no `async`/`await` overhead.

## Platform Backends

| Platform       | Backend                  | Mechanism                          |
|----------------|--------------------------|-------------------------------------|
| Windows        | `WindowsRIOGroup`        | Registered I/O via native `ioring.dll` |
| Windows        | `WindowsManagedRIOGroup` | Registered I/O, pure C# (no native DLL) |
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

# Managed RIO server (pure C#, Windows only — for A/B comparison)
dotnet run --project TestServer -c Release -- --mrio --benchmark --duration 10

# PollGroup server (cross-platform baseline)
dotnet run --project TestServer -c Release -- --pollgroup --benchmark --duration 10

# Client (connect and blast echo traffic)
dotnet run --project TestClient -c Release -- --host 127.0.0.1 --port 5000
```

Use `IORING_MANAGED_RIO=1` to select the managed RIO backend via `IORingGroup.Create()`.

## License

[BSD-3-Clause](LICENSE)
