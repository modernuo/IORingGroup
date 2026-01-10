// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.Windows;

/// <summary>
/// Windows implementation of IIORingGroup using ioring.dll (IoRing/RIO).
/// </summary>
public sealed class WindowsIORingGroup : IIORingGroup
{
    private readonly nint _ring;
    private readonly int _queueSize;
    private bool _disposed;

    // Pre-allocated CQE buffer for marshalling
    private readonly Win_x64.IORingCqe[] _cqeBuffer;
    private GCHandle _cqeBufferHandle;
    private readonly nint _cqeBufferPtr;

    public WindowsIORingGroup(int queueSize)
    {
        if (!IORingGroup.IsPowerOfTwo(queueSize))
        {
            throw new ArgumentException("Queue size must be a power of 2", nameof(queueSize));
        }

        _queueSize = queueSize;
        _ring = Win_x64.ioring_create((uint)queueSize);

        if (_ring == 0)
        {
            var error = Win_x64.ioring_get_last_error();
            throw new InvalidOperationException($"Failed to create IORing: error {error}");
        }

        // Pre-allocate CQE buffer
        _cqeBuffer = new Win_x64.IORingCqe[queueSize * 2];
        _cqeBufferHandle = GCHandle.Alloc(_cqeBuffer, GCHandleType.Pinned);
        _cqeBufferPtr = _cqeBufferHandle.AddrOfPinnedObject();
    }

    /// <summary>
    /// Gets the backend type being used (IoRing or RIO).
    /// </summary>
    public Win_x64.IORingBackend Backend => Win_x64.ioring_get_backend(_ring);

    /// <inheritdoc/>
    public int SubmissionQueueSpace => (int)Win_x64.ioring_sq_space_left(_ring);

    /// <inheritdoc/>
    public int CompletionQueueCount => (int)Win_x64.ioring_cq_ready(_ring);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private nint GetSqe()
    {
        var sqe = Win_x64.ioring_get_sqe(_ring);
        if (sqe == 0)
        {
            throw new InvalidOperationException("Submission queue is full");
        }
        return sqe;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollAdd(nint fd, PollMask mask, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_poll_add(sqe, fd, (uint)mask, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollRemove(ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_poll_remove(sqe, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_accept(sqe, listenFd, addr, addrLen, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_connect(sqe, fd, addr, addrLen, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_send(sqe, fd, buf, (uint)len, (int)flags, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_recv(sqe, fd, buf, (uint)len, (int)flags, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareClose(nint fd, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_close(sqe, fd, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareCancel(ulong targetUserData, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_cancel(sqe, targetUserData, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareShutdown(nint fd, int how, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_shutdown(sqe, fd, how, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Submit()
    {
        return Win_x64.ioring_submit(_ring);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SubmitAndWait(int waitNr)
    {
        return Win_x64.ioring_submit_and_wait(_ring, (uint)waitNr);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekCompletions(Span<Completion> completions)
    {
        var maxCount = Math.Min(completions.Length, _cqeBuffer.Length);
        var count = Win_x64.ioring_peek_cqe(_ring, _cqeBufferPtr, (uint)maxCount);

        for (var i = 0; i < count; i++)
        {
            ref var cqe = ref _cqeBuffer[i];
            completions[i] = new Completion(cqe.UserData, cqe.Res, (CompletionFlags)cqe.Flags);
        }

        return (int)count;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WaitCompletions(Span<Completion> completions, int minComplete, int timeoutMs)
    {
        var maxCount = Math.Min(completions.Length, _cqeBuffer.Length);
        var count = Win_x64.ioring_wait_cqe(_ring, _cqeBufferPtr, (uint)maxCount, (uint)minComplete, timeoutMs);

        for (var i = 0; i < count; i++)
        {
            ref var cqe = ref _cqeBuffer[i];
            completions[i] = new Completion(cqe.UserData, cqe.Res, (CompletionFlags)cqe.Flags);
        }

        return (int)count;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceCompletionQueue(int count)
    {
        Win_x64.ioring_cq_advance(_ring, (uint)count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cqeBufferHandle.IsAllocated)
        {
            _cqeBufferHandle.Free();
        }

        if (_ring != 0)
        {
            Win_x64.ioring_destroy(_ring);
        }
    }
}
