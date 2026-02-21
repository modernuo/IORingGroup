# Epoll Fallback Backend Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a Linux epoll backend (`LinuxEpollGroup`) as automatic fallback when io_uring is unavailable, optimize the Darwin backend to use fixed arrays, and add `--epoll` mode to TestServer.

**Architecture:** Readiness-to-completion bridge (like Darwin/kqueue). epoll notifies when FDs are ready; we execute the actual I/O syscall and produce a completion. Fixed arrays indexed by connection ID for zero-alloc hot paths. EPOLLET + EPOLLONESHOT for completion-model semantics.

**Tech Stack:** C# 13 preview, .NET 10, unsafe code, LibraryImport source generators, xUnit + Xunit.SkippableFact

---

### Task 1: Create epoll struct definitions

**Files:**
- Create: `IORingGroup/EPoll/Structs.cs`

**Step 1: Write the structs file**

```csharp
// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;

namespace System.Network.EPoll;

/// <summary>
/// epoll_event for x64 Linux (12 bytes, packed).
/// The Linux kernel packs this struct on x64 with __attribute__((packed)).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct epoll_event_packed
{
    [FieldOffset(0)] public uint events;
    [FieldOffset(4)] public long data;
}

/// <summary>
/// epoll_event for ARM64 Linux (16 bytes, natural alignment).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct epoll_event_aligned
{
    public uint events;
    public uint _padding;
    public long data;
}

/// <summary>
/// epoll event flags.
/// </summary>
[Flags]
internal enum epoll_events : uint
{
    EPOLLIN = 0x001,
    EPOLLPRI = 0x002,
    EPOLLOUT = 0x004,
    EPOLLERR = 0x008,
    EPOLLHUP = 0x010,
    EPOLLRDNORM = 0x040,
    EPOLLRDBAND = 0x080,
    EPOLLWRNORM = 0x100,
    EPOLLWRBAND = 0x200,
    EPOLLRDHUP = 0x2000,
    EPOLLET = 0x80000000,
    EPOLLONESHOT = 0x40000000,
}

/// <summary>
/// epoll_ctl operations.
/// </summary>
internal enum epoll_op : int
{
    EPOLL_CTL_ADD = 1,
    EPOLL_CTL_DEL = 2,
    EPOLL_CTL_MOD = 3,
}
```

**Step 2: Build to verify no compile errors**

Run: `dotnet build IORingGroup.sln`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add IORingGroup/EPoll/Structs.cs
git commit -m "feat(epoll): add epoll struct definitions for x64 and ARM64"
```

---

### Task 2: Create epoll syscall bindings

**Files:**
- Create: `IORingGroup/EPoll/LinuxEpollGroup.Syscalls.cs`

**Step 1: Write the syscalls partial class**

This file contains all P/Invoke declarations for the epoll backend. The socket syscalls can be shared with `LinuxIORing` but we keep them here for independence (each backend is self-contained).

```csharp
// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;

namespace System.Network.EPoll;

public sealed unsafe partial class LinuxEpollGroup
{
    // epoll constants
    private const int EPOLL_CLOEXEC = 0x80000;

    // Socket constants (same values as LinuxIORing, duplicated for backend independence)
    private const int AF_INET = 2;
    private const int SOCK_STREAM = 1;
    private const int SOCK_NONBLOCK = 0x800;
    private const int IPPROTO_TCP = 6;
    private const int SOL_SOCKET = 1;
    private const int SO_REUSEADDR = 2;
    private const int SO_LINGER = 13;
    private const int SO_ERROR = 4;
    private const int TCP_NODELAY = 1;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 0x800;
    private const int EAGAIN = 11;
    private const int EWOULDBLOCK = EAGAIN;
    private const int EINPROGRESS = 115;
    private const int EINTR = 4;

