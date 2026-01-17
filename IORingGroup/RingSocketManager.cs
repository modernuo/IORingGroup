// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Collections.Concurrent;

namespace System.Network;

/// <summary>
/// Event types returned by <see cref="RingSocketManager.ProcessCompletions"/>.
/// </summary>
public enum RingSocketEventType : byte
{
    /// <summary>No event (placeholder).</summary>
    None = 0,

    /// <summary>Data was received into the socket's RecvBuffer.</summary>
    DataReceived = 1,

    /// <summary>Data was sent from the socket's SendBuffer.</summary>
    DataSent = 2,

    /// <summary>Socket was disconnected (graceful close or error).</summary>
    Disconnected = 3,

    /// <summary>A new connection was accepted. Application should call CreateSocket.</summary>
    Accept = 4
}

/// <summary>
/// An event from socket I/O operations.
/// </summary>
public readonly struct RingSocketEvent
{
    /// <summary>The type of event.</summary>
    public RingSocketEventType Type { get; init; }

    /// <summary>The socket this event relates to (null for Accept events).</summary>
    public RingSocket Socket { get; init; }

    /// <summary>Number of bytes transferred (for DataReceived/DataSent).</summary>
    public int BytesTransferred { get; init; }

    /// <summary>Error code if disconnect was due to error (0 for graceful close).</summary>
    public int Error { get; init; }

    /// <summary>The accepted socket handle (for Accept events).</summary>
    public nint AcceptedSocketHandle { get; init; }

    /// <summary>
    /// Creates a data received event.
    /// </summary>
    public static RingSocketEvent Received(RingSocket socket, int bytes) => new()
    {
        Type = RingSocketEventType.DataReceived,
        Socket = socket,
        BytesTransferred = bytes
    };

    /// <summary>
    /// Creates a data sent event.
    /// </summary>
    public static RingSocketEvent Sent(RingSocket socket, int bytes) => new()
    {
        Type = RingSocketEventType.DataSent,
        Socket = socket,
        BytesTransferred = bytes
    };

    /// <summary>
    /// Creates a disconnected event.
    /// </summary>
    public static RingSocketEvent Disconnected(RingSocket socket, int error = 0) => new()
    {
        Type = RingSocketEventType.Disconnected,
        Socket = socket,
        Error = error
    };

    /// <summary>
    /// Creates an accept event.
    /// </summary>
    /// <param name="socketHandle">The accepted socket handle, or negative for error.</param>
    public static RingSocketEvent Accepted(nint socketHandle) => new()
    {
        Type = RingSocketEventType.Accept,
        AcceptedSocketHandle = socketHandle
    };
}

/// <summary>
/// Manages RingSocket instances with automatic buffer lifecycle and graceful disconnect.
/// </summary>
/// <remarks>
/// <para>
/// RingSocketManager handles all the complexity of zero-copy I/O:
/// <list type="bullet">
/// <item>Socket ID allocation with O(1) amortized slot finding</item>
/// <item>Generation tracking to detect stale completions</item>
/// <item>Automatic buffer acquisition and release</item>
/// <item>Graceful disconnect ensuring buffers aren't released during I/O</item>
/// <item>Flush queue management for batched sends</item>
/// </list>
/// </para>
/// <para>
/// Typical usage:
/// <code>
/// // Create manager
/// var manager = new RingSocketManager(ring, maxSockets: 4096);
///
/// // On accept completion
/// var socket = manager.CreateSocket(acceptedHandle);
/// _appState[socket.Id] = new MyConnectionState(socket);
///
/// // In main loop
/// int eventCount = manager.ProcessCompletions(events);
/// for (int i = 0; i &lt; eventCount; i++)
/// {
///     var state = _appState[events[i].Socket.Id];
///     switch (events[i].Type)
///     {
///         case DataReceived: state.OnDataReceived(); break;
///         case DataSent: /* flush-and-forget, nothing to do */ break;
///         case Disconnected: state.OnDisconnected(); _appState[events[i].Socket.Id] = null; break;
///     }
/// }
/// manager.Submit();
/// </code>
/// </para>
/// </remarks>
public sealed class RingSocketManager : IDisposable
{
    private const string Version = "2026-01-16-v4";

