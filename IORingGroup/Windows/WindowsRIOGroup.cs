// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.Windows;

/// <summary>
/// High-performance Windows RIO (Registered I/O) implementation.
/// Uses pre-registered buffers for zero-copy I/O and true async operations.
/// </summary>
/// <remarks>
/// <para>
/// Usage pattern:
/// 1. Create with max connections and buffer sizes
/// 2. Accept connections using PrepareAccept on listener (AcceptEx is handled internally)
/// 3. Register each accepted socket with RegisterSocket() to get buffer pointers
/// 4. Use PrepareRecvRegistered/PrepareSendRegistered with connection IDs
/// 5. Call UnregisterSocket() before closing sockets
/// </para>
/// <para>
/// <b>Important:</b> PrepareAccept automatically uses AcceptEx internally to create
/// RIO-compatible sockets. The accepted socket handle returned in the completion
/// can be directly registered with RegisterSocket().
/// </para>
/// </remarks>
public sealed class WindowsRIOGroup : IIORingGroup
{
    private readonly nint _ring;
    private readonly int _queueSize;
    private readonly int _maxConnections;
    private readonly int _recvBufferSize;
    private readonly int _sendBufferSize;
    private bool _disposed;

    // Pre-allocated CQE buffer for marshalling
    private readonly Win_x64.IORingCqe[] _cqeBuffer;
    private GCHandle _cqeBufferHandle;
    private readonly nint _cqeBufferPtr;

    /// <summary>
    /// Default outstanding operations per socket per direction.
    /// </summary>
    public const int DefaultOutstandingPerSocket = 2;

    /// <summary>
    /// Maximum outstanding operations per socket per direction.
    /// </summary>
    public const int MaxOutstandingPerSocket = 16;

    /// <summary>
    /// Creates a new RIO ring with pre-allocated buffer pool.
    /// </summary>
    /// <param name="queueSize">Submission/completion queue size (power of 2)</param>
    /// <param name="maxConnections">Maximum concurrent connections</param>
    /// <param name="recvBufferSize">Receive buffer size per connection</param>
    /// <param name="sendBufferSize">Send buffer size per connection</param>
    public WindowsRIOGroup(int queueSize, int maxConnections, int recvBufferSize, int sendBufferSize)
        : this(queueSize, maxConnections, recvBufferSize, sendBufferSize, DefaultOutstandingPerSocket)
    {
    }

    /// <summary>
    /// Creates a new RIO ring with pre-allocated buffer pool and configurable outstanding operations.
    /// </summary>
    /// <param name="queueSize">Submission/completion queue size (power of 2)</param>
    /// <param name="maxConnections">Maximum concurrent connections</param>
    /// <param name="recvBufferSize">Receive buffer size per connection</param>
    /// <param name="sendBufferSize">Send buffer size per connection</param>
    /// <param name="outstandingPerSocket">Max outstanding ops per direction per socket (1-16, default 2)</param>
    /// <remarks>
    /// The completion queue is auto-sized to: maxConnections * outstandingPerSocket * 2.
    /// For simple request-response patterns (like echo), use 1-2.
    /// For pipelined protocols, use 4-8.
    /// </remarks>
    public WindowsRIOGroup(int queueSize, int maxConnections, int recvBufferSize, int sendBufferSize, int outstandingPerSocket)
    {
        if (!IORingGroup.IsPowerOfTwo(queueSize))
        {
            throw new ArgumentException("Queue size must be a power of 2", nameof(queueSize));
        }

        if (outstandingPerSocket < 1 || outstandingPerSocket > MaxOutstandingPerSocket)
        {
            throw new ArgumentOutOfRangeException(nameof(outstandingPerSocket),
                $"Must be between 1 and {MaxOutstandingPerSocket}");
        }

        _queueSize = queueSize;
        _maxConnections = maxConnections;
        _recvBufferSize = recvBufferSize;
        _sendBufferSize = sendBufferSize;

        _ring = Win_x64.ioring_create_rio_ex((uint)queueSize, (uint)maxConnections,
            (uint)recvBufferSize, (uint)sendBufferSize, (uint)outstandingPerSocket);

        if (_ring == 0)
        {
            var error = Win_x64.ioring_get_last_error();
            throw new InvalidOperationException($"Failed to create RIO ring: error {error}");
        }

        // Pre-allocate CQE buffer
        _cqeBuffer = new Win_x64.IORingCqe[queueSize * 2];
        _cqeBufferHandle = GCHandle.Alloc(_cqeBuffer, GCHandleType.Pinned);
        _cqeBufferPtr = _cqeBufferHandle.AddrOfPinnedObject();
    }

    /// <summary>
    /// Gets whether this ring is using RIO mode.
    /// </summary>
    public bool IsRIO => Win_x64.ioring_is_rio(_ring) != 0;

    /// <summary>
    /// Gets the backend type being used.
    /// </summary>
    public Win_x64.IORingBackend Backend => Win_x64.ioring_get_backend(_ring);

