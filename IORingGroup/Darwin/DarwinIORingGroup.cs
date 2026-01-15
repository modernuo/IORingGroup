// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.Darwin;

/// <summary>
/// macOS/BSD implementation of IIORingGroup using dispatch_io and kqueue.
/// </summary>
public sealed unsafe partial class DarwinIORingGroup : IIORingGroup
{
    private readonly int _kqueueFd;
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

    // kevent array for batch operations
    private readonly kevent[] _kevents;
    private readonly kevent[] _resultEvents;

    private bool _disposed;

    // External buffer tracking (for IORingBuffer/Pool support)
    // Size = maxConnections * 3 to support recv + send + pipe per connection
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
        _sqMask = queueSize - 1;
        _cqMask = queueSize * 2 - 1;

        _sqEntries = new PendingOp[queueSize];
        _cqEntries = new Completion[queueSize * 2];
        _kevents = new kevent[queueSize];
        _resultEvents = new kevent[queueSize];

        // Initialize external buffer tracking (maxConnections * 3 to support recv + send + pipe per connection)
        _maxExternalBuffers = maxConnections * 3;
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

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        ref var sqe = ref GetSqe();
        sqe.Opcode = (byte)IORingOp.Send;
        sqe.Fd = fd;
        sqe.Addr = buf;
        sqe.Len = len;
        sqe.Flags = (int)flags;
        sqe.UserData = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
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

            // Add completion
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
            // Use kevent to wait
            var timeout = new timespec { tv_sec = 0, tv_nsec = 1_000_000 }; // 1ms
            var result = Darwin.kevent(_kqueueFd, null, 0, _resultEvents, _resultEvents.Length, ref timeout);

