// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.Darwin;

/// <summary>
/// macOS/BSD implementation of IIORingGroup using kqueue for async I/O.
/// </summary>
/// <remarks>
/// Unlike io_uring/RIO which are true completion-based, kqueue is readiness-based.
/// This implementation bridges the gap:
/// - Prepare* methods queue operations
/// - Submit() registers kqueue interest for those operations
/// - PeekCompletions() polls kqueue, executes I/O on ready sockets, returns completions
/// </remarks>
public sealed unsafe partial class DarwinIORingGroup : IIORingGroup
{
    private readonly int _kqueueFd;
    private readonly int _queueSize;

    // Connection ID management (dense slot allocation)
    private readonly int _maxConnections;
    private readonly int[] _connIdToFd;       // connId -> FD, initialized to -1
    private readonly int[] _freeSlots;        // free stack
    private int _freeSlotCount;

    // Pending operations (fixed arrays indexed by connection ID)
    private readonly PendingOp[] _pendingRecvs;
    private readonly PendingOp[] _pendingSends;
    private readonly bool[] _hasRecv;
    private readonly bool[] _hasSend;

    // Accept operations stay as dictionary (keyed by listener FD, sparse, typically 1-2)
    private readonly Dictionary<nint, PendingOp> _pendingAccepts = new();

    // Listener fds whose ONESHOT read filter is currently registered; delivery removes the fd.
    private readonly HashSet<nint> _acceptsSubmitted = new();

    // Directions currently armed in the kqueue per connection. ONESHOT filters die on delivery
    // (which clears the bit); a cancelled op's filter stays armed and is reused by the next op.
    private const byte ArmedRead = 1;
    private const byte ArmedWrite = 2;
    private readonly byte[] _armedIo;

    // Dirty-connection queue: Submit() re-examines only connections whose arming inputs changed
    // (O(changed), not O(maxConnections)). Invariant: a connection with wanted-but-unarmed work
    // is always queued. _isDirty deduplicates, bounding the queue at _maxConnections.
    private readonly int[] _dirtyQueue;
    private readonly bool[] _isDirty;
    private int _dirtyCount;

    // Completion queue (user-space ring buffer)
    private readonly Completion[] _cqEntries;
    private int _cqHead;
    private int _cqTail;
    private readonly int _cqMask;

    // kevent arrays for batch operations
    private readonly kevent[] _changeList;
    private int _changeCount;
    private readonly kevent[] _resultEvents;

    // EVFILT_USER identity for Wake(); the only registration in that filter, so a fixed value.
    private const nint WakeIdent = 1;
    private const uint NOTE_TRIGGER = 0x01000000;

    // Prebuilt change list for Wake(); kevent treats it as input only, so cross-thread sharing
    // is safe.
    private readonly kevent[] _wakeTrigger;
    private bool _wakeRegistered = true;
    private volatile bool _loggedWakeFailure;

    // Wake() calls in flight past the _disposed check; Dispose drains to zero before closing the
    // kqueue fd.
    private int _wakeGuard;

    private volatile bool _disposed;

    // External buffer tracking (for IORingBuffer/Pool support)
    private readonly int _maxExternalBuffers;
    private readonly nint[] _externalBufferPtrs;
    private readonly int[] _externalBufferLengths;
    private int _externalBufferCount;

    private struct PendingOp
    {
        public byte Opcode;
        public nint Fd;
        public nint Addr;
        public nint Addr2;
        public int Len;
        public int Flags;
        public ulong UserData;
    }

