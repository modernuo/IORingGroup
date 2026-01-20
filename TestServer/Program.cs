// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;
using System.Network.Windows;

namespace TestServer;

public enum ServerBackend
{
    IORing,     // Unified: Windows RIO / Linux io_uring / Darwin kqueue
    PollGroup   // Cross-platform: wepoll (Windows) / epoll (Linux) / kqueue (macOS)
}

/// <summary>
/// High-performance single-threaded echo server using unified IORingGroup API.
/// No async/await - tight event loop for maximum throughput.
/// </summary>
public class Program
{
    private const int Port = 5000;
    private const int BufferSize = 4096;
    private const int MaxClients = 8192;
    private const int ListenBacklog = 4096;

    private static volatile bool _running = true;
    private static bool _quietMode;
    private static int _benchmarkDuration; // 0 = run until clients disconnect

    // User data encoding for operations
    // Layout: [4 bits op][1 bit buffer][59 bits client index]
    private const ulong OpAccept = 0x1000_0000_0000_0000UL;
    private const ulong OpRecv = 0x2000_0000_0000_0000UL;
    private const ulong OpSend = 0x3000_0000_0000_0000UL;
    private const ulong OpMask = 0xF000_0000_0000_0000UL;
    private const ulong BufferBBit = 0x0800_0000_0000_0000UL;  // Bit 59: which buffer (0=A, 1=B)
    private const ulong IndexMask = 0x07FF_FFFF_FFFF_FFFFUL;   // Bottom 59 bits for client index

    // Number of accepts to keep queued
    private const int PendingAccepts = 128;

    // Client state for true zero-copy echo using IORingBuffer circular buffer
    // Single buffer per connection: recv writes to it (tail), send reads from it (head)
    private struct ClientState
    {
        public nint Socket;
        public int ConnId;              // RIO connection ID (-1 if not using RIO)
        public IORingBuffer? Buffer;    // Double-mapped circular buffer for zero-copy I/O
        public bool RecvPending;        // Is there an outstanding recv?
        public bool SendPending;        // Is there an outstanding send?
        public bool Active;
    }

    private static readonly ClientState[] _clients = new ClientState[MaxClients];
    private static IORingBufferPool? _bufferPool;

    // Benchmark stats
    private static long _totalMessages;
    private static long _totalBytes;
    private static long _lastReportedMessages;
    private static readonly Stopwatch _benchmarkStopwatch = new();
    private static bool _benchmarkStarted;
    private static bool _finalStatsPrinted;
    private static int _peakConnections;
    private static int _pendingAcceptCount;
    private static ulong _acceptSequence;  // Salt for accept userData to track unique operations

    // Syscall timing stats
    private static long _pollCallCount;
    private static long _pollTotalTicks;
    private static long _pollTotalCompletions;

    // Memory stats (captured at start)
    private static long _startAllocatedBytes;
    private static int _startGen0;
    private static int _startGen1;
    private static int _startGen2;

