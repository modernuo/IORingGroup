// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network.Windows;

/// <summary>
/// High-performance Windows RIO (Registered I/O) implementation.
/// Uses externally registered buffers for zero-copy I/O and true async operations.
/// </summary>
/// <remarks>
/// <para>
/// Usage pattern:
/// 1. Create with max connections
/// 2. Register external buffers (e.g., IORingBufferPool buffers)
/// 3. Accept connections using PrepareAccept on listener (AcceptEx is handled internally)
/// 4. Register each accepted socket with RegisterSocket()
/// 5. Use PrepareSendBuffer/PrepareRecvBuffer with connection IDs and buffer IDs
/// 6. Call UnregisterSocket() before closing sockets
/// </para>
/// <para>
/// <b>Important:</b> PrepareAccept automatically uses AcceptEx internally to create
/// RIO-compatible sockets. The accepted socket handle returned in the completion
/// can be directly registered with RegisterSocket().
/// </para>
/// <para>
/// <b>Note:</b> Buffers are now managed externally via IORingBufferPool. The recv_buffer_size
/// and send_buffer_size constructor parameters are retained for API compatibility but unused.
/// </para>
/// </remarks>
public sealed class WindowsRIOGroup : IIORingGroup
{
    private readonly nint _ring;
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
    /// Native ring handle (for diagnostic purposes).
    /// </summary>
    public nint Handle => _ring;

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
    /// Gets the socket handle for a connection ID.
    /// </summary>
    /// <param name="connId">Connection ID returned by AcceptEx completion or RegisterSocket.</param>
    /// <returns>Socket handle, or -1 (INVALID_SOCKET) if not found.</returns>
    /// <remarks>
    /// Use this method to get the actual socket handle for shutdown/close operations.
    /// For send/recv, use <see cref="PrepareSendBuffer"/> and <see cref="PrepareRecvBuffer"/>
    /// with the connection ID directly.
    /// </remarks>
    public nint GetSocket(int connId) => Win_x64.ioring_rio_get_socket(_ring, connId);

    /// <summary>
    /// Gets the receive buffer size per connection.
    /// </summary>
    public int RecvBufferSize => _recvBufferSize;

    /// <summary>
    /// Gets the send buffer size per connection (unused - buffers managed externally).
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
    /// <returns>Connection ID (>= 0) on success, -1 on failure</returns>
    /// <remarks>
    /// After registration, use PrepareSendBuffer/PrepareRecvBuffer with external buffer IDs.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterSocket(nint socket)
    {
        return Win_x64.ioring_rio_register(_ring, socket);
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
    /// Creates a listener socket owned by the ring with WSA_FLAG_REGISTERED_IO.
    /// </summary>
    /// <param name="bindAddress">IP address to bind to (e.g., "0.0.0.0" or "127.0.0.1")</param>
    /// <param name="port">Port number to listen on</param>
    /// <param name="backlog">Listen queue size (e.g., 128 or SOMAXCONN)</param>
    /// <returns>Listener socket handle on success, -1 on failure</returns>
    /// <remarks>
    /// <para>
    /// <b>Important:</b> For RIO mode, use this method instead of creating a .NET Socket
    /// for the listener. The listener must have WSA_FLAG_REGISTERED_IO for AcceptEx
    /// sockets to work with RIO operations.
    /// </para>
    /// <para>
    /// Close the listener with <see cref="CloseListener"/> when done.
    /// </para>
    /// </remarks>
    public nint CreateListener(string bindAddress, ushort port, int backlog)
    {
        return Win_x64.ioring_rio_create_listener(_ring, bindAddress, port, backlog);
    }

    /// <summary>
    /// Closes a listener socket created by <see cref="CreateListener"/>.
    /// </summary>
    /// <param name="listener">The listener socket handle</param>
    public void CloseListener(nint listener)
    {
        Win_x64.ioring_rio_close_listener(_ring, listener);
    }

    /// <summary>
    /// Configures an accepted socket with optimal settings.
    /// </summary>
    /// <param name="socket">The accepted socket handle</param>
    /// <remarks>
    /// On Windows RIO, sockets created via AcceptEx are already configured by the library.
    /// This method is provided for API consistency but may be a no-op if the socket
    /// was created via PrepareAccept.
    /// </remarks>
    public void ConfigureSocket(nint socket)
    {
        // AcceptEx sockets from PrepareAccept are already configured by ioring.dll
        // For manually created sockets, call setsockopt for TCP_NODELAY etc.
        Win_x64.ioring_rio_configure_socket(socket);
    }

    /// <summary>
    /// Closes a socket.
    /// </summary>
    /// <param name="socket">The socket handle to close</param>
    public void CloseSocket(nint socket)
    {
        Win_x64.closesocket(socket);
    }

    // =============================================================================
    // External Buffer Support (for zero-copy from user-owned memory like Pipe)
    // =============================================================================

    /// <summary>
    /// Registers an external buffer (e.g., VirtualAlloc'd Pipe memory) with RIO.
    /// </summary>
    /// <param name="buffer">Pointer to buffer memory</param>
    /// <param name="length">Size in bytes (for double-mapped buffers, use 2x physical size)</param>
    /// <returns>Buffer ID on success (>= 0), -1 on failure</returns>
    /// <remarks>
    /// <para>
    /// External buffers allow zero-copy I/O from user-owned memory. This is useful
    /// for circular buffers like ModernUO's Pipe where you want to send directly
    /// from the application's buffer without copying to RIO's internal buffers.
    /// </para>
    /// <para>
    /// The buffer must remain valid until <see cref="UnregisterExternalBuffer"/> is called.
    /// </para>
    /// <para>
    /// Example usage with Pipe:
    /// <code>
    /// var pipe = new Pipe(256 * 1024);
    /// int bufId = ring.RegisterExternalBuffer(pipe.GetBufferPointer(), pipe.GetVirtualSize());
    ///
    /// // Write data to pipe
    /// var span = pipe.Writer.AvailableToWrite();
    /// // ... write packet data ...
    /// pipe.Writer.Advance(bytesWritten);
    ///
    /// // Send directly from pipe memory (zero-copy!)
    /// var toSend = pipe.Reader.AvailableToRead();
    /// int offset = pipe.Reader.GetReadOffset();
    /// ring.PrepareSendExternal(connId, bufId, offset, toSend.Length, userData);
    /// </code>
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterExternalBuffer(nint buffer, uint length)
    {
        return Win_x64.ioring_rio_register_external_buffer(_ring, buffer, length);
    }

    /// <summary>
    /// Unregisters an external buffer.
    /// </summary>
    /// <param name="bufferId">Buffer ID returned by <see cref="RegisterExternalBuffer"/></param>
    /// <remarks>
    /// The caller is responsible for freeing the buffer memory after unregistering.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnregisterExternalBuffer(int bufferId)
    {
        Win_x64.ioring_rio_unregister_external_buffer(_ring, bufferId);
    }

    /// <summary>
    /// Prepares a send operation from an external registered buffer.
    /// </summary>
    /// <param name="connId">Connection ID from <see cref="RegisterSocket"/></param>
    /// <param name="bufferId">External buffer ID from <see cref="RegisterExternalBuffer"/></param>
    /// <param name="offset">Offset within the registered buffer to start sending from</param>
    /// <param name="len">Number of bytes to send</param>
    /// <param name="userData">User data returned with completion</param>
    /// <remarks>
    /// This allows zero-copy sends directly from user-owned memory like Pipe.
    /// The data must already be written at the specified offset in the external buffer.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSendExternal(int connId, int bufferId, int offset, int len, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_send_external(sqe, connId, bufferId, (uint)offset, (uint)len, userData);
    }

