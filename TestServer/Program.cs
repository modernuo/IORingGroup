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
    private const int PipeBufferSize = 64 * 1024;  // 64KB send pipe per connection

    private static volatile bool _running = true;
    private static bool _quietMode;
    private static bool _usePipeMode;  // Use Pipe with external buffer for zero-copy sends (Windows RIO only)
    private static int _benchmarkDuration; // 0 = run until clients disconnect

    // User data encoding for operations
    private const ulong OpAccept = 0x1000_0000_0000_0000UL;
    private const ulong OpRecv = 0x2000_0000_0000_0000UL;
    private const ulong OpSend = 0x3000_0000_0000_0000UL;
    private const ulong OpMask = 0xF000_0000_0000_0000UL;
    private const ulong IndexMask = 0x0FFF_FFFF_FFFF_FFFFUL;

    // Number of accepts to keep queued
    private const int PendingAccepts = 64;

    // Unified client state (works with both registered and non-registered buffers)
    private struct ClientState
    {
        public nint Socket;
        public int ConnId;           // RIO connection ID (-1 if not using registered buffers)
        public nint RecvBuffer;      // Pointer to recv buffer
        public nint SendBuffer;      // Pointer to send buffer
        public GCHandle RecvHandle;  // For pinned buffers (non-RIO mode)
        public GCHandle SendHandle;
        public bool Active;

        // Pipe mode fields (for zero-copy sends from user-owned memory, Windows RIO only)
        public Pipe? SendPipe;
        public int ExternalBufferId;
    }

    private static readonly ClientState[] _clients = new ClientState[MaxClients];

    // Benchmark stats
    private static long _totalMessages;
    private static long _totalBytes;
    private static long _lastReportedMessages;
    private static readonly Stopwatch _benchmarkStopwatch = new();
    private static bool _benchmarkStarted;
    private static bool _finalStatsPrinted;
    private static int _peakConnections;
    private static int _pendingAcceptCount;

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
        var useRegisteredBuffers = false;  // Windows RIO registered buffer mode

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
                     arg.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("--iouring", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-u", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.IORing;
            }
            else if (arg.Equals("--registered", StringComparison.OrdinalIgnoreCase))
            {
                useRegisteredBuffers = true;
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
                _quietMode = true;
            }
            else if (arg.Equals("--pipe", StringComparison.OrdinalIgnoreCase))
            {
                _usePipeMode = true;
                useRegisteredBuffers = true;  // Pipe mode requires registered buffers
            }
            else if ((arg.Equals("--affinity", StringComparison.OrdinalIgnoreCase) ||
                      arg.Equals("-a", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                var affinityStr = args[++i];
                if (affinityStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    cpuAffinity = (nint)Convert.ToInt64(affinityStr[2..], 16);
                else
                    cpuAffinity = (nint)long.Parse(affinityStr);
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

        Console.WriteLine("IORingGroup Unified Benchmark Server (Single-threaded)");
        Console.WriteLine($"Platform: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Backend: {backend}");
        Console.WriteLine($"Benchmark mode: {benchmarkMode}, Duration: {(_benchmarkDuration > 0 ? $"{_benchmarkDuration}s" : "unlimited")}, Quiet: {_quietMode}");
        Console.WriteLine($"Port: {Port}");
        Console.WriteLine("Usage: TestServer [--ioring|--rio|-r|--iouring|-u] [--pollgroup|-p|--epoll] [--benchmark|-b] [--duration|-d <seconds>] [--quiet|-q] [--registered] [--pipe] [--affinity|-a <mask>]");
        Console.WriteLine("  --ioring/--rio/--iouring: Unified IORing API (Windows RIO / Linux io_uring / Darwin kqueue)");
        Console.WriteLine("  --pollgroup/--epoll: Cross-platform poll-based (wepoll/epoll/kqueue)");
        Console.WriteLine("  --registered: Use RIO registered buffers (Windows only, higher performance)");
        Console.WriteLine("  --pipe: Use Pipe circular buffer with zero-copy sends (Windows RIO only, implies --registered)");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        if (backend == ServerBackend.IORing)
        {
            RunIORingServer(benchmarkMode, useRegisteredBuffers);
        }
        else
        {
            RunPollGroupServer(benchmarkMode);
        }
    }

    #region Unified IORing Server

    private static void RunIORingServer(bool benchmarkMode, bool useRegisteredBuffers)
    {
        try
        {
            // Create the appropriate ring based on platform
            IIORingGroup ring;
            WindowsRIOGroup? rioRing = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, use WindowsRIOGroup for full RIO support
                rioRing = new WindowsRIOGroup(
                    queueSize: 16384,
                    maxConnections: MaxClients,
                    recvBufferSize: BufferSize,
                    sendBufferSize: BufferSize
                );
                ring = rioRing;
                Console.WriteLine($"Windows RIO ring created: MaxConnections={rioRing.MaxConnections}, Buffers={rioRing.RecvBufferSize}");
                Console.WriteLine($"Using registered buffers: {useRegisteredBuffers}");
            }
            else
            {
                // On Linux/macOS, use IORingGroup.Create() for native io_uring/kqueue
                ring = IORingGroup.Create(queueSize: 16384);
                useRegisteredBuffers = false;  // Not supported on non-Windows
                Console.WriteLine($"IORing created (native {(RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "io_uring" : "kqueue")})");
            }

            using (ring)
            {
                // Create listener using the ring's native method
                var listener = ring.CreateListener("0.0.0.0", Port, ListenBacklog);
                if (listener == -1)
                {
                    throw new InvalidOperationException("Failed to create listener");
                }

                Console.WriteLine($"Server listening on port {Port}");

                try
                {
                    RunServerLoop(ring, rioRing, listener, benchmarkMode, useRegisteredBuffers);
                }
                finally
                {
                    ring.CloseListener(listener);

                    // Cleanup all clients
                    for (var i = 0; i < MaxClients; i++)
                    {
                        if (_clients[i].Active)
                        {
                            CloseClient(ring, rioRing, i, useRegisteredBuffers);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IORing server error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static void RunServerLoop(IIORingGroup ring, WindowsRIOGroup? rioRing, nint listener, bool benchmarkMode, bool useRegisteredBuffers)
    {
        // Reset stats
        _totalMessages = 0;
        _totalBytes = 0;
        _lastReportedMessages = 0;
        _pendingAcceptCount = 0;
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
            ring.PrepareAccept(listener, 0, 0, OpAccept);
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

            _pollCallCount++;
            _pollTotalTicks += pollEnd - pollStart;
            _pollTotalCompletions += count;

            for (var i = 0; i < count; i++)
            {
                ProcessCompletion(ring, rioRing, listener, ref completions[i], benchmarkMode, useRegisteredBuffers);
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
            PrintFinalStats(rioRing);
        }
    }

    private static void ProcessCompletion(IIORingGroup ring, WindowsRIOGroup? rioRing, nint listener, ref Completion cqe, bool benchmarkMode, bool useRegisteredBuffers)
    {
        var op = cqe.UserData & OpMask;
        var index = (int)(cqe.UserData & IndexMask);

        switch (op)
        {
            case OpAccept:
                HandleAccept(ring, rioRing, listener, cqe.Result, benchmarkMode, useRegisteredBuffers);
                break;
            case OpRecv:
                HandleRecv(ring, rioRing, index, cqe.Result, benchmarkMode, useRegisteredBuffers);
                break;
            case OpSend:
                HandleSend(ring, rioRing, index, cqe.Result, benchmarkMode, useRegisteredBuffers);
                break;
        }
    }

    private static void HandleAccept(IIORingGroup ring, WindowsRIOGroup? rioRing, nint listener, int result, bool benchmarkMode, bool useRegisteredBuffers)
    {
        _pendingAcceptCount--;

        if (result >= 0)
        {
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
                client.ExternalBufferId = -1;
                client.SendPipe = null;

                if (useRegisteredBuffers && rioRing != null)
                {
                    // Register socket with RIO for async completion queue
                    var connId = rioRing.RegisterSocket(clientSocket);
                    if (connId >= 0)
                    {
                        client.ConnId = connId;

                        // In pipe mode, create a Pipe and register it as an external buffer
                        if (_usePipeMode)
                        {
                            var pipe = new Pipe(PipeBufferSize);
                            var extBufId = rioRing.RegisterExternalBuffer(pipe.GetBufferPointer(), pipe.GetVirtualSize());
                            if (extBufId >= 0)
                            {
                                client.SendPipe = pipe;
                                client.ExternalBufferId = extBufId;
                            }
                            else
                            {
                                pipe.Dispose();
                            }
                        }
                    }
                    else
                    {
                        // Fall back to pinned buffers
                        useRegisteredBuffers = false;
                    }
                }

                // Always use pinned buffers for recv/send (per-connection RIO buffers removed)
                {
                    var recvBuffer = new byte[BufferSize];
                    var sendBuffer = new byte[BufferSize];
                    client.RecvHandle = GCHandle.Alloc(recvBuffer, GCHandleType.Pinned);
                    client.SendHandle = GCHandle.Alloc(sendBuffer, GCHandleType.Pinned);
                    client.RecvBuffer = client.RecvHandle.AddrOfPinnedObject();
                    client.SendBuffer = client.SendHandle.AddrOfPinnedObject();
                }

                // Start timing on first connection
                if (!_benchmarkStarted)
                {
                    _benchmarkStarted = true;
                    CaptureStartMemoryStats();
                    _benchmarkStopwatch.Start();
                    if (benchmarkMode)
                        Console.WriteLine("First client connected - benchmark started!");
                }

                // Track peak connections
                var activeCount = CountActiveClients();
                if (activeCount > _peakConnections)
                    _peakConnections = activeCount;

                if (!benchmarkMode && !_quietMode)
                    Console.WriteLine($"Client {clientIndex} connected (socket={result})");

                // Post initial receive
                ring.PrepareRecv(client.Socket, client.RecvBuffer, BufferSize, MsgFlags.None, OpRecv | (uint)clientIndex);
            }
            else
            {
                Console.WriteLine($"No free slot for client, closing socket {result}");
                ring.CloseSocket(clientSocket);
            }
        }
        else if (result != -11 && result != -4) // EAGAIN, EINTR
        {
            Console.WriteLine($"Accept error: {result}");
        }

        // Replenish accepts
        while (_pendingAcceptCount < PendingAccepts)
        {
            ring.PrepareAccept(listener, 0, 0, OpAccept);
            _pendingAcceptCount++;
        }
    }

    private static void HandleRecv(IIORingGroup ring, WindowsRIOGroup? rioRing, int index, int result, bool benchmarkMode, bool useRegisteredBuffers)
    {
        if (result <= 0)
        {
            CloseClient(ring, rioRing, index, useRegisteredBuffers);
            return;
        }

        _totalBytes += result;

        ref var client = ref _clients[index];

        // In pipe mode, use zero-copy send from Pipe's circular buffer
        if (_usePipeMode && client.SendPipe != null && client.ExternalBufferId >= 0 && rioRing != null)
        {
            var writeSpan = client.SendPipe.Writer.AvailableToWrite();
            if (writeSpan.Length >= result)
            {
                unsafe
                {
                    new Span<byte>((void*)client.RecvBuffer, result).CopyTo(writeSpan);
                }
                client.SendPipe.Writer.Advance((uint)result);

                var readSpan = client.SendPipe.Reader.AvailableToRead();
                var offset = client.SendPipe.Reader.GetReadOffset();

                // Queue send using external buffer (zero-copy from Pipe memory)
                rioRing.PrepareSendExternal(client.ConnId, client.ExternalBufferId, offset, readSpan.Length, OpSend | (uint)index);
            }
            else
            {
                CloseClient(ring, rioRing, index, useRegisteredBuffers);
            }
        }
        else
        {
            // Standard mode: copy recv->send buffer
            unsafe
            {
                Buffer.MemoryCopy((void*)client.RecvBuffer, (void*)client.SendBuffer, BufferSize, result);
            }
            ring.PrepareSend(client.Socket, client.SendBuffer, result, MsgFlags.None, OpSend | (uint)index);
        }
    }

    private static void HandleSend(IIORingGroup ring, WindowsRIOGroup? rioRing, int index, int result, bool benchmarkMode, bool useRegisteredBuffers)
    {
        if (result <= 0)
        {
            CloseClient(ring, rioRing, index, useRegisteredBuffers);
            return;
        }

        _totalBytes += result;
        _totalMessages++;

        ref var client = ref _clients[index];

        // In pipe mode, advance the pipe reader
        if (_usePipeMode && client.SendPipe != null)
        {
            client.SendPipe.Reader.Advance((uint)result);
        }

        // Queue next receive
        ring.PrepareRecv(client.Socket, client.RecvBuffer, BufferSize, MsgFlags.None, OpRecv | (uint)index);
    }

    private static void CloseClient(IIORingGroup ring, WindowsRIOGroup? rioRing, int index, bool useRegisteredBuffers)
    {
        ref var client = ref _clients[index];
        if (!client.Active) return;

        if (!_quietMode)
            Console.WriteLine($"Client {index} disconnected");

        // Cleanup pipe mode resources
        if (client.SendPipe != null)
        {
            if (client.ExternalBufferId >= 0 && rioRing != null)
                rioRing.UnregisterExternalBuffer(client.ExternalBufferId);
            client.SendPipe.Dispose();
        }

        // Unregister from RIO if using registered buffers
        if (client.ConnId >= 0 && rioRing != null)
        {
            rioRing.UnregisterSocket(client.ConnId);
        }

        // Free pinned buffers
        if (client.RecvHandle.IsAllocated)
            client.RecvHandle.Free();
        if (client.SendHandle.IsAllocated)
            client.SendHandle.Free();

        ring.CloseSocket(client.Socket);
        client = default;

        // Stop benchmark when all clients disconnect (only if not using duration mode)
        if (_benchmarkStarted && CountActiveClients() == 0 && !_finalStatsPrinted && _benchmarkDuration == 0)
        {
            _benchmarkStopwatch.Stop();
            Console.WriteLine("All clients disconnected - benchmark stopped!");
            _finalStatsPrinted = true;
            PrintFinalStats(rioRing);
            _running = false;
        }
    }

    private static int FindFreeSlot()
    {
        for (var i = 0; i < MaxClients; i++)
            if (!_clients[i].Active)
                return i;
        return -1;
    }

    private static int CountActiveClients()
    {
        var count = 0;
        for (var i = 0; i < MaxClients; i++)
            if (_clients[i].Active)
                count++;
        return count;
    }

    #endregion

    #region PollGroup Server

    private class PollClientState
    {
        public Socket Socket = null!;
        public byte[] RecvBuffer = new byte[BufferSize];
        public byte[] SendBuffer = new byte[BufferSize];
        public GCHandle Handle;
        public int PendingSendBytes;
        public int PendingSendOffset;
    }

    private static readonly PollClientState?[] _pollClients = new PollClientState[MaxClients];

    private static void RunPollGroupServer(bool benchmarkMode)
    {
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
        var pendingSendCount = 0;

        // Local function to close a poll client (captures pollGroup from enclosing scope)
        void ClosePollClient(PollClientState state)
        {
            for (var i = 0; i < MaxClients; i++)
            {
                if (_pollClients[i] == state)
                {
                    if (!benchmarkMode && !_quietMode)
                        Console.WriteLine($"[PollGroup] Client {i} disconnected");

                    pollGroup.Remove(state.Socket, state.Handle);
                    state.Handle.Free();
                    state.Socket.Close();
                    _pollClients[i] = null;

                    // Stop benchmark when all clients disconnect (only if not using duration mode)
                    if (_benchmarkStarted && CountActivePollClients() == 0 && !_finalStatsPrinted && _benchmarkDuration == 0)
                    {
                        _benchmarkStopwatch.Stop();
                        Console.WriteLine("[PollGroup] All clients disconnected - benchmark stopped!");
                        _finalStatsPrinted = true;
                        PrintFinalStats(null);
                        _running = false;
                    }
                    break;
                }
            }
        }

        // Local function to handle poll client (captures pollGroup from enclosing scope)
        void HandlePollClient(PollClientState state)
        {
            try
            {
                // Try to send pending data first
                if (state.PendingSendBytes > 0)
                {
                    var sent = state.Socket.Send(state.SendBuffer, state.PendingSendOffset, state.PendingSendBytes, SocketFlags.None);
                    if (sent > 0)
                    {
                        _totalBytes += sent;
                        state.PendingSendOffset += sent;
                        state.PendingSendBytes -= sent;

                        if (state.PendingSendBytes == 0)
                        {
                            _totalMessages++;
                            pendingSendCount--;
                        }
                    }
                }

                // Try to receive if no pending send
                if (state.PendingSendBytes == 0)
                {
                    var received = state.Socket.Receive(state.RecvBuffer, SocketFlags.None);
                    if (received > 0)
                    {
                        _totalBytes += received;

                        // Echo: copy to send buffer and queue send
                        Buffer.BlockCopy(state.RecvBuffer, 0, state.SendBuffer, 0, received);

                        // Try immediate send
                        var sent = state.Socket.Send(state.SendBuffer, 0, received, SocketFlags.None);
                        if (sent > 0)
                        {
                            _totalBytes += sent;
                        }

                        if (sent < received)
                        {
                            // Partial send - queue remainder
                            state.PendingSendOffset = sent;
                            state.PendingSendBytes = received - sent;
                            pendingSendCount++;
                        }
                        else
                        {
                            _totalMessages++;
                        }
                    }
                    else if (received == 0)
                    {
                        ClosePollClient(state);
                    }
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                // Normal - no data available
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

            _pollCallCount++;
            _pollTotalTicks += pollEnd - pollStart;
            _pollTotalCompletions += count;

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
                        client.LingerState = new LingerOption(true, 0);

                        var clientIndex = FindFreePollSlot();
                        if (clientIndex >= 0)
                        {
                            var clientState = new PollClientState { Socket = client };
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
                                    Console.WriteLine("[PollGroup] First client connected - benchmark started!");
                            }

                            var activeCount = CountActivePollClients();
                            if (activeCount > _peakConnections)
                                _peakConnections = activeCount;

                            if (!benchmarkMode && !_quietMode)
                                Console.WriteLine($"[PollGroup] Client {clientIndex} connected");
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
            PrintFinalStats(null);
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
                return i;
        return -1;
    }

    private static int CountActivePollClients()
    {
        var count = 0;
        for (var i = 0; i < MaxClients; i++)
            if (_pollClients[i] != null)
                count++;
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

    private static void PrintFinalStats(WindowsRIOGroup? rioRing)
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
        Console.WriteLine($"=== I/O Syscall Statistics ===");
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

        if (rioRing != null)
        {
            Console.WriteLine();
            Console.WriteLine("=== RIO Ring Info ===");
            Console.WriteLine($"Active connections: {rioRing.ActiveConnections}");
            Console.WriteLine($"Max connections: {rioRing.MaxConnections}");

            if (_usePipeMode)
            {
                Console.WriteLine();
                Console.WriteLine("=== Pipe Mode (Zero-Copy External Buffers) ===");
                Console.WriteLine($"External buffers registered: {rioRing.ExternalBufferCount}");
                Console.WriteLine($"Pipe buffer size: {PipeBufferSize / 1024} KB per connection");
            }
        }
    }

    #endregion
}
