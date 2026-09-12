// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Diagnostics;
using System.Runtime.CompilerServices;

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
    public RingSocketEventType Type { get; private init; }

    /// <summary>The socket this event relates to (null for Accept events).</summary>
    public RingSocket Socket { get; private init; }

    /// <summary>Number of bytes transferred (for DataReceived/DataSent).</summary>
    public int BytesTransferred { get; private init; }

    /// <summary>Error code if disconnect was due to error (0 for graceful close).</summary>
    public int Error { get; private init; }

    /// <summary>The accepted socket handle (for Accept events).</summary>
    public nint AcceptedSocketHandle { get; private init; }

    /// <summary>
    /// Creates a data received event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RingSocketEvent Received(RingSocket socket, int bytes) => new()
    {
        Type = RingSocketEventType.DataReceived,
        Socket = socket,
        BytesTransferred = bytes
    };

    /// <summary>
    /// Creates a data sent event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RingSocketEvent Sent(RingSocket socket, int bytes) => new()
    {
        Type = RingSocketEventType.DataSent,
        Socket = socket,
        BytesTransferred = bytes
    };

    /// <summary>
    /// Creates a disconnected event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
/// <para><b>Threading:</b> This class is <b>not thread-safe</b>. All method calls —
/// <see cref="ProcessCompletions"/>, <see cref="Submit"/>, <see cref="CreateSocket"/>,
/// <see cref="DisconnectImmediate"/>, and <see cref="ProcessSendQueue"/> — must be made
/// from a single thread (the ring's processing thread). The internal send and disconnect
/// queues are plain <see cref="System.Collections.Generic.Queue{T}"/> with no synchronization.
/// This is intentional: single-threaded access eliminates lock contention and enables
/// zero-allocation hot paths.</para>
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
    private readonly IIORingGroup _ring;
    private readonly IORingBufferPool _recvBufferPool;
    private readonly IORingBufferPool _sendBufferPool;
    private readonly IORingBufferPool[] _sendTiers; // index 0 = 2 x base
    private readonly int _maxSockets;

    private int _growthRefusals;

    // The budget, not this cap, bounds growth
    private const int TierMaxSlabs = 1024;

    // Slab capped at 8 MiB (floor 4 buffers) so the minimum budget stays sane
    private const int TierSlabByteCap = 8 * 1024 * 1024;

    // A tiny socket table would otherwise land on one-buffer base slabs
    private const int MinimumBaseSlabSize = 16;

    /// <summary>
    /// Hard ceiling on <see cref="MaxSendBufferSize"/>; above this a tier's slab byte count stops fitting the arithmetic that bounds it.
    /// </summary>
    private const int SendBufferSizeCeiling = 256 * 1024 * 1024;

    /// <summary>Number of growth tiers above the base send buffer size.</summary>
    public int SendBufferTierCount => _sendTiers.Length;

    /// <summary>Largest send buffer a socket can grow to.</summary>
    public int MaxSendBufferSize { get; }

    /// <summary>Bytes of tier-pool capacity allowed across all tiers.</summary>
    public long SendBufferGrowthBudget { get; }

    // Socket storage
    private readonly RingSocket?[] _sockets;
    private readonly ushort[] _generations;
    private int _nextFreeSlot;

    // Completions buffer
    private readonly Completion[] _completions;

    // Send queue (flush-and-forget)
    private readonly Queue<RingSocket> _sendQueue = new();

    // Disconnect queue
    private readonly Queue<RingSocket> _disconnectQueue = new();
    private readonly List<RingSocket> _releasePending = new();

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
    /// <param name="initialBufferSlabs">
    /// Slabs each base pool allocates up front and never trims below (default 1); the rest arrive a
    /// slab at a time as connections do.
    /// </param>
    /// <param name="maxBufferSlabs">
    /// Divisor setting base slab size (default 128): <see cref="BasePoolSlabSize"/> buffers per slab,
    /// and as many slabs as <paramref name="maxSockets"/> needs.
    /// </param>
    /// <param name="maxSendBufferSize">
    /// Largest send buffer a socket may grow to. 0 (default) means <paramref name="sendBufferSize"/>,
    /// disabling growth. Must be a power of two, no smaller than <paramref name="sendBufferSize"/>
    /// and no larger than 256 MiB.
    /// </param>
    /// <param name="sendBufferGrowthBudget">
    /// Bytes of tier-pool capacity allowed across all tiers. 0 refuses every growth; a positive
    /// value must be at least <see cref="MinimumSendBufferGrowthBudget"/> (one slab).
    /// </param>
    /// <param name="sendBufferRetentionWindows">
    /// Number of <see cref="Maintain"/> windows a pool's peak usage stays in force (default 15).
    /// </param>
    public RingSocketManager(
        IIORingGroup ring,
        int maxSockets,
        int recvBufferSize = 64 * 1024,
        int sendBufferSize = 256 * 1024,
        int initialBufferSlabs = 1,
        int maxBufferSlabs = 128,
        int maxSendBufferSize = 0,
        long sendBufferGrowthBudget = 0,
        int sendBufferRetentionWindows = 15)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));

        if (maxSockets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSockets), "Must be positive");
        }

        if (sendBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sendBufferSize), "Must be positive");
        }

        if (sendBufferRetentionWindows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sendBufferRetentionWindows), "Must be at least 1");
        }

        // Validate before allocating; a later throw would strand registered memory
        if (maxSendBufferSize > SendBufferSizeCeiling)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSendBufferSize),
                $"Must not exceed {SendBufferSizeCeiling} bytes (256 MiB)"
            );
        }

        MaxSendBufferSize = maxSendBufferSize > 0 ? maxSendBufferSize : sendBufferSize;
        if (MaxSendBufferSize < sendBufferSize || !IORingGroup.IsPowerOfTwo(MaxSendBufferSize))
        {
            throw new ArgumentOutOfRangeException(nameof(maxSendBufferSize), "Must be a power of two no smaller than sendBufferSize");
        }

        // Tier buffers come a slab at a time
        if (MaxSendBufferSize > sendBufferSize && sendBufferGrowthBudget > 0)
        {
            var minimumBudget = MinimumSendBufferGrowthBudget(sendBufferSize);
            if (sendBufferGrowthBudget < minimumBudget)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sendBufferGrowthBudget),
                    $"Must be at least {minimumBudget} bytes (one first-tier slab), or 0 to refuse all growth"
                );
            }
        }

        // Fail here rather than at an accept or growth where the cause is invisible
        var needed = RequiredRegisteredBuffers(maxSockets, sendBufferSize, MaxSendBufferSize, sendBufferGrowthBudget, maxBufferSlabs);
        if (ring.MaxRegisteredBuffers > 0 && ring.MaxRegisteredBuffers < needed)
        {
            throw new ArgumentException(
                $"The ring registers at most {ring.MaxRegisteredBuffers} buffers but this configuration needs {needed}; " +
                "pass RingSocketManager.RequiredRegisteredBuffers(...) to IORingGroup.Create(maxRegisteredBuffers:).",
                nameof(ring)
            );
        }

        SendBufferGrowthBudget = sendBufferGrowthBudget;

        _maxSockets = maxSockets;
        MaxOutstandingSendsPerSocket = Math.Max(1, ring.MaxOutstandingSendsPerSocket);
        _sockets = new RingSocket?[maxSockets];
        _generations = new ushort[maxSockets];
        _completions = new Completion[maxSockets];
        _nextFreeSlot = 0;

        // Both base pools hand out one buffer per socket, so they are sized identically
        var slabSize = BasePoolSlabSize(maxSockets, maxBufferSlabs);
        var baseSlabs = SlabsPerSocketSet(maxSockets, slabSize);
        var baseInitialSlabs = Math.Min(initialBufferSlabs, baseSlabs);

        // long: an int doubling past 1 GiB wraps and loops forever
        var tierCount = 0;
        for (var size = (long)sendBufferSize * 2; size <= MaxSendBufferSize; size *= 2)
        {
            tierCount++;
        }

        _sendTiers = new IORingBufferPool[tierCount];

        // A later pool throwing would strand the earlier ones
        var created = new List<IORingBufferPool>(tierCount + 2);
        try
        {
            _recvBufferPool = new IORingBufferPool(
                ring,
                slabSize: slabSize,
                bufferSize: recvBufferSize,
                initialSlabs: baseInitialSlabs,
                maxSlabs: baseSlabs,
                retentionWindows: sendBufferRetentionWindows,
                minSlabs: baseInitialSlabs
            );
            created.Add(_recvBufferPool);

            _sendBufferPool = new IORingBufferPool(
                ring,
                slabSize: slabSize,
                bufferSize: sendBufferSize,
                initialSlabs: baseInitialSlabs,
                maxSlabs: baseSlabs,
                retentionWindows: sendBufferRetentionWindows,
                minSlabs: baseInitialSlabs
            );
            created.Add(_sendBufferPool);

            var tierSize = sendBufferSize;
            for (var i = 0; i < tierCount; i++)
            {
                tierSize *= 2;
                var tierPool = new IORingBufferPool(
                    ring,
                    slabSize: TierSlabSize(tierSize),
                    bufferSize: tierSize,
                    initialSlabs: 0,
                    maxSlabs: TierMaxSlabs,
                    retentionWindows: sendBufferRetentionWindows
                );

                _sendTiers[i] = tierPool;
                created.Add(tierPool);
            }
        }
        catch
        {
            for (var i = 0; i < created.Count; i++)
            {
                created[i].Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Buffers per slab in either base pool; shared with <see cref="RequiredRegisteredBuffers"/> so the two cannot drift.
    /// </summary>
    /// <param name="maxSockets">Maximum number of concurrent sockets.</param>
    /// <param name="maxBufferSlabs">Slabs <paramref name="maxSockets"/> is divided into, floored at <see cref="MinimumBaseSlabSize"/> buffers.</param>
    public static int BasePoolSlabSize(int maxSockets, int maxBufferSlabs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSockets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSlabs);

        return Math.Max(MinimumBaseSlabSize, maxSockets / maxBufferSlabs);
    }

    /// <summary>
    /// Slabs of <paramref name="slabSize"/> buffers needed before every one of
    /// <paramref name="maxSockets"/> sockets holds one; a base pool can never use more than this.
    /// </summary>
    private static int SlabsPerSocketSet(int maxSockets, int slabSize) => (maxSockets - 1) / slabSize + 1;

    /// <summary>Buffers those slabs hold.</summary>
    private static int RoundUpToSlabs(int maxSockets, int slabSize) =>
        SlabsPerSocketSet(maxSockets, slabSize) * slabSize;

    /// <summary>
    /// Buffers per slab in the tier pool holding buffers of <paramref name="tierSize"/> bytes.
    /// </summary>
    private static int TierSlabSize(int tierSize) => Math.Max(4, Math.Min(16, TierSlabByteCap / tierSize));

    /// <summary>
    /// Smallest usable growth budget for this base size: one first-tier slab.
    /// </summary>
    public static long MinimumSendBufferGrowthBudget(int sendBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBufferSize);

        checked
        {
            var tierSize = sendBufferSize * 2;
            return (long)TierSlabSize(tierSize) * tierSize;
        }
    }

    /// <summary>
    /// Registration table size for a manager with send buffer growth disabled: what both base pools
    /// can hand out. This is what <see cref="IORingGroup.Create"/> sizes its table to by default.
    /// </summary>
    /// <param name="maxSockets">Maximum number of concurrent sockets.</param>
    /// <param name="maxBufferSlabs">Divisor setting base slab size; see <see cref="BasePoolSlabSize"/>.</param>
    public static int RequiredRegisteredBuffers(int maxSockets, int maxBufferSlabs = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSockets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSlabs);

        checked
        {
            return RoundUpToSlabs(maxSockets, BasePoolSlabSize(maxSockets, maxBufferSlabs)) * 2;
        }
    }

    /// <summary>
    /// Registration table size for this configuration: what both base pools can hand out plus
    /// the first-tier buffers the growth budget can hold.
    /// </summary>
    /// <remarks>
    /// A socket holds at most one base recv and one base send buffer, even mid-swap (a shrink
    /// acquires before it releases), so each base pool is bounded by <paramref name="maxSockets"/> rounded to slabs.
    /// </remarks>
    public static int RequiredRegisteredBuffers(
        int maxSockets,
        int sendBufferSize,
        int maxSendBufferSize,
        long sendBufferGrowthBudget,
        int maxBufferSlabs = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBufferSize);

        checked
        {
            var baseMax = RequiredRegisteredBuffers(maxSockets, maxBufferSlabs);

            var tierHeadroom = maxSendBufferSize > sendBufferSize && sendBufferGrowthBudget > 0
                ? (int)(sendBufferGrowthBudget / (sendBufferSize * 2L))
                : 0;

            return baseMax + tierHeadroom;
        }
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
        var slotId = FindFreeSlot();
        if (slotId < 0)
        {
            return null;
        }

        if (!_recvBufferPool.TryAcquire(out var recvBuffer))
        {
            return null;
        }

        if (!_sendBufferPool.TryAcquire(out var sendBuffer))
        {
            _recvBufferPool.Release(recvBuffer!);
            return null;
        }

        var connId = _ring.RegisterSocket(socketHandle);
        if (connId < 0)
        {
            _recvBufferPool.Release(recvBuffer!);
            _sendBufferPool.Release(sendBuffer!);
            return null;
        }

        var generation = ++_generations[slotId];

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

        PostRecv(socket);

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
    /// <param name="events">Buffer to receive events. Size it at 2 × maxSockets so a full batch of
    /// completions and the disconnects that follow both fit; a Disconnected event that does not
    /// fit waits for the next pass.</param>
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

        // Buffers of sockets finalized last pass go back to the pools now, after the consumer has
        // read the events that referenced them.
        ReleaseRetiredBuffers();

        ProcessSendQueue();

        var completionCount = _ring.PeekCompletions(_completions);

        for (var i = 0; i < completionCount; i++)
        {
            ref var cqe = ref _completions[i];
            var (opType, socketId, generation) = IORingUserData.Decode(cqe.UserData);

            if (opType == IORingUserData.OpAccept)
            {
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

                case IORingUserData.OpShutdown:
                {
                    // Shutdown completion - nothing to do, FIN was sent
                    // The recv will complete with 0 when client responds with FIN
                    break;
                }
            }
        }

        _ring.AdvanceCompletionQueue(completionCount);

        // Operations prepared this pass reference registrations that finalization frees, so they
        // are submitted first.
        _ring.Submit();

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
    /// Waits for I/O completions or until the specified timeout expires.
    /// Delegates to the underlying ring's platform-native wait mechanism.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    public void WaitForCompletion(int timeoutMs)
    {
        _ring.WaitForCompletion(timeoutMs);
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

    private const int ShutdownWrite = 1;
    private const int ShutdownBoth = 2;

    /// <summary>
    /// Force-closes a socket: buffered data is not drained, and the socket is released once every
    /// outstanding operation has retired. Use when the graceful path cannot finish.
    /// </summary>
    /// <remarks>
    /// Anything already prepared against this socket is submitted first, while its handle and
    /// registration are still valid. Shutting down both directions makes the peer see FIN and
    /// retires a pending recv or send on every backend but RIO, which cancels them on close; there
    /// the handle is closed and the registration dropped at once so a recycled handle value cannot
    /// resolve to this socket. Elsewhere the handle stays open until the socket is finalized, so no
    /// operation can reach a recycled descriptor. The slot and buffers stay owned until every
    /// completion has been consumed; releasing earlier would hand the kernel's target memory to
    /// another connection.
    /// </remarks>
    public void DisconnectImmediate(RingSocket socket)
    {
        if (socket.Aborting || socket.DisconnectQueued)
        {
            return;
        }

        socket.Aborting = true;
        socket.Connected = false;

        _ring.Submit();
        _ring.Shutdown(socket.Handle, ShutdownBoth);

        if (_ring.CloseCancelsPendingIo)
        {
            CloseHandle(socket);
            Unregister(socket);
        }

        if (socket.IoRetired)
        {
            QueueForDisconnect(socket);
        }
    }

    private void Unregister(RingSocket socket)
    {
        if (socket is { ConnectionId: >= 0, RioUnregistered: false })
        {
            socket.RioUnregistered = true;
            _ring.UnregisterSocket(socket.ConnectionId);
        }
    }

    /// <summary>
    /// Queues a retired socket for release on the next pass. No further I/O may be posted on it.
    /// </summary>
    private void QueueForDisconnect(RingSocket socket)
    {
        socket.Connected = false;

        // Prevent double-queueing which would cause double buffer release
        if (socket.DisconnectQueued)
        {
            return;
        }

        socket.DisconnectQueued = true;
        _disconnectQueue.Enqueue(socket);
    }

    private void CloseHandle(RingSocket socket)
    {
        if (socket.HandleClosed)
        {
            return;
        }

        socket.HandleClosed = true;
        _ring.CloseSocket(socket.Handle);
    }

    /// <summary>
    /// Shuts down the write side (FIN); the pending recv completes when the peer answers with its
    /// own FIN. Synchronous on purpose: a ring shutdown op resolves its descriptor late and could
    /// reach a recycled handle.
    /// </summary>
    internal void CloseSocketHandle(RingSocket socket)
    {
        socket.ShutdownSent = true;
        _ring.Shutdown(socket.Handle, ShutdownWrite);
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

        var recvBuffer = socket.RecvBuffer;
        var writeLength = recvBuffer.WritableBytes;
        if (writeLength == 0)
        {
            return;
        }

        _ring.PrepareRecvBuffer(
            socket.ConnectionId,
            recvBuffer.BufferId,
            recvBuffer.WriteOffset,
            writeLength,
            IORingUserData.Encode(IORingUserData.OpRecv, socket.Id, socket.Generation)
        );
        socket.RecvPending = true;
    }

    /// <summary>
    /// Maximum sends in flight per socket, taken from the ring so it can never exceed what the
    /// platform reserved at request-queue creation.
    /// </summary>
    public int MaxOutstandingSendsPerSocket { get; }

    private void PostSend(RingSocket socket)
    {
        if (!socket.Connected)
        {
            return;
        }

        // The retiring buffer drains before the current one posts
        var sendBuffer = socket.SendSource;

        // Drain everything queued, up to the outstanding limit. Posting from SendOffset rather than
        // ReadOffset allows a second send while the first is still outstanding.
        while (socket.SendsInFlight < MaxOutstandingSendsPerSocket)
        {
            var sendLength = sendBuffer.SendableBytes;
            if (sendLength == 0)
            {
                return;
            }

            _ring.PrepareSendBuffer(
                socket.ConnectionId,
                sendBuffer.BufferId,
                sendBuffer.SendOffset,
                sendLength,
                IORingUserData.Encode(IORingUserData.OpSend, socket.Id, socket.Generation)
            );

            sendBuffer.CommitSend(sendLength);
            socket.PushInFlight(sendLength, sendBuffer);
        }
    }

    /// <summary>
    /// Processes the send queue, posting any queued sends.
    /// Called automatically at the start of ProcessCompletions, but can also be
    /// called manually to ensure sends are posted before checking disconnect state.
    /// </summary>
    public void ProcessSendQueue()
    {
        while (_sendQueue.Count > 0)
        {
            var socket = _sendQueue.Dequeue();
            socket.SendQueued = false;

            // Post send even if DisconnectPending - we need to drain the buffer before disconnecting
            if (socket.Connected)
            {
                PostSend(socket);
            }
        }
    }

    private int HandleRecvCompletion(RingSocket socket, int result, Span<RingSocketEvent> events, int eventIndex)
    {
        socket.RecvPending = false;

        if (socket.Aborting)
        {
            if (socket.IoRetired)
            {
                QueueForDisconnect(socket);
            }
            return 0;
        }

        if (result < 0)
        {
            // Nothing can be delivered in either direction any more
            DisconnectImmediate(socket);
            return 0;
        }

        if (result == 0)
        {
            // Peer EOF: drain what is buffered, then close
            if (!socket.DisconnectPending)
            {
                socket.Disconnect();
            }

            if (socket.CheckDisconnect())
            {
                QueueForDisconnect(socket);
            }
            return 0;
        }

        if (!socket.Connected)
        {
            if (socket.CheckDisconnect())
            {
                QueueForDisconnect(socket);
            }
            return 0;
        }

        socket.RecvBuffer.CommitWrite(result);

        if (socket is { DisconnectPending: false, RecvBuffer.WritableBytes: > 0 })
        {
            PostRecv(socket);
        }

        if (socket.CheckDisconnect())
        {
            QueueForDisconnect(socket);
        }

        if (eventIndex >= events.Length)
        {
            return 0;
        }

        events[eventIndex] = RingSocketEvent.Received(socket, result);
        return 1;
    }

    /// <summary>
    /// When disconnect is pending and sends have drained but recv is still in flight,
    /// close the write side (send FIN) so the pending recv can complete.
    /// This bridges the gap between Path A and Path B in RingSocket.Disconnect().
    /// </summary>
    private void TrySendFinForPendingDisconnect(RingSocket socket)
    {
        if (socket is { DisconnectPending: true, SendPending: false, RecvPending: true, ShutdownSent: false }
            && socket.SendDrained)
        {
            CloseSocketHandle(socket);
        }
    }

    private int HandleSendCompletion(RingSocket socket, int result, Span<RingSocketEvent> events, int eventIndex)
    {
        // Accounting lost track of the transport; guessing corrupts the stream
        Debug.Assert(socket.SendsInFlight > 0, "send completion with nothing in flight");
        if (socket.SendsInFlight == 0)
        {
            DisconnectImmediate(socket);
            return 0;
        }

        // May no longer be the current buffer
        var (posted, buffer) = socket.PopInFlight();

        if (socket.Aborting)
        {
            if (socket.IoRetired)
            {
                QueueForDisconnect(socket);
            }
            return 0;
        }

        if (result <= 0)
        {
            // The failed bytes stay counted as readable, so a drain could never finish
            DisconnectImmediate(socket);
            return 0;
        }

        // Short send: routine on send() semantics, absent on RIO. Recoverable only while nothing
        // else is outstanding; sends already posted beyond the gap cannot be repaired in order, and
        // a corrupted stream is worse than a dropped connection.
        if (result != posted)
        {
            if (socket.SendsInFlight > 0)
            {
                DisconnectImmediate(socket);
                return 0;
            }

            buffer.CommitShortSend(result);
            RetireIfDrained(socket, buffer);

            if (socket.Connected && socket.SendSource.SendableBytes > 0)
            {
                PostSend(socket);
            }

            if (socket.CheckDisconnect())
            {
                QueueForDisconnect(socket);
            }
            else
            {
                TrySendFinForPendingDisconnect(socket);
            }

            return eventIndex < events.Length ? EmitSent(socket, result, events, eventIndex) : 0;
        }

        buffer.CommitRead(result);
        RetireIfDrained(socket, buffer);

        // Continue sending if more data (even if DisconnectPending - drain the buffer).
        // SendableBytes, not ReadableBytes: the latter now also counts bytes still in flight.
        if (socket.Connected && socket.SendSource.SendableBytes > 0)
        {
            PostSend(socket);
        }

        if (socket.CheckDisconnect())
        {
            QueueForDisconnect(socket);
        }
        else
        {
            TrySendFinForPendingDisconnect(socket);
        }

        if (eventIndex < events.Length)
        {
            events[eventIndex] = RingSocketEvent.Sent(socket, result);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Returns the retiring send buffer once its last posted byte has sent; sends then come from the current buffer.
    /// </summary>
    private void RetireIfDrained(RingSocket socket, IORingBuffer buffer)
    {
        if (buffer == socket.RetiringSendBuffer && buffer.ReadableBytes == 0)
        {
            socket.RetiringSendBuffer = null;
            ReleaseSendBuffer(buffer);
        }
    }

    /// <summary>
    /// Index of the tier pool a buffer of this physical size came from, or -1 for the base pool.
    /// </summary>
    private int TierIndexOf(IORingBuffer buffer)
    {
        var size = _sendBufferPool.BufferSize;
        for (var i = 0; i < _sendTiers.Length; i++)
        {
            size *= 2;
            if (buffer.PhysicalSize == size)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns a send buffer to the pool it came from.
    /// </summary>
    private void ReleaseSendBuffer(IORingBuffer buffer)
    {
        var tier = TierIndexOf(buffer);
        if (tier < 0)
        {
            _sendBufferPool.Release(buffer);
        }
        else
        {
            _sendTiers[tier].Release(buffer);
        }
    }

    /// <summary>Slab capacity allocated across every tier pool, in bytes.</summary>
    private long TierCapacityBytes
    {
        get
        {
            long total = 0;
            for (var i = 0; i < _sendTiers.Length; i++)
            {
                total += _sendTiers[i].CapacityBytes;
            }

            return total;
        }
    }

    /// <summary>
    /// Takes a buffer from a tier pool, refusing before the pool would allocate a slab the budget cannot cover.
    /// </summary>
    private bool TryAcquireTier(int tier, out IORingBuffer? buffer)
    {
        var pool = _sendTiers[tier];
        if (!pool.HasFreeBuffer && TierCapacityBytes + pool.SlabBytes > SendBufferGrowthBudget)
        {
            buffer = null;
            return false;
        }

        return pool.TryAcquire(out buffer);
    }

    /// <summary>
    /// Moves the socket's queued-but-unsent bytes into the next larger send buffer. Bytes already
    /// handed to the transport stay in the old buffer, which is released once they complete.
    /// </summary>
    /// <returns>False if the socket is closing, already at the largest tier, or the growth budget
    /// cannot supply a buffer.</returns>
    public bool TryGrowSendBuffer(RingSocket socket)
    {
        if (!socket.Connected || socket.DisconnectPending)
        {
            return false;
        }

        var current = socket.SendBuffer;
        var tier = TierIndexOf(current) + 1;
        if (tier >= _sendTiers.Length)
        {
            return false;
        }

        // Only one retiring slot
        if (socket.RetiringSendBuffer != null && current.InFlightBytes != 0)
        {
            Debug.Assert(false, "growth with a retiring buffer found bytes in flight on the current buffer");
            return false;
        }

        if (!TryAcquireTier(tier, out var next))
        {
            _growthRefusals++;
            return false;
        }

        var unsent = current.SendableBytes;
        if (unsent > 0)
        {
            current.GetSendableSpan().CopyTo(next!.GetWriteSpan());
            next.CommitWrite(unsent);
            current.DiscardSendable();
        }

        if (current.ReadableBytes == 0)
        {
            ReleaseSendBuffer(current);
        }
        else
        {
            // The guard above refused the retiring case
            Debug.Assert(socket.RetiringSendBuffer == null, "growth would drop an unretired send buffer");
            socket.RetiringSendBuffer = current;
        }

        socket.SendBuffer = next!;
        return true;
    }

    /// <summary>
    /// Returns a drained socket to a base-size send buffer. No copy: nothing is readable.
    /// </summary>
    public bool TryShrinkSendBuffer(RingSocket socket)
    {
        if (!socket.Connected || socket.RetiringSendBuffer != null || socket.SendPending ||
            socket.SendBuffer.ReadableBytes != 0 || TierIndexOf(socket.SendBuffer) < 0)
        {
            return false;
        }

        if (!_sendBufferPool.TryAcquire(out var baseBuffer))
        {
            return false;
        }

        ReleaseSendBuffer(socket.SendBuffer);
        socket.SendBuffer = baseBuffer!;
        return true;
    }

    /// <summary>Snapshot returned by <see cref="Maintain"/>.</summary>
    /// <param name="BuffersReleased">Buffers returned to the OS by this call, summed across tiers.</param>
    /// <param name="GrowthRefusals">Budget refusals since the previous call, which resets the counter.</param>
    /// <param name="TierCapacityBytes">Slab capacity allocated across every tier pool, in bytes.</param>
    /// <param name="TierInUse">
    /// Buffers handed out, summed across tiers (a count; see <see cref="GetSendBufferTierStats"/> per tier).
    /// </param>
    /// <param name="TierRetainFloor">
    /// Retention floors summed across tiers, in buffers; same caveat as <paramref name="TierInUse"/>.
    /// </param>
    /// <param name="BaseBuffersReleased">Buffers the two base pools returned to the OS by this call.</param>
    /// <param name="BaseCapacityBytes">Slab capacity allocated across both base pools, in bytes.</param>
    public readonly record struct SendBufferMaintenance(
        int BuffersReleased,
        int GrowthRefusals,
        long TierCapacityBytes,
        int TierInUse,
        int TierRetainFloor,
        int BaseBuffersReleased,
        long BaseCapacityBytes
    );

    /// <summary>One growth tier's pool usage, in buffers of <paramref name="BufferSize"/> bytes.</summary>
    /// <param name="BufferSize">Physical size of every buffer in this tier.</param>
    /// <param name="Capacity">Buffers the tier's allocated slabs hold.</param>
    /// <param name="InUse">Buffers currently handed out.</param>
    /// <param name="RetainFloor">Peak usage the retention window is still holding capacity for.</param>
    public readonly record struct SendBufferTierStats(int BufferSize, int Capacity, int InUse, int RetainFloor);

    /// <summary>
    /// Usage of one growth tier.
    /// </summary>
    /// <param name="tier">Tier index, 0 being twice the base send buffer size.</param>
    public SendBufferTierStats GetSendBufferTierStats(int tier)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tier);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tier, _sendTiers.Length);

        var pool = _sendTiers[tier];
        return new SendBufferTierStats(pool.BufferSize, pool.TotalCapacity, pool.InUse, pool.RetainFloor);
    }

    /// <summary>
    /// Rotates every pool's usage window and trims at most one idle slab per pool; call once a minute from the ring thread.
    /// </summary>
    public SendBufferMaintenance Maintain()
    {
        var released = 0;
        var inUse = 0;
        var floor = 0;
        for (var i = 0; i < _sendTiers.Length; i++)
        {
            released += _sendTiers[i].Maintain();
            inUse += _sendTiers[i].InUse;
            floor += _sendTiers[i].RetainFloor;
        }

        // The base pools never trim below the slabs they were built with
        var baseReleased = _recvBufferPool.Maintain() + _sendBufferPool.Maintain();
        var baseCapacity = _recvBufferPool.CapacityBytes + _sendBufferPool.CapacityBytes;

        var refusals = _growthRefusals;
        _growthRefusals = 0;
        return new SendBufferMaintenance(
            released, refusals, TierCapacityBytes, inUse, floor, baseReleased, baseCapacity
        );
    }

    private static int EmitSent(RingSocket socket, int result, Span<RingSocketEvent> events, int eventIndex)
    {
        events[eventIndex] = RingSocketEvent.Sent(socket, result);
        return 1;
    }

    private void ProcessDisconnectQueue(Span<RingSocketEvent> events, ref int eventCount)
    {
        // A socket whose Disconnected event does not fit waits for the next pass rather than
        // losing the event.
        while (_disconnectQueue.Count > 0 && eventCount < events.Length)
        {
            var socket = _disconnectQueue.Dequeue();

            Debug.Assert(socket.IoRetired, "socket finalized with I/O outstanding");

            Unregister(socket);
            CloseHandle(socket);

            // The consumer may still read this pass's events from these buffers; the slot goes with
            // them so capacity is not freed ahead of the buffers
            _releasePending.Add(socket);
            ConnectedCount--;

            events[eventCount++] = RingSocketEvent.Disconnected(socket);
        }
    }

    private void ReleaseRetiredBuffers()
    {
        for (var i = 0; i < _releasePending.Count; i++)
        {
            var socket = _releasePending[i];
            _recvBufferPool.Release(socket.RecvBuffer);
            ReleaseSendBuffer(socket.SendBuffer);

            // Finalized mid-swap; never drained
            if (socket.RetiringSendBuffer != null)
            {
                ReleaseSendBuffer(socket.RetiringSendBuffer);
                socket.RetiringSendBuffer = null;
            }

            _sockets[socket.Id] = null;
        }

        _releasePending.Clear();
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
                CloseHandle(socket);
                Unregister(socket);
                _sockets[i] = null;
            }
        }

        // Dispose buffer pools
        _recvBufferPool.Dispose();
        _sendBufferPool.Dispose();

        for (var i = 0; i < _sendTiers.Length; i++)
        {
            _sendTiers[i].Dispose();
        }
    }
}