            if (result > 0)
            {
                // Process kqueue events
                for (var i = 0; i < result; i++)
                {
                    ref var ev = ref _resultEvents[i];
                    var cqIndex = _cqTail & _cqMask;
                    _cqEntries[cqIndex] = new Completion((ulong)ev.udata, (int)ev.data, CompletionFlags.None);
                    _cqTail++;
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
        // Convert poll mask to kqueue filter
        short filter = 0;
        if ((sqe.Flags & (int)PollMask.In) != 0)
            filter = (short)kqueue_filter.READ;
        else if ((sqe.Flags & (int)PollMask.Out) != 0)
            filter = (short)kqueue_filter.WRITE;

        var ev = new kevent
        {
            ident = sqe.Fd,
            filter = filter,
            flags = (ushort)(kqueue_flags.ADD | kqueue_flags.CLEAR | kqueue_flags.ONESHOT),
            udata = (nint)sqe.UserData
        };

        var result = Darwin.kevent(_kqueueFd, new[] { ev }, 1, null, 0, nint.Zero);
        return result < 0 ? -Marshal.GetLastPInvokeError() : 0;
    }

    private int ExecuteAccept(ref PendingOp sqe)
    {
        unsafe
        {
            var addrLen = (int*)sqe.Addr2;
            var result = Darwin.accept((int)sqe.Fd, sqe.Addr, addrLen);
            return result;
        }
    }

    private int ExecuteConnect(ref PendingOp sqe)
    {
        var result = Darwin.connect((int)sqe.Fd, sqe.Addr, sqe.Len);
        return result == 0 ? 0 : -Marshal.GetLastPInvokeError();
    }

    private int ExecuteSend(ref PendingOp sqe)
    {
        var result = Darwin.send((int)sqe.Fd, sqe.Addr, (nuint)sqe.Len, sqe.Flags);
        return (int)result;
    }

    private int ExecuteRecv(ref PendingOp sqe)
    {
        var result = Darwin.recv((int)sqe.Fd, sqe.Addr, (nuint)sqe.Len, sqe.Flags);
        return (int)result;
    }

    private int ExecuteClose(ref PendingOp sqe)
    {
        var result = Darwin.close((int)sqe.Fd);
        return result == 0 ? 0 : -Marshal.GetLastPInvokeError();
    }

    private int ExecuteShutdown(ref PendingOp sqe)
    {
        var result = Darwin.shutdown((int)sqe.Fd, sqe.Flags);
        return result == 0 ? 0 : -Marshal.GetLastPInvokeError();
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
        // Wait for completions using kevent
        while (CompletionQueueCount < minComplete)
        {
            var timeout = new timespec
            {
                tv_sec = timeoutMs / 1000,
                tv_nsec = (timeoutMs % 1000) * 1_000_000
            };

            var result = Darwin.kevent(_kqueueFd, null, 0, _resultEvents, _resultEvents.Length, ref timeout);

            if (result > 0)
            {
                for (var i = 0; i < result; i++)
                {
                    ref var ev = ref _resultEvents[i];
                    var cqIndex = _cqTail & _cqMask;
                    _cqEntries[cqIndex] = new Completion((ulong)ev.udata, (int)ev.data, CompletionFlags.None);
                    _cqTail++;
                }
            }
            else if (result == 0)
            {
                break; // Timeout
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
        // Create TCP socket
        var fd = Darwin.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (fd < 0) return -1;

        // Set non-blocking
        var flags = Darwin.fcntl(fd, F_GETFL, 0);
        if (flags >= 0)
            Darwin.fcntl(fd, F_SETFL, flags | O_NONBLOCK);

        // Disable SO_REUSEADDR (exclusive address use)
        int optval = 0;
        Darwin.setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, (nint)(&optval), sizeof(int));

        // TCP_NODELAY (disable Nagle)
        optval = 1;
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
            Darwin.close((int)listener);
    }

    /// <inheritdoc/>
    public unsafe void ConfigureSocket(nint socket)
    {
        var fd = (int)socket;

        // Set non-blocking
        var flags = Darwin.fcntl(fd, F_GETFL, 0);
        if (flags >= 0)
            Darwin.fcntl(fd, F_SETFL, flags | O_NONBLOCK);

        // TCP_NODELAY (disable Nagle)
        int optval = 1;
        Darwin.setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new linger { l_onoff = 0, l_linger = 0 };
        Darwin.setsockopt(fd, SOL_SOCKET, SO_LINGER, (nint)(&linger), sizeof(linger));
    }

    /// <inheritdoc/>
    public void CloseSocket(nint socket)
    {
        if (socket >= 0)
            Darwin.close((int)socket);
    }

    // =============================================================================
    // Registered Buffer Operations
    // =============================================================================
    // macOS kqueue does not have kernel-level buffer registration like Windows RIO.
    // However, we track buffers locally and use the pointers directly with send/recv,
    // providing a consistent API across all platforms.

    /// <inheritdoc/>
    /// <remarks>
    /// Registers a buffer for use with PrepareSendBuffer/PrepareRecvBuffer.
    /// Unlike Windows RIO, macOS doesn't have kernel-level buffer registration -
    /// the buffer pointer is tracked locally and used directly with send/recv operations.
    /// </remarks>
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
            return; // Silently ignore invalid IDs for consistency with other platforms

        if (_externalBufferPtrs[bufferId] != 0)
        {
            _externalBufferPtrs[bufferId] = 0;
            _externalBufferLengths[bufferId] = 0;
            _externalBufferCount--;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Queues a send operation using the registered buffer.
    /// The connId parameter is the socket file descriptor on macOS.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSendBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
            throw new ArgumentOutOfRangeException(nameof(bufferId));

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");

        // Use regular send with calculated buffer address
        PrepareSend(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Queues a receive operation using the registered buffer.
    /// The connId parameter is the socket file descriptor on macOS.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
            throw new ArgumentOutOfRangeException(nameof(bufferId));

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");

        // Use regular recv with calculated buffer address
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

    // Socket constants for macOS/BSD
    private const int AF_INET = 2;
    private const int SOCK_STREAM = 1;
    private const int IPPROTO_TCP = 6;
    private const int SOL_SOCKET = 0xFFFF;
    private const int SO_REUSEADDR = 0x0004;
    private const int SO_LINGER = 0x0080;
    private const int TCP_NODELAY = 0x01;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 0x0004;

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
        if (_disposed) return;
        _disposed = true;

        if (_kqueueFd >= 0)
        {
            Darwin.close(_kqueueFd);
        }
    }

    // P/Invoke declarations
    private static unsafe partial class Darwin
    {
        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int kqueue();

        [LibraryImport("libSystem.dylib", SetLastError = true)]
        public static partial int kevent(int kq, kevent[]? changelist, int nchanges, kevent[]? eventlist, int nevents, ref timespec timeout);

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
        EOF = 0x8000,
    }
}