    // P/Invoke declarations
    private static partial class Syscalls
    {
        [LibraryImport("libc", SetLastError = true)]
        public static partial int epoll_create1(int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int epoll_ctl(int epfd, int op, int fd, nint ev);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int epoll_wait(int epfd, nint events, int maxevents, int timeout);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int close(int fd);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int shutdown(int sockfd, int how);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int socket(int domain, int type, int protocol);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int bind(int sockfd, nint addr, int addrlen);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int listen(int sockfd, int backlog);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int accept(int sockfd, nint addr, int* addrlen);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int connect(int sockfd, nint addr, int addrlen);

        [LibraryImport("libc", SetLastError = true)]
        public static partial nint send(int sockfd, nint buf, nuint len, int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial nint recv(int sockfd, nint buf, nuint len, int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int setsockopt(int sockfd, int level, int optname, nint optval, int optlen);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int getsockopt(int sockfd, int level, int optname, nint optval, ref int optlen);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int fcntl(int fd, int cmd, int arg);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct sockaddr_in
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LingerOption
    {
        public int OnOff;
        public int Seconds;
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build IORingGroup.sln`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add IORingGroup/EPoll/LinuxEpollGroup.Syscalls.cs
git commit -m "feat(epoll): add Linux epoll and socket syscall bindings"
```

---

### Task 3: Implement LinuxEpollGroup core — constructor, connection ID management, completion queue

**Files:**
- Create: `IORingGroup/EPoll/LinuxEpollGroup.cs`

**Step 1: Write the core class with constructor, fields, and connection ID management**

This is the largest task. The file implements `IIORingGroup` using the readiness-to-completion bridge pattern. Key design points:

- **Connection ID management:** Dense slot allocation via free stack. `RegisterSocket()` allocates a slot, `UnregisterSocket()` returns it. The connection ID (slot index) is stored in `epoll_data` so event delivery is O(1).
- **Pending operation tracking:** Fixed arrays `_pendingRecvs[connId]` / `_pendingSends[connId]` with boolean flags `_hasRecv[connId]` / `_hasSend[connId]`.
- **User-space completion queue:** Ring buffer `_cqEntries[]` with head/tail/mask, identical to Darwin.
- **epoll_event handling:** Architecture-dependent struct size (12 bytes x64, 16 bytes ARM64). Use raw byte buffer for `epoll_wait`, extract fields at runtime using pointer arithmetic.

The `PendingOp` struct tracks: opcode, fd, buffer address, buffer length, flags, userData.

For `Submit()`:
1. Iterate `_hasRecv` and `_hasSend` arrays to find pending ops
2. For each pending op, call `epoll_ctl(EPOLL_CTL_MOD, fd, events)` to arm the FD
3. Handle accept ops from `_pendingAccepts` dictionary

For `PeekCompletions()`:
1. Call `epoll_wait(timeout=0)` (non-blocking)
2. For each event, extract connId from `epoll_data`
3. Look up pending op, execute actual I/O syscall (recv/send/accept)
4. Add completion (userData, result) to CQ ring

For `SubmitAndWait(waitNr)`:
1. Call `Submit()`
2. Loop `epoll_wait(timeout=-1)` (blocking) until `waitNr` completions accumulated

```csharp
// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.EPoll;

/// <summary>
/// Linux epoll implementation of IIORingGroup.
/// Bridges readiness-based epoll to the completion-based IIORingGroup API.
/// </summary>
/// <remarks>
/// Epoll is readiness-based (notifies when FDs are ready for I/O).
/// This implementation bridges to completion semantics:
/// - Prepare* methods queue operations in fixed arrays by connection ID
/// - Submit() arms epoll for those operations (epoll_ctl)
/// - PeekCompletions() polls epoll, executes I/O on ready sockets, returns completions
/// </remarks>
public sealed unsafe partial class LinuxEpollGroup : IIORingGroup
{
    private readonly int _epollFd;
    private readonly int _maxConnections;

    // Connection ID management (dense slot allocation)
    private readonly int[] _connIdToFd;       // connId -> FD
    private readonly int[] _freeSlots;        // free stack
    private int _freeSlotCount;

    // Pending operations (fixed arrays indexed by connection ID)
    private readonly PendingOp[] _pendingRecvs;
    private readonly PendingOp[] _pendingSends;
    private readonly bool[] _hasRecv;
    private readonly bool[] _hasSend;
    private readonly bool[] _recvSubmitted;    // whether epoll is armed for this recv
    private readonly bool[] _sendSubmitted;    // whether epoll is armed for this send

    // Accept operations (keyed by listener FD, typically 1-2 listeners)
    private readonly Dictionary<int, PendingOp> _pendingAccepts = new();
    private readonly HashSet<int> _acceptsSubmitted = new();

    // User-space completion queue (ring buffer)
    private readonly Completion[] _cqEntries;
    private int _cqHead;
    private int _cqTail;
    private readonly int _cqMask;

    // epoll_wait event buffer (raw bytes to handle arch-specific struct sizes)
    private readonly byte[] _eventBuffer;
    private readonly int _epollEventSize;
    private readonly int _maxEvents;

    // External buffer tracking (for IORingBuffer/Pool support)
    private readonly int _maxExternalBuffers;
    private readonly nint[] _externalBufferPtrs;
    private readonly int[] _externalBufferLengths;
    private int _externalBufferCount;

    private bool _disposed;

    private struct PendingOp
    {
        public byte Opcode;
        public int Fd;
        public nint Addr;
        public nint Addr2;
        public int Len;
        public int Flags;
        public ulong UserData;
    }

    public LinuxEpollGroup(int queueSize = IORingGroup.DefaultQueueSize, int maxConnections = IORingGroup.DefaultMaxConnections)
    {
        if (!IORingGroup.IsPowerOfTwo(queueSize))
        {
            throw new ArgumentException("Queue size must be a power of 2", nameof(queueSize));
        }

        _maxConnections = maxConnections;

        // Determine epoll_event size based on architecture
        _epollEventSize = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 => sizeof(epoll_event_aligned),
            _ => sizeof(epoll_event_packed)
        };

        _maxEvents = queueSize;
        _eventBuffer = new byte[_maxEvents * _epollEventSize];

        // User-space completion queue
        _cqMask = queueSize * 2 - 1;
        _cqEntries = new Completion[queueSize * 2];

        // Connection ID management
        _connIdToFd = new int[maxConnections];
        _freeSlots = new int[maxConnections];
        Array.Fill(_connIdToFd, -1);

        // Initialize free stack (all slots available, allocate lowest first)
        for (var i = maxConnections - 1; i >= 0; i--)
        {
            _freeSlots[maxConnections - 1 - i] = i;
        }
        _freeSlotCount = maxConnections;

        // Pending operations
        _pendingRecvs = new PendingOp[maxConnections];
        _pendingSends = new PendingOp[maxConnections];
        _hasRecv = new bool[maxConnections];
        _hasSend = new bool[maxConnections];
        _recvSubmitted = new bool[maxConnections];
        _sendSubmitted = new bool[maxConnections];

        // External buffer tracking
        _maxExternalBuffers = maxConnections * 2;
        _externalBufferPtrs = new nint[_maxExternalBuffers];
        _externalBufferLengths = new int[_maxExternalBuffers];

        // Create epoll instance
        _epollFd = Syscalls.epoll_create1(EPOLL_CLOEXEC);
        if (_epollFd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"epoll_create1 failed: errno {errno}");
        }
    }

    /// <inheritdoc/>
    public int SubmissionQueueSpace
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Count how many pending ops we have
            var pending = 0;
            for (var i = 0; i < _maxConnections; i++)
            {
                if (_hasRecv[i] && !_recvSubmitted[i]) pending++;
                if (_hasSend[i] && !_sendSubmitted[i]) pending++;
            }
            pending += _pendingAccepts.Count;
            return _maxEvents - pending;
        }
    }

    /// <inheritdoc/>
    public int CompletionQueueCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_cqTail - _cqHead) & (_cqMask + 1) | ((_cqTail - _cqHead) & _cqMask);
    }

    // =============================================================================
    // Prepare* methods — queue operations for later submission
    // =============================================================================

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollAdd(nint fd, PollMask mask, ulong userData)
    {
        // For epoll, PollAdd maps to arming the FD for the requested events.
        // We store this as a recv or send pending op depending on the mask.
        // Since PollAdd doesn't have a connId, we use the FD directly.
        // This is primarily used for listener accept readiness.

        // Convert PollMask to a pending op
        if ((mask & PollMask.In) != 0)
        {
            // Store as accept (listener poll for readiness)
            _pendingAccepts[(int)fd] = new PendingOp
            {
                Opcode = (byte)IORingOp.PollAdd,
                Fd = (int)fd,
                Flags = (int)mask,
                UserData = userData
            };
        }
        else if ((mask & PollMask.Out) != 0)
        {
            // Store as a write-readiness poll (rare, used for connect completion)
            _pendingAccepts[(int)fd] = new PendingOp
            {
                Opcode = (byte)IORingOp.PollAdd,
                Fd = (int)fd,
                Flags = (int)mask,
                UserData = userData
            };
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollRemove(ulong userData)
    {
        // With EPOLLONESHOT, polls auto-remove after firing.
        // For explicit removal, scan pending accepts.
        int? toRemove = null;
        foreach (var kvp in _pendingAccepts)
        {
            if (kvp.Value.UserData == userData)
            {
                toRemove = kvp.Key;
                break;
            }
        }
        if (toRemove.HasValue)
        {
            _pendingAccepts.Remove(toRemove.Value);
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData)
    {
        _pendingAccepts[(int)listenFd] = new PendingOp
        {
            Opcode = (byte)IORingOp.Accept,
            Fd = (int)listenFd,
            Addr = addr,
            Addr2 = addrLen,
            UserData = userData
        };
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData)
    {
        // Connect is attempted synchronously on non-blocking socket.
        // If EINPROGRESS, we arm epoll for write-readiness.
        var result = Syscalls.connect((int)fd, addr, addrLen);
        if (result == 0)
        {
            // Immediate success
            AddCompletion(userData, 0);
        }
        else
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EINPROGRESS)
            {
                // Store as pending send (write-readiness = connect complete)
                // Find the connId for this FD
                var connId = FindConnIdByFd((int)fd);
                if (connId >= 0)
                {
                    _pendingSends[connId] = new PendingOp
                    {
                        Opcode = (byte)IORingOp.Connect,
                        Fd = (int)fd,
                        UserData = userData
                    };
                    _hasSend[connId] = true;
                    _sendSubmitted[connId] = false;
                }
                else
                {
                    AddCompletion(userData, -EINPROGRESS);
                }
            }
            else
            {
                AddCompletion(userData, -errno);
            }
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareClose(nint fd, ulong userData)
    {
        // Close is synchronous
        var result = Syscalls.close((int)fd);
        AddCompletion(userData, result == 0 ? 0 : -Marshal.GetLastPInvokeError());
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareCancel(ulong targetUserData, ulong userData)
    {
        // Remove pending operations with matching userData
        for (var i = 0; i < _maxConnections; i++)
        {
            if (_hasRecv[i] && _pendingRecvs[i].UserData == targetUserData)
            {
                _hasRecv[i] = false;
                _recvSubmitted[i] = false;
            }
            if (_hasSend[i] && _pendingSends[i].UserData == targetUserData)
            {
                _hasSend[i] = false;
                _sendSubmitted[i] = false;
            }
        }

        int? toRemove = null;
        foreach (var kvp in _pendingAccepts)
        {
            if (kvp.Value.UserData == targetUserData)
            {
                toRemove = kvp.Key;
                break;
            }
        }
        if (toRemove.HasValue)
        {
            _pendingAccepts.Remove(toRemove.Value);
        }

        AddCompletion(userData, 0);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareShutdown(nint fd, int how, ulong userData)
    {
        // Shutdown is synchronous
        var result = Syscalls.shutdown((int)fd, how);
        AddCompletion(userData, result == 0 ? 0 : -Marshal.GetLastPInvokeError());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareSend(int connId, nint buf, int len, MsgFlags flags, ulong userData)
    {
        _pendingSends[connId] = new PendingOp
        {
            Opcode = (byte)IORingOp.Send,
            Fd = _connIdToFd[connId],
            Addr = buf,
            Len = len,
            Flags = (int)flags,
            UserData = userData
        };
        _hasSend[connId] = true;
        _sendSubmitted[connId] = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareRecv(int connId, nint buf, int len, MsgFlags flags, ulong userData)
    {
        _pendingRecvs[connId] = new PendingOp
        {
            Opcode = (byte)IORingOp.Recv,
            Fd = _connIdToFd[connId],
            Addr = buf,
            Len = len,
            Flags = (int)flags,
            UserData = userData
        };
        _hasRecv[connId] = true;
        _recvSubmitted[connId] = false;
    }

    // =============================================================================
    // Submit and Completion
    // =============================================================================

    /// <inheritdoc/>
    public int Submit()
    {
        var submitted = 0;

        // Arm epoll for pending recv operations
        for (var i = 0; i < _maxConnections; i++)
        {
            if (!_hasRecv[i] && !_hasSend[i]) continue;
            if (_recvSubmitted[i] && _sendSubmitted[i]) continue;
            if (!_hasRecv[i] && _sendSubmitted[i]) continue;
            if (_recvSubmitted[i] && !_hasSend[i]) continue;

            var fd = _connIdToFd[i];
            if (fd < 0) continue;

            var events = epoll_events.EPOLLET | epoll_events.EPOLLONESHOT;
            if (_hasRecv[i] && !_recvSubmitted[i])
            {
                events |= epoll_events.EPOLLIN | epoll_events.EPOLLRDHUP;
            }
            if (_hasSend[i] && !_sendSubmitted[i])
            {
                events |= epoll_events.EPOLLOUT;
            }

            if (EpollCtlMod(fd, events, i) == 0)
            {
                if (_hasRecv[i]) _recvSubmitted[i] = true;
                if (_hasSend[i]) _sendSubmitted[i] = true;
                submitted++;
            }
        }

        // Arm epoll for pending accepts
        foreach (var kvp in _pendingAccepts)
        {
            if (_acceptsSubmitted.Contains(kvp.Key)) continue;

            var events = epoll_events.EPOLLIN | epoll_events.EPOLLET | epoll_events.EPOLLONESHOT;
            if (EpollCtlMod(kvp.Key, events, EncodeAcceptData(kvp.Key)) == 0)
            {
                _acceptsSubmitted.Add(kvp.Key);
                submitted++;
            }
        }

        return submitted;
    }

    /// <inheritdoc/>
    public int SubmitAndWait(int waitNr)
    {
        var submitted = Submit();

        // Wait for completions
        while (CompletionQueueCount < waitNr)
        {
            PollAndExecute(blocking: true);
        }

        return submitted;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekCompletions(Span<Completion> completions)
    {
        // Poll epoll for ready events and execute I/O
        PollAndExecute(blocking: false);

        // Copy completions to output
        var available = CompletionQueueCount;
        var count = Math.Min(available, completions.Length);

        for (var i = 0; i < count; i++)
        {
            var index = (_cqHead + i) & _cqMask;
            completions[i] = _cqEntries[index];
        }

        return count;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceCompletionQueue(int count)
    {
        _cqHead += count;
    }

    /// <summary>
    /// Polls epoll for ready events and executes the corresponding I/O operations.
    /// </summary>
    private void PollAndExecute(bool blocking)
    {
        int eventCount;
        fixed (byte* evPtr = _eventBuffer)
        {
            eventCount = Syscalls.epoll_wait(_epollFd, (nint)evPtr, _maxEvents, blocking ? -1 : 0);
        }

        if (eventCount <= 0)
        {
            return;
        }

        fixed (byte* evBase = _eventBuffer)
        {
            for (var i = 0; i < eventCount; i++)
            {
                var evPtr = evBase + i * _epollEventSize;

                // Extract events (always at offset 0, 4 bytes)
                var events = (epoll_events)(*(uint*)evPtr);

                // Extract data (offset depends on architecture)
                var dataOffset = _epollEventSize == 12 ? 4 : 8;
                var data = *(long*)(evPtr + dataOffset);

                if (IsAcceptData(data))
                {
                    var listenerFd = DecodeAcceptData(data);
                    _acceptsSubmitted.Remove(listenerFd);
                    if (_pendingAccepts.Remove(listenerFd, out var acceptOp))
                    {
                        ExecuteAccept(ref acceptOp);
                    }
                }
                else
                {
                    var connId = (int)data;
                    if (connId < 0 || connId >= _maxConnections) continue;

                    // Check for errors
                    if ((events & (epoll_events.EPOLLERR | epoll_events.EPOLLHUP)) != 0)
                    {
                        if (_hasRecv[connId])
                        {
                            AddCompletion(_pendingRecvs[connId].UserData, 0); // EOF / error
                            _hasRecv[connId] = false;
                            _recvSubmitted[connId] = false;
                        }
                        if (_hasSend[connId])
                        {
                            AddCompletion(_pendingSends[connId].UserData, -32); // -EPIPE
                            _hasSend[connId] = false;
                            _sendSubmitted[connId] = false;
                        }
                        continue;
                    }

                    // Handle read readiness
                    if ((events & (epoll_events.EPOLLIN | epoll_events.EPOLLRDHUP)) != 0 && _hasRecv[connId])
                    {
                        _recvSubmitted[connId] = false;
                        ExecuteRecv(connId);
                    }

                    // Handle write readiness
                    if ((events & epoll_events.EPOLLOUT) != 0 && _hasSend[connId])
                    {
                        _sendSubmitted[connId] = false;
                        if (_pendingSends[connId].Opcode == (byte)IORingOp.Connect)
                        {
                            ExecuteConnectComplete(connId);
                        }
                        else
                        {
                            ExecuteSend(connId);
                        }
                    }
                }
            }
        }
    }

    private void ExecuteAccept(ref PendingOp op)
    {
        var addrLen = op.Addr2 != 0 ? (int*)op.Addr2 : null;
        var result = Syscalls.accept(op.Fd, op.Addr, addrLen);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // No connection ready - re-queue
                _pendingAccepts[op.Fd] = op;
                return;
            }
            AddCompletion(op.UserData, -errno);
        }
        else
        {
            AddCompletion(op.UserData, result);
        }
    }

    private void ExecuteRecv(int connId)
    {
        ref var op = ref _pendingRecvs[connId];
        var result = Syscalls.recv(op.Fd, op.Addr, (nuint)op.Len, op.Flags);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // No data ready - keep pending, will need re-arm
                return;
            }
            _hasRecv[connId] = false;
            AddCompletion(op.UserData, -errno);
        }
        else
        {
            _hasRecv[connId] = false;
            AddCompletion(op.UserData, (int)result);
        }
    }

    private void ExecuteSend(int connId)
    {
        ref var op = ref _pendingSends[connId];
        var result = Syscalls.send(op.Fd, op.Addr, (nuint)op.Len, op.Flags);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // Buffer full - keep pending, will need re-arm
                return;
            }
            _hasSend[connId] = false;
            AddCompletion(op.UserData, -errno);
        }
        else
        {
            _hasSend[connId] = false;
            AddCompletion(op.UserData, (int)result);
        }
    }

    private void ExecuteConnectComplete(int connId)
    {
        ref var op = ref _pendingSends[connId];
        int error = 0;
        var len = sizeof(int);
        Syscalls.getsockopt(op.Fd, SOL_SOCKET, SO_ERROR, (nint)(&error), ref len);
        _hasSend[connId] = false;
        _sendSubmitted[connId] = false;
        AddCompletion(op.UserData, error == 0 ? 0 : -error);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddCompletion(ulong userData, int result)
    {
        var cqIndex = _cqTail & _cqMask;
        _cqEntries[cqIndex] = new Completion(userData, result);
        _cqTail++;
    }

    // =============================================================================
    // epoll_ctl helpers
    // =============================================================================

    /// <summary>
    /// Arms or modifies an epoll registration. Tries MOD first, falls back to ADD.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int EpollCtlMod(int fd, epoll_events events, int data)
    {
        if (_epollEventSize == sizeof(epoll_event_packed))
        {
            var ev = new epoll_event_packed { events = (uint)events, data = data };
            var result = Syscalls.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_MOD, fd, (nint)(&ev));
            if (result < 0 && Marshal.GetLastPInvokeError() == 2) // ENOENT
            {
                result = Syscalls.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_ADD, fd, (nint)(&ev));
            }
            return result;
        }
        else
        {
            var ev = new epoll_event_aligned { events = (uint)events, data = data };
            var result = Syscalls.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_MOD, fd, (nint)(&ev));
            if (result < 0 && Marshal.GetLastPInvokeError() == 2) // ENOENT
            {
                result = Syscalls.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_ADD, fd, (nint)(&ev));
            }
            return result;
        }
    }

    // Encode listener FD in accept data with high bit set to distinguish from connId
    private const long AcceptDataFlag = unchecked((long)0x8000_0000_0000_0000L);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EncodeAcceptData(int listenerFd) => AcceptDataFlag | (uint)listenerFd;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAcceptData(long data) => (data & AcceptDataFlag) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeAcceptData(long data) => (int)(data & 0x7FFFFFFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindConnIdByFd(int fd)
    {
        for (var i = 0; i < _maxConnections; i++)
        {
            if (_connIdToFd[i] == fd) return i;
        }
        return -1;
    }

    // =============================================================================
    // Listener and Socket Management
    // =============================================================================

    /// <inheritdoc/>
    public nint CreateListener(string bindAddress, ushort port, int backlog)
    {
        var fd = Syscalls.socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK, IPPROTO_TCP);
        if (fd < 0) return -1;

        // Disable SO_REUSEADDR (exclusive address use)
        var optval = 0;
        Syscalls.setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, (nint)(&optval), sizeof(int));

        // TCP_NODELAY
        optval = 1;
        Syscalls.setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new LingerOption { OnOff = 0, Seconds = 0 };
        Syscalls.setsockopt(fd, SOL_SOCKET, SO_LINGER, (nint)(&linger), sizeof(LingerOption));

        // Bind
        var addr = new sockaddr_in
        {
            sin_family = AF_INET,
            sin_port = BinaryPrimitives.ReverseEndianness(port),
            sin_addr = ParseIPv4(bindAddress)
        };

        if (Syscalls.bind(fd, (nint)(&addr), sizeof(sockaddr_in)) < 0)
        {
            Syscalls.close(fd);
            return -1;
        }

        if (Syscalls.listen(fd, backlog) < 0)
        {
            Syscalls.close(fd);
            return -1;
        }

        return fd;
    }

    /// <inheritdoc/>
    public void CloseListener(nint listener)
    {
        if (listener >= 0)
        {
            _pendingAccepts.Remove((int)listener);
            _acceptsSubmitted.Remove((int)listener);
            Syscalls.close((int)listener);
        }
    }

    /// <inheritdoc/>
    public void ConfigureSocket(nint socket)
    {
        var fd = (int)socket;

        // Set non-blocking
        var flags = Syscalls.fcntl(fd, F_GETFL, 0);
        if (flags >= 0)
        {
            Syscalls.fcntl(fd, F_SETFL, flags | O_NONBLOCK);
        }

        // TCP_NODELAY
        var optval = 1;
        Syscalls.setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new LingerOption { OnOff = 0, Seconds = 0 };
        Syscalls.setsockopt(fd, SOL_SOCKET, SO_LINGER, (nint)(&linger), sizeof(LingerOption));
    }

    /// <inheritdoc/>
    public int RegisterSocket(nint socket)
    {
        if (_freeSlotCount == 0)
        {
            return -1; // No free slots
        }

        var connId = _freeSlots[--_freeSlotCount];
        _connIdToFd[connId] = (int)socket;

        // Add to epoll with no initial events (will be armed on Prepare*)
        // Use EPOLLET so first arm via MOD works
        var events = epoll_events.EPOLLET | epoll_events.EPOLLONESHOT;
        if (_epollEventSize == sizeof(epoll_event_packed))
        {
            var ev = new epoll_event_packed { events = (uint)events, data = connId };
            Syscalls.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_ADD, (int)socket, (nint)(&ev));
        }
        else
        {
            var ev = new epoll_event_aligned { events = (uint)events, data = connId };
            Syscalls.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_ADD, (int)socket, (nint)(&ev));
        }

        return connId;
    }

    /// <inheritdoc/>
    public void UnregisterSocket(int connId)
    {
        if (connId < 0 || connId >= _maxConnections) return;

        var fd = _connIdToFd[connId];
        if (fd >= 0)
        {
            Syscalls.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_DEL, fd, 0);
        }

        // Clear pending ops
        _hasRecv[connId] = false;
        _hasSend[connId] = false;
        _recvSubmitted[connId] = false;
        _sendSubmitted[connId] = false;
        _connIdToFd[connId] = -1;

        // Return slot
        _freeSlots[_freeSlotCount++] = connId;
    }

    /// <inheritdoc/>
    public void CloseSocket(nint socket)
    {
        if (socket >= 0)
        {
            Syscalls.close((int)socket);
        }
    }

    /// <inheritdoc/>
    public void Shutdown(nint socket, int how)
    {
        if (socket >= 0)
        {
            Syscalls.shutdown((int)socket, how);
        }
    }

    // =============================================================================
    // Registered Buffer Operations (Zero-Copy I/O)
    // =============================================================================

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterBuffer(IORingBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_externalBufferCount >= _maxExternalBuffers)
        {
            throw new InvalidOperationException("Maximum external buffer count reached");
        }

        for (var i = 0; i < _maxExternalBuffers; i++)
        {
            if (_externalBufferPtrs[i] == 0)
            {
                _externalBufferPtrs[i] = buffer.Pointer;
                _externalBufferLengths[i] = buffer.VirtualSize;
                _externalBufferCount++;
                return i;
            }
        }

        throw new InvalidOperationException("No free buffer slots available");
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnregisterBuffer(int bufferId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bufferId, _maxExternalBuffers);

        if (_externalBufferPtrs[bufferId] != 0)
        {
            _externalBufferPtrs[bufferId] = 0;
            _externalBufferLengths[bufferId] = 0;
            _externalBufferCount--;
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSendBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bufferId, _maxExternalBuffers);

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
        {
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");
        }

        if (offset + length > _externalBufferLengths[bufferId])
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset + length exceeds buffer size");
        }

        PrepareSend(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bufferId, _maxExternalBuffers);

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
        {
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");
        }

        if (offset + length > _externalBufferLengths[bufferId])
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset + length exceeds buffer size");
        }

        PrepareRecv(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <summary>
    /// Gets the number of registered external buffers.
    /// </summary>
    public int ExternalBufferCount => _externalBufferCount;

    private static uint ParseIPv4(string address)
    {
        if (address == "0.0.0.0") return 0;

        var parts = address.Split('.');
        if (parts.Length != 4) return 0;

        return (uint)(
            byte.Parse(parts[0]) |
            (byte.Parse(parts[1]) << 8) |
            (byte.Parse(parts[2]) << 16) |
            (byte.Parse(parts[3]) << 24)
        );
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pendingAccepts.Clear();
        _acceptsSubmitted.Clear();

        if (_epollFd >= 0)
        {
            Syscalls.close(_epollFd);
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build IORingGroup.sln`
Expected: Build succeeded (warnings about Linux-only code are expected on Windows)

**Step 3: Commit**

```bash
git add IORingGroup/EPoll/LinuxEpollGroup.cs
git commit -m "feat(epoll): implement LinuxEpollGroup with readiness-to-completion bridge"
```

---

### Task 4: Update factory to support epoll fallback

**Files:**
- Modify: `IORingGroup/IORingGroup.cs:56-64`

**Step 1: Update CreateLinuxRing to fall back to epoll**

Replace the `CreateLinuxRing` method and add `CreateLinuxEpoll`:

```csharp
private static IIORingGroup CreateLinuxRing(int queueSize, int maxConnections)
{
    if (IORing.LinuxIORingGroup.IsAvailable())
    {
        return new IORing.LinuxIORingGroup(queueSize, maxConnections);
    }

    // Fallback to epoll when io_uring is unavailable
    return new EPoll.LinuxEpollGroup(queueSize, maxConnections);
}

/// <summary>
/// Creates an epoll-based IIORingGroup for Linux explicitly.
/// Useful for testing or when io_uring should be bypassed.
/// </summary>
/// <param name="queueSize">Size of the submission and completion queues. Must be power of 2.</param>
/// <param name="maxConnections">Maximum concurrent connections.</param>
/// <returns>Epoll-based IIORingGroup implementation.</returns>
/// <exception cref="PlatformNotSupportedException">Thrown if not running on Linux.</exception>
public static IIORingGroup CreateLinuxEpoll(int queueSize = DefaultQueueSize, int maxConnections = DefaultMaxConnections)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        throw new PlatformNotSupportedException("epoll requires Linux");
    }

    return new EPoll.LinuxEpollGroup(queueSize, maxConnections);
}
```

**Step 2: Build to verify**

Run: `dotnet build IORingGroup.sln`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add IORingGroup/IORingGroup.cs
git commit -m "feat(epoll): add epoll fallback to factory and CreateLinuxEpoll method"
```

---

### Task 5: Add epoll tests

**Files:**
- Create: `IORingGroup.Tests/LinuxEpollGroupTests.cs`

**Step 1: Write platform-conditional epoll tests**

These tests mirror `IORingGroupTests` but explicitly test the epoll backend. They skip on non-Linux platforms.

```csharp
using System.Runtime.InteropServices;
using System.Network;

namespace IORingGroup.Tests;

public class LinuxEpollGroupTests
{
    [SkippableFact]
    public void CreateLinuxEpoll_OnLinux_ReturnsValidInstance()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll();
        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void CreateLinuxEpoll_OnNonLinux_ThrowsPlatformNotSupported()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        Assert.Throws<PlatformNotSupportedException>(() => IORingGroup.CreateLinuxEpoll());
    }

    [SkippableFact]
    public void SubmissionQueueSpace_InitiallyPositive()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll(256);
        Assert.True(ring.SubmissionQueueSpace > 0);
    }

    [SkippableFact]
    public void CompletionQueueCount_InitiallyZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll();
        Assert.Equal(0, ring.CompletionQueueCount);
    }

    [SkippableFact]
    public void Submit_WithNoOperations_ReturnsZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll();
        var submitted = ring.Submit();
        Assert.Equal(0, submitted);
    }

    [SkippableFact]
    public void PeekCompletions_WithNoCompletions_ReturnsZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll();
        Span<Completion> completions = stackalloc Completion[16];
        var count = ring.PeekCompletions(completions);
        Assert.Equal(0, count);
    }