    public DarwinIORingGroup(int queueSize, int maxConnections = IORingGroup.DefaultMaxConnections)
    {
        if (!IORingGroup.IsPowerOfTwo(queueSize))
        {
            throw new ArgumentException("Queue size must be a power of 2", nameof(queueSize));
        }

        _queueSize = queueSize;
        _maxConnections = maxConnections;
        _cqMask = queueSize * 2 - 1;

        _cqEntries = new Completion[queueSize * 2];
        _changeList = new kevent[queueSize];
        _resultEvents = new kevent[queueSize];

        // Allocate connection ID management arrays
        _connIdToFd = new int[maxConnections];
        _freeSlots = new int[maxConnections];
        _pendingRecvs = new PendingOp[maxConnections];
        _pendingSends = new PendingOp[maxConnections];
        _hasRecv = new bool[maxConnections];
        _hasSend = new bool[maxConnections];
        _armedIo = new byte[maxConnections];
        _dirtyQueue = new int[maxConnections];
        _isDirty = new bool[maxConnections];

        // Initialize free stack (all slots available, lowest first for pop order)
        for (var i = 0; i < maxConnections; i++)
        {
            _connIdToFd[i] = -1;
            _freeSlots[i] = maxConnections - 1 - i; // stack: top = 0, bottom = maxConnections-1
        }
        _freeSlotCount = maxConnections;

        // Initialize external buffer tracking (maxConnections * 2 for recv + send buffer per connection)
        _maxExternalBuffers = maxConnections * 2;
        _externalBufferPtrs = new nint[_maxExternalBuffers];
        _externalBufferLengths = new int[_maxExternalBuffers];

        _kqueueFd = Darwin.kqueue();
        if (_kqueueFd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"kqueue() failed: errno {errno}");
        }

        // Register the user event used by Wake(). EV_CLEAR latches it until delivered once, so a
        // Wake() landing before the caller blocks is not lost.
        _wakeTrigger =
        [
            new kevent
            {
                ident = WakeIdent,
                filter = (short)kqueue_filter.USER,
                fflags = NOTE_TRIGGER
            }
        ];

        kevent[] register =
        [
            new kevent
            {
                ident = WakeIdent,
                filter = (short)kqueue_filter.USER,
                flags = (ushort)(kqueue_flags.ADD | kqueue_flags.CLEAR)
            }
        ];

