// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

namespace System.Network;

/// <summary>
/// High-performance asynchronous I/O interface modeled after Linux io_uring.
/// Provides submission queue/completion queue semantics for batched async I/O operations.
/// </summary>
public interface IIORingGroup : IDisposable
{
    /// <summary>
    /// Gets the number of entries available in the submission queue.
    /// </summary>
    int SubmissionQueueSpace { get; }

    /// <summary>
    /// Gets the number of pending completions in the completion queue.
    /// </summary>
    int CompletionQueueCount { get; }

    /// <summary>
    /// Queues a poll operation to monitor a file descriptor for events.
    /// </summary>
    /// <param name="fd">File descriptor or socket handle to poll.</param>
    /// <param name="mask">Events to monitor (In, Out, etc.).</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PreparePollAdd(nint fd, PollMask mask, ulong userData);

    /// <summary>
    /// Queues removal of a previously submitted poll operation.
    /// </summary>
    /// <param name="userData">User data of the poll operation to cancel.</param>
    void PreparePollRemove(ulong userData);

    /// <summary>
    /// Queues an accept operation on a listening socket.
    /// </summary>
    /// <param name="listenFd">Listening socket file descriptor.</param>
    /// <param name="addr">Pointer to sockaddr buffer to receive client address (can be null).</param>
    /// <param name="addrLen">Pointer to address length (in/out parameter, can be null).</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PrepareAccept(nint listenFd, nint addr, nint addrLen, ulong userData);

    /// <summary>
    /// Queues a connect operation to establish a connection.
    /// </summary>
    /// <param name="fd">Socket file descriptor.</param>
    /// <param name="addr">Pointer to sockaddr containing target address.</param>
    /// <param name="addrLen">Length of the address structure.</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PrepareConnect(nint fd, nint addr, int addrLen, ulong userData);

    /// <summary>
    /// Queues a close operation on a file descriptor.
    /// </summary>
    /// <param name="fd">File descriptor to close.</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PrepareClose(nint fd, ulong userData);

    /// <summary>
    /// Queues cancellation of a pending operation.
    /// </summary>
    /// <param name="targetUserData">User data of the operation to cancel.</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PrepareCancel(ulong targetUserData, ulong userData);

    /// <summary>
    /// Queues a socket shutdown operation.
    /// </summary>
    /// <param name="fd">Socket file descriptor.</param>
    /// <param name="how">Shutdown mode: 0=SHUT_RD, 1=SHUT_WR, 2=SHUT_RDWR.</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PrepareShutdown(nint fd, int how, ulong userData);

    /// <summary>
    /// Submits all queued operations to the kernel for processing.
    /// </summary>
    /// <returns>Number of operations successfully submitted.</returns>
    int Submit();

    /// <summary>
    /// Submits queued operations and waits for at least the specified number of completions.
    /// </summary>
    /// <param name="waitNr">Minimum number of completions to wait for.</param>
    /// <returns>Number of operations successfully submitted.</returns>
    int SubmitAndWait(int waitNr);

    /// <summary>
    /// Retrieves completed operations without blocking.
    /// </summary>
    /// <param name="completions">Buffer to receive completions.</param>
    /// <returns>Number of completions retrieved.</returns>
    int PeekCompletions(Span<Completion> completions);

    /// <summary>
    /// Advances the completion queue head, marking completions as consumed.
    /// Call this after processing completions from PeekCompletions.
    /// </summary>
    /// <param name="count">Number of completions to mark as consumed.</param>
    void AdvanceCompletionQueue(int count);

    // =============================================================================
    // Listener and Socket Management
    // =============================================================================

    /// <summary>
    /// Creates a listening socket bound to the specified address and port.
    /// </summary>
    /// <param name="bindAddress">IP address to bind to (e.g., "0.0.0.0" for all interfaces).</param>
    /// <param name="port">Port number to listen on.</param>
    /// <param name="backlog">Maximum pending connections queue length.</param>
    /// <returns>Listener socket handle on success, -1 on failure.</returns>
    /// <remarks>
    /// The socket is created with platform-optimal flags:
    /// - Non-blocking mode
    /// - TCP_NODELAY (Nagle disabled)
    /// - SO_REUSEADDR disabled (exclusive address use)
    /// - SO_LINGER disabled
    /// On Windows RIO, the socket includes WSA_FLAG_REGISTERED_IO for AcceptEx compatibility.
    /// </remarks>
    nint CreateListener(string bindAddress, ushort port, int backlog);