    /// <summary>
    /// Gets the number of registered external buffers.
    /// </summary>
    public int ExternalBufferCount => (int)Win_x64.ioring_rio_get_external_buffer_count(_ring);

    /// <summary>
    /// Gets the maximum number of external buffers (based on max_connections * 3).
    /// </summary>
    public int MaxExternalBuffers => (int)Win_x64.ioring_rio_get_max_external_buffers(_ring);

    /// <summary>
    /// Prepares a receive operation from an external registered buffer.
    /// </summary>
    /// <param name="connId">Connection ID from <see cref="RegisterSocket"/></param>
    /// <param name="bufferId">External buffer ID from <see cref="RegisterExternalBuffer"/></param>
    /// <param name="offset">Offset within the registered buffer to receive into</param>
    /// <param name="len">Maximum number of bytes to receive</param>
    /// <param name="userData">User data returned with completion</param>
    /// <remarks>
    /// This allows zero-copy receives directly into user-owned memory like Pipe.
    /// After the completion, read the received data at the specified offset.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvExternal(int connId, int bufferId, int offset, int len, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_recv_external(sqe, connId, bufferId, (uint)offset, (uint)len, userData);
    }

    // =============================================================================
    // IIORingGroup Registered Buffer Operations
    // =============================================================================

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int RegisterBuffer(IORingBuffer buffer)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        return Win_x64.ioring_rio_register_external_buffer(_ring, buffer.Pointer, (uint)buffer.VirtualSize);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UnregisterBuffer(int bufferId)
    {
        Win_x64.ioring_rio_unregister_external_buffer(_ring, bufferId);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareSendBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_send_external(sqe, connId, bufferId, (uint)offset, (uint)length, userData);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData)
    {
        var sqe = GetSqe();
        Win_x64.ioring_prep_recv_external(sqe, connId, bufferId, (uint)offset, (uint)length, userData);
    }

    // =============================================================================
    // IIORingGroup implementation (for accept and non-registered operations)
    // =============================================================================

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollAdd(nint fd, PollMask mask, ulong userData) =>
        Win_x64.ioring_prep_poll_add(GetSqe(), fd, (uint)mask, userData);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PreparePollRemove(ulong userData) => Win_x64.ioring_prep_poll_remove(GetSqe(), userData);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData) =>
        Win_x64.ioring_prep_accept(GetSqe(), listenFd, addr, addrLen, userData);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData) =>
        Win_x64.ioring_prep_connect(GetSqe(), fd, addr, addrLen, userData);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareClose(nint fd, ulong userData) => Win_x64.ioring_prep_close(GetSqe(), fd, userData);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareCancel(ulong targetUserData, ulong userData) =>
        Win_x64.ioring_prep_cancel(GetSqe(), targetUserData, userData);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PrepareShutdown(nint fd, int how, ulong userData) =>
        Win_x64.ioring_prep_shutdown(GetSqe(), fd, how, userData);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Submit() => Win_x64.ioring_submit(_ring);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SubmitAndWait(int waitNr) => Win_x64.ioring_submit_and_wait(_ring, (uint)waitNr);

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
    public void AdvanceCompletionQueue(int count) => Win_x64.ioring_cq_advance(_ring, (uint)count);

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