        // Not fatal (callers fall back to their timeout) but must not be silent.
        if (Darwin.kevent(_kqueueFd, register, 1, null, 0, nint.Zero) < 0)
        {
            _wakeRegistered = false;
            Console.Error.WriteLine(
                $"IORingGroup: failed to register EVFILT_USER for Wake() (errno {Marshal.GetLastPInvokeError()}); " +
                "callers will only wake on I/O or timeout."
            );
        }
    }

    /// <inheritdoc/>
    public int SubmissionQueueSpace
    {
        get
        {
            var pending = _pendingAccepts.Count;
            for (var i = 0; i < _maxConnections; i++)
            {
                if (_hasRecv[i]) pending++;
                if (_hasSend[i]) pending++;
            }
            return _queueSize - _changeCount - pending;
        }
    }

    /// <inheritdoc/>
    public int CompletionQueueCount => _cqTail - _cqHead;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollAdd(nint fd, PollMask mask, ulong userData)
    {
        // Direct kqueue registration for poll operations
        short filter = 0;
        if ((mask & PollMask.In) != 0)
        {
            filter = (short)kqueue_filter.READ;
        }
        else if ((mask & PollMask.Out) != 0)
        {
            filter = (short)kqueue_filter.WRITE;
        }

        AddKqueueChange(fd, filter, kqueue_flags.ADD | kqueue_flags.ONESHOT, (nint)userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollRemove(ulong userData)
    {
        // For kqueue, we'd need to track the fd associated with this userData
        // For now, ONESHOT handles cleanup automatically
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData)
    {
        _pendingAccepts[listenFd] = new PendingOp
        {
            Opcode = (byte)IORingOp.Accept,
            Fd = listenFd,
            Addr = addr,
            Addr2 = addrLen,
            UserData = userData
        };
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData)
    {
        // Connect is typically done synchronously on non-blocking socket
        // Returns immediately with EINPROGRESS, then we wait for EVFILT_WRITE
        var result = Darwin.connect((int)fd, addr, addrLen);
        if (result == 0)
        {
            // Immediate success (rare for TCP)
            AddCompletion(userData, 0);
        }
        else
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EINPROGRESS)
            {
                // Connection in progress — find connId and store as pending send
                var connId = FindConnIdByFd((int)fd);
                if (connId >= 0)
                {
                    _pendingSends[connId] = new PendingOp
                    {
                        Opcode = (byte)IORingOp.Connect,
                        Fd = fd,
                        UserData = userData
                    };
                    _hasSend[connId] = true;
                    MarkDirty(connId);
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

    // One pending send per connection: this overwrites the slot unconditionally, so a second send
    // posted before the first completes discards it and wedges the socket. Upheld by
    // MaxOutstandingSendsPerSocket == 1; raising it requires a real queue here.
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
        MarkDirty(connId);
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
        MarkDirty(connId);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareClose(nint fd, ulong userData)
    {
        // Close is synchronous - execute immediately
        var result = Darwin.close((int)fd);
        AddCompletion(userData, result == 0 ? 0 : -Marshal.GetLastPInvokeError());
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareCancel(ulong targetUserData, ulong userData)
    {
        // Scan fixed arrays for matching userData — O(n) but cancellation is rare
        for (var i = 0; i < _maxConnections; i++)
        {
            if (_hasRecv[i] && _pendingRecvs[i].UserData == targetUserData)
            {
                _hasRecv[i] = false;
                MarkDirty(i);
            }
            if (_hasSend[i] && _pendingSends[i].UserData == targetUserData)
            {
                _hasSend[i] = false;
                MarkDirty(i);
            }
        }

        // Also check accepts
        nint? toRemove = null;
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
            // Forget the armed state too: a stale entry would make Submit skip arming a new
            // listener that recycles this fd, and a redundant re-ADD is a harmless update.
            _pendingAccepts.Remove(toRemove.Value);
            _acceptsSubmitted.Remove(toRemove.Value);
        }

        AddCompletion(userData, 0);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareShutdown(nint fd, int how, ulong userData)
    {
        // Shutdown is synchronous
        var result = Darwin.shutdown((int)fd, how);
        AddCompletion(userData, result == 0 ? 0 : -Marshal.GetLastPInvokeError());
    }

    // Every write to _hasRecv/_hasSend/_armedIo/_connIdToFd must be paired with this, or Submit()
    // will never re-examine the connection.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkDirty(int connId)
    {
        if (!_isDirty[connId])
        {
            _isDirty[connId] = true;
            _dirtyQueue[_dirtyCount++] = connId;
        }
    }

    /// <inheritdoc/>
    public int Submit()
    {
        var submitted = 0;

        // Arm only listeners not already armed; delivery removes them from _acceptsSubmitted.
        foreach (var kvp in _pendingAccepts)
        {
            if (_acceptsSubmitted.Add(kvp.Key))
            {
                AddKqueueChange(kvp.Key, (short)kqueue_filter.READ, kqueue_flags.ADD | kqueue_flags.ONESHOT, kvp.Key);
                submitted++;
            }
        }

        // Arm only dirty connections. A wanted-and-armed direction keeps its live kernel filter
        // (ONESHOT dies on delivery, where the bit is cleared), so it queues no change.
        var dirty = _dirtyCount;
        _dirtyCount = 0;
        for (var d = 0; d < dirty; d++)
        {
            var i = _dirtyQueue[d];
            _isDirty[i] = false;

            var fd = _connIdToFd[i];
            if (fd < 0)
            {
                continue;
            }

            if (_hasRecv[i] && (_armedIo[i] & ArmedRead) == 0)
            {
                AddKqueueChange(fd, (short)kqueue_filter.READ, kqueue_flags.ADD | kqueue_flags.ONESHOT, (nint)i);
                _armedIo[i] |= ArmedRead;
                submitted++;
            }

            if (_hasSend[i] && (_armedIo[i] & ArmedWrite) == 0)
            {
                AddKqueueChange(fd, (short)kqueue_filter.WRITE, kqueue_flags.ADD | kqueue_flags.ONESHOT, (nint)i);
                _armedIo[i] |= ArmedWrite;
                submitted++;
            }
        }

        // Submit all kqueue changes
        if (_changeCount > 0)
        {
            var result = Darwin.kevent(_kqueueFd, _changeList, _changeCount, null, 0, nint.Zero);
            if (result < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                // Log error but don't throw - some changes may have succeeded
                Console.Error.WriteLine($"[DarwinIORingGroup] kevent submit failed: errno {errno}");

                // Forget what this round claimed to arm so the next Submit retries everything;
                // re-ADDing a filter that did land is a harmless update.
                for (var d = 0; d < dirty; d++)
                {
                    var i = _dirtyQueue[d];
                    _armedIo[i] = 0;
                    MarkDirty(i);
                }

                _acceptsSubmitted.Clear();
            }
            _changeCount = 0;
        }

        return submitted;
    }

    /// <inheritdoc/>
    public int SubmitAndWait(int waitNr)
    {
        var submitted = Submit();

        // Wait for at least waitNr completions
        while (CompletionQueueCount < waitNr)
        {
            PollAndExecute(-1);
        }

        return submitted;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekCompletions(Span<Completion> completions)
    {
        // Poll kqueue for ready events and execute I/O
        PollAndExecute(0);

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
    /// Polls kqueue for ready events and executes the corresponding I/O operations.
    /// </summary>
    /// <param name="timeoutMs">
    /// 0 for non-blocking, negative for blocking indefinitely, positive for timed wait.
    /// </param>
    private void PollAndExecute(int timeoutMs)
    {
        var timeout = new timespec
        {
            tv_sec = timeoutMs > 0 ? timeoutMs / 1000 : 0,
            tv_nsec = timeoutMs > 0 ? (timeoutMs % 1000) * 1_000_000L : 0
        };
        var timeoutPtr = timeoutMs < 0 ? nint.Zero : (nint)(&timeout);

        var eventCount = Darwin.kevent(_kqueueFd, null, 0, _resultEvents, _resultEvents.Length, timeoutPtr);

        if (eventCount < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EINTR)
            {
                // Interrupted by a signal — the caller re-polls.
                return;
            }

            // A hard error would otherwise spin SubmitAndWait's wait loop forever.
            throw new InvalidOperationException($"kevent() wait failed: errno {errno}");
        }

        if (eventCount == 0)
        {
            return;
        }

        for (var i = 0; i < eventCount; i++)
        {
            ref var ev = ref _resultEvents[i];
            var fd = ev.ident;

            // Wake() only exists to break the kevent wait; there is no I/O behind it.
            if (ev.filter == (short)kqueue_filter.USER)
            {
                continue;
            }

            // Check for accept events first (by checking the accept dictionary)
            if (_pendingAccepts.Remove(fd, out var acceptOp))
            {
                // The ONESHOT filter died on delivery; a re-posted accept must be re-armed.
                _acceptsSubmitted.Remove(fd);

                // Check for errors on accept
                if ((ev.flags & (ushort)kqueue_flags.ERROR) != 0)
                {
                    AddCompletion(acceptOp.UserData, -(int)ev.data);
                    continue;
                }

                ExecuteAccept(ref acceptOp);
                continue;
            }

            // Connection event — extract connId from udata
            var connId = (int)ev.udata;
            if (connId < 0 || connId >= _maxConnections)
            {
                continue;
            }

            // The delivered ONESHOT filter died, so clear its armed bit unconditionally -- before
            // the staleness guards below. The dirty mark makes the next Submit re-arm whatever
            // the handlers leave pending (and covers their flag writes).
            if (ev.filter == (short)kqueue_filter.READ)
            {
                _armedIo[connId] &= unchecked((byte)~ArmedRead);
            }
            else if (ev.filter == (short)kqueue_filter.WRITE)
            {
                _armedIo[connId] &= unchecked((byte)~ArmedWrite);
            }
            MarkDirty(connId);

            // Check for errors
            if ((ev.flags & (ushort)kqueue_flags.ERROR) != 0)
            {
                if (_hasRecv[connId])
                {
                    _hasRecv[connId] = false;
                    AddCompletion(_pendingRecvs[connId].UserData, -(int)ev.data);
                }
                if (_hasSend[connId])
                {
                    _hasSend[connId] = false;
                    AddCompletion(_pendingSends[connId].UserData, -(int)ev.data);
                }
                continue;
            }

            // Handle based on filter type
            if (ev.filter == (short)kqueue_filter.READ && _hasRecv[connId])
            {
                ExecuteRecv(connId);
            }
            else if (ev.filter == (short)kqueue_filter.WRITE && _hasSend[connId])
            {
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

    private void ExecuteAccept(ref PendingOp op)
    {
        var addrLen = op.Addr2 != 0 ? (int*)op.Addr2 : null;
        var result = Darwin.accept((int)op.Fd, op.Addr, addrLen);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // No connection ready yet; re-queue and re-arm directly, recorded as submitted.
                _pendingAccepts[op.Fd] = op;
                _acceptsSubmitted.Add(op.Fd);
                AddKqueueChange(op.Fd, (short)kqueue_filter.READ, kqueue_flags.ADD | kqueue_flags.ONESHOT, op.Fd);
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
        var result = Darwin.recv((int)op.Fd, op.Addr, (nuint)op.Len, op.Flags);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // No data ready yet — keep pending, next Submit() will re-arm
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
        var result = Darwin.send((int)op.Fd, op.Addr, (nuint)op.Len, op.Flags);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // Buffer full — keep pending, next Submit() will re-arm
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
        Darwin.getsockopt((int)op.Fd, SOL_SOCKET, SO_ERROR, (nint)(&error), ref len);
        _hasSend[connId] = false;
        AddCompletion(op.UserData, error == 0 ? 0 : -error);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddKqueueChange(nint fd, short filter, kqueue_flags flags, nint udata)
    {
        if (_changeCount >= _changeList.Length)
        {
            // Flush changes to make room
            Darwin.kevent(_kqueueFd, _changeList, _changeCount, null, 0, nint.Zero);
            _changeCount = 0;
        }

        _changeList[_changeCount++] = new kevent
        {
            ident = fd,
            filter = filter,
            flags = (ushort)flags,
            udata = udata
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddCompletion(ulong userData, int result)
    {
        var cqIndex = _cqTail & _cqMask;
        _cqEntries[cqIndex] = new Completion(userData, result);
        _cqTail++;
    }

    /// <summary>
    /// Linear scan to find a connId by FD. Only used for PrepareConnect which is rare.
    /// </summary>
    private int FindConnIdByFd(int fd)
    {
        for (var i = 0; i < _maxConnections; i++)
        {
            if (_connIdToFd[i] == fd)
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc/>
    public void WaitForCompletion(int timeoutMs)
    {
        PollAndExecute(timeoutMs);
    }

    /// <inheritdoc/>
    /// <remarks>kevent takes a timespec with nanosecond granularity.</remarks>
    public bool SupportsHighResolutionWait => true;

    /// <inheritdoc/>
    public void Wake()
    {
        // Guarded so Dispose can drain in-flight calls before closing the kqueue fd.
        Interlocked.Increment(ref _wakeGuard);
        try
        {
            if (_disposed || !_wakeRegistered)
            {
                return;
            }

            // Straight to kevent: the batched change list is not thread-safe, and this is called
            // from other threads.
            if (Darwin.kevent(_kqueueFd, _wakeTrigger, 1, null, 0, nint.Zero) < 0 && !_loggedWakeFailure)
            {
                // Logged once; a silently lost wake leaves nothing to point at.
                _loggedWakeFailure = true;
                Console.Error.WriteLine(
                    $"IORingGroup: wake kevent failed (errno {Marshal.GetLastPInvokeError()}); " +
                    "callers will only wake on I/O or timeout."
                );
            }
        }
        finally
        {
            Interlocked.Decrement(ref _wakeGuard);
        }
    }

    // =============================================================================
    // Listener and Socket Management
    // =============================================================================

    /// <inheritdoc/>
    public nint CreateListener(string bindAddress, ushort port, int backlog)
    {
        // Create TCP socket
        var fd = Darwin.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (fd < 0)
        {
            return -1;
        }

        // Set non-blocking FIRST
        var flags = Darwin.fcntl(fd, F_GETFL, 0);
        if (flags < 0 || Darwin.fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0)
        {
            Darwin.close(fd);
            return -1;
        }

        // SO_REUSEADDR for quick restart
        var optval = 1;
        Darwin.setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, (nint)(&optval), sizeof(int));

        // TCP_NODELAY (disable Nagle)
        Darwin.setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new linger { l_onoff = 0, l_linger = 0 };
        Darwin.setsockopt(fd, SOL_SOCKET, SO_LINGER, (nint)(&linger), sizeof(linger));

        // Parse and bind address
        var addr = new sockaddr_in
        {
            sin_len = (byte)sizeof(sockaddr_in),
            sin_family = AF_INET,
            sin_port = BinaryPrimitives.ReverseEndianness(port),
            sin_addr = ParseIPv4(bindAddress)
        };

        if (Darwin.bind(fd, (nint)(&addr), sizeof(sockaddr_in)) < 0)
        {
            Darwin.close(fd);
            return -1;
        }

        if (Darwin.listen(fd, backlog) < 0)
        {
            Darwin.close(fd);
            return -1;
        }

        return fd;
    }

    /// <inheritdoc/>
    public void CloseListener(nint listener)
    {
        if (listener >= 0)
        {
            // The armed entry must go too: the filter dies with the fd, and a stale entry would
            // make Submit skip arming a new listener that recycles this fd number.
            _pendingAccepts.Remove(listener);
            _acceptsSubmitted.Remove(listener);
            Darwin.close((int)listener);
        }
    }

    /// <inheritdoc/>
    public void ConfigureSocket(nint socket)
    {
        var fd = (int)socket;

        // Set non-blocking
        var flags = Darwin.fcntl(fd, F_GETFL, 0);
        if (flags >= 0)
        {
            Darwin.fcntl(fd, F_SETFL, flags | O_NONBLOCK);
        }

        // TCP_NODELAY (disable Nagle)
        var optval = 1;
        Darwin.setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new linger { l_onoff = 0, l_linger = 0 };
        Darwin.setsockopt(fd, SOL_SOCKET, SO_LINGER, (nint)(&linger), sizeof(linger));
    }

    /// <inheritdoc/>
    public int RegisterSocket(nint socket)
    {
        if (_freeSlotCount <= 0)
        {
            return -1;
        }

        // Pop from free stack
        var connId = _freeSlots[--_freeSlotCount];
        _connIdToFd[connId] = (int)socket;

        // A recycled slot may carry armed bits from a socket whose filters died with its fd.
        _armedIo[connId] = 0;
        MarkDirty(connId);

        return connId;
    }

    /// <inheritdoc/>
    public void UnregisterSocket(int connId)
    {
        if (connId < 0 || connId >= _maxConnections)
        {
            return;
        }

        // Clear pending ops
        _hasRecv[connId] = false;
        _hasSend[connId] = false;
        _armedIo[connId] = 0;

        // Return slot to free stack
        _connIdToFd[connId] = -1;
        _freeSlots[_freeSlotCount++] = connId;
        MarkDirty(connId);
    }

    /// <inheritdoc/>
    public void CloseSocket(nint socket)
    {
        if (socket >= 0)
        {
            Darwin.close((int)socket);
        }
    }

    /// <inheritdoc/>
    public void Shutdown(nint socket, int how)
    {
        if (socket >= 0)
        {
            Darwin.shutdown((int)socket, how);
        }
    }

    // =============================================================================
    // Registered Buffer Operations
    // =============================================================================

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterBuffer(IORingBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (_externalBufferCount >= _maxExternalBuffers)
        {
            throw new InvalidOperationException($"Maximum of {_maxExternalBuffers} external buffers reached");
        }

        // Find first free slot
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
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
        {
            return;
        }

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
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferId));
        }

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
        {
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");
        }

        PrepareSend(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferId));
        }

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
        {
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");
        }

        PrepareRecv(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <summary>
    /// Gets the number of registered external buffers.
    /// </summary>
    public int ExternalBufferCount => _externalBufferCount;

    private static uint ParseIPv4(string address)
    {
        if (address == "0.0.0.0")
        {
            return 0; // INADDR_ANY
        }

        var parts = address.Split('.');
        if (parts.Length != 4)
        {
            return 0;
        }

        return (uint)(
            byte.Parse(parts[0]) |
            (byte.Parse(parts[1]) << 8) |
            (byte.Parse(parts[2]) << 16) |
            (byte.Parse(parts[3]) << 24)
        );
    }

    // Socket constants for macOS/BSD
    private const int AF_INET = 2;
    private const int SOCK_STREAM = 1;
    private const int IPPROTO_TCP = 6;
    private const int SOL_SOCKET = 0xFFFF;
    private const int SO_REUSEADDR = 0x0004;
    private const int SO_LINGER = 0x0080;
    private const int SO_ERROR = 0x1007;
    private const int TCP_NODELAY = 0x01;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 0x0004;
    private const int EAGAIN = 35;
    private const int EWOULDBLOCK = EAGAIN;
    private const int EINPROGRESS = 36;
    private const int EINTR = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct sockaddr_in
    {
        public byte sin_len;
        public byte sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct linger
    {
        public int l_onoff;
        public int l_linger;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Drain in-flight Wake() calls before closing the kqueue fd.
        var spinner = new SpinWait();
        while (Volatile.Read(ref _wakeGuard) != 0)
        {
            spinner.SpinOnce();
        }

        // Clear pending ops
        for (var i = 0; i < _maxConnections; i++)
        {
            _hasRecv[i] = false;
            _hasSend[i] = false;
        }

        _pendingAccepts.Clear();
        _acceptsSubmitted.Clear();

        if (_kqueueFd >= 0)
        {
            Darwin.close(_kqueueFd);
        }
    }

    // P/Invoke declarations
    private static partial class Darwin
    {
        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int kqueue();

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int kevent(int kq, kevent[]? changelist, int nchanges, kevent[]? eventlist, int nevents, nint timeout);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int accept(int sockfd, nint addr, int* addrlen);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int connect(int sockfd, nint addr, int addrlen);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial nint send(int sockfd, nint buf, nuint len, int flags);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial nint recv(int sockfd, nint buf, nuint len, int flags);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int close(int fd);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int shutdown(int sockfd, int how);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int socket(int domain, int type, int protocol);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int bind(int sockfd, nint addr, int addrlen);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int listen(int sockfd, int backlog);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int setsockopt(int sockfd, int level, int optname, nint optval, int optlen);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int getsockopt(int sockfd, int level, int optname, nint optval, ref int optlen);

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int fcntl(int fd, int cmd, int arg);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct kevent
    {
        public nint ident;
        public short filter;
        public ushort flags;
        public uint fflags;
        public nint data;
        public nint udata;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    private enum kqueue_filter : short
    {
        READ = -1,
        WRITE = -2,
        USER = -10,
    }

    [Flags]
    private enum kqueue_flags : ushort
    {
        ADD = 0x0001,
        DELETE = 0x0002,
        ENABLE = 0x0004,
        DISABLE = 0x0008,
        ONESHOT = 0x0010,
        CLEAR = 0x0020,
        ERROR = 0x4000,
        EOF = 0x8000,
    }
}
