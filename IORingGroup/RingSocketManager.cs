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

    // The pool preallocates its slab list from maxSlabs, so keep this modest; the budget, not this
    // cap, is what bounds growth.
    private const int TierMaxSlabs = 1024;

    // Tier buffers are handed out a slab at a time, so a slab is the budget's unit of granularity.
    // Cap one at 8 MiB (floor of 4 buffers) so a large base size cannot make the smallest usable
    // budget absurd.
    private const int TierSlabByteCap = 8 * 1024 * 1024;

    /// <summary>
    /// Hard ceiling on <see cref="MaxSendBufferSize"/>. Above this a tier's slab byte count stops
    /// fitting the arithmetic that bounds it, and no single connection has any business holding
    /// that much send buffer anyway.
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
    /// <param name="initialBufferSlabs">Initial buffer pool slabs (default 8).</param>
    /// <param name="maxBufferSlabs">
    /// Upper bound on base buffer pool slabs (default 32). Both base pools are additionally capped
    /// at the slabs <paramref name="maxSockets"/> sockets can occupy, one buffer each, so a small
    /// socket count silently lowers this and <paramref name="initialBufferSlabs"/> with it.
    /// </param>
    /// <param name="maxSendBufferSize">
    /// Largest send buffer a socket may grow to. 0 (default) means <paramref name="sendBufferSize"/>,
    /// which disables growth. Must be a power of two no smaller than <paramref name="sendBufferSize"/>
    /// and no larger than 256 MiB.
    /// </param>
    /// <param name="sendBufferGrowthBudget">
    /// Bytes of tier-pool capacity allowed across all tiers. 0 is allowed with tiers configured and
    /// simply refuses every growth; anything positive must be at least
    /// <see cref="MinimumSendBufferGrowthBudget"/> for that base size, since tier buffers are only
    /// ever allocated a slab at a time.
    /// </param>
    /// <param name="sendBufferRetentionWindows">
    /// Number of <see cref="Maintain"/> windows a tier pool's peak usage stays in force (default 15).
    /// </param>
    public RingSocketManager(
        IIORingGroup ring,
        int maxSockets,
        int recvBufferSize = 64 * 1024,
        int sendBufferSize = 256 * 1024,
        int initialBufferSlabs = 8,
        int maxBufferSlabs = 32,
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

        // Validated before anything is allocated: the pools map and register memory with the ring in
        // their constructors, and a throw from here would strand it.
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

        // A positive budget below one first-tier slab buys nothing, because tier buffers come a slab
        // at a time; refuse it rather than let every growth fail silently.
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

        // A ring whose table cannot hold this configuration fails much later, at an accept or a
        // growth, where the cause is invisible. The default table (maxConnections x 2) is too small
        // for the pools at any modest maxSockets, so check it while nothing has been allocated yet.
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

        // Create buffer pools
        // Estimate slab size based on max sockets
        var slabSize = BufferPoolSlabSize(maxSockets, maxBufferSlabs);
        var sendSlabSize = SendPoolSlabSize(slabSize);

        // Neither base pool can hand out more than one buffer per socket, so slabs past that are
        // memory and registration-table entries that nothing could ever acquire - and the table is
        // sized from the same bound. Clamp rather than throw: the defaults are deliberately generous
        // and a small maxSockets is not the caller's mistake.
        var recvSlabs = Math.Min(maxBufferSlabs, SlabsPerSocketSet(maxSockets, slabSize));
        var sendSlabs = Math.Min(maxBufferSlabs, SlabsPerSocketSet(maxSockets, sendSlabSize));

        // long: MaxSendBufferSize is capped at 256 MiB, but sendBufferSize is not, and an int
        // doubling past 1 GiB wraps negative and loops forever.
        var tierCount = 0;
        for (var size = (long)sendBufferSize * 2; size <= MaxSendBufferSize; size *= 2)
        {
            tierCount++;
        }

        _sendTiers = new IORingBufferPool[tierCount];

        // Each pool maps and registers its memory in its own constructor, so one that throws after
        // earlier pools were built would strand theirs: nothing outside this constructor has a
        // reference to the half-built manager to dispose it. Release what exists, then rethrow.
        var created = new List<IORingBufferPool>(tierCount + 2);
        try
        {
            _recvBufferPool = new IORingBufferPool(
                ring,
                slabSize: slabSize,
                bufferSize: recvBufferSize,
                initialSlabs: Math.Min(initialBufferSlabs, recvSlabs),
                maxSlabs: recvSlabs
            );
            created.Add(_recvBufferPool);

            _sendBufferPool = new IORingBufferPool(
                ring,
                slabSize: sendSlabSize, // Fewer send buffers typically needed
                bufferSize: sendBufferSize,
                initialSlabs: Math.Min(initialBufferSlabs / 2, sendSlabs),
                maxSlabs: sendSlabs
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
    /// Buffers per slab in the base recv pool. The send pool takes a quarter of this. Shared with
    /// <see cref="RequiredRegisteredBuffers"/> so the two cannot drift.
    /// </summary>
    private static int BufferPoolSlabSize(int maxSockets, int maxBufferSlabs) =>
        Math.Max(64, maxSockets / maxBufferSlabs);

    /// <summary>
    /// Buffers per slab in the base send pool: a quarter of the recv pool's, since a connection
    /// sends far less often than it receives. Shared with <see cref="RequiredRegisteredBuffers"/>
    /// so the two cannot drift.
    /// </summary>
    private static int SendPoolSlabSize(int recvSlabSize) => recvSlabSize / 4;

    /// <summary>
    /// Slabs of <paramref name="slabSize"/> buffers needed before every one of
    /// <paramref name="maxSockets"/> sockets holds one. A base pool can never use more than this,
    /// whatever <c>maxBufferSlabs</c> allows.
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
    /// Smallest growth budget that can hand out anything for this base send buffer size: one slab of
    /// the first tier. A budget between 1 and this is rejected by the constructor, because tier
    /// buffers are only ever allocated a slab at a time.
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
    /// Registration table size a ring needs for this configuration: everything both base pools can
    /// actually hand out, plus as many first-tier buffers as the growth budget can hold (larger
    /// tiers use fewer).
    /// </summary>
    /// <remarks>
    /// A socket holds at most one base recv buffer and one base send buffer at a time - a retiring
    /// buffer is a tier buffer's predecessor, and a shrink acquires its base buffer before releasing
    /// the tier one it replaces - so neither base pool can ever hand out more than
    /// <paramref name="maxSockets"/> buffers, rounded up to whole slabs. Its <c>maxSlabs</c> ceiling
    /// is only an upper bound on top of that, and at the library's own defaults it is the looser of
    /// the two by a wide margin; bounding by both is what lets
    /// <c>IORingGroup.Create(maxConnections: n)</c> and <c>new RingSocketManager(ring, n)</c> compose
    /// without an explicit table size.
    /// </remarks>
    public static int RequiredRegisteredBuffers(
        int maxSockets,
        int sendBufferSize,
        int maxSendBufferSize,
        long sendBufferGrowthBudget,
        int maxBufferSlabs = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSockets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSlabs);

        checked
        {
            var slabSize = BufferPoolSlabSize(maxSockets, maxBufferSlabs);
            var sendSlabSize = SendPoolSlabSize(slabSize);
            var recvMax = Math.Min(maxBufferSlabs * slabSize, RoundUpToSlabs(maxSockets, slabSize));
            var sendMax = Math.Min(maxBufferSlabs * sendSlabSize, RoundUpToSlabs(maxSockets, sendSlabSize));

            var tierHeadroom = maxSendBufferSize > sendBufferSize && sendBufferGrowthBudget > 0
                ? (int)(sendBufferGrowthBudget / (sendBufferSize * 2L))
                : 0;

            return recvMax + sendMax + tierHeadroom;
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

        // A retiring buffer is drained before anything is posted from the current one, so the byte
        // stream stays in order across a swap.
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
        // Every send completion has a matching entry; a completion without one means the in-flight
        // book-keeping has lost track of the transport, and there is no buffer or posted length to
        // reconcile the result against. Guessing at one routes into the short-send branch and
        // corrupts the stream, so drop the connection instead.
        Debug.Assert(socket.SendsInFlight > 0, "send completion with nothing in flight");
        if (socket.SendsInFlight == 0)
        {
            DisconnectImmediate(socket);
            return 0;
        }

        // The buffer the send was posted from, which may no longer be the socket's current one.
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
    /// Returns the retiring send buffer once the last byte posted from it has been sent. From here
    /// on sends come from the socket's current buffer.
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
    /// Takes a buffer from a tier pool, refusing when satisfying it would need a slab the budget
    /// cannot cover. The check comes first because the pool would otherwise allocate that slab
    /// itself.
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

        // While a buffer retires nothing is posted from the current one, so all of its bytes are
        // still copyable. Bytes in flight here would mean a second buffer the kernel is reading,
        // which the single retiring slot cannot hold: refuse rather than overwrite it.
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
            // Unreachable with a retiring buffer present: the guard above refused, and a current
            // buffer with nothing in flight has nothing readable once its sendable bytes moved.
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
    /// Buffers handed out, summed across tiers. A count, not a size: tiers hold buffers of different
    /// sizes, so this says how many are out, never how much memory they are. Use
    /// <see cref="GetSendBufferTierStats"/> for a single tier, or
    /// <paramref name="TierCapacityBytes"/> for bytes.
    /// </param>
    /// <param name="TierRetainFloor">
    /// Retention floors summed across tiers, in buffers. Same caveat as <paramref name="TierInUse"/>:
    /// the sum mixes tier sizes and is a trend indicator, not a capacity.
    /// </param>
    public readonly record struct SendBufferMaintenance(
        int BuffersReleased,
        int GrowthRefusals,
        long TierCapacityBytes,
        int TierInUse,
        int TierRetainFloor
    );

    /// <summary>One growth tier's pool usage, in buffers of <paramref name="BufferSize"/> bytes.</summary>
    /// <param name="BufferSize">Physical size of every buffer in this tier.</param>
    /// <param name="Capacity">Buffers the tier's allocated slabs hold.</param>
    /// <param name="InUse">Buffers currently handed out.</param>
    /// <param name="RetainFloor">Peak usage the retention window is still holding capacity for.</param>
    public readonly record struct SendBufferTierStats(int BufferSize, int Capacity, int InUse, int RetainFloor);

    /// <summary>
    /// Usage of a single growth tier, which <see cref="Maintain"/>'s aggregate cannot express
    /// because it sums buffers of different sizes.
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
    /// Rotates each tier pool's usage window and trims at most one idle slab per tier. Call once
    /// a minute from the ring thread. <see cref="SendBufferMaintenance.GrowthRefusals"/> counts the
    /// budget refusals since the previous call and is reset by it.
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

        var refusals = _growthRefusals;
        _growthRefusals = 0;
        return new SendBufferMaintenance(released, refusals, TierCapacityBytes, inUse, floor);
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

            // A socket can be finalized mid-swap, with the retiring buffer never drained
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
