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

    // Pending operations keyed by fd - we can have at most one recv and one send per fd
    // Using Dictionary for simplicity, could optimize with arrays for production
    private readonly Dictionary<nint, PendingOp> _pendingRecvs = new();
    private readonly Dictionary<nint, PendingOp> _pendingSends = new();
    private readonly Dictionary<nint, PendingOp> _pendingAccepts = new();

    // Completion queue (user-space ring buffer)
    private readonly Completion[] _cqEntries;
    private int _cqHead;
    private int _cqTail;
    private readonly int _cqMask;

    // kevent arrays for batch operations
    private readonly kevent[] _changeList;
    private int _changeCount;
    private readonly kevent[] _resultEvents;

    private bool _disposed;

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
        _cqMask = queueSize * 2 - 1;

        _cqEntries = new Completion[queueSize * 2];
        _changeList = new kevent[queueSize];
        _resultEvents = new kevent[queueSize];

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
    }

    /// <inheritdoc/>
    public int SubmissionQueueSpace => _queueSize - _changeCount -
        _pendingRecvs.Count - _pendingSends.Count - _pendingAccepts.Count;

    /// <inheritdoc/>
    public int CompletionQueueCount => (_cqTail - _cqHead) & _cqMask;

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

        AddKqueueChange(fd, filter, kqueue_flags.ADD | kqueue_flags.ONESHOT, userData);
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
                // Connection in progress - register for write readiness
                _pendingSends[fd] = new PendingOp
                {
                    Opcode = (byte)IORingOp.Connect,
                    Fd = fd,
                    UserData = userData
                };
            }
            else
            {
                AddCompletion(userData, -errno);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        _pendingSends[fd] = new PendingOp
        {
            Opcode = (byte)IORingOp.Send,
            Fd = fd,
            Addr = buf,
            Len = len,
            Flags = (int)flags,
            UserData = userData
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        _pendingRecvs[fd] = new PendingOp
        {
            Opcode = (byte)IORingOp.Recv,
            Fd = fd,
            Addr = buf,
            Len = len,
            Flags = (int)flags,
            UserData = userData
        };
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
        // Remove pending operations with matching userData
        // This is O(n) but cancellation should be rare
        RemovePendingByUserData(_pendingRecvs, targetUserData);
        RemovePendingByUserData(_pendingSends, targetUserData);
        RemovePendingByUserData(_pendingAccepts, targetUserData);
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

    /// <inheritdoc/>
    public int Submit()
    {
        var submitted = 0;

        // Register kqueue interest for all pending operations
        foreach (var kvp in _pendingAccepts)
        {
            AddKqueueChange(kvp.Key, (short)kqueue_filter.READ, kqueue_flags.ADD | kqueue_flags.ONESHOT, kvp.Value.UserData);
            submitted++;
        }

        foreach (var kvp in _pendingRecvs)
        {
            AddKqueueChange(kvp.Key, (short)kqueue_filter.READ, kqueue_flags.ADD | kqueue_flags.ONESHOT, kvp.Value.UserData);
            submitted++;
        }

        foreach (var kvp in _pendingSends)
        {
            AddKqueueChange(kvp.Key, (short)kqueue_filter.WRITE, kqueue_flags.ADD | kqueue_flags.ONESHOT, kvp.Value.UserData);
            submitted++;
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
            PollAndExecute(blocking: true);
        }

        return submitted;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekCompletions(Span<Completion> completions)
    {
        // Poll kqueue for ready events and execute I/O
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
    /// Polls kqueue for ready events and executes the corresponding I/O operations.
    /// </summary>
    private void PollAndExecute(bool blocking)
    {
        // Use a zero timeout for non-blocking poll
        timespec timeout = default;
        var timeoutPtr = blocking ? nint.Zero : (nint)(&timeout);

        var eventCount = Darwin.kevent(_kqueueFd, null, 0, _resultEvents, _resultEvents.Length, timeoutPtr);

        if (eventCount <= 0)
        {
            return;
        }

        for (var i = 0; i < eventCount; i++)
        {
            ref var ev = ref _resultEvents[i];
            var fd = ev.ident;

            // Check for errors
            if ((ev.flags & (ushort)kqueue_flags.ERROR) != 0)
            {
                // Error on this fd - find and complete the pending operation
                if (_pendingRecvs.Remove(fd, out var recvOp))
                {
                    AddCompletion(recvOp.UserData, -(int)ev.data);
                }
                if (_pendingSends.Remove(fd, out var sendOp))
                {
                    AddCompletion(sendOp.UserData, -(int)ev.data);
                }
                if (_pendingAccepts.Remove(fd, out var acceptOp))
                {
                    AddCompletion(acceptOp.UserData, -(int)ev.data);
                }
                continue;
            }

            // Handle based on filter type
            if (ev.filter == (short)kqueue_filter.READ)
            {
                // Check for accept first
                if (_pendingAccepts.Remove(fd, out var acceptOp))
                {
                    ExecuteAccept(ref acceptOp);
                }
                else if (_pendingRecvs.Remove(fd, out var recvOp))
                {
                    ExecuteRecv(ref recvOp);
                }
            }
            else if (ev.filter == (short)kqueue_filter.WRITE)
            {
                if (_pendingSends.Remove(fd, out var sendOp))
                {
                    if (sendOp.Opcode == (byte)IORingOp.Connect)
                    {
                        // Connect completed - check for errors
                        int error = 0;
                        var len = sizeof(int);
                        Darwin.getsockopt((int)fd, SOL_SOCKET, SO_ERROR, (nint)(&error), ref len);
                        AddCompletion(sendOp.UserData, error == 0 ? 0 : -error);
                    }
                    else
                    {
                        ExecuteSend(ref sendOp);
                    }
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
                // No connection ready yet - re-queue the accept
                _pendingAccepts[op.Fd] = op;
                AddKqueueChange(op.Fd, (short)kqueue_filter.READ, kqueue_flags.ADD | kqueue_flags.ONESHOT, op.UserData);
                return;
            }
            AddCompletion(op.UserData, -errno);
        }
        else
        {
            AddCompletion(op.UserData, result);
        }
    }

    private void ExecuteRecv(ref PendingOp op)
    {
        var result = Darwin.recv((int)op.Fd, op.Addr, (nuint)op.Len, op.Flags);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // No data ready yet - re-queue the recv
                _pendingRecvs[op.Fd] = op;
                AddKqueueChange(op.Fd, (short)kqueue_filter.READ, kqueue_flags.ADD | kqueue_flags.ONESHOT, op.UserData);
                return;
            }
            AddCompletion(op.UserData, -errno);
        }
        else
        {
            AddCompletion(op.UserData, (int)result);
        }
    }

    private void ExecuteSend(ref PendingOp op)
    {
        var result = Darwin.send((int)op.Fd, op.Addr, (nuint)op.Len, op.Flags);

        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EAGAIN || errno == EWOULDBLOCK)
            {
                // Buffer full - re-queue the send
                _pendingSends[op.Fd] = op;
                AddKqueueChange(op.Fd, (short)kqueue_filter.WRITE, kqueue_flags.ADD | kqueue_flags.ONESHOT, op.UserData);
                return;
            }
            AddCompletion(op.UserData, -errno);
        }
        else
        {
            AddCompletion(op.UserData, (int)result);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddKqueueChange(nint fd, short filter, kqueue_flags flags, ulong userData)
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
            udata = (nint)userData
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddCompletion(ulong userData, int result)
    {
        var cqIndex = _cqTail & _cqMask;
        _cqEntries[cqIndex] = new Completion(userData, result);
        _cqTail++;
    }

    private static void RemovePendingByUserData(Dictionary<nint, PendingOp> dict, ulong targetUserData)
    {
        nint? toRemove = null;
        foreach (var kvp in dict)
        {
            if (kvp.Value.UserData == targetUserData)
            {
                toRemove = kvp.Key;
                break;
            }
        }

        if (toRemove.HasValue)
        {
            dict.Remove(toRemove.Value);
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
            // Remove any pending accepts for this listener
            _pendingAccepts.Remove(listener);
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
        // On Darwin, the socket fd is used directly as the connection ID
        return (int)socket;
    }

    /// <inheritdoc/>
    public void UnregisterSocket(int connId)
    {
        // Remove any pending operations for this socket
        var fd = (nint)connId;
        _pendingRecvs.Remove(fd);
        _pendingSends.Remove(fd);
    }

    /// <inheritdoc/>
    public void CloseSocket(nint socket)
    {
        if (socket >= 0)
        {
            _pendingRecvs.Remove(socket);
            _pendingSends.Remove(socket);
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

        _pendingRecvs.Clear();
        _pendingSends.Clear();
        _pendingAccepts.Clear();

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