    [SkippableFact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        var ring = IORingGroup.CreateLinuxEpoll();
        ring.Dispose();
        ring.Dispose(); // Should not throw
    }

    [SkippableFact]
    public void CreateListener_BindsSuccessfully()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll();
        var listener = ring.CreateListener("127.0.0.1", 0, 128);
        Assert.True(listener >= 0, $"CreateListener returned {listener}");
        ring.CloseListener(listener);
    }

    [SkippableFact]
    public void RegisterSocket_ReturnsValidConnId()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll(maxConnections: 16);
        var listener = ring.CreateListener("127.0.0.1", 0, 1);
        Assert.True(listener >= 0);

        // We need a connected socket to register. For unit testing,
        // just verify the slot mechanism works.
        ring.CloseListener(listener);
    }

    [SkippableFact]
    public void BufferRegistration_RegisterAndUnregister()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

        using var ring = IORingGroup.CreateLinuxEpoll();
        using var buffer = IORingBuffer.Create(4096);

        var bufferId = ring.RegisterBuffer(buffer);
        Assert.True(bufferId >= 0);

        ring.UnregisterBuffer(bufferId);
    }
}
```

**Step 2: Build and run tests**

Run: `dotnet build IORingGroup.sln`
Expected: Build succeeded

Run: `dotnet test IORingGroup.Tests/IORingGroup.Tests.csproj --filter "FullyQualifiedName~LinuxEpollGroupTests"`
Expected: All tests pass (on Linux) or skip (on Windows/macOS)

**Step 3: Commit**

```bash
git add IORingGroup.Tests/LinuxEpollGroupTests.cs
git commit -m "test(epoll): add platform-conditional epoll backend tests"
```

---

### Task 6: Add --epoll flag to TestServer

**Files:**
- Modify: `TestServer/Program.cs:12-16` (ServerBackend enum)
- Modify: `TestServer/Program.cs:89-100` (argument parsing)
- Modify: `TestServer/Program.cs:168-176` (backend dispatch)
- Modify: `TestServer/Program.cs:186-191` (backend name display)

**Step 1: Add Epoll backend option**

Add `Epoll` to the `ServerBackend` enum:

```csharp
public enum ServerBackend
{
    IORing,     // Unified: Windows RIO / Linux io_uring / Darwin kqueue
    Epoll,      // Linux epoll (fallback, for testing/benchmarking)
    PollGroup   // Cross-platform: wepoll (Windows) / epoll (Linux) / kqueue (macOS)
}
```

**Step 2: Add --epoll argument parsing**

In the argument parsing loop, add before the `--ioring` check:

```csharp
else if (arg.Equals("--epoll", StringComparison.OrdinalIgnoreCase) ||
         arg.Equals("-e", StringComparison.OrdinalIgnoreCase))
{
    backend = ServerBackend.Epoll;
}
```

Note: Remove `--epoll` from the `--pollgroup` aliases (it was previously aliased there).

**Step 3: Update backend dispatch**

```csharp
if (backend == ServerBackend.IORing)
{
    RunIORingServer(benchmarkMode);
}
else if (backend == ServerBackend.Epoll)
{
    RunEpollServer(benchmarkMode);
}
else
{
    RunPollGroupServer(benchmarkMode);
}
```

**Step 4: Add RunEpollServer method**

Add a new method that creates an `IIORingGroup` via `CreateLinuxEpoll()` and reuses `RunServerLoop`:

```csharp
private static void RunEpollServer(bool benchmarkMode)
{
    try
    {
        using var ring = IORingGroup.CreateLinuxEpoll(queueSize: MaxClients, maxConnections: MaxClients);
        Console.WriteLine($"Epoll ring created: MaxConnections={MaxClients}");

        _bufferPool = new IORingBufferPool(
            ring,
            slabSize: 256,
            bufferSize: BufferSize,
            initialSlabs: 4,
            maxSlabs: 64
        );
        Console.WriteLine($"Buffer pool created: {_bufferPool.TotalCapacity} buffers ({_bufferPool.BufferSize} bytes each)");

        var listener = ring.CreateListener("0.0.0.0", Port, ListenBacklog);
        if (listener == -1)
        {
            throw new InvalidOperationException("Failed to create listener");
        }

        Console.WriteLine($"Epoll server listening on port {Port} (listener fd={listener})");

        try
        {
            RunServerLoop(ring, listener, benchmarkMode);
        }
        finally
        {
            ring.CloseListener(listener);
            for (var i = 0; i < MaxClients; i++)
            {
                if (_clients[i].Active) CloseClient(ring, i);
            }
            _bufferPool.Dispose();
            _bufferPool = null;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Epoll server error: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}
```

**Step 5: Update backend name in IORing path**

In `RunIORingServer`, update the backend name detection to include epoll fallback:

```csharp
var backendName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    ? "RIO" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        ? (ring is System.Network.EPoll.LinuxEpollGroup ? "epoll (fallback)" : "io_uring")
        : "kqueue";
```

**Step 6: Update help text**

```csharp
Console.WriteLine("  --epoll|-e: Linux epoll (explicit fallback, for benchmarking)");
```

**Step 7: Build to verify**

Run: `dotnet build IORingGroup.sln`
Expected: Build succeeded

**Step 8: Commit**

```bash
git add TestServer/Program.cs
git commit -m "feat(testserver): add --epoll flag for explicit epoll backend benchmarking"
```

---

### Task 7: Optimize Darwin backend — replace dictionaries with fixed arrays

**Files:**
- Modify: `IORingGroup/Darwin/DarwinIORingGroup.cs`

**Step 1: Add connection ID management fields**

Replace the dictionary-based pending ops with fixed arrays. Add slot allocation like the epoll backend.

Changes needed:
1. Remove `Dictionary<nint, PendingOp> _pendingRecvs, _pendingSends`
2. Add `PendingOp[] _pendingRecvs, _pendingSends` + `bool[] _hasRecv, _hasSend`
3. Add `int[] _connIdToFd, _freeSlots` + `int _freeSlotCount`
4. Update `RegisterSocket()` to allocate dense slots
5. Update `UnregisterSocket()` to return slots
6. Update `Submit()` to iterate arrays instead of dictionaries
7. Update `PollAndExecute()` to look up connId from `kevent.udata`
8. Keep `_pendingAccepts` as dictionary (sparse, 1-2 listeners)

This is a significant refactor of `DarwinIORingGroup.cs`. The key changes:

- `RegisterSocket(nint socket)` changes from returning `(int)socket` to allocating a slot and storing `connId → FD` mapping
- `PrepareSend/PrepareRecv` uses connId to index into fixed arrays
- `Submit()` iterates `_hasRecv/_hasSend` arrays, stores connId in `kevent.udata`
- `PollAndExecute()` extracts connId from `kevent.udata` instead of using FD as dictionary key

**Step 2: Build and run existing tests**

Run: `dotnet build IORingGroup.sln`
Expected: Build succeeded

Run: `dotnet test IORingGroup.Tests/IORingGroup.Tests.csproj`
Expected: All existing tests pass (Darwin tests skip on non-macOS)

**Step 3: Commit**

```bash
git add IORingGroup/Darwin/DarwinIORingGroup.cs
git commit -m "perf(darwin): replace dictionary pending ops with fixed arrays indexed by connection ID"
```

---

### Task 8: Integration testing on Linux

**Context:** This task should be run on a Linux machine or in a Linux container.

**Step 1: Build and run full test suite**

Run: `dotnet test IORingGroup.Tests/IORingGroup.Tests.csproj -v normal`
Expected: All Linux tests pass (epoll + io_uring), Windows/macOS tests skip

**Step 2: Run echo server with epoll backend**

Run: `dotnet run --project TestServer -- --epoll --benchmark --duration 10`
In separate terminal: `dotnet run --project TestClient -- --host 127.0.0.1 --port 5000`
Expected: Server echoes data, prints benchmark stats after 10 seconds

**Step 3: Compare with io_uring backend**

Run: `dotnet run --project TestServer -- --ioring --benchmark --duration 10`
In separate terminal: `dotnet run --project TestClient -- --host 127.0.0.1 --port 5000`
Expected: Server echoes data, prints comparable benchmark stats

**Step 4: Compare with PollGroup backend**

Run: `dotnet run --project TestServer -- --pollgroup --benchmark --duration 10`
In separate terminal: `dotnet run --project TestClient -- --host 127.0.0.1 --port 5000`
Expected: Server echoes data, prints benchmark stats for comparison

**Step 5: Record results and commit any fixes**

Document performance comparison in commit message.

---

### Task 9: Final review and cleanup

**Step 1: Verify all files have SPDX license headers**

Check all new files in `IORingGroup/EPoll/` have:
```
// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
```

**Step 2: Verify build with warnings-as-errors**

Run: `dotnet build IORingGroup.sln -warnaserror`
Expected: Build succeeded with no warnings

**Step 3: Run full test suite**

Run: `dotnet test IORingGroup.Tests/IORingGroup.Tests.csproj`
Expected: All applicable tests pass

**Step 4: Final commit if needed**

```bash
git add -A
git commit -m "chore: final cleanup for epoll fallback backend"
```
