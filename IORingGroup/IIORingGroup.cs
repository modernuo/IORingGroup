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
    /// Queues a send operation to transmit data.
    /// </summary>
    /// <param name="fd">Socket file descriptor.</param>
    /// <param name="buf">Pointer to data buffer.</param>
    /// <param name="len">Number of bytes to send.</param>
    /// <param name="flags">Send flags.</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PrepareSend(nint fd, nint buf, int len, MsgFlags flags, ulong userData);

    /// <summary>
    /// Queues a receive operation to receive data.
    /// </summary>
    /// <param name="fd">Socket file descriptor.</param>
    /// <param name="buf">Pointer to buffer to receive data.</param>
    /// <param name="len">Maximum bytes to receive.</param>
    /// <param name="flags">Receive flags.</param>
    /// <param name="userData">User data returned with the completion.</param>
    void PrepareRecv(nint fd, nint buf, int len, MsgFlags flags, ulong userData);

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
    /// Waits for and retrieves completed operations.
    /// </summary>
    /// <param name="completions">Buffer to receive completions.</param>
    /// <param name="minComplete">Minimum number of completions to wait for.</param>
    /// <param name="timeoutMs">Timeout in milliseconds (-1 for infinite).</param>
    /// <returns>Number of completions retrieved.</returns>
    int WaitCompletions(Span<Completion> completions, int minComplete, int timeoutMs);

    /// <summary>
    /// Advances the completion queue head, marking completions as consumed.
    /// Call this after processing completions from PeekCompletions/WaitCompletions.
    /// </summary>
    /// <param name="count">Number of completions to mark as consumed.</param>
    void AdvanceCompletionQueue(int count);
}