    /// <summary>
    /// Closes a listener socket created by <see cref="CreateListener"/>.
    /// </summary>
    /// <param name="listener">The listener socket handle.</param>
    void CloseListener(nint listener);

    /// <summary>
    /// Configures an accepted socket with optimal settings.
    /// </summary>
    /// <param name="socket">The accepted socket handle.</param>
    /// <remarks>
    /// Sets:
    /// - Non-blocking mode
    /// - TCP_NODELAY (Nagle disabled)
    /// - SO_LINGER disabled
    /// Call this on sockets returned from accept completions.
    /// </remarks>
    void ConfigureSocket(nint socket);

    /// <summary>
    /// Registers a socket for I/O operations and returns a connection ID.
    /// </summary>
    /// <param name="socket">The socket handle to register.</param>
    /// <returns>Connection ID on success (>= 0), -1 on failure.</returns>
    /// <remarks>
    /// On Windows RIO, this creates a Request Queue for the socket.
    /// On Linux/Darwin, the socket handle is used directly as the connection ID.
    /// The returned connection ID is used with <see cref="PrepareSendBuffer"/> and <see cref="PrepareRecvBuffer"/>.
    /// </remarks>
    int RegisterSocket(nint socket);

    /// <summary>
    /// Unregisters a previously registered socket.
    /// </summary>
    /// <param name="connId">The connection ID returned by <see cref="RegisterSocket"/>.</param>
    /// <remarks>
    /// Call this before closing the socket. On Windows RIO, this frees the Request Queue.
    /// On Linux/Darwin, this is a no-op but should still be called for consistency.
    /// </remarks>
    void UnregisterSocket(int connId);

    /// <summary>
    /// Closes a socket.
    /// </summary>
    /// <param name="socket">The socket handle to close.</param>
    void CloseSocket(nint socket);

    // =============================================================================
    // Registered Buffer Operations (Zero-Copy I/O)
    // =============================================================================

    /// <summary>
    /// Registers a buffer for zero-copy I/O operations.
    /// </summary>
    /// <param name="buffer">The buffer to register.</param>
    /// <returns>Buffer ID for use with buffer-based I/O operations.</returns>
    /// <remarks>
    /// On Windows RIO, this calls RIORegisterBuffer.
    /// On Linux io_uring, this registers with io_uring_register_buffers.
    /// The buffer's entire virtual size (2x physical for double-mapped) is registered.
    /// </remarks>
    int RegisterBuffer(IORingBuffer buffer);

    /// <summary>
    /// Unregisters a previously registered buffer.
    /// </summary>
    /// <param name="bufferId">The buffer ID returned from <see cref="RegisterBuffer"/>.</param>
    void UnregisterBuffer(int bufferId);

    /// <summary>
    /// Queues a send operation using a registered buffer (zero-copy).
    /// </summary>
    /// <param name="connId">Connection ID (from socket registration if applicable).</param>
    /// <param name="bufferId">Registered buffer ID.</param>
    /// <param name="offset">Offset within the buffer.</param>
    /// <param name="length">Number of bytes to send.</param>
    /// <param name="userData">User data returned with the completion.</param>
    /// <remarks>
    /// This enables true zero-copy sends directly from a registered buffer.
    /// The offset can extend into the double-mapped region (0 to 2x physical size).
    /// </remarks>
    void PrepareSendBuffer(int connId, int bufferId, int offset, int length, ulong userData);

    /// <summary>
    /// Queues a receive operation using a registered buffer (zero-copy).
    /// </summary>
    /// <param name="connId">Connection ID (from socket registration if applicable).</param>
    /// <param name="bufferId">Registered buffer ID.</param>
    /// <param name="offset">Offset within the buffer.</param>
    /// <param name="length">Maximum bytes to receive.</param>
    /// <param name="userData">User data returned with the completion.</param>
    /// <remarks>
    /// This enables true zero-copy receives directly into a registered buffer.
    /// The offset can extend into the double-mapped region (0 to 2x physical size).
    /// </remarks>
    void PrepareRecvBuffer(int connId, int bufferId, int offset, int length, ulong userData);
}