    /// <summary>
    /// Gets the maximum number of connections this ring supports.
    /// </summary>
    public int MaxConnections => _maxConnections;

    /// <summary>
    /// Gets the current number of active (registered) connections.
    /// </summary>
    public int ActiveConnections => (int)Win_x64.ioring_rio_get_active_connections(_ring);

    /// <summary>
    /// Gets the receive buffer size per connection.
    /// </summary>
    public int RecvBufferSize => _recvBufferSize;

    /// <summary>
    /// Gets the send buffer size per connection.
    /// </summary>
    public int SendBufferSize => _sendBufferSize;

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

    // =============================================================================
    // RIO-specific methods
    // =============================================================================

    /// <summary>
    /// Registers a connected socket for RIO operations.
    /// </summary>
    /// <param name="socket">The connected socket handle</param>
    /// <param name="recvBuffer">Output: pointer to the pre-allocated receive buffer</param>
    /// <param name="sendBuffer">Output: pointer to the pre-allocated send buffer</param>
    /// <returns>Connection ID (>= 0) on success, -1 on failure</returns>
    /// <remarks>
    /// The returned buffer pointers are valid until UnregisterSocket is called.
    /// Write data to sendBuffer before calling PrepareSendRegistered.
    /// Read data from recvBuffer after a recv completion.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterSocket(nint socket, out nint recvBuffer, out nint sendBuffer)
    {
        return Win_x64.ioring_rio_register(_ring, socket, out recvBuffer, out sendBuffer);
    }

    /// <summary>
    /// Unregisters a connection. Call this BEFORE closing the socket.
    /// </summary>
    /// <param name="connId">The connection ID returned from RegisterSocket</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnregisterSocket(int connId)
    {
        Win_x64.ioring_rio_unregister(_ring, connId);
    }

    /// <summary>
    /// Creates a socket with WSA_FLAG_REGISTERED_IO for use with manual AcceptEx.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Note:</b> This is only needed for advanced scenarios where you want to
    /// manually manage AcceptEx. For normal use, simply call PrepareAccept which
    /// handles AcceptEx internally.
    /// </para>
    /// <para>
    /// Manual AcceptEx pattern (advanced):
    /// <code>
    /// // 1. Create accept socket with RIO flag
    /// var acceptSocket = WindowsRIOGroup.CreateAcceptSocket();
    ///
    /// // 2. Use AcceptEx to accept into this socket (via Windows API)
    /// AcceptEx(listenSocket, acceptSocket, buffer, ...);
    ///
    /// // 3. After AcceptEx completes, update socket context
    /// setsockopt(acceptSocket, SOL_SOCKET, SO_UPDATE_ACCEPT_CONTEXT, listenSocket);
    ///
    /// // 4. Register with RIO
    /// var connId = ring.RegisterSocket(acceptSocket, out recvBuf, out sendBuf);
    /// </code>
    /// </para>
    /// </remarks>
    /// <returns>Socket handle, or -1 on failure</returns>
    public static nint CreateAcceptSocket()
    {
        return Win_x64.ioring_rio_create_accept_socket();
    }

    /// <summary>
    /// Gets the AcceptEx function pointer for use with manual async accept operations.
    /// </summary>
    /// <remarks>
    /// <b>Note:</b> This is only needed for advanced scenarios. PrepareAccept handles
    /// AcceptEx internally for normal use.
    /// </remarks>
    /// <param name="listenSocket">Optional listening socket (0 to use temp socket)</param>
    /// <returns>AcceptEx function pointer, or IntPtr.Zero on failure</returns>
    public static nint GetAcceptEx(nint listenSocket = 0)
    {
        return Win_x64.ioring_get_acceptex(listenSocket);
    }

    /// <summary>
    /// Prepares a receive operation on a registered connection.
    /// Data will be placed in the recvBuffer returned from RegisterSocket.
    /// </summary>
    /// <param name="connId">Connection ID from RegisterSocket</param>
    /// <param name="maxLen">Maximum bytes to receive (up to RecvBufferSize)</param>
    /// <param name="userData">User data returned with completion</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvRegistered(int connId, int maxLen, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_recv_registered(sqe, connId, (uint)maxLen, userData);
    }

    /// <summary>
    /// Prepares a send operation on a registered connection.
    /// Data must already be written to the sendBuffer from RegisterSocket.
    /// </summary>
    /// <param name="connId">Connection ID from RegisterSocket</param>
    /// <param name="len">Number of bytes to send (already in sendBuffer)</param>
    /// <param name="userData">User data returned with completion</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSendRegistered(int connId, int len, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_send_registered(sqe, connId, (uint)len, userData);
    }

    // =============================================================================
    // IIORingGroup implementation (for accept and non-registered operations)
    // =============================================================================

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
    /// <remarks>
    /// For registered connections, prefer PrepareSendRegistered for better performance.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_send(sqe, fd, buf, (uint)len, (int)flags, userData);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// For registered connections, prefer PrepareRecvRegistered for better performance.
    /// </remarks>
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
