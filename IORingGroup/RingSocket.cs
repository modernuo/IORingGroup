// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

namespace System.Network;

/// <summary>
/// A managed socket for zero-copy I/O operations with automatic buffer lifecycle management.
/// </summary>
/// <remarks>
/// <para>
/// RingSocket wraps an OS socket handle with pre-registered buffers for zero-copy I/O.
/// It tracks in-flight operations and handles graceful disconnect to ensure buffers
/// are not released while the kernel is still reading from or writing to them.
/// </para>
/// <para>
/// The socket supports "flush-and-forget" semantics: write data to SendBuffer,
/// call <see cref="QueueSend"/>, and the manager handles the rest. You don't need
/// to track whether sends complete - the manager ensures safe buffer lifecycle.
/// </para>
/// </remarks>
public sealed class RingSocket
{
    private readonly RingSocketManager _manager;

    /// <summary>
    /// Gets the unique identifier for this socket within the manager.
    /// Use this ID to associate application state (e.g., NetState) with this socket.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// Gets the underlying OS socket handle.
    /// </summary>
    public nint Handle { get; }

    /// <summary>
    /// Gets the generation counter for this socket.
    /// Used to detect stale completions after socket ID reuse.
    /// </summary>
    public ushort Generation { get; }

    /// <summary>
    /// Gets the connection ID for ring I/O operations.
    /// </summary>
    internal int ConnectionId { get; }

    /// <summary>
    /// Gets the receive buffer for incoming data.
    /// Data arrives here after recv completions.
    /// </summary>
    public IORingBuffer RecvBuffer { get; }

    /// <summary>
    /// Gets the send buffer for outgoing data.
    /// Write data here and call <see cref="QueueSend"/> to transmit.
    /// </summary>
    public IORingBuffer SendBuffer { get; }

    /// <summary>
    /// Gets whether the socket is connected and operational.
    /// </summary>
    public bool Connected { get; internal set; }

    /// <summary>
    /// Gets whether a recv operation is currently in-flight.
    /// </summary>
    internal bool RecvPending { get; set; }

    /// <summary>
    /// Gets whether a send operation is currently in-flight.
    /// </summary>
    internal bool SendPending { get; set; }

    /// <summary>
    /// Gets whether a disconnect has been requested.
    /// The socket will close after all in-flight I/O completes.
    /// </summary>
    public bool DisconnectPending { get; internal set; }

    /// <summary>
    /// Gets whether the underlying socket handle has been closed.
    /// Used to prevent double-close in cleanup.
    /// </summary>
    internal bool SocketClosed { get; set; }

    /// <summary>
    /// Gets whether this socket has been queued for sending.
    /// Used to prevent duplicate queueing.
    /// </summary>
    internal bool SendQueued { get; set; }

    /// <summary>
    /// Creates a new RingSocket.
    /// </summary>
    internal RingSocket(
        RingSocketManager manager,
        int id,
        nint handle,
        int connectionId,
        ushort generation,
        IORingBuffer recvBuffer,
        IORingBuffer sendBuffer)
    {
        _manager = manager;
        Id = id;
        Handle = handle;
        ConnectionId = connectionId;
        Generation = generation;
        RecvBuffer = recvBuffer;
        SendBuffer = sendBuffer;
        Connected = true;
    }

    /// <summary>
    /// Queues this socket for sending any data in the send buffer.
    /// Safe to call multiple times - the socket is only queued once.
    /// </summary>
    /// <remarks>
    /// This is a "flush-and-forget" operation. Write data to <see cref="SendBuffer"/>,
    /// call this method, and the manager handles the rest. You don't need to wait
    /// for send completion - the manager ensures safe buffer lifecycle.
    /// </remarks>
    public void QueueSend()
    {
        if (!Connected || SendQueued)
        {
            return;
        }

        _manager.QueueSend(this);
    }

    /// <summary>
    /// Requests a graceful disconnect. The socket will be closed after all
    /// in-flight I/O operations complete, ensuring buffer safety.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe to call multiple times. If called while sends are pending (either
    /// in-flight or buffered), the disconnect is deferred until all data is sent.
    /// </para>
    /// <para>
    /// This is critical for zero-copy I/O: if we released buffers while the kernel
    /// was still reading from them, we'd corrupt data or crash. This method ensures
    /// we wait for all I/O to complete before releasing resources.
    /// </para>
    /// </remarks>
    public void Disconnect()
    {
        Console.WriteLine($"[DEBUG] RingSocket.Disconnect: id={Id}, Connected={Connected}, DisconnectPending={DisconnectPending}, RecvPending={RecvPending}, SendPending={SendPending}");

        if (!Connected || DisconnectPending)
        {
            Console.WriteLine($"[DEBUG] RingSocket.Disconnect: early return");
            return;
        }

        DisconnectPending = true;

        // If no I/O is pending, disconnect immediately
        if (!RecvPending && !SendPending)
        {
            Console.WriteLine($"[DEBUG] RingSocket.Disconnect: no pending I/O, calling DisconnectImmediate");
            _manager.DisconnectImmediate(this);
        }
        else
        {
            // I/O is pending - close socket to cancel operations
            // Completions will come back with errors, then we can safely release buffers
            Console.WriteLine($"[DEBUG] RingSocket.Disconnect: I/O pending, calling InitiateClose");
            _manager.InitiateClose(this);
        }
    }

    /// <summary>
    /// Checks if disconnect can proceed after I/O completion.
    /// Called internally by the manager.
    /// </summary>
    /// <returns>True if disconnect should proceed now.</returns>
    internal bool CheckDisconnect()
    {
        if (!DisconnectPending)
        {
            return false;
        }

        // Wait for all in-flight operations
        // If socket is closed, ignore remaining send data (can't be sent anyway)
        var canDisconnect = !RecvPending && !SendPending && (SocketClosed || SendBuffer.ReadableBytes <= 0);
        Console.WriteLine($"[DEBUG] CheckDisconnect: id={Id}, RecvPending={RecvPending}, SendPending={SendPending}, SocketClosed={SocketClosed}, SendBufferReadable={SendBuffer.ReadableBytes}, result={canDisconnect}");
        return canDisconnect;
    }

    /// <summary>
    /// Returns a string representation of this socket.
    /// </summary>
    public override string ToString() =>
        $"RingSocket[{Id}] Handle={Handle} Gen={Generation} Connected={Connected} DisconnectPending={DisconnectPending}";
}
