// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.IORing;

/// <summary>
/// Linux io_uring implementation of IIORingGroup.
/// </summary>
public sealed unsafe class LinuxIORingGroup : IIORingGroup
{
    /// <summary>
    /// Tests if io_uring is available and functional on this system.
    /// </summary>
    /// <param name="arch">Architecture-specific syscall implementation.</param>
    /// <returns>True if io_uring is available, false if blocked or unsupported.</returns>
    public static bool IsAvailable(ILinuxArch arch)
    {
        // Try to create a minimal io_uring (smallest valid queue size)
        var p = new io_uring_params();
        var fd = arch.io_uring_setup(8, ref p);

        if (fd < 0)
        {
            // Failed - common errno values:
            // ENOSYS (38) = syscall not implemented in kernel
            // EPERM (1) = blocked by seccomp/security policy
            return false;
        }

        // Success - clean up the test ring
        arch.close(fd);
        return true;
    }

    private readonly ILinuxArch _arch;
    private readonly int _ringFd;
    private readonly uint _sqEntries;
    private readonly uint _sqMask;
    private readonly uint _cqMask;

    // Ring memory mappings
    private readonly nint _sqRingPtr;
    private readonly nint _cqRingPtr;
    private readonly nint _sqesPtr;
    private readonly nuint _sqRingSize;
    private readonly nuint _cqRingSize;
    private readonly nuint _sqesSize;

    // Ring pointers (into mapped memory)
    private readonly uint* _sqHead;
    private readonly uint* _sqTail;
    private readonly uint* _sqArray;
    private readonly io_uring_sqe* _sqes;

    private readonly uint* _cqHead;
    private readonly uint* _cqTail;
    private readonly io_uring_cqe* _cqes;

    private bool _disposed;

    public LinuxIORingGroup(ILinuxArch arch, int queueSize, int maxConnections = IORingGroup.DefaultMaxConnections)
    {
        if (!IORingGroup.IsPowerOfTwo(queueSize))
        {
            throw new ArgumentException("Queue size must be a power of 2", nameof(queueSize));
        }

        _arch = arch;

        // Initialize external buffer tracking (maxConnections * 3 to support recv + send + pipe per connection)
        _maxExternalBuffers = maxConnections * 3;
        _externalBufferPtrs = new nint[_maxExternalBuffers];
        _externalBufferLengths = new int[_maxExternalBuffers];

        var p = new io_uring_params();
        _ringFd = arch.io_uring_setup((uint)queueSize, ref p);

        if (_ringFd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"io_uring_setup failed: errno {errno}");
        }

        _sqEntries = p.sq_entries;
        // Note: ring_mask values are OFFSETS - actual masks are read after mmap

        // Map submission queue ring
        _sqRingSize = p.sq_off.array + p.sq_entries * sizeof(uint);
        _sqRingPtr = arch.mmap(
            0,
            _sqRingSize,
            arch.PROT_READ | arch.PROT_WRITE,
            arch.MAP_SHARED | arch.MAP_POPULATE,
            _ringFd,
            (long)arch.IORING_OFF_SQ_RING
        );

        if (_sqRingPtr == -1)
        {
            arch.close(_ringFd);
            throw new InvalidOperationException("Failed to mmap SQ ring");
        }

        // Map completion queue ring
        _cqRingSize = p.cq_off.cqes + p.cq_entries * (uint)sizeof(io_uring_cqe);
        _cqRingPtr = arch.mmap(
            0,
            _cqRingSize,
            arch.PROT_READ | arch.PROT_WRITE,
            arch.MAP_SHARED | arch.MAP_POPULATE,
            _ringFd,
            (long)arch.IORING_OFF_CQ_RING
        );

        if (_cqRingPtr == -1)
        {
            arch.munmap(_sqRingPtr, _sqRingSize);
            arch.close(_ringFd);
            throw new InvalidOperationException("Failed to mmap CQ ring");
        }

        // Map SQEs array
        _sqesSize = (nuint)(p.sq_entries * sizeof(io_uring_sqe));
        _sqesPtr = arch.mmap(
            0,
            _sqesSize,
            arch.PROT_READ | arch.PROT_WRITE,
            arch.MAP_SHARED | arch.MAP_POPULATE,
            _ringFd,
            (long)arch.IORING_OFF_SQES
        );

        if (_sqesPtr == -1)
        {
            arch.munmap(_cqRingPtr, _cqRingSize);
            arch.munmap(_sqRingPtr, _sqRingSize);
            arch.close(_ringFd);
            throw new InvalidOperationException("Failed to mmap SQEs");
        }

        // Set up pointers into mapped memory
        _sqHead = (uint*)(_sqRingPtr + (nint)p.sq_off.head);
        _sqTail = (uint*)(_sqRingPtr + (nint)p.sq_off.tail);
        _sqArray = (uint*)(_sqRingPtr + (nint)p.sq_off.array);
        _sqes = (io_uring_sqe*)_sqesPtr;

