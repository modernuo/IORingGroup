// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.EPoll;

/// <summary>
/// Linux epoll implementation of IIORingGroup.
/// Used as fallback when io_uring is not available (Docker, WSL2, old kernels).
/// Follows the same pattern as DarwinIORingGroup with user-space ring buffers.
/// </summary>
public sealed unsafe class LinuxEpollGroup : IIORingGroup
{
    private readonly ILinuxEpollArch _arch;
    private readonly int _epollFd;
    private readonly int _queueSize;

    // Submission queue (user-space ring buffer)
    private readonly PendingOp[] _sqEntries;
    private int _sqHead;
    private int _sqTail;
    private readonly int _sqMask;

    // Completion queue (user-space ring buffer)
    private readonly Completion[] _cqEntries;
    private int _cqHead;
    private int _cqTail;
    private readonly int _cqMask;

    // epoll_event buffer for batch operations
    private readonly byte[] _epollEventsBuffer;
    private readonly int _epollEventSize;

    private bool _disposed;

    // External buffer tracking (for IORingBuffer/Pool support)
    private readonly int _maxExternalBuffers;
    private readonly nint[] _externalBufferPtrs;
    private readonly int[] _externalBufferLengths;
    private int _externalBufferCount;

    // Pending poll operations - maps fd to userData for poll events
    private readonly Dictionary<int, ulong> _pendingPolls = new();

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

