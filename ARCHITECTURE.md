# IORingGroup Architecture

This document provides a comprehensive overview of the IORingGroup library architecture, designed to help engineers understand the system's design and implementation.

## Table of Contents

1. [Overview](#overview)
2. [Design Goals](#design-goals)
3. [Platform Implementations](#platform-implementations)
4. [Core Abstractions](#core-abstractions)
5. [Zero-Copy Architecture](#zero-copy-architecture)
6. [Sequence Diagrams](#sequence-diagrams)
7. [Data Flow](#data-flow)
8. [Performance Considerations](#performance-considerations)
9. [API Reference](#api-reference)

---

## Overview

IORingGroup is a cross-platform, high-performance asynchronous socket I/O library that provides a unified API modeled after Linux's `io_uring`. It enables zero-copy network I/O through platform-specific optimizations while maintaining a consistent programming interface.

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Application Layer                           │
│                    (TestServer, ModernUO, etc.)                     │
└─────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         IIORingGroup Interface                      │
│         Prepare* → Submit → PeekCompletions → AdvanceQueue          │
└─────────────────────────────────────────────────────────────────────┘
                                   │
           ┌───────────────────────┼───────────────────────┐
           ▼                       ▼                       ▼
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│ WindowsRIOGroup │     │ LinuxIORingGroup│     │DarwinIORingGroup│
│   (ioring.dll)  │     │  (io_uring)     │     │    (kqueue)     │
└─────────────────┘     └─────────────────┘     └─────────────────┘
           │                       │                       │
           ▼                       ▼                       ▼
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│  Windows RIO    │     │  Linux Kernel   │     │  macOS Kernel   │
│ (Registered I/O)│     │  (io_uring)     │     │    (kqueue)     │
└─────────────────┘     └─────────────────┘     └─────────────────┘
```

---

## Design Goals

1. **Zero-Copy I/O**: Minimize memory copies between application and kernel
2. **Batched Operations**: Reduce syscall overhead via submission/completion queues
3. **Unified API**: Single interface across Windows, Linux, and macOS
4. **Hot-Path Performance**: No allocations in steady-state operations
5. **Buffer Pooling**: Reuse registered buffers across connections

---

## Platform Implementations

### Windows: RIO via ioring.dll

Windows uses **Registered I/O (RIO)**, a high-performance socket API available since Windows 8. Note that Windows IoRing (the io_uring equivalent) does NOT support socket operations, only file I/O.

#### ioring.dll Architecture

The native C library (`ioring/ioring.c`) wraps RIO to provide an io_uring-compatible API:

```
┌────────────────────────────────────────────────────────────────────┐
│                             ioring.dll                             │
├────────────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────┐  │
│  │ Submission   │  │ Completion   │  │    Connection Pool       │  │
│  │ Queue (SQ)   │  │ Queue (CQ)   │  │  (RIO Request Queues)    │  │
│  └──────────────┘  └──────────────┘  └──────────────────────────┘  │
│          │                │                      │                 │
│          ▼                ▼                      ▼                 │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │                      RIO Function Table                      │  │
│  │  RIOReceive | RIOSend | RIOCreateRequestQueue | ...          │  │
│  └──────────────────────────────────────────────────────────────┘  │
│          │                                                         │
│          ▼                                                         │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │                  IOCP (I/O Completion Port)                  │  |
│  │             (Single IOCP for all RIO operations)             │  │
│  └──────────────────────────────────────────────────────────────   │
└────────────────────────────────────────────────────────────────────┘
```

#### Key RIO Concepts

1. **Request Queue (RQ)**: Each socket gets its own RQ for recv/send operations
2. **Completion Queue (CQ)**: Shared CQ for all sockets (more efficient)
3. **Registered Buffers**: Memory registered with `RIORegisterBuffer` for zero-copy
4. **AcceptEx Pool**: Pre-created sockets with `WSA_FLAG_REGISTERED_IO` for accepts

#### Critical Windows RIO Constraint

**Sockets from `accept()` do NOT inherit `WSA_FLAG_REGISTERED_IO`.**

For RIO to work with accepted connections:
1. Create listener with `WSA_FLAG_REGISTERED_IO`
2. Create accept sockets with `WSA_FLAG_REGISTERED_IO`
3. Use `AcceptEx` (not `accept`) to accept into pre-created sockets
4. Call `SO_UPDATE_ACCEPT_CONTEXT` after AcceptEx completes
5. Register socket with RIO to create its Request Queue

### Linux: Native io_uring

Linux implementation uses direct syscalls to the kernel's io_uring interface:

```
┌─────────────────────────────────────────────────────────────────────┐
│                         LinuxIORingGroup                         │
├─────────────────────────────────────────────────────────────────────┤
│  ┌────────────────────────────────────────────────────────────┐  │
│  │              Memory-Mapped Ring Buffers                    │  │
│  │  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐     │  │
│  │  │  SQ Ring    │    │  CQ Ring    │    │   SQEs      │     │  │
│  │  │  (indices)  │    │  (results)  │    │  (entries)  │     │  │
│  │  └─────────────┘    └─────────────┘    └─────────────┘     │  │
│  └────────────────────────────────────────────────────────────┘  │
│                              │                                   │
│                              ▼                                   │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │                    io_uring Syscalls                       │  │
│  │     io_uring_setup | io_uring_enter | io_uring_register    │  │
│  └────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

#### io_uring Operation Flow

1. **Setup**: `io_uring_setup()` creates the ring and returns file descriptor
2. **Map Memory**: `mmap()` the SQ ring, CQ ring, and SQEs into user space
3. **Submit**: Write SQEs, update SQ tail, call `io_uring_enter()`
4. **Complete**: Read CQ entries, update CQ head

### macOS/BSD: kqueue Abstraction

macOS uses kqueue for event notification with synchronous socket operations:

```
┌──────────────────────────────────────────────────────────────────┐
│                     DarwinIORingGroup                            │
├──────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐    ┌─────────────────┐                      │
│  │ User-Space SQ   │    │ User-Space CQ   │                      │
│  │ (pending ops)   │    │ (completions)   │                      │
│  └─────────────────┘    └─────────────────┘                      │
│           │                      ▲                               │
│           ▼                      │                               │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │                    kqueue + kevent                         │  │
│  │              (event notification only)                     │  │
│  └────────────────────────────────────────────────────────────┘  │
│           │                      │                               │
│           ▼                      ▼                               │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │              Synchronous Socket Operations                 │  │
│  │           recv() | send() | accept() | connect()           │  │
│  └────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

---

## Core Abstractions

### IIORingGroup Interface

The unified interface provides io_uring-style submission/completion queue semantics:

```csharp
public interface IIORingGroup : IDisposable
{
    // Queue Management
    int SubmissionQueueSpace { get; }
    int CompletionQueueCount { get; }

    // Operation Preparation (adds to SQ, no syscall)
    void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData);
    void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData);
    void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData);
    void PrepareClose(nint fd, ulong userData);
    // ... more operations

    // Submission (syscall to kernel)
    int Submit();
    int SubmitAndWait(int waitNr);

    // Completion Retrieval
    int PeekCompletions(Span<Completion> completions);
    int WaitCompletions(Span<Completion> completions, int minComplete, int timeoutMs);
    void AdvanceCompletionQueue(int count);

    // Zero-Copy Buffer Operations
    int RegisterBuffer(IORingBuffer buffer);
    void UnregisterBuffer(int bufferId);
    void PrepareSendBuffer(int connId, int bufferId, int offset, int length, ulong userData);
    void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData);
}
```

### IORingBuffer: Double-Mapped Circular Buffer

The magic behind zero-copy I/O is the **double-mapped circular buffer**:

```
Physical Memory (4KB):
┌────────────────────────────────────────┐
│  0x1000: [  actual buffer data  ]      │
└────────────────────────────────────────┘

Virtual Memory (8KB = 2 × Physical):
┌────────────────────────────────────────┬────────────────────────────────────────┐
│  View 1: [  actual buffer data  ]      │  View 2: [  actual buffer data  ]      │
│  0x2000 ─────────────────────────────► │  0x3000 ─────────────────────────────► │
└────────────────────────────────────────┴────────────────────────────────────────┘
         │                                         │
         └─────────── Same physical memory ────────┘
```

**Why Double-Mapping?**

Without double-mapping, wrapping around a circular buffer requires two separate operations:

```
Traditional Circular Buffer (requires 2 reads for wrap-around):
┌──────────────────────────────────────────────────┐
│  [.....data2.....][empty][.....data1.....]      │
│                   ↑      ↑                       │
│                   tail   head                    │
└──────────────────────────────────────────────────┘
Read 1: head to end of buffer
Read 2: start of buffer to tail
```

With double-mapping, the same data appears contiguously in the mirrored region:

```
Double-Mapped Circular Buffer (single contiguous read):
┌──────────────────────────────────┬────────────────────────────────┐
│  [...data2...][empty][..data1..]│[...data2...][empty][..data1..]  │
│                       ↑                        ↑                  │
│                       head reads through ──────┘ (contiguous!)    │
└──────────────────────────────────┴────────────────────────────────┘
```

#### IORingBuffer API

```csharp
public sealed class IORingBuffer : IDisposable
{
    // Properties
    nint Pointer { get; }           // Base pointer for registration
    int PhysicalSize { get; }       // Actual buffer size
    int VirtualSize { get; }        // 2× physical (for registration)
    int BufferId { get; set; }      // Assigned by ring registration

    // Circular buffer operations
    int ReadableBytes { get; }      // Data available to send
    int WritableBytes { get; }      // Space available for recv

    Span<byte> GetWriteSpan(out int offset);  // For recv
    void CommitWrite(int count);              // After recv completes

    Span<byte> GetReadSpan(out int offset);   // For send
    void CommitRead(int count);               // After send completes
}
```

### IORingBufferPool: Buffer Lifecycle Management

```
┌─────────────────────────────────────────────────────────────────┐
│                         IORingBufferPool                        │
├─────────────────────────────────────────────────────────────────┤
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                         Slabs                             │  │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐       ┌─────────┐  │  │
│  │  │ Slab 0  │  │ Slab 1  │  │ Slab 2  │  ...  │ Slab N  │  │  │
│  │  │256 bufs │  │256 bufs │  │256 bufs │       │256 bufs │  │  │
│  │  └─────────┘  └─────────┘  └─────────┘       └─────────┘  │  │
│  └───────────────────────────────────────────────────────────┘  │
│                              │                                  │
│                              ▼                                  │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                    Free Stack per Slab                    │  │
│  │              [idx: 255, 254, 253, ...]                    │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  Features:                                                      │
│  • On-demand slab allocation (lazy growth)                      │
│  • Pre-registered with ring (no per-acquire registration)       │
│  • O(1) acquire and release                                     │
│  • Fallback allocation if pool exhausted                        │
└─────────────────────────────────────────────────────────────────┘
```

---

## Zero-Copy Architecture

### Echo Server Data Flow (Single Buffer per Connection)

For an echo server, we use a single `IORingBuffer` per connection where:
- **Recv** writes to the buffer at `tail` (write position)
- **Send** reads from the buffer at `head` (read position)

```
IORingBuffer State Machine:

Initial (empty):
┌────────────────────────────────────────────────────────────────────┐
│  [                      empty                                    ] │
│  ↑ head = tail = 0                                                 │
└────────────────────────────────────────────────────────────────────┘

After Recv (100 bytes received):
┌────────────────────────────────────────────────────────────────────┐
│  [████████████ received data ████████████][      empty           ] │
│  ↑ head = 0                              ↑ tail = 100              │
│  (ReadableBytes = 100)                   (WritableBytes = 3995)    │
└────────────────────────────────────────────────────────────────────┘

After Send (100 bytes sent):
┌────────────────────────────────────────────────────────────────────┐
│  [                      empty                                    ] │
│  ↑ head = tail = 0 (reset optimization)                            │
└────────────────────────────────────────────────────────────────────┘

Concurrent Recv/Send (advanced scenario):
┌────────────────────────────────────────────────────────────────────┐
│  [sent][████ pending send ████][██ received ██][    recv space   ] │
│        ↑ head                  ↑               ↑ tail              │
│        (send from here)        (data boundary) (recv into here)    │
└────────────────────────────────────────────────────────────────────┘
```

### Zero-Copy Benefits

1. **No Application ↔ Kernel Copy**: Data flows directly between network and registered buffer
2. **No Recv ↔ Send Copy**: Same buffer used for both (echo pattern)
3. **No Per-Operation Registration**: Buffers pre-registered in pool

---

## Sequence Diagrams

### Server Initialization

```
Application              IORingGroup              OS Kernel
     │                        │                       │
     │  Create(queueSize)     │                       │
     │───────────────────────►│                       │
     │                        │  io_uring_setup()     │
     │                        │  (or RIO init)        │
     │                        │──────────────────────►│
     │                        │◄──────────────────────│
     │                        │                       │
     │  CreateBufferPool()    │                       │
     │───────────────────────►│                       │
     │                        │  RegisterBuffer()×N   │
     │                        │──────────────────────►│
     │                        │◄──────────────────────│
     │◄───────────────────────│                       │
     │                        │                       │
     │  CreateListener()      │                       │
     │───────────────────────►│                       │
     │                        │  socket() + bind()    │
     │                        │  + listen()           │
     │                        │──────────────────────►│
     │◄───────────────────────│◄──────────────────────│
     │                        │                       │
     │  PrepareAccept()×N     │                       │
     │───────────────────────►│  (queued in SQ)       │
     │                        │                       │
     │  Submit()              │                       │
     │───────────────────────►│  io_uring_enter()     │
     │                        │  (or IOCP post)       │
     │                        │──────────────────────►│
     │◄───────────────────────│◄──────────────────────│
```

### Accept → Recv → Send → Recv Loop (Zero-Copy)

```
Application              IORingGroup              OS Kernel
     │                        │                       │
     │  PeekCompletions()     │                       │
     │───────────────────────►│  (check CQ)           │
     │  Accept completion     │                       │
     │◄───────────────────────│                       │
     │                        │                       │
     │  pool.Acquire()        │                       │
     │───────────────────────►│                       │
     │ buffer (pre-registered)│                       │
     │◄───────────────────────│                       │
     │                        │                       │
     │  RegisterSocket()      │                       │
     │───────────────────────►│  RIOCreateRequestQueue│
     │  connId                │──────────────────────►│
     │◄───────────────────────│◄──────────────────────│
     │                        │                       │
     │  PrepareRecvBuffer(    │                       │
     │    connId, bufferId,   │                       │
     │    offset=0, len=4096) │                       │
     │───────────────────────►│  (queued in SQ)       │
     │                        │                       │
     │  Submit()              │                       │
     │───────────────────────►│  RIOReceive()         │
     │                        │──────────────────────►│
     │◄───────────────────────│◄──────────────────────│
     │                        │                       │
     │        ... time passes, data arrives ...       │
     │                        │                       │
     │  PeekCompletions()     │                       │
     │───────────────────────►│  (check CQ)           │
     │  Recv completion       │                       │
     │  (result = 100 bytes)  │                       │
     │◄───────────────────────│                       │
     │                        │                       │
     │  buffer.CommitWrite(100)                       │
     │  (tail += 100)         │                       │
     │                        │                       │
     │  // Zero-copy: send from SAME buffer           │
     │  PrepareSendBuffer(    │                       │
     │    connId, bufferId,   │                       │
     │    offset=0, len=100)  │  ← Same buffer!       │
     │───────────────────────►│  (queued in SQ)       │
     │                        │                       │
     │  // Concurrent: also prepare next recv         │
     │  PrepareRecvBuffer(    │                       │
     │    connId, bufferId,   │                       │
     │    offset=100, len=3996)│ ← Different offset!  │
     │───────────────────────►│  (queued in SQ)       │
     │                        │                       │
     │  Submit()              │                       │
     │───────────────────────►│  RIOSend + RIOReceive │
     │                        │──────────────────────►│
     │◄───────────────────────│◄──────────────────────│
```

### Connection Cleanup

```
Application              IORingGroup                OS Kernel
     │                        │                         │
     │  (recv returns 0 = FIN)│                         │
     │                        │                         │
     │  UnregisterSocket()    │                         │
     │───────────────────────►│  RIOCloseCompletionQueue│
     │                        │────────────────────────►│
     │◄───────────────────────│◄────────────────────────│
     │                        │                         │
     │  pool.Release(buffer)  │                         │
     │───────────────────────►│  (returns to free stack)│
     │◄───────────────────────│                         │
     │                        │                         │
     │  CloseSocket()         │                         │
     │───────────────────────►│  closesocket()          │
     │                        │────────────────────────►│
     │◄───────────────────────│◄────────────────────────│
```

---

## Data Flow

### Complete Request/Response Path (Zero-Copy Echo)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              Network Card (NIC)                              │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │ ▲
                                      │ │
                              DMA to/from registered buffer
                                      │ │
                                      ▼ │
┌──────────────────────────────────────────────────────────────────────────────┐
│                       IORingBuffer (registered memory)                       │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │    [received data]────────────────────────────────►[same data sent]    │  │
│  │         ↑                                                   │          │  │
│  │         │              NO COPY (same bytes!)                │          │  │
│  │         │                                                   ▼          │  │
│  │    RIOReceive()                                        RIOSend()       │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      │ (only pointer + length passed)
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              Application Logic                               │
│                                                                              │
│   1. Recv completes → buffer.CommitWrite(bytesReceived)                      │
│   2. Read data available → buffer.ReadableBytes                              │
│   3. Send from same buffer → PrepareSendBuffer(offset=head, len=readable)    │
│   4. Send completes → buffer.CommitRead(bytesSent)                           │
│   5. Write space available → buffer.WritableBytes                            │
│   6. Recv into same buffer → PrepareRecvBuffer(offset=tail, len=writable)    │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Performance Considerations

### Syscall Batching

```
Traditional I/O (N syscalls):
┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐
│recv │ │send │ │recv │ │send │  ...  (N syscalls)
└─────┘ └─────┘ └─────┘ └─────┘

io_uring/RIO Style (2 syscalls per batch):
┌──────────────────────────────────────────────────────────────────┐
│  Submit(): queue recv₁, recv₂, send₁, send₂, recv₃, ...          │  1 syscall
└──────────────────────────────────────────────────────────────────┘
                               │
                               ▼
┌──────────────────────────────────────────────────────────────────┐
│  PeekCompletions(): get all completions at once                  │  1 syscall
└──────────────────────────────────────────────────────────────────┘
```

### Memory Layout Optimization

```
Hot Path Memory Access Pattern:

IORingBuffer (cache-friendly):
┌─────────────────────────────────────────────────────────────────┐
│  _head (read position)    ← frequently accessed together        │
│  _tail (write position)   ← in same cache line                  │
│  _physicalSize            ← rarely changes                      │
│  _buffer (pointer)        ← constant after init                 │
└─────────────────────────────────────────────────────────────────┘

ClientState (minimal struct):
┌─────────────────────────────────────────────────────────────────┐
│  Socket (nint)            │                                     │
│  ConnId (int)             │  All fit in ~32 bytes               │
│  Buffer (reference)       │  Good cache locality                │
│  RecvPending (bool)       │                                     │
│  SendPending (bool)       │                                     │
│  Active (bool)            │                                     │
└─────────────────────────────────────────────────────────────────┘
```

### Zero-Allocation in Hot Path

```csharp
// Hot path operations (no allocations):
Span<Completion> completions = stackalloc Completion[1024];  // Stack allocated
int count = ring.PeekCompletions(completions);               // No allocation

for (int i = 0; i < count; i++)
{
    ref var cqe = ref completions[i];  // By-ref access
    ProcessCompletion(ref cqe);         // No boxing
}

ring.AdvanceCompletionQueue(count);  // Just increments counter
```

---

## API Reference

### Factory Methods

```csharp
// Create platform-appropriate ring
IIORingGroup ring = IORingGroup.Create(queueSize: 4096);

// Windows-specific: RIO ring with connection tracking
WindowsRIOGroup rioRing = new WindowsRIOGroup(
    queueSize: 8192,
    maxConnections: 10000,
    recvBufferSize: 4096,  // unused, retained for API compat
    sendBufferSize: 4096   // unused, retained for API compat
);
```

### Buffer Pool Usage

```csharp
// Create pool
var pool = new IORingBufferPool(
    ring,
    slabSize: 256,      // Buffers per slab
    bufferSize: 4096,   // Buffer size (page-aligned, power of 2)
    initialSlabs: 4,    // Pre-allocate 1024 buffers
    maxSlabs: 64        // Allow growth to 16K buffers
);

// Acquire (O(1), no allocation in steady state)
if (pool.TryAcquire(out var buffer))
{
    // buffer.BufferId is already set (pre-registered)
}

// Release (O(1))
pool.Release(buffer);

// Stats
var stats = pool.GetStats();
Console.WriteLine(stats.ToString());
// "Slabs: 4/64, Buffers: 1000/1024 (97.7%), Fallbacks: 0 total"
```

### Complete Echo Server Pattern

```csharp
// Accept handling
void HandleAccept(nint clientSocket, int clientIndex)
{
    ring.ConfigureSocket(clientSocket);

    var buffer = pool.Acquire();
    var connId = rioRing.RegisterSocket(clientSocket);

    clients[clientIndex] = new ClientState {
        Socket = clientSocket,
        ConnId = connId,
        Buffer = buffer,
        Active = true
    };

    // Post initial recv
    buffer.GetWriteSpan(out var writeOffset);
    rioRing.PrepareRecvBuffer(connId, buffer.BufferId,
        writeOffset, buffer.WritableBytes, MakeUserData(OpRecv, clientIndex));
}

// Recv handling (zero-copy)
void HandleRecv(int clientIndex, int bytesReceived)
{
    ref var client = ref clients[clientIndex];

    // Commit received data
    client.Buffer.CommitWrite(bytesReceived);

    // Send from same buffer (zero-copy!)
    client.Buffer.GetReadSpan(out var readOffset);
    rioRing.PrepareSendBuffer(client.ConnId, client.Buffer.BufferId,
        readOffset, client.Buffer.ReadableBytes, MakeUserData(OpSend, clientIndex));

    // Also post concurrent recv
    client.Buffer.GetWriteSpan(out var writeOffset);
    if (client.Buffer.WritableBytes > 0)
    {
        rioRing.PrepareRecvBuffer(client.ConnId, client.Buffer.BufferId,
            writeOffset, client.Buffer.WritableBytes, MakeUserData(OpRecv, clientIndex));
    }
}
```

---

## Glossary

| Term | Description |
|------|-------------|
| **SQ** | Submission Queue - where operations are queued before submission |
| **CQ** | Completion Queue - where completed operations are reported |
| **SQE** | Submission Queue Entry - a single operation descriptor |
| **CQE** | Completion Queue Entry - a single completion result |
| **RIO** | Registered I/O - Windows high-performance socket API |
| **RQ** | Request Queue - RIO per-socket queue for operations |
| **IOCP** | I/O Completion Port - Windows async I/O notification mechanism |
| **Double-Mapping** | Memory trick where same physical pages are mapped twice consecutively |
| **Zero-Copy** | Data transfer without intermediate memory copies |
| **Buffer Pool** | Pre-allocated, pre-registered buffer collection for reuse |

---

## References

- [Linux io_uring documentation](https://kernel.dk/io_uring.pdf)
- [Windows RIO documentation](https://docs.microsoft.com/en-us/windows/win32/winsock/registered-i-o)
- [ModernUO Pipe implementation](https://github.com/modernuo/ModernUO/blob/main/Projects/Server/Network/Pipe.cs)