        _cqHead = (uint*)(_cqRingPtr + (nint)p.cq_off.head);
        _cqTail = (uint*)(_cqRingPtr + (nint)p.cq_off.tail);
        _cqes = (io_uring_cqe*)(_cqRingPtr + (nint)p.cq_off.cqes);

        // Read actual ring masks from mapped memory (ring_mask field is an OFFSET, not the value!)
        _sqMask = *(uint*)(_sqRingPtr + (nint)p.sq_off.ring_mask);
        _cqMask = *(uint*)(_cqRingPtr + (nint)p.cq_off.ring_mask);

        // Initialize local tail from shared memory
        _sqTailLocal = *_sqTail;
    }

    /// <inheritdoc/>
    public int SubmissionQueueSpace
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var head = Volatile.Read(ref *_sqHead);
            return (int)(_sqEntries - (_sqTailLocal - head));
        }
    }

    /// <inheritdoc/>
    public int CompletionQueueCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var head = *_cqHead;
            var tail = Volatile.Read(ref *_cqTail);
            return (int)(tail - head);
        }
    }

    // Track local tail for batching - only written to shared memory on Submit
    private uint _sqTailLocal;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private io_uring_sqe* GetSqe()
    {
        var head = Volatile.Read(ref *_sqHead);
        var tail = _sqTailLocal;

        if (tail - head >= _sqEntries)
        {
            return null;
        }

        var index = tail & _sqMask;
        var sqe = &_sqes[index];

        // Clear the SQE
        Unsafe.InitBlock(sqe, 0, (uint)sizeof(io_uring_sqe));

        // Update the SQ array and local tail (NOT shared memory yet)
        _sqArray[index] = index;
        _sqTailLocal = tail + 1;

        return sqe;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollAdd(nint fd, PollMask mask, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.POLL_ADD;
        sqe->fd = (int)fd;
        sqe->op_flags = (uint)mask;
        sqe->user_data = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollRemove(ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.POLL_REMOVE;
        sqe->addr = userData;
        sqe->user_data = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.ACCEPT;
        sqe->fd = (int)listenFd;
        sqe->addr = (ulong)addr;
        sqe->off = (ulong)addrLen;
        sqe->user_data = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.CONNECT;
        sqe->fd = (int)fd;
        sqe->addr = (ulong)addr;
        sqe->off = (ulong)addrLen;
        sqe->user_data = userData;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.SEND;
        sqe->fd = (int)fd;
        sqe->addr = (ulong)buf;
        sqe->len = (uint)len;
        sqe->op_flags = (uint)flags;
        sqe->user_data = userData;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.RECV;
        sqe->fd = (int)fd;
        sqe->addr = (ulong)buf;
        sqe->len = (uint)len;
        sqe->op_flags = (uint)flags;
        sqe->user_data = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareClose(nint fd, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.CLOSE;
        sqe->fd = (int)fd;
        sqe->user_data = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareCancel(ulong targetUserData, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.ASYNC_CANCEL;
        sqe->addr = targetUserData;
        sqe->user_data = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareShutdown(nint fd, int how, ulong userData)
    {
        var sqe = GetSqe();
        if (sqe == null) throw new InvalidOperationException("Submission queue full");

        sqe->opcode = (byte)IORING_OP.SHUTDOWN;
        sqe->fd = (int)fd;
        sqe->len = (uint)how;
        sqe->user_data = userData;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Submit()
    {
        var head = Volatile.Read(ref *_sqHead);
        var tail = _sqTailLocal;
        var toSubmit = tail - head;

        if (toSubmit == 0) return 0;

        // Release store: ensures all SQE writes are visible before kernel sees new tail
        Volatile.Write(ref *_sqTail, tail);

        return _arch.io_uring_enter(_ringFd, toSubmit, 0, 0);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SubmitAndWait(int waitNr)
    {
        var head = Volatile.Read(ref *_sqHead);
        var tail = _sqTailLocal;
        var toSubmit = tail - head;

        // Release store: ensures all SQE writes are visible before kernel sees new tail
        Volatile.Write(ref *_sqTail, tail);

        var ret = _arch.io_uring_enter(_ringFd, toSubmit, (uint)waitNr, (uint)IORING_ENTER.GETEVENTS);
        return ret;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekCompletions(Span<Completion> completions)
    {
        var head = *_cqHead;
        var tail = Volatile.Read(ref *_cqTail);
        var available = tail - head;

        var count = Math.Min((int)available, completions.Length);

        for (var i = 0; i < count; i++)
        {
            var index = (head + (uint)i) & _cqMask;
            ref var cqe = ref _cqes[index];
            completions[i] = new Completion(cqe.user_data, cqe.res, (CompletionFlags)cqe.flags);
        }

        return count;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceCompletionQueue(int count)
    {
        Volatile.Write(ref *_cqHead, *_cqHead + (uint)count);
    }

    // =============================================================================
    // Listener and Socket Management
    // =============================================================================

    /// <inheritdoc/>
    public nint CreateListener(string bindAddress, ushort port, int backlog)
    {
        // Create non-blocking TCP socket
        var fd = _arch.socket(
            _arch.AF_INET,
            _arch.SOCK_STREAM | _arch.SOCK_NONBLOCK,
            _arch.IPPROTO_TCP
        );

        if (fd < 0) return -1;

        // Disable SO_REUSEADDR (exclusive address use)
        var optval = 0;
        _arch.setsockopt(fd, _arch.SOL_SOCKET, _arch.SO_REUSEADDR, (nint)(&optval), sizeof(int));

        // Disable TCP_NODELAY (Nagle off)
        optval = 1;
        _arch.setsockopt(fd, _arch.IPPROTO_TCP, _arch.TCP_NODELAY, (nint)(&optval), sizeof(int));

        // Disable SO_LINGER
        var linger = new LingerOption { OnOff = 0, Seconds = 0 };
        _arch.setsockopt(fd, _arch.SOL_SOCKET, _arch.SO_LINGER, (nint)(&linger), sizeof(LingerOption));

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
            _arch.close((int)listener);
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
        var linger = new LingerOption { OnOff = 0, Seconds = 0 };
        _arch.setsockopt(fd, _arch.SOL_SOCKET, _arch.SO_LINGER, (nint)(&linger), sizeof(LingerOption));
    }

    /// <inheritdoc/>
    public int RegisterSocket(nint socket)
    {
        // On Linux, the socket fd is used directly as the connection ID
        return (int)socket;
    }

    /// <inheritdoc/>
    public void UnregisterSocket(int connId)
    {
        // No-op on Linux - socket fd is used directly
    }

    /// <inheritdoc/>
    public void CloseSocket(nint socket)
    {
        if (socket >= 0)
            _arch.close((int)socket);
    }

    // =============================================================================
    // Registered Buffer Operations (Zero-Copy I/O)
    // =============================================================================

    // External buffer tracking (similar to Windows RIO)
    // Size = maxConnections * 3 to support recv + send + pipe per connection
    private readonly int _maxExternalBuffers;
    private readonly nint[] _externalBufferPtrs;
    private readonly int[] _externalBufferLengths;
    private int _externalBufferCount;

    /// <inheritdoc/>
    /// <remarks>
    /// On Linux, buffer registration tracks the buffer locally for use with
    /// PrepareSendBuffer/PrepareRecvBuffer. Unlike Windows RIO, Linux io_uring
    /// doesn't require kernel-level buffer registration for socket I/O - the
    /// buffer pointer is used directly with SEND/RECV operations.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterBuffer(IORingBuffer buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));

        if (_externalBufferCount >= _maxExternalBuffers)
            throw new InvalidOperationException("Maximum external buffer count reached");

        // Find free slot
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
            throw new ArgumentOutOfRangeException(nameof(bufferId));

        if (_externalBufferPtrs[bufferId] != 0)
        {
            _externalBufferPtrs[bufferId] = 0;
            _externalBufferLengths[bufferId] = 0;
            _externalBufferCount--;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// On Linux, this uses a regular SEND operation with the buffer pointer
    /// calculated from the registered buffer. The connId is used as the fd.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSendBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
            throw new ArgumentOutOfRangeException(nameof(bufferId));

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");

        if (offset + length > _externalBufferLengths[bufferId])
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset + length exceeds buffer size");

        // Use regular SEND with calculated buffer address
        PrepareSend(connId, bufPtr + offset, length, MsgFlags.None, userData);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// On Linux, this uses a regular RECV operation with the buffer pointer
    /// calculated from the registered buffer. The connId is used as the fd.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        if (bufferId < 0 || bufferId >= _maxExternalBuffers)
            throw new ArgumentOutOfRangeException(nameof(bufferId));

        var bufPtr = _externalBufferPtrs[bufferId];
        if (bufPtr == 0)
            throw new InvalidOperationException($"Buffer {bufferId} is not registered");

        if (offset + length > _externalBufferLengths[bufferId])
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset + length exceeds buffer size");

        // Use regular RECV with calculated buffer address
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sqesPtr != 0 && _sqesPtr != -1)
            _arch.munmap(_sqesPtr, _sqesSize);

        if (_cqRingPtr != 0 && _cqRingPtr != -1)
            _arch.munmap(_cqRingPtr, _cqRingSize);

        if (_sqRingPtr != 0 && _sqRingPtr != -1)
            _arch.munmap(_sqRingPtr, _sqRingSize);

        if (_ringFd >= 0)
            _arch.close(_ringFd);
    }
}

