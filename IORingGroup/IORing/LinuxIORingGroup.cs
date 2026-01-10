using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.IORing;

/// <summary>
/// Linux io_uring implementation of IIORingGroup.
/// </summary>
public sealed unsafe class LinuxIORingGroup : IIORingGroup
{
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

    public LinuxIORingGroup(ILinuxArch arch, int queueSize)
    {
        if (!IORingGroup.IsPowerOfTwo(queueSize))
        {
            throw new ArgumentException("Queue size must be a power of 2", nameof(queueSize));
        }

        _arch = arch;

        var p = new io_uring_params();
        _ringFd = arch.io_uring_setup((uint)queueSize, ref p);

        if (_ringFd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"io_uring_setup failed: errno {errno}");
        }

        _sqEntries = p.sq_entries;
        _sqMask = p.sq_off.ring_mask;
        _cqMask = p.cq_off.ring_mask;

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

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
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

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
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

        var ret = _arch.io_uring_enter(_ringFd, toSubmit, 0, 0);
        return ret;
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
    public int WaitCompletions(Span<Completion> completions, int minComplete, int timeoutMs)
    {
        // First check if we already have enough completions
        var available = CompletionQueueCount;
        if (available < minComplete)
        {
            // Need to wait for more completions
            _arch.io_uring_enter(_ringFd, 0, (uint)minComplete, (uint)IORING_ENTER.GETEVENTS);
        }

        return PeekCompletions(completions);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceCompletionQueue(int count)
    {
        Volatile.Write(ref *_cqHead, *_cqHead + (uint)count);
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