    public static void Main(string[] args)
    {
        var backend = ServerBackend.IORing;  // Default to unified IORing
        var benchmarkMode = false;
        var benchmarkDuration = 0;
        nint cpuAffinity = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--pollgroup", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-p", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--epoll", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.PollGroup;
            }
            else if (arg.Equals("--ioring", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--rio", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-r", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.IORing;
            }
            else if (arg.Equals("--benchmark", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-b", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkMode = true;
            }
            else if ((arg.Equals("--duration", StringComparison.OrdinalIgnoreCase) ||
                      arg.Equals("-d", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                benchmarkDuration = int.Parse(args[++i]);
            }
            else if (arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-q", StringComparison.OrdinalIgnoreCase))
            {
                // DEBUG: Force quiet mode off for debugging io_uring issue
                // _quietMode = true;
                _quietMode = false;
            }
            else if ((arg.Equals("--affinity", StringComparison.OrdinalIgnoreCase) ||
                      arg.Equals("-a", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                var affinityStr = args[++i];
                if (affinityStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    cpuAffinity = (nint)Convert.ToInt64(affinityStr[2..], 16);
                }
                else
                {
                    cpuAffinity = (nint)long.Parse(affinityStr);
                }
            }
        }

        _benchmarkDuration = benchmarkDuration;

        // Set CPU affinity if specified
        if (cpuAffinity != 0)
        {
            try
            {
                Process.GetCurrentProcess().ProcessorAffinity = cpuAffinity;
                Console.WriteLine($"CPU affinity set to: 0x{cpuAffinity:X}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to set CPU affinity: {ex.Message}");
            }
        }

        // Darwin/macOS: kqueue is synchronous like epoll, fall back to PollGroup
        if (backend == ServerBackend.IORing &&
            (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)))
        {
            Console.WriteLine("Darwin/BSD detected, falling back to PollGroup (kqueue)...");
            backend = ServerBackend.PollGroup;
        }

        Console.WriteLine("IORingGroup Unified Benchmark Server (Single-threaded)");
        Console.WriteLine($"Platform: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Backend: {backend}");
        Console.WriteLine($"Benchmark mode: {benchmarkMode}, Duration: {(_benchmarkDuration > 0 ? $"{_benchmarkDuration}s" : "unlimited")}, Quiet: {_quietMode}");
        Console.WriteLine($"Port: {Port}");
        Console.WriteLine("Usage: TestServer [--ioring|--rio|-r] [--pollgroup|-p] [--benchmark|-b] [--duration|-d <seconds>] [--quiet|-q] [--affinity|-a <mask>]");
        Console.WriteLine("  --ioring|--rio|-r: Windows RIO / Linux io_uring / Darwin kqueue (zero-copy echo)");
        Console.WriteLine("  --pollgroup|-p: Cross-platform poll-based (wepoll/epoll/kqueue)");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        if (backend == ServerBackend.IORing)
        {
            RunIORingServer(benchmarkMode);
        }
        else
        {
            RunPollGroupServer(benchmarkMode);
        }
    }

    #region Unified IORing Server

    private static void RunIORingServer(bool benchmarkMode)
    {
        try
        {
            // Create the appropriate ring based on platform
            IIORingGroup ring;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, use WindowsRIOGroup for full RIO support
                ring = new WindowsRIOGroup(
                    queueSize: MaxClients,
                    maxConnections: MaxClients
                );
                Console.WriteLine($"Windows RIO ring created: MaxConnections={MaxClients}");
            }
            else
            {
                // On Linux/macOS, use IORingGroup.Create() for native io_uring/kqueue/epoll
                ring = IORingGroup.Create(queueSize: MaxClients);
                var backendName = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "io_uring" : "kqueue";
                Console.WriteLine($"IORing created (native {backendName})");
            }

            using var ringGroup = ring;
            // Create buffer pool for zero-copy I/O
            // Each buffer is a double-mapped circular buffer
            _bufferPool = new IORingBufferPool(
                ring,
                slabSize: 256,           // 256 buffers per slab
                bufferSize: BufferSize,  // 4KB per buffer (page-aligned)
                initialSlabs: 4,         // Start with 1024 buffers
                maxSlabs: 64             // Max 16K buffers
            );
            Console.WriteLine($"Buffer pool created: {_bufferPool.TotalCapacity} buffers ({_bufferPool.BufferSize} bytes each)");

            // Create listener using the ring's native method
            var listener = ring.CreateListener("0.0.0.0", Port, ListenBacklog);
            if (listener == -1)
            {
                throw new InvalidOperationException("Failed to create listener");
            }

            Console.WriteLine($"Server listening on port {Port} (listener fd={listener})");

            try
            {
                RunServerLoop(ring, listener, benchmarkMode);
            }
            finally
            {
                ring.CloseListener(listener);

                // Cleanup all clients
                for (var i = 0; i < MaxClients; i++)
                {
                    if (_clients[i].Active)
                    {
                        CloseClient(ring, i);
                    }
                }

                // Dispose buffer pool
                _bufferPool.Dispose();
                _bufferPool = null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IORing server error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static void RunServerLoop(IIORingGroup ring, nint listener, bool benchmarkMode)
    {
        // Reset stats
        _totalMessages = 0;
        _totalBytes = 0;
        _lastReportedMessages = 0;
        _pendingAcceptCount = 0;
        _acceptSequence = 0;
        _benchmarkStarted = false;
        _finalStatsPrinted = false;
        _peakConnections = 0;
        _pollCallCount = 0;
        _pollTotalTicks = 0;
        _pollTotalCompletions = 0;
        _benchmarkStopwatch.Reset();

        // Pre-post multiple accepts
        for (var i = 0; i < PendingAccepts; i++)
        {
            ring.PrepareAccept(listener, 0, 0, OpAccept | (_acceptSequence++ & IndexMask));
            _pendingAcceptCount++;
        }

        Span<Completion> completions = stackalloc Completion[1024];
        var lastStatsMs = 0L;

        Console.WriteLine("Waiting for first client connection to start benchmark...");

        while (_running)
        {
            // Check duration limit
            if (_benchmarkDuration > 0 && _benchmarkStarted && _benchmarkStopwatch.Elapsed.TotalSeconds >= _benchmarkDuration)
            {
                Console.WriteLine($"Benchmark duration ({_benchmarkDuration}s) reached - stopping...");
                break;
            }

            ring.Submit();

            // Time the PeekCompletions syscall
            var pollStart = Stopwatch.GetTimestamp();
            var count = ring.PeekCompletions(completions);
            var pollEnd = Stopwatch.GetTimestamp();

            // Only count stats after first client connects
            if (_benchmarkStarted)
            {
                _pollCallCount++;
                _pollTotalTicks += pollEnd - pollStart;
                _pollTotalCompletions += count;
            }

            for (var i = 0; i < count; i++)
            {
                ProcessCompletion(ring, listener, ref completions[i], benchmarkMode);
            }

            ring.AdvanceCompletionQueue(count);

            // Print stats every second in benchmark mode
            if (benchmarkMode)
            {
                var elapsedMs = _benchmarkStopwatch.ElapsedMilliseconds;
                if (elapsedMs - lastStatsMs >= 1000)
                {
                    PrintStats();
                    lastStatsMs = elapsedMs;
                }
            }

            // Yield when no work
            if (count == 0)
            {
                Thread.Sleep(1);
            }
        }

        _benchmarkStopwatch.Stop();
        if (!_finalStatsPrinted)
        {
            _finalStatsPrinted = true;
            PrintFinalStats();
        }
    }

    private static void ProcessCompletion(IIORingGroup ring, nint listener, ref Completion cqe, bool benchmarkMode)
    {
        var op = cqe.UserData & OpMask;
        var index = (int)(cqe.UserData & IndexMask);

        switch (op)
        {
            case OpAccept:
            {
                HandleAccept(ring, listener, cqe.Result, benchmarkMode);
                break;
            }
            case OpRecv:
            {
                HandleRecv(ring, index, cqe.Result, benchmarkMode);
                break;
            }
            case OpSend:
            {
                HandleSend(ring, index, cqe.Result, benchmarkMode);
                break;
            }
        }
    }

    private static void HandleAccept(IIORingGroup ring, nint listener, int result, bool benchmarkMode)
    {
        _pendingAcceptCount--;

        // EAGAIN (-11) means no connection pending - just re-queue accept
        // This happens with epoll/kqueue sync implementations
        if (result == -11) // -EAGAIN
        {
            goto ReplenishAccepts;
        }

        if (result >= 0)
        {
            // Result is socket handle
            var clientSocket = (nint)result;
            var clientIndex = FindFreeSlot();

            if (clientIndex >= 0)
            {
                // Configure the accepted socket
                ring.ConfigureSocket(clientSocket);

                ref var client = ref _clients[clientIndex];
                client.Socket = clientSocket;
                client.Active = true;
                client.ConnId = -1;
                client.RecvPending = false;
                client.SendPending = false;

                // Acquire buffer from pool (already registered with ring)
                if (_bufferPool == null || !_bufferPool.TryAcquire(out var buffer) || buffer == null)
                {
                    Console.WriteLine("Buffer pool exhausted!");
                    ring.CloseSocket(clientSocket);
                    client = default;
                    goto ReplenishAccepts;
                }

                client.Buffer = buffer;

                // Register socket to get connection ID
                var connId = ring.RegisterSocket(clientSocket);
                if (connId < 0)
                {
                    Console.WriteLine($"Socket registration failed for socket {clientSocket}");
                    _bufferPool.Release(buffer);
                    ring.CloseSocket(clientSocket);
                    client = default;
                    goto ReplenishAccepts;
                }
                client.ConnId = connId;

                // Start timing on first connection
                if (!_benchmarkStarted)
                {
                    _benchmarkStarted = true;
                    CaptureStartMemoryStats();
                    _benchmarkStopwatch.Start();
                    if (benchmarkMode)
                    {
                        Console.WriteLine("First client connected - benchmark started!");
                    }
                }

                // Track peak connections
                var activeCount = CountActiveClients();
                if (activeCount > _peakConnections)
                {
                    _peakConnections = activeCount;
                }

                if (!benchmarkMode && !_quietMode)
                {
                    Console.WriteLine($"Client {clientIndex} connected (socket={clientSocket}, connId={client.ConnId})");
                }

                // Post initial receive - recv into buffer at tail (write position)
                var availableSpace = client.Buffer.WritableBytes;
                if (availableSpace > 0)
                {
                    ring.PrepareRecvBuffer(
                        client.ConnId,
                        client.Buffer.BufferId,
                        client.Buffer.WriteOffset,  // Offset = current write position (tail)
                        availableSpace,             // Length = available space
                        OpRecv | (uint)clientIndex
                    );
                    client.RecvPending = true;
                }
            }
            else
            {
                Console.WriteLine($"No free slot for client, closing socket {clientSocket}");
                ring.CloseSocket(clientSocket);
            }
        }
        else if (result != -4) // EINTR
        {
            Console.WriteLine($"Accept error: {result}");
        }

        ReplenishAccepts:
        // Replenish accepts
        while (_pendingAcceptCount < PendingAccepts)
        {
            ring.PrepareAccept(listener, 0, 0, OpAccept | (_acceptSequence++ & IndexMask));
            _pendingAcceptCount++;
        }
    }

    private static void HandleRecv(IIORingGroup ring, int index, int result, bool benchmarkMode)
    {
        ref var client = ref _clients[index];
        client.RecvPending = false;

        if (result <= 0)
        {
            CloseClient(ring, index);
            return;
        }

        // Note: Don't count bytes here - count once on send completion to match PollGroup

        if (client.Buffer == null)
        {
            return;
        }

        // Commit the write - data is now available for sending
        client.Buffer.CommitWrite(result);

        // If we have data to send and no send is pending, start sending
        if (!client.SendPending)
        {
            var dataToSend = client.Buffer.ReadableBytes;
            if (dataToSend > 0)
            {
                ring.PrepareSendBuffer(
                    client.ConnId,
                    client.Buffer.BufferId,
                    client.Buffer.ReadOffset,   // Offset = current read position (head)
                    dataToSend,                 // Length = data available
                    OpSend | (uint)index
                );
                client.SendPending = true;
            }
        }

        // If we have space for more recv and no recv is pending, start receiving
        if (!client.RecvPending)
        {
            var spaceForRecv = client.Buffer.WritableBytes;
            if (spaceForRecv > 0)
            {
                ring.PrepareRecvBuffer(
                    client.ConnId,
                    client.Buffer.BufferId,
                    client.Buffer.WriteOffset,  // Offset = current write position (tail)
                    spaceForRecv,               // Length = space available
                    OpRecv | (uint)index
                );
                client.RecvPending = true;
            }
        }
    }

    private static void HandleSend(IIORingGroup ring, int index, int result, bool benchmarkMode)
    {
        ref var client = ref _clients[index];
        client.SendPending = false;

        if (result <= 0)
        {
            CloseClient(ring, index);
            return;
        }

        _totalBytes += result;
        _totalMessages++;

        if (client.Buffer == null)
        {
            return;
        }

        // Commit the read - data has been sent, free up space
        client.Buffer.CommitRead(result);

        // If we have more data to send, keep sending
        if (!client.SendPending)
        {
            var dataToSend = client.Buffer.ReadableBytes;
            if (dataToSend > 0)
            {
                ring.PrepareSendBuffer(
                    client.ConnId,
                    client.Buffer.BufferId,
                    client.Buffer.ReadOffset,   // Offset = current read position (head)
                    dataToSend,                 // Length = data available
                    OpSend | (uint)index
                );
                client.SendPending = true;
            }
        }

        // If we have space for recv and no recv is pending, start receiving
        if (!client.RecvPending)
        {
            var spaceForRecv = client.Buffer.WritableBytes;
            if (spaceForRecv > 0)
            {
                ring.PrepareRecvBuffer(
                    client.ConnId,
                    client.Buffer.BufferId,
                    client.Buffer.WriteOffset,  // Offset = current write position (tail)
                    spaceForRecv,               // Length = space available
                    OpRecv | (uint)index
                );
                client.RecvPending = true;
            }
        }
    }

    private static void CloseClient(IIORingGroup ring, int index)
    {
        ref var client = ref _clients[index];
        if (!client.Active)
        {
            return;
        }

        if (!_quietMode)
        {
            Console.WriteLine($"Client {index} disconnected");
        }

        // Unregister socket
        if (client.ConnId >= 0)
        {
            ring.UnregisterSocket(client.ConnId);
        }

        // Release buffer back to pool (buffer stays registered with ring)
        if (client.Buffer != null && _bufferPool != null)
        {
            _bufferPool.Release(client.Buffer);
        }

        ring.CloseSocket(client.Socket);
        client = default;

        // Stop benchmark when all clients disconnect
        if (_benchmarkStarted && CountActiveClients() == 0 && !_finalStatsPrinted)
        {
            _benchmarkStopwatch.Stop();
            Console.WriteLine("All clients disconnected - benchmark complete!");
            _finalStatsPrinted = true;
            PrintFinalStats();
            _running = false;
        }
    }

    private static int FindFreeSlot()
    {
        for (var i = 0; i < MaxClients; i++)
            if (!_clients[i].Active)
            {
                return i;
            }

        return -1;
    }

    private static int CountActiveClients()
    {
        var count = 0;
        for (var i = 0; i < MaxClients; i++)
            if (_clients[i].Active)
            {
                count++;
            }

        return count;
    }

    #endregion

    #region PollGroup Server

    private class PollClientState : IDisposable
    {
        public Socket? Socket;
        public readonly IORingBuffer Buffer;  // Circular buffer for zero-copy recv/send
        public GCHandle Handle;

        public PollClientState()
        {
            // Use IORingBuffer for circular buffer semantics
            // This allows concurrent recv (write to tail) and send (read from head)
            // without the deadlock from separate buffers
            Buffer = IORingBuffer.Create(BufferSize);
        }

        public void Reset()
        {
            Socket = null;
            Buffer.Reset();  // Reset head/tail to 0
            // Note: Don't free Handle here - it's managed by acquire/release
        }

        public void Dispose()
        {
            Buffer.Dispose();
        }
    }

    private static readonly PollClientState?[] _pollClients = new PollClientState[MaxClients];

    // Pool of pre-allocated PollClientState objects to avoid per-connection allocations
    private static readonly Stack<PollClientState> _pollClientPool = new(MaxClients);
    private static bool _pollPoolInitialized;

    // Cached LingerOption to avoid allocation per-accept
    private static readonly LingerOption _lingerOption = new(true, 0);

    private static PollClientState? AcquirePollClientState()
    {
        lock (_pollClientPool)
        {
            return _pollClientPool.Count > 0 ? _pollClientPool.Pop() : null;
        }
    }

    private static void ReleasePollClientState(PollClientState state)
    {
        state.Reset();
        lock (_pollClientPool)
        {
            _pollClientPool.Push(state);
        }
    }

    private static void InitializePollPool(int size)
    {
        if (_pollPoolInitialized)
        {
            return;
        }

        Console.WriteLine($"Pre-allocating {size} PollClientState objects with IORingBuffer...");
        for (var i = 0; i < size; i++)
        {
            _pollClientPool.Push(new PollClientState());
        }
        _pollPoolInitialized = true;
        // IORingBuffer: PhysicalSize = BufferSize, VirtualSize = 2x (double-mapped)
        Console.WriteLine($"PollClientState pool initialized: {_pollClientPool.Count} objects ({size * BufferSize / 1024 / 1024} MB physical)");
    }

    private static void RunPollGroupServer(bool benchmarkMode)
    {
        // Initialize pool before anything else to avoid allocations during benchmark
        InitializePollPool(MaxClients);

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        listener.Bind(new IPEndPoint(IPAddress.Any, Port));
        listener.NoDelay = true;
        listener.Listen(ListenBacklog);
        listener.Blocking = false;

        Console.WriteLine($"PollGroup server listening on port {Port}");

        using var pollGroup = PollGroup.Create();

        var listenerState = new PollClientState { Socket = listener };
        listenerState.Handle = GCHandle.Alloc(listenerState, GCHandleType.Normal);
        pollGroup.Add(listener, listenerState.Handle);

        // Reset stats
        _totalMessages = 0;
        _totalBytes = 0;
        _lastReportedMessages = 0;
        _benchmarkStarted = false;
        _finalStatsPrinted = false;
        _peakConnections = 0;
        _pollCallCount = 0;
        _pollTotalTicks = 0;
        _pollTotalCompletions = 0;
        _benchmarkStopwatch.Reset();

        var handles = new GCHandle[1024];
        var lastStatsMs = 0L;

        // Local function to close a poll client (captures pollGroup from enclosing scope)
        void ClosePollClient(PollClientState state)
        {
            for (var i = 0; i < MaxClients; i++)
            {
                if (_pollClients[i] == state)
                {
                    if (!benchmarkMode && !_quietMode)
                    {
                        Console.WriteLine($"[PollGroup] Client {i} disconnected");
                    }

                    pollGroup.Remove(state.Socket!, state.Handle);
                    state.Handle.Free();
                    state.Socket!.Close();
                    _pollClients[i] = null;

                    // Return to pool for reuse (avoids allocation on next accept)
                    ReleasePollClientState(state);

                    // Stop benchmark when all clients disconnect
                    if (_benchmarkStarted && CountActivePollClients() == 0 && !_finalStatsPrinted)
                    {
                        _benchmarkStopwatch.Stop();
                        Console.WriteLine("[PollGroup] All clients disconnected - benchmark complete!");
                        _finalStatsPrinted = true;
                        PrintFinalStats();
                        _running = false;
                    }
                    break;
                }
            }
        }

        // Local function to handle poll client (captures pollGroup from enclosing scope)
        // Uses circular buffer (IORingBuffer) to allow concurrent recv/send without deadlock
        void HandlePollClient(PollClientState state)
        {
            var socket = state.Socket!;
            var buffer = state.Buffer;

            try
            {
                // SEND: If there's data to send (ReadableBytes > 0) and socket is writable
                // Reads from head of circular buffer
                if (buffer.ReadableBytes > 0 && socket.Poll(0, SelectMode.SelectWrite))
                {
                    var dataToSend = buffer.GetReadSpan();
                    var sent = socket.Send(dataToSend);
                    if (sent > 0)
                    {
                        buffer.CommitRead(sent);
                        _totalMessages++;
                        _totalBytes += sent;
                    }
                }

                // RECV: If there's space to receive (WritableBytes > 0) and socket is readable
                // Writes to tail of circular buffer - INDEPENDENT of send!
                // This is the key fix: we can always receive as long as there's buffer space
                if (buffer.WritableBytes > 0 && socket.Poll(0, SelectMode.SelectRead))
                {
                    var recvSpan = buffer.GetWriteSpan();
                    var received = socket.Receive(recvSpan);
                    if (received > 0)
                    {
                        buffer.CommitWrite(received);
                        // Data is now in buffer, will be sent on next poll cycle
                    }
                    else if (received == 0)
                    {
                        ClosePollClient(state);
                    }
                }
            }
            catch (SocketException)
            {
                ClosePollClient(state);
            }
        }

        Console.WriteLine("Waiting for first client connection to start benchmark...");

        while (_running)
        {
            // Check duration limit
            if (_benchmarkDuration > 0 && _benchmarkStarted && _benchmarkStopwatch.Elapsed.TotalSeconds >= _benchmarkDuration)
            {
                Console.WriteLine($"[PollGroup] Benchmark duration ({_benchmarkDuration}s) reached - stopping...");
                break;
            }

            // Time the Poll syscall
            var pollStart = Stopwatch.GetTimestamp();
            var count = pollGroup.Poll(handles);
            var pollEnd = Stopwatch.GetTimestamp();

            // Only count stats after first client connects
            if (_benchmarkStarted)
            {
                _pollCallCount++;
                _pollTotalTicks += pollEnd - pollStart;
                _pollTotalCompletions += count;
            }

            // Handle sockets with events
            for (var i = 0; i < count; i++)
            {
                var state = (PollClientState)handles[i].Target!;

                if (state.Socket != listener)
                {
                    HandlePollClient(state);
                    continue;
                }

                // Accept new connections
                while (true)
                {
                    try
                    {
                        var client = listener.Accept();
                        client.Blocking = false;
                        client.NoDelay = true;
                        client.LingerState = _lingerOption;

                        var clientIndex = FindFreePollSlot();
                        if (clientIndex >= 0)
                        {
                            // Acquire from pool instead of allocating new
                            var clientState = AcquirePollClientState();
                            if (clientState == null)
                            {
                                Console.WriteLine("[PollGroup] Pool exhausted!");
                                client.Close();
                                continue;
                            }

                            clientState.Socket = client;
                            clientState.Handle = GCHandle.Alloc(clientState, GCHandleType.Normal);
                            _pollClients[clientIndex] = clientState;
                            pollGroup.Add(client, clientState.Handle);

                            // Start timing on first connection
                            if (!_benchmarkStarted)
                            {
                                _benchmarkStarted = true;
                                CaptureStartMemoryStats();
                                _benchmarkStopwatch.Start();
                                if (benchmarkMode)
                                {
                                    Console.WriteLine("[PollGroup] First client connected - benchmark started!");
                                }
                            }

                            var activeCount = CountActivePollClients();
                            if (activeCount > _peakConnections)
                            {
                                _peakConnections = activeCount;
                            }

                            if (!benchmarkMode && !_quietMode)
                            {
                                Console.WriteLine($"[PollGroup] Client {clientIndex} connected");
                            }
                        }
                        else
                        {
                            client.Close();
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
                    {
                        break;
                    }
                }
            }

            // Flush pending sends for ALL clients, not just those with events
            // This prevents deadlock when Poll() returns no events but we have buffered data
            for (var i = 0; i < MaxClients; i++)
            {
                var state = _pollClients[i];
                if (state?.Socket != null && state.Buffer.ReadableBytes > 0)
                {
                    try
                    {
                        if (state.Socket.Poll(0, SelectMode.SelectWrite))
                        {
                            var dataToSend = state.Buffer.GetReadSpan();
                            var sent = state.Socket.Send(dataToSend);
                            if (sent > 0)
                            {
                                state.Buffer.CommitRead(sent);
                                _totalMessages++;
                                _totalBytes += sent;
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        ClosePollClient(state);
                    }
                }
            }

            // Print stats every second in benchmark mode
            if (benchmarkMode)
            {
                var elapsedMs = _benchmarkStopwatch.ElapsedMilliseconds;
                if (elapsedMs - lastStatsMs >= 1000)
                {
                    PrintStats();
                    lastStatsMs = elapsedMs;
                }
            }
        }

        _benchmarkStopwatch.Stop();
        if (!_finalStatsPrinted)
        {
            _finalStatsPrinted = true;
            PrintFinalStats();
        }

        // Cleanup
        listenerState.Handle.Free();
        for (var i = 0; i < MaxClients; i++)
        {
            var state = _pollClients[i];
            if (state != null)
            {
                pollGroup.Remove(state.Socket, state.Handle);
                state.Handle.Free();
                state.Socket.Close();
                _pollClients[i] = null;
            }
        }
    }

    private static int FindFreePollSlot()
    {
        for (var i = 0; i < MaxClients; i++)
            if (_pollClients[i] == null)
            {
                return i;
            }

        return -1;
    }

    private static int CountActivePollClients()
    {
        var count = 0;
        for (var i = 0; i < MaxClients; i++)
            if (_pollClients[i] != null)
            {
                count++;
            }

        return count;
    }

    #endregion

    #region Stats

    private static void CaptureStartMemoryStats()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _startAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        _startGen0 = GC.CollectionCount(0);
        _startGen1 = GC.CollectionCount(1);
        _startGen2 = GC.CollectionCount(2);
    }

    private static void PrintStats()
    {
        if (!_quietMode)
        {
            var elapsed = _benchmarkStopwatch.Elapsed.TotalSeconds;
            var messagesPerSec = elapsed > 0 ? _totalMessages / elapsed : 0;
            var newMessages = _totalMessages - _lastReportedMessages;
            _lastReportedMessages = _totalMessages;
            Console.WriteLine($"[{elapsed:F1}s] {_totalMessages:N0} messages ({messagesPerSec:N0}/s, +{newMessages:N0})");
        }
    }

    private static void PrintFinalStats()
    {
        var elapsed = _benchmarkStopwatch.Elapsed.TotalSeconds;
        var messages = _totalMessages;
        var bytes = _totalBytes;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - _startAllocatedBytes;
        var gen0 = GC.CollectionCount(0) - _startGen0;
        var gen1 = GC.CollectionCount(1) - _startGen1;
        var gen2 = GC.CollectionCount(2) - _startGen2;

        Console.WriteLine();
        Console.WriteLine("=== Final Statistics ===");
        Console.WriteLine($"Duration: {elapsed:N2} seconds");
        Console.WriteLine($"Messages: {messages:N0}");
        Console.WriteLine($"Bytes: {bytes:N0}");
        Console.WriteLine($"Messages/sec: {(elapsed > 0 ? messages / elapsed : 0):N0}");
        Console.WriteLine($"Throughput: {(elapsed > 0 ? bytes / elapsed / 1024 / 1024 : 0):N2} MB/s");
        Console.WriteLine();
        Console.WriteLine("=== Memory Statistics ===");
        Console.WriteLine($"Allocated bytes: {allocatedBytes:N0} ({allocatedBytes / 1024.0 / 1024.0:N2} MB)");
        Console.WriteLine($"GC collections: Gen0={gen0}, Gen1={gen1}, Gen2={gen2}");
        if (messages > 0)
        {
            Console.WriteLine($"Bytes allocated per message: {(double)allocatedBytes / messages:N2}");
        }

        Console.WriteLine($"Peak connections: {_peakConnections}");

        // Syscall timing stats
        Console.WriteLine();
        Console.WriteLine("=== I/O Syscall Statistics ===");
        Console.WriteLine($"Total calls: {_pollCallCount:N0}");
        Console.WriteLine($"Total completions: {_pollTotalCompletions:N0}");
        if (_pollCallCount > 0)
        {
            var avgCompletionsPerCall = (double)_pollTotalCompletions / _pollCallCount;
            var totalTimeMs = _pollTotalTicks * 1000.0 / Stopwatch.Frequency;
            var avgTimePerCallUs = totalTimeMs * 1000.0 / _pollCallCount;
            var callsPerSec = elapsed > 0 ? _pollCallCount / elapsed : 0;

            Console.WriteLine($"Avg completions/call: {avgCompletionsPerCall:N2}");
            Console.WriteLine($"Total time in syscalls: {totalTimeMs:N2} ms");
            Console.WriteLine($"Avg time/call: {avgTimePerCallUs:N2} µs");
            Console.WriteLine($"Calls/sec: {callsPerSec:N0}");

            if (elapsed > 0)
            {
                var syscallOverheadPct = (totalTimeMs / 1000.0) / elapsed * 100.0;
                Console.WriteLine($"Syscall overhead: {syscallOverheadPct:N1}% of total time");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== RIO Ring Info ===");
        Console.WriteLine($"Max connections: {MaxClients}");

        if (_bufferPool != null)
        {
            var stats = _bufferPool.GetStats();
            Console.WriteLine();
            Console.WriteLine("=== Buffer Pool (Zero-Copy Circular Buffers) ===");
            Console.WriteLine($"Slabs: {stats.CurrentSlabs}/{stats.MaxSlabs}");
            Console.WriteLine($"Buffers: {stats.InUseCount}/{stats.TotalCapacity} ({stats.UtilizationPercent:F1}% utilization)");
            Console.WriteLine($"Buffer size: {stats.BufferSize} bytes (double-mapped)");
            Console.WriteLine($"Committed memory: {stats.CommittedBytes / 1024.0 / 1024.0:F2} MB");
            if (stats.HasFallbacks)
            {
                Console.WriteLine($"Fallback allocations: {stats.TotalFallbackAllocations} total, {stats.PeakFallbackCount} peak");
            }
        }
    }

    #endregion
}