    internal LinuxEpollGroup(ILinuxEpollArch arch, int queueSize, int maxConnections = IORingGroup.DefaultMaxConnections)
    {
        if (!IORingGroup.IsPowerOfTwo(queueSize))
        {
            throw new ArgumentException("Queue size must be a power of 2", nameof(queueSize));
        }

        _arch = arch;
        _queueSize = queueSize;
        _sqMask = queueSize - 1;
        _cqMask = queueSize * 2 - 1;
        _epollEventSize = arch.EpollEventSize;

        _sqEntries = new PendingOp[queueSize];
        _cqEntries = new Completion[queueSize * 2];
        _epollEventsBuffer = new byte[queueSize * _epollEventSize];

        // Initialize external buffer tracking (maxConnections * 3 to support recv + send + pipe per connection)
        _maxExternalBuffers = maxConnections * 3;
        _externalBufferPtrs = new nint[_maxExternalBuffers];
        _externalBufferLengths = new int[_maxExternalBuffers];

        // Create epoll instance with CLOEXEC
        _epollFd = arch.epoll_create1((int)epoll_flags.CLOEXEC);
        if (_epollFd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"epoll_create1 failed: errno {errno}");
        }
    }

    /// <inheritdoc/>
    public int SubmissionQueueSpace => _queueSize - ((_sqTail - _sqHead) & _sqMask);

    /// <inheritdoc/>
    public int CompletionQueueCount => (_cqTail - _cqHead) & _cqMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref PendingOp GetSqe()
    {
        if (SubmissionQueueSpace == 0)
        {
            throw new InvalidOperationException("Submission queue full");
        }

        var index = _sqTail & _sqMask;
        _sqTail++;
        return ref _sqEntries[index];
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollAdd(nint fd, PollMask mask, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.PollAdd;
        sqe.Fd = fd;
        sqe.Flags = (int)mask;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollRemove(ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.PollRemove;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Accept;
        sqe.Fd = listenFd;
        sqe.Addr = addr;
        sqe.Addr2 = addrLen;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Connect;
        sqe.Fd = fd;
        sqe.Addr = addr;
        sqe.Len = addrLen;
        sqe.UserData = userData;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Send;
        sqe.Fd = fd;
        sqe.Addr = buf;
        sqe.Len = len;
        sqe.Flags = (int)flags;
        sqe.UserData = userData;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Recv;
        sqe.Fd = fd;
        sqe.Addr = buf;
        sqe.Len = len;
        sqe.Flags = (int)flags;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareClose(nint fd, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Close;
        sqe.Fd = fd;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareCancel(ulong targetUserData, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Cancel;
        sqe.Addr = (nint)targetUserData;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareShutdown(nint fd, int how, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Shutdown;
        sqe.Fd = fd;
        sqe.Flags = how;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    public int Submit()
    {
        var submitted = 0;

        while (_sqHead != _sqTail)
        {
            var index = _sqHead & _sqMask;
            ref var sqe = ref _sqEntries[index];

            var result = ExecuteOp(ref sqe);

            // Add completion for each operation (synchronous execution)
            var cqIndex = _cqTail & _cqMask;
            _cqEntries[cqIndex] = new Completion(sqe.UserData, result, CompletionFlags.None);
            _cqTail++;

            _sqHead++;
            submitted++;
        }

        return submitted;
    }

    /// <inheritdoc/>
    public int SubmitAndWait(int waitNr)
    {
        var submitted = Submit();

        // Wait for completions if needed
        while (CompletionQueueCount < waitNr)
        {
            fixed (byte* eventsPtr = _epollEventsBuffer)
            {
                var result = _arch.epoll_wait(_epollFd, (nint)eventsPtr, _queueSize, 1);

                if (result > 0)
                {
                    ProcessEpollEvents(eventsPtr, result);
                }
            }
        }

        return submitted;
    }

    private int ExecuteOp(ref PendingOp sqe)
    {
        return sqe.Opcode switch
        {
            (byte)IORingOp.PollAdd => ExecutePollAdd(ref sqe),
            (byte)IORingOp.PollRemove => ExecutePollRemove(ref sqe),
            (byte)IORingOp.Accept => ExecuteAccept(ref sqe),
            (byte)IORingOp.Connect => ExecuteConnect(ref sqe),
            (byte)IORingOp.Send => ExecuteSend(ref sqe),
            (byte)IORingOp.Recv => ExecuteRecv(ref sqe),
            (byte)IORingOp.Close => ExecuteClose(ref sqe),
            (byte)IORingOp.Shutdown => ExecuteShutdown(ref sqe),
            _ => -38 // ENOSYS
        };
    }

    private int ExecutePollAdd(ref PendingOp sqe)
    {
        var fd = (int)sqe.Fd;

        // Convert PollMask to epoll events
        var events = epoll_events.EPOLLET | epoll_events.EPOLLONESHOT;
        if ((sqe.Flags & (int)PollMask.In) != 0)
            events |= epoll_events.EPOLLIN;
        if ((sqe.Flags & (int)PollMask.Out) != 0)
            events |= epoll_events.EPOLLOUT;
        if ((sqe.Flags & (int)PollMask.Err) != 0)
            events |= epoll_events.EPOLLERR;
        if ((sqe.Flags & (int)PollMask.Hup) != 0)
            events |= epoll_events.EPOLLHUP;
        if ((sqe.Flags & (int)PollMask.RdHup) != 0)
            events |= epoll_events.EPOLLRDHUP;

        // Store userData for lookup when event fires
        _pendingPolls[fd] = sqe.UserData;

        // Create epoll_event structure on stack
        // Store fd in data field so we can look up userData later
        Span<byte> evBuffer = stackalloc byte[16]; // Max size
        fixed (byte* evPtr = evBuffer)
        {
            // Write events (4 bytes)
            *(uint*)evPtr = (uint)events;
            // Write fd as data (so we can look up in _pendingPolls)
            if (_epollEventSize == 12)
                *(ulong*)(evPtr + 4) = (ulong)fd;
            else
                *(ulong*)(evPtr + 8) = (ulong)fd;

            var result = _arch.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_ADD, fd, (nint)evPtr);
            if (result < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                // Try MOD if ADD fails with EEXIST (17)
                if (errno == 17)
                {
                    result = _arch.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_MOD, fd, (nint)evPtr);
                    if (result < 0)
                    {
                        _pendingPolls.Remove(fd);
                        return -Marshal.GetLastPInvokeError();
                    }
                }
                else
                {
                    _pendingPolls.Remove(fd);
                    return -errno;
                }
            }
        }

        return 0;
    }

    private int ExecutePollRemove(ref PendingOp sqe)
    {
        // For poll remove, we need to delete from epoll
        // The userData contains info about what to remove, but epoll_ctl DEL ignores the event
        var result = _arch.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_DEL, (int)sqe.Addr, 0);
        return result == 0 ? 0 : -Marshal.GetLastPInvokeError();
    }

    private int ExecuteAccept(ref PendingOp sqe)
    {
        var result = _arch.accept((int)sqe.Fd, sqe.Addr, sqe.Addr2);
        if (result < 0)
            return -Marshal.GetLastPInvokeError();
        return result;
    }

    private int ExecuteConnect(ref PendingOp sqe)
    {
        var result = _arch.connect((int)sqe.Fd, sqe.Addr, sqe.Len);
        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            // EINPROGRESS (115) is expected for non-blocking connect
            if (errno == 115)
                return 0;
            return -errno;
        }
        return 0;
    }

    private int ExecuteSend(ref PendingOp sqe)
    {
        var result = _arch.send((int)sqe.Fd, sqe.Addr, (nuint)sqe.Len, sqe.Flags);
        if (result < 0)
            return -Marshal.GetLastPInvokeError();
        return (int)result;
    }

    private int ExecuteRecv(ref PendingOp sqe)
    {
        var result = _arch.recv((int)sqe.Fd, sqe.Addr, (nuint)sqe.Len, sqe.Flags);
        if (result < 0)
            return -Marshal.GetLastPInvokeError();
        return (int)result;
    }

    private int ExecuteClose(ref PendingOp sqe)
    {
        // Remove from epoll first (ignore errors)
        _arch.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_DEL, (int)sqe.Fd, 0);

        var result = _arch.close((int)sqe.Fd);
        return result == 0 ? 0 : -Marshal.GetLastPInvokeError();
    }

    private int ExecuteShutdown(ref PendingOp sqe)
    {
        var result = _arch.shutdown((int)sqe.Fd, sqe.Flags);
        return result == 0 ? 0 : -Marshal.GetLastPInvokeError();
    }

    private void ProcessEpollEvents(byte* eventsPtr, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var evPtr = eventsPtr + i * _epollEventSize;

            // Read events (4 bytes at offset 0)
            var events = (epoll_events)(*(uint*)evPtr);

            // Read fd from data field (we store fd, not userData directly)
            int fd;
            if (_epollEventSize == 12)
                fd = (int)(*(ulong*)(evPtr + 4));
            else
                fd = (int)(*(ulong*)(evPtr + 8));

            // Check if this is a pending poll
            if (_pendingPolls.TryGetValue(fd, out var userData))
            {
                _pendingPolls.Remove(fd);

                // Convert epoll events to result
                var result = 0;
                if ((events & epoll_events.EPOLLERR) != 0)
                    result = -1;
                else if ((events & epoll_events.EPOLLHUP) != 0)
                    result = 0; // EOF

                // Add completion
                var cqIndex = _cqTail & _cqMask;
                _cqEntries[cqIndex] = new Completion(userData, result, CompletionFlags.None);
                _cqTail++;
            }
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekCompletions(Span<Completion> completions)
    {
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
    public int WaitCompletions(Span<Completion> completions, int minComplete, int timeoutMs)
    {
        // Wait for completions using epoll_wait
        while (CompletionQueueCount < minComplete)
        {
            fixed (byte* eventsPtr = _epollEventsBuffer)
            {
                var result = _arch.epoll_wait(_epollFd, (nint)eventsPtr, _queueSize, timeoutMs);

                if (result > 0)
                {
                    ProcessEpollEvents(eventsPtr, result);
                }
                else if (result == 0)
                {
                    break; // Timeout
                }
            }
        }

        return PeekCompletions(completions);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceCompletionQueue(int count)
    {
        _cqHead += count;
    }

    // =============================================================================
    // Listener and Socket Management
    // =============================================================================

    /// <inheritdoc/>
    public unsafe nint CreateListener(string bindAddress, ushort port, int backlog)
    {
        // Create non-blocking TCP socket
        var fd = _arch.socket(_arch.AF_INET, _arch.SOCK_STREAM | _arch.SOCK_NONBLOCK, _arch.IPPROTO_TCP);
        if (fd < 0) return -1;

        // Disable SO_REUSEADDR (exclusive address use)
        var optval = 0;
        _arch.setsockopt(fd, _arch.SOL_SOCKET, _arch.SO_REUSEADDR, (nint)(&optval), sizeof(int));

        // TCP_NODELAY (disable Nagle)
        optval = 1;
        _arch.setsockopt(fd, _arch.IPPROTO_TCP, _arch.TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new linger { l_onoff = 0, l_linger = 0 };
        _arch.setsockopt(fd, _arch.SOL_SOCKET, _arch.SO_LINGER, (nint)(&linger), sizeof(linger));

        // Parse and bind address
        var addr = new sockaddr_in
        {
            sin_family = (ushort)_arch.AF_INET,
            sin_port = BinaryPrimitives.ReverseEndianness(port),
            sin_addr = ParseIPv4(bindAddress)
        };

        if (_arch.bind(fd, (nint)(&addr), sizeof(sockaddr_in)) < 0)
        {
            _arch.close(fd);
            return -1;
        }

        if (_arch.listen(fd, backlog) < 0)
        {
            _arch.close(fd);
            return -1;
        }

        return fd;
    }

    /// <inheritdoc/>
    public void CloseListener(nint listener)
    {
        if (listener >= 0)
        {
            _arch.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_DEL, (int)listener, 0);
            _arch.close((int)listener);
        }
    }

    /// <inheritdoc/>
    public unsafe void ConfigureSocket(nint socket)
    {
        var fd = (int)socket;

        // Set non-blocking
        var flags = _arch.fcntl(fd, _arch.F_GETFL, 0);
        if (flags >= 0)
            _arch.fcntl(fd, _arch.F_SETFL, flags | _arch.O_NONBLOCK);

        // TCP_NODELAY (disable Nagle)
        var optval = 1;
        _arch.setsockopt(fd, _arch.IPPROTO_TCP, _arch.TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new linger { l_onoff = 0, l_linger = 0 };
        _arch.setsockopt(fd, _arch.SOL_SOCKET, _arch.SO_LINGER, (nint)(&linger), sizeof(linger));
    }

    /// <inheritdoc/>
    public int RegisterSocket(nint socket)
    {
        // On Linux epoll, the socket fd is used directly as the connection ID
        return (int)socket;
    }

    /// <inheritdoc/>
    public void UnregisterSocket(int connId)
    {
        // No-op on Linux epoll - socket fd is used directly
    }

    /// <inheritdoc/>
    public void CloseSocket(nint socket)
    {
        if (socket >= 0)
        {
            _arch.epoll_ctl(_epollFd, (int)epoll_op.EPOLL_CTL_DEL, (int)socket, 0);
            _arch.close((int)socket);
        }
    }

    // =============================================================================
    // Registered Buffer Operations
    // =============================================================================

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterBuffer(IORingBuffer buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (_externalBufferCount >= _maxExternalBuffers)
            throw new InvalidOperationException($"Maximum of {_maxExternalBuffers} external buffers reached");

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
            return;

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
            throw new ArgumentOutOfRangeException(nameof(bufferId));

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");

        PrepareSend(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
            throw new ArgumentOutOfRangeException(nameof(bufferId));

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");

        PrepareRecv(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <summary>
    /// Gets the number of registered external buffers.
    /// </summary>
    public int ExternalBufferCount => _externalBufferCount;

    private static uint ParseIPv4(string address)
    {
        if (address == "0.0.0.0") return 0; // INADDR_ANY

        var parts = address.Split('.');
        if (parts.Length != 4) return 0;

        return (uint)(
            byte.Parse(parts[0]) |
            (byte.Parse(parts[1]) << 8) |
            (byte.Parse(parts[2]) << 16) |
            (byte.Parse(parts[3]) << 24)
        );
    }

    // Linux socket structures
    [StructLayout(LayoutKind.Sequential)]
    private struct sockaddr_in
    {
        public ushort sin_family;
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
        if (_disposed) return;
        _disposed = true;

        if (_epollFd >= 0)
            _arch.close(_epollFd);
    }
}