    private readonly IIORingGroup _ring;
    private readonly IORingBufferPool _recvBufferPool;
    private readonly IORingBufferPool _sendBufferPool;
    private readonly int _maxSockets;

    // Socket storage
    private readonly RingSocket?[] _sockets;
    private readonly ushort[] _generations;
    private int _nextFreeSlot;

    // Completions buffer
    private readonly Completion[] _completions;

    // Send queue (flush-and-forget)
    private readonly ConcurrentQueue<RingSocket> _sendQueue = new();

    // Disconnect queue
    private readonly ConcurrentQueue<RingSocket> _disconnectQueue = new();

    private bool _disposed;

    /// <summary>
    /// Gets the underlying ring.
    /// </summary>
    public IIORingGroup Ring => _ring;

    /// <summary>
    /// Gets the maximum number of sockets this manager can handle.
    /// </summary>
    public int MaxSockets => _maxSockets;

    /// <summary>
    /// Gets the current number of connected sockets.
    /// </summary>
    public int ConnectedCount { get; private set; }

    /// <summary>
    /// Creates a new socket manager.
    /// </summary>
    /// <param name="ring">The IORingGroup for I/O operations.</param>
    /// <param name="maxSockets">Maximum number of concurrent sockets.</param>
    /// <param name="recvBufferSize">Size of each receive buffer (default 64KB).</param>
    /// <param name="sendBufferSize">Size of each send buffer (default 256KB).</param>
    /// <param name="initialBufferSlabs">Initial buffer pool slabs (default 8).</param>
    /// <param name="maxBufferSlabs">Maximum buffer pool slabs (default 32).</param>
    public RingSocketManager(
        IIORingGroup ring,
        int maxSockets,
        int recvBufferSize = 64 * 1024,
        int sendBufferSize = 256 * 1024,
        int initialBufferSlabs = 8,
        int maxBufferSlabs = 32)
    {
        Console.WriteLine($"[MANAGED] RingSocketManager version: {Version}");

        _ring = ring ?? throw new ArgumentNullException(nameof(ring));

        if (maxSockets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSockets), "Must be positive");
        }

        _maxSockets = maxSockets;
        _sockets = new RingSocket?[maxSockets];
        _generations = new ushort[maxSockets];
        _completions = new Completion[maxSockets];
        _nextFreeSlot = 0;

        // Create buffer pools
        // Estimate slab size based on max sockets
        var slabSize = Math.Max(64, maxSockets / maxBufferSlabs);

        _recvBufferPool = new IORingBufferPool(
            ring,
            slabSize: slabSize,
            bufferSize: recvBufferSize,
            initialSlabs: initialBufferSlabs,
            maxSlabs: maxBufferSlabs
        );

        _sendBufferPool = new IORingBufferPool(
            ring,
            slabSize: slabSize / 4, // Fewer send buffers typically needed
            bufferSize: sendBufferSize,
            initialSlabs: initialBufferSlabs / 2,
            maxSlabs: maxBufferSlabs
        );
    }

    /// <summary>
    /// Creates a new managed socket from an accepted socket handle.
    /// </summary>
    /// <param name="socketHandle">The accepted OS socket handle.</param>
    /// <returns>The created RingSocket, or null if resources exhausted.</returns>
    /// <remarks>
    /// This method:
    /// <list type="number">
    /// <item>Finds a free slot</item>
    /// <item>Acquires recv and send buffers from pools</item>
    /// <item>Registers the socket with the ring</item>
    /// <item>Posts an initial recv operation</item>
    /// </list>
    /// If any step fails, resources are cleaned up and null is returned.
    /// </remarks>
    public RingSocket? CreateSocket(nint socketHandle)
    {
        // Find free slot
        var slotId = FindFreeSlot();
        if (slotId < 0)
        {
            Console.WriteLine("[DEBUG] CreateSocket: NO FREE SLOT!");
            return null;
        }

        // Acquire buffers
        if (!_recvBufferPool.TryAcquire(out var recvBuffer))
        {
            return null;
        }

        if (!_sendBufferPool.TryAcquire(out var sendBuffer))
        {
            _recvBufferPool.Release(recvBuffer!);
            return null;
        }

        // Register socket with ring
        var connId = _ring.RegisterSocket(socketHandle);
        if (connId < 0)
        {
            Console.WriteLine($"[DEBUG] CreateSocket: RegisterSocket FAILED for handle={socketHandle}");
            _recvBufferPool.Release(recvBuffer!);
            _sendBufferPool.Release(sendBuffer!);
            return null;
        }

        // Increment generation for stale completion detection
        var generation = ++_generations[slotId];

        // Create socket
        var socket = new RingSocket(
            this,
            slotId,
            socketHandle,
            connId,
            generation,
            recvBuffer!,
            sendBuffer!
        );

        _sockets[slotId] = socket;
        ConnectedCount++;

        // Post initial recv
        PostRecv(socket);

        Console.WriteLine($"[DEBUG] CreateSocket: slot={slotId}, gen={generation}, connId={connId}");

        return socket;
    }

    /// <summary>
    /// Gets a socket by ID, validating the generation.
    /// </summary>
    /// <param name="socketId">The socket ID.</param>
    /// <param name="generation">The expected generation.</param>
    /// <returns>The socket if valid, null if stale or invalid.</returns>
    public RingSocket? GetSocket(int socketId, ushort generation)
    {
        if (socketId < 0 || socketId >= _maxSockets)
        {
            return null;
        }

        var socket = _sockets[socketId];
        if (socket == null || socket.Generation != generation)
        {
            return null;
        }

        return socket;
    }

    /// <summary>
    /// Processes completions from the ring and returns application events.
    /// </summary>
    /// <param name="events">Buffer to receive events. Should be at least maxSockets in size.</param>
    /// <returns>Number of events written.</returns>
    /// <remarks>
    /// This method:
    /// <list type="bullet">
    /// <item>Processes the send queue (posts pending sends)</item>
    /// <item>Peeks completions from the ring</item>
    /// <item>Filters stale completions using generation</item>
    /// <item>Updates buffer positions on recv/send completion</item>
    /// <item>Handles graceful disconnect coordination</item>
    /// <item>Posts follow-up recv operations</item>
    /// <item>Processes disconnects and releases resources</item>
    /// </list>
    /// Call <see cref="Submit"/> after this to submit queued operations.
    /// </remarks>
    public int ProcessCompletions(Span<RingSocketEvent> events)
    {
        var eventCount = 0;

        // Process send queue
        ProcessSendQueue();

        // Get completions
        var completionCount = _ring.PeekCompletions(_completions);
        // Reduced spam - only log if there are completions and it's interesting
        // if (completionCount > 0) Console.WriteLine($"[DEBUG] PeekCompletions: {completionCount}");

        for (var i = 0; i < completionCount; i++)
        {
            ref var cqe = ref _completions[i];
            var (opType, socketId, generation) = IORingUserData.Decode(cqe.UserData);

            // Return accept completions as events for the application to handle
            if (opType == IORingUserData.OpAccept)
            {
                Console.WriteLine($"[DEBUG] ACCEPT completion: result={cqe.Result}");
                if (eventCount < events.Length)
                {
                    events[eventCount++] = RingSocketEvent.Accepted(cqe.Result);
                }
                continue;
            }

            // Get socket and validate generation
            if (socketId < 0 || socketId >= _maxSockets)
            {
                continue;
            }

            var socket = _sockets[socketId];
            if (socket == null || socket.Generation != generation)
            {
                // Stale completion - ignore silently
                continue;
            }

            switch (opType)
            {
                case IORingUserData.OpRecv:
                {
                    eventCount += HandleRecvCompletion(socket, cqe.Result, events, eventCount);
                    break;
                }

                case IORingUserData.OpSend:
                {
                    eventCount += HandleSendCompletion(socket, cqe.Result, events, eventCount);
                    break;
                }
            }
        }

        _ring.AdvanceCompletionQueue(completionCount);

        // Process disconnect queue
        ProcessDisconnectQueue(events, ref eventCount);

        return eventCount;
    }

    /// <summary>
    /// Submits pending operations to the ring.
    /// Call this after <see cref="ProcessCompletions"/>.
    /// </summary>
    /// <returns>Number of operations submitted.</returns>
    public int Submit()
    {
        return _ring.Submit();
    }

    /// <summary>
    /// Queues a socket for sending.
    /// Called by RingSocket.QueueSend().
    /// </summary>
    internal void QueueSend(RingSocket socket)
    {
        if (socket.SendQueued)
        {
            return;
        }

        socket.SendQueued = true;
        _sendQueue.Enqueue(socket);
    }

    /// <summary>
    /// Disconnects a socket immediately and queues for resource release.
    /// Called by RingSocket.Disconnect() when no I/O is pending and no unsent data.
    /// </summary>
    internal void DisconnectImmediate(RingSocket socket)
    {
        Console.WriteLine($"[DEBUG] DisconnectImmediate: slot={socket.Id}, gen={socket.Generation}, handle={socket.Handle}");
        socket.Connected = false;
        _disconnectQueue.Enqueue(socket);
    }

    /// <summary>
    /// Shuts down the socket for write (sends FIN to peer).
    /// This unblocks pending recv when we want to disconnect but client is waiting for data.
    /// </summary>
    internal void ShutdownForWrite(RingSocket socket)
    {
        Console.WriteLine($"[DEBUG] ShutdownForWrite: slot={socket.Id}, handle={socket.Handle}");
        // SHUT_WR = 1 - shutdown write side, sends FIN to client
        _ring.PrepareShutdown(socket.Handle, 1, 0);
    }

    /// <summary>
    /// Initiates socket shutdown by sending FIN.
    /// Does NOT unregister from RIO - the pending recv should complete naturally
    /// when the client responds to our FIN with its own FIN (recv returns 0).
    /// </summary>
    internal static void CloseSocketHandle(RingSocket socket)
    {
        Console.WriteLine($"[DEBUG] CloseSocketHandle: slot={socket.Id}, handle={socket.Handle}, connId={socket.ConnectionId}");

        // Send FIN to notify client we're done sending
        // SD_SEND = 1
        // DON'T unregister from RIO yet - let pending recv complete naturally
        // When client receives our FIN, it should send FIN back, and our recv returns 0
        Windows.Win_x64.shutdown(socket.Handle, 1);

        // DON'T set Connected=false yet - socket is still valid for receiving
        // The recv should complete with 0 when client sends FIN
    }

    private int FindFreeSlot()
    {
        // Start from hint and scan forward
        var start = _nextFreeSlot;
        for (var i = 0; i < _maxSockets; i++)
        {
            var slot = (start + i) % _maxSockets;
            if (_sockets[slot] == null)
            {
                _nextFreeSlot = (slot + 1) % _maxSockets;
                return slot;
            }
        }
        return -1;
    }

    private void PostRecv(RingSocket socket)
    {
        if (socket.RecvPending || !socket.Connected)
        {
            return;
        }

        var writeSpan = socket.RecvBuffer.GetWriteSpan(out var offset);
        if (writeSpan.Length == 0)
        {
            return;
        }

        _ring.PrepareRecvBuffer(
            socket.ConnectionId,
            socket.RecvBuffer.BufferId,
            offset,
            writeSpan.Length,
            IORingUserData.Encode(IORingUserData.OpRecv, socket.Id, socket.Generation)
        );
        socket.RecvPending = true;
    }

    private void PostSend(RingSocket socket)
    {
        if (socket.SendPending || !socket.Connected)
        {
            return;
        }

        var readSpan = socket.SendBuffer.GetReadSpan(out var offset);
        if (readSpan.Length == 0)
        {
            return;
        }

        _ring.PrepareSendBuffer(
            socket.ConnectionId,
            socket.SendBuffer.BufferId,
            offset,
            readSpan.Length,
            IORingUserData.Encode(IORingUserData.OpSend, socket.Id, socket.Generation)
        );
        socket.SendPending = true;
    }

    /// <summary>
    /// Processes the send queue, posting any queued sends.
    /// This is called automatically at the start of ProcessCompletions,
    /// but can also be called manually to ensure sends are posted before
    /// checking disconnect state.
    /// </summary>
    public void ProcessSendQueue()
    {
        var count = 0;
        while (_sendQueue.TryDequeue(out var socket))
        {
            socket.SendQueued = false;

            if (socket.Connected && !socket.DisconnectPending)
            {
                Console.WriteLine($"[DEBUG] ProcessSendQueue: slot={socket.Id}, SendBuffer.ReadableBytes={socket.SendBuffer.ReadableBytes}");
                PostSend(socket);
                Console.WriteLine($"[DEBUG] ProcessSendQueue: PostSend complete, SendPending={socket.SendPending}");
                count++;
            }
            else
            {
                Console.WriteLine($"[DEBUG] ProcessSendQueue: SKIPPED slot={socket.Id}, Connected={socket.Connected}, DisconnectPending={socket.DisconnectPending}");
            }
        }

        if (count > 0)
        {
            Console.WriteLine($"[DEBUG] ProcessSendQueue: Posted {count} sends");
        }
    }

    private int HandleRecvCompletion(RingSocket socket, int result, Span<RingSocketEvent> events, int eventIndex)
    {
        socket.RecvPending = false;

        if (result <= 0)
        {
            Console.WriteLine($"[DEBUG] RECV ERROR/CLOSE: slot={socket.Id}, result={result}, disconnectPending={socket.DisconnectPending}");
            // Error or graceful close - initiate disconnect if not already pending
            if (!socket.DisconnectPending)
            {
                socket.Disconnect();
            }

            // Check if ready to cleanup (no pending I/O, no unsent data)
            var canDisconnect = socket.CheckDisconnect();
            Console.WriteLine($"[DEBUG] RECV CheckDisconnect: slot={socket.Id}, canDisconnect={canDisconnect}");
            if (canDisconnect)
            {
                _disconnectQueue.Enqueue(socket);
            }
            return 0;
        }

        // If socket was already closed (forced disconnect), just queue for cleanup
        if (!socket.Connected)
        {
            Console.WriteLine($"[DEBUG] RECV on closed socket: slot={socket.Id}, queuing disconnect");
            if (socket.CheckDisconnect())
            {
                _disconnectQueue.Enqueue(socket);
            }
            return 0;
        }

        // Commit received data
        socket.RecvBuffer.CommitWrite(result);

        // Post next recv if space available and not disconnecting
        if (!socket.DisconnectPending && socket.RecvBuffer.WritableBytes > 0)
        {
            PostRecv(socket);
        }

        // Check if disconnect was pending and we're now done with all I/O
        if (socket.CheckDisconnect())
        {
            Console.WriteLine($"[DEBUG] RECV queuing disconnect after success: slot={socket.Id}");
            _disconnectQueue.Enqueue(socket);
        }

        // Return event to application
        if (eventIndex < events.Length)
        {
            events[eventIndex] = RingSocketEvent.Received(socket, result);
            return 1;
        }

        return 0;
    }

    private int HandleSendCompletion(RingSocket socket, int result, Span<RingSocketEvent> events, int eventIndex)
    {
        socket.SendPending = false;

        if (result <= 0)
        {
            Console.WriteLine($"[DEBUG] SEND ERROR: slot={socket.Id}, result={result}");
            // Error - initiate disconnect if not already pending
            if (!socket.DisconnectPending)
            {
                socket.Disconnect();
            }

            // Check if ready to cleanup
            if (socket.CheckDisconnect())
            {
                _disconnectQueue.Enqueue(socket);
            }
            return 0;
        }

        // Commit sent data
        socket.SendBuffer.CommitRead(result);

        // Post next send if more data to send
        // IMPORTANT: Continue sending even if DisconnectPending - we need to drain the buffer
        if (socket.Connected && socket.SendBuffer.ReadableBytes > 0)
        {
            PostSend(socket);
        }

        // Check if disconnect was pending and we're now done with all I/O
        if (socket.CheckDisconnect())
        {
            Console.WriteLine($"[DEBUG] SEND queuing disconnect after success: slot={socket.Id}");
            _disconnectQueue.Enqueue(socket);
        }

        // Return event to application
        if (eventIndex < events.Length)
        {
            events[eventIndex] = RingSocketEvent.Sent(socket, result);
            return 1;
        }

        return 0;
    }

    private void ProcessDisconnectQueue(Span<RingSocketEvent> events, ref int eventCount)
    {
        while (_disconnectQueue.TryDequeue(out var socket))
        {
            Console.WriteLine($"[DEBUG] ProcessDisconnect: slot={socket.Id}, handle={socket.Handle}, connId={socket.ConnectionId}, gen={socket.Generation}, Connected={socket.Connected}, ConnectedCount={ConnectedCount}");

            // If socket is still connected (normal disconnect path), unregister first
            if (socket.Connected)
            {
                // Unregister from ring first
                Console.WriteLine($"[DEBUG] ProcessDisconnect: UnregisterSocket({socket.ConnectionId})");
                _ring.UnregisterSocket(socket.ConnectionId);
            }

            // Close the socket handle if not already closed
            // This is the ONLY place we call closesocket - CloseSocketHandle just sends FIN
            // By waiting until here, there are no pending RIO operations to cause RST
            if (!socket.HandleClosed)
            {
                Console.WriteLine($"[DEBUG] ProcessDisconnect: CloseSocket({socket.Handle})");
                _ring.CloseSocket(socket.Handle);
                socket.HandleClosed = true;
            }
            else
            {
                Console.WriteLine("[DEBUG] ProcessDisconnect: Handle already closed, skipping CloseSocket");
            }

            // Release buffers - safe now because no I/O is pending
            _recvBufferPool.Release(socket.RecvBuffer);
            _sendBufferPool.Release(socket.SendBuffer);

            _sockets[socket.Id] = null;
            ConnectedCount--;

            Console.WriteLine($"[DEBUG] ProcessDisconnect COMPLETE: slot={socket.Id} now free, ConnectedCount={ConnectedCount}");

            // Return event to application
            if (eventCount < events.Length)
            {
                events[eventCount++] = RingSocketEvent.Disconnected(socket);
            }
        }
    }

    /// <summary>
    /// Disposes the manager and all managed sockets.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Close all sockets (close first, then unregister for graceful close)
        for (var i = 0; i < _maxSockets; i++)
        {
            var socket = _sockets[i];
            if (socket != null)
            {
                _ring.CloseSocket(socket.Handle);
                _ring.UnregisterSocket(socket.ConnectionId);
                _sockets[i] = null;
            }
        }

        // Dispose buffer pools
        _recvBufferPool.Dispose();
        _sendBufferPool.Dispose();
    }
}
