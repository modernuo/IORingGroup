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
    RIO,        // True RIO with registered buffers
    PollGroup   // wepoll-based polling
}

/// <summary>
/// High-performance single-threaded echo server comparing RIO vs PollGroup.
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

    public static void Main(string[] args)
    {
        var backend = ServerBackend.RIO;
        var benchmarkMode = false;
        nint cpuAffinity = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--pollgroup", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.PollGroup;
            }
            else if (arg.Equals("--rio", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-r", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.RIO;
            }
            else if (arg.Equals("--benchmark", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-b", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkMode = true;
            }
            else if (arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-q", StringComparison.OrdinalIgnoreCase))
            {
                _quietMode = true;
            }
            else if ((arg.Equals("--affinity", StringComparison.OrdinalIgnoreCase) ||
                      arg.Equals("-a", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                // CPU affinity mask (e.g., 0x1 for CPU 0, 0x2 for CPU 1, 0xF for CPUs 0-3)
                var affinityStr = args[++i];
                if (affinityStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    cpuAffinity = (nint)Convert.ToInt64(affinityStr[2..], 16);
                else
                    cpuAffinity = (nint)long.Parse(affinityStr);
            }
        }

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

        Console.WriteLine("RIO vs PollGroup Benchmark Server (Single-threaded)");
        Console.WriteLine($"Backend: {backend}");
        Console.WriteLine($"Benchmark mode: {benchmarkMode}, Quiet: {_quietMode}");
        Console.WriteLine($"Port: {Port}");
        Console.WriteLine("Usage: TestServer [--rio|-r] [--pollgroup|-p] [--benchmark|-b] [--quiet|-q] [--affinity|-a <mask>]");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        if (backend == ServerBackend.RIO)
        {
            RunRIOServer(benchmarkMode);
        }
        else
        {
            RunPollGroupServer(benchmarkMode);
        }
    }

    #region RIO Server (True Registered I/O)

    // User data encoding for RIO operations
    private const ulong OpAccept = 0x1000_0000_0000_0000UL;
    private const ulong OpRecv = 0x2000_0000_0000_0000UL;
    private const ulong OpSend = 0x3000_0000_0000_0000UL;
    private const ulong OpMask = 0xF000_0000_0000_0000UL;
    private const ulong IndexMask = 0x0FFF_FFFF_FFFF_FFFFUL;

    // Number of accepts to keep queued (must not exceed accept pool size in ioring.dll, currently 64)
    private const int PendingAccepts = 64;

    // Client state for RIO
    private struct RIOClientState
    {
        public nint Socket;
        public int ConnId;          // RIO connection ID from RegisterSocket
        public nint RecvBuffer;     // Pointer to registered recv buffer
        public nint SendBuffer;     // Pointer to registered send buffer
        public bool Active;
    }

    private static readonly RIOClientState[] _rioClients = new RIOClientState[MaxClients];

    // Benchmark stats
    private static long _totalMessages;
    private static long _totalBytes;
    private static long _lastReportedMessages;
    private static readonly Stopwatch _benchmarkStopwatch = new();
    private static bool _benchmarkStarted;
    private static bool _finalStatsPrinted;
    private static int _peakConnections;

    // Memory stats (captured at start)
    private static long _startAllocatedBytes;
    private static int _startGen0;
    private static int _startGen1;
    private static int _startGen2;

    // Track pending accepts
    private static int _pendingAcceptCount;

    private static void RunRIOServer(bool benchmarkMode)
    {
        try
        {
            // Create RIO ring with pre-allocated buffers for max clients
            using var ring = new WindowsRIOGroup(
                queueSize: 16384,
                maxConnections: MaxClients,
                recvBufferSize: BufferSize,
                sendBufferSize: BufferSize
            );

            Console.WriteLine($"RIO ring created: MaxConnections={ring.MaxConnections}, RecvBuf={ring.RecvBufferSize}, SendBuf={ring.SendBufferSize}");

            // IMPORTANT: Use library-owned listener with WSA_FLAG_REGISTERED_IO
            // A .NET Socket listener won't work - the listener MUST have WSA_FLAG_REGISTERED_IO
            // for AcceptEx sockets to work with RIO operations.
            var listener = ring.CreateListener("0.0.0.0", Port, ListenBacklog);
            if (listener == -1)
            {
                var error = Win_x64.ioring_get_last_error();
                throw new InvalidOperationException($"Failed to create listener: error {error}");
            }

            try
            {
                Console.WriteLine($"RIO server listening on port {Port}");
                Console.WriteLine($"Backend: {ring.Backend} (IsRIO: {ring.IsRIO})");

                // Reset stats
                _totalMessages = 0;
                _totalBytes = 0;
                _lastReportedMessages = 0;
                _pendingAcceptCount = 0;
                _benchmarkStarted = false;
                _finalStatsPrinted = false;
                _peakConnections = 0;
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
                    ring.Submit();
                    var count = ring.WaitCompletions(completions, 1, 1);

                    for (var i = 0; i < count; i++)
                    {
                        ProcessRIOCompletion(ring, listener, ref completions[i], benchmarkMode);
                    }

                    ring.AdvanceCompletionQueue(count);

                    // Print stats every second in benchmark mode
                    if (benchmarkMode)
                    {
                        var elapsedMs = _benchmarkStopwatch.ElapsedMilliseconds;
                        if (elapsedMs - lastStatsMs >= 1000)
                        {
                            PrintStats("RIO");
                            lastStatsMs = elapsedMs;
                        }
                    }
                }

                _benchmarkStopwatch.Stop();
                if (!_finalStatsPrinted)
                {
                    _finalStatsPrinted = true;
                    PrintFinalStats("RIO", ring);
                }
            }
            finally
            {
                ring.CloseListener(listener);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RIO server error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static void ProcessRIOCompletion(WindowsRIOGroup ring, nint listenerFd, ref Completion cqe, bool benchmarkMode)
    {
        var op = cqe.UserData & OpMask;
        var index = (int)(cqe.UserData & IndexMask);

        switch (op)
        {
            case OpAccept:
                HandleRIOAccept(ring, listenerFd, cqe.Result, benchmarkMode);
                break;
            case OpRecv:
                HandleRIORecv(ring, index, cqe.Result, benchmarkMode);
                break;
            case OpSend:
                HandleRIOSend(ring, index, cqe.Result, benchmarkMode);
                break;
        }
    }

    private static void HandleRIOAccept(WindowsRIOGroup ring, nint listenerFd, int result, bool benchmarkMode)
    {
        _pendingAcceptCount--;

        if (result >= 0)
        {
            var clientSocket = (nint)result;
            var clientIndex = FindFreeRIOSlot();

            if (clientIndex >= 0)
            {
                // Register socket with RIO to get buffer pointers
                var connId = ring.RegisterSocket(clientSocket, out var recvBuf, out var sendBuf);

                if (connId >= 0)
                {
                    _rioClients[clientIndex] = new RIOClientState
                    {
                        Socket = clientSocket,
                        ConnId = connId,
                        RecvBuffer = recvBuf,
                        SendBuffer = sendBuf,
                        Active = true
                    };

                    // Start timing on first connection
                    if (!_benchmarkStarted)
                    {
                        _benchmarkStarted = true;
                        CaptureStartMemoryStats();
                        _benchmarkStopwatch.Start();
                        if (benchmarkMode)
                            Console.WriteLine("[RIO] First client connected - benchmark started!");
                    }

                    // Track peak connections
                    var activeCount = ring.ActiveConnections;
                    if (activeCount > _peakConnections)
                        _peakConnections = activeCount;

                    if (!benchmarkMode)
                        Console.WriteLine($"[RIO] Client {clientIndex} connected (socket={result}, connId={connId})");

                    // Post initial receive using registered buffer
                    ring.PrepareRecvRegistered(connId, BufferSize, OpRecv | (uint)clientIndex);
                }
                else
                {
                    var error = Win_x64.ioring_get_last_error();
                    Console.WriteLine($"[RIO] Failed to register socket {result}, error={error} (0x{error:X})");
                    closesocket(clientSocket);
                }
            }
            else
            {
                Console.WriteLine($"[RIO] No free slot for client, closing socket {result}");
                closesocket(clientSocket);
            }
        }
        else
        {
            // Accept error - but don't log WSAEWOULDBLOCK spam
            if (result != -10035) // WSAEWOULDBLOCK
                Console.WriteLine($"[RIO] Accept error: {result}");
        }

        // Replenish accepts
        while (_pendingAcceptCount < PendingAccepts)
        {
            ring.PrepareAccept(listenerFd, 0, 0, OpAccept);
            _pendingAcceptCount++;
        }
    }

    private static void HandleRIORecv(WindowsRIOGroup ring, int index, int result, bool benchmarkMode)
    {
        if (result <= 0)
        {
            // Error or disconnect
            CloseRIOClient(ring, index, benchmarkMode);
            return;
        }

        _totalBytes += result;

        ref var client = ref _rioClients[index];

        // Copy from recv buffer to send buffer for echo
        // Both buffers are in the RIO registered memory pool
        unsafe
        {
            Buffer.MemoryCopy((void*)client.RecvBuffer, (void*)client.SendBuffer, BufferSize, result);
        }

        // Queue send using registered buffer
        ring.PrepareSendRegistered(client.ConnId, result, OpSend | (uint)index);
    }

    private static void HandleRIOSend(WindowsRIOGroup ring, int index, int result, bool benchmarkMode)
    {
        if (result <= 0)
        {
            CloseRIOClient(ring, index, benchmarkMode);
            return;
        }

        _totalBytes += result;
        _totalMessages++;

        ref var client = ref _rioClients[index];

        // Queue next receive using registered buffer
        ring.PrepareRecvRegistered(client.ConnId, BufferSize, OpRecv | (uint)index);
    }

    private static int FindFreeRIOSlot()
    {
        for (var i = 0; i < MaxClients; i++)
            if (!_rioClients[i].Active)
                return i;
        return -1;
    }

    private static void CloseRIOClient(WindowsRIOGroup ring, int index, bool benchmarkMode)
    {
        ref var client = ref _rioClients[index];
        if (!client.Active) return;

        if (!benchmarkMode)
            Console.WriteLine($"[RIO] Client {index} disconnected");

        // Unregister from RIO before closing socket
        ring.UnregisterSocket(client.ConnId);
        closesocket(client.Socket);

        client = default;

        // Stop benchmark when all clients disconnect (after benchmark started)
        if (benchmarkMode && _benchmarkStarted && ring.ActiveConnections == 0 && !_finalStatsPrinted)
        {
            _benchmarkStopwatch.Stop();
            Console.WriteLine("[RIO] All clients disconnected - benchmark stopped!");
            _finalStatsPrinted = true;
            PrintFinalStats("RIO", ring);
            _running = false;  // Exit the server loop
        }
    }

    #endregion

    #region PollGroup Server

    private class PollClientState
    {
        public Socket Socket = null!;
        public byte[] RecvBuffer = new byte[BufferSize];
        public byte[] SendBuffer = new byte[BufferSize];
        public int SendOffset;
        public int SendLength;
        public bool NeedsSend;
        public GCHandle Handle;
    }

    private static readonly PollClientState?[] _pollClients = new PollClientState[MaxClients];

    private static void RunPollGroupServer(bool benchmarkMode)
    {
        using var pollGroup = PollGroup.Create();
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Blocking = false;
        listener.Bind(new IPEndPoint(IPAddress.Any, Port));
        listener.NoDelay = true;
        listener.Listen(ListenBacklog);

        // Register listener
        var listenerState = new PollClientState { Socket = listener };
        listenerState.Handle = GCHandle.Alloc(listenerState, GCHandleType.Normal);
        pollGroup.Add(listener, listenerState.Handle);

        Console.WriteLine($"PollGroup server listening on port {Port}");
        Console.WriteLine("Backend: wepoll (Windows) / epoll (Linux) / kqueue (macOS)");

        // Reset stats
        _totalMessages = 0;
        _totalBytes = 0;
        _lastReportedMessages = 0;
        _benchmarkStarted = false;
        _finalStatsPrinted = false;
        _peakConnections = 0;
        _benchmarkStopwatch.Reset();

        var handles = new GCHandle[1024];
        var lastStatsMs = 0L;

        var pendingSendCount = 0;

        Console.WriteLine("Waiting for first client connection to start benchmark...");

        while (_running)
        {
            var count = pollGroup.Poll(handles);

            for (var i = 0; i < count; i++)
            {
                var state = (PollClientState)handles[i].Target!;

                if (state.Socket != listener)
                {
                    HandlePollClient(pollGroup, state, ref pendingSendCount, benchmarkMode);
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

                            // Track peak connections
                            var activeCount = CountActivePollClients();
                            if (activeCount > _peakConnections)
                                _peakConnections = activeCount;

                            if (!benchmarkMode)
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

            // Retry pending sends
            if (pendingSendCount > 0)
            {
                for (var i = 0; i < MaxClients && pendingSendCount > 0; i++)
                {
                    var client = _pollClients[i];
                    if (client?.NeedsSend == true)
                    {
                        TryPollSend(pollGroup, client, ref pendingSendCount, benchmarkMode);
                    }
                }
            }

            // Print stats every second
            if (benchmarkMode)
            {
                var elapsedMs = _benchmarkStopwatch.ElapsedMilliseconds;
                if (elapsedMs - lastStatsMs >= 1000)
                {
                    PrintStats("PollGroup");
                    lastStatsMs = elapsedMs;
                }
            }

            if (count == 0 && pendingSendCount == 0)
            {
                Thread.Sleep(1);
            }
        }

        _benchmarkStopwatch.Stop();
        if (!_finalStatsPrinted)
        {
            _finalStatsPrinted = true;
            PrintFinalStats("PollGroup", null);
        }

        // Cleanup
        listenerState.Handle.Free();
        for (var i = 0; i < MaxClients; i++)
        {
            var client = _pollClients[i];
            if (client != null)
            {
                pollGroup.Remove(client.Socket, client.Handle);
                client.Handle.Free();
                client.Socket.Close();
                _pollClients[i] = null;
            }
        }
    }

    private static void HandlePollClient(IPollGroup pollGroup, PollClientState state, ref int pendingSendCount, bool benchmarkMode)
    {
        try
        {
            var bytesRead = state.Socket.Receive(state.RecvBuffer, SocketFlags.None);
            if (bytesRead == 0)
            {
                ClosePollClient(pollGroup, state, ref pendingSendCount, benchmarkMode);
                return;
            }

            _totalBytes += bytesRead;

            Buffer.BlockCopy(state.RecvBuffer, 0, state.SendBuffer, 0, bytesRead);
            state.SendOffset = 0;
            state.SendLength = bytesRead;

            if (!state.NeedsSend)
            {
                state.NeedsSend = true;
                pendingSendCount++;
            }
            TryPollSend(pollGroup, state, ref pendingSendCount, benchmarkMode);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            // No data available
        }
        catch (SocketException)
        {
            ClosePollClient(pollGroup, state, ref pendingSendCount, benchmarkMode);
        }
    }

    private static bool TryPollSend(IPollGroup pollGroup, PollClientState state, ref int pendingSendCount, bool benchmarkMode)
    {
        try
        {
            var sent = state.Socket.Send(state.SendBuffer, state.SendOffset, state.SendLength - state.SendOffset, SocketFlags.None);
            state.SendOffset += sent;
            _totalBytes += sent;

            if (state.SendOffset >= state.SendLength)
            {
                state.NeedsSend = false;
                pendingSendCount--;
                _totalMessages++;
                return true;
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            // Can't send right now
        }
        catch (SocketException)
        {
            ClosePollClient(pollGroup, state, ref pendingSendCount, benchmarkMode);
        }
        return false;
    }

    private static void ClosePollClient(IPollGroup pollGroup, PollClientState state, ref int pendingSendCount, bool benchmarkMode)
    {
        for (var i = 0; i < MaxClients; i++)
        {
            if (_pollClients[i] == state)
            {
                if (!benchmarkMode)
                    Console.WriteLine($"[PollGroup] Client {i} disconnected");
                if (state.NeedsSend)
                {
                    pendingSendCount--;
                }
                pollGroup.Remove(state.Socket, state.Handle);
                state.Handle.Free();
                state.Socket.Close();
                _pollClients[i] = null;

                // Stop benchmark when all clients disconnect (after benchmark started)
                if (benchmarkMode && _benchmarkStarted && CountActivePollClients() == 0 && !_finalStatsPrinted)
                {
                    _benchmarkStopwatch.Stop();
                    Console.WriteLine("[PollGroup] All clients disconnected - benchmark stopped!");
                    _finalStatsPrinted = true;
                    PrintFinalStats("PollGroup", null);
                    _running = false;  // Exit the server loop
                }
                break;
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
        // Force a collection to get a clean baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _startAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        _startGen0 = GC.CollectionCount(0);
        _startGen1 = GC.CollectionCount(1);
        _startGen2 = GC.CollectionCount(2);
    }

    private static void PrintStats(string backend)
    {
        if (_quietMode) return;

        var messages = Interlocked.Read(ref _totalMessages);

        if (messages == _lastReportedMessages)
            return;

        _lastReportedMessages = messages;

        var elapsed = _benchmarkStopwatch.Elapsed.TotalSeconds;
        var bytes = Interlocked.Read(ref _totalBytes);

        var msgPerSec = elapsed > 0 ? messages / elapsed : 0;
        var mbPerSec = elapsed > 0 ? (bytes / 1024.0 / 1024.0) / elapsed : 0;

        // Include GC stats
        var gen0 = GC.CollectionCount(0) - _startGen0;
        var gen1 = GC.CollectionCount(1) - _startGen1;
        var gen2 = GC.CollectionCount(2) - _startGen2;

        Console.WriteLine($"[{backend}] Msgs: {messages:N0} | {msgPerSec:N0} msg/s | {mbPerSec:N2} MB/s | GC: {gen0}/{gen1}/{gen2}");
    }

    private static void PrintFinalStats(string backend, WindowsRIOGroup? rioRing)
    {
        var elapsed = _benchmarkStopwatch.Elapsed.TotalSeconds;
        var messages = Interlocked.Read(ref _totalMessages);
        var bytes = Interlocked.Read(ref _totalBytes);

        // Memory stats
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - _startAllocatedBytes;
        var gen0 = GC.CollectionCount(0) - _startGen0;
        var gen1 = GC.CollectionCount(1) - _startGen1;
        var gen2 = GC.CollectionCount(2) - _startGen2;

        Console.WriteLine();
        Console.WriteLine($"=== {backend} Final Statistics ===");
        Console.WriteLine($"Total time: {elapsed:N2} seconds");
        Console.WriteLine($"Total messages: {messages:N0}");
        Console.WriteLine($"Total bytes: {bytes:N0}");
        if (elapsed > 0)
        {
            Console.WriteLine($"Average rate: {messages / elapsed:N0} msg/s");
            Console.WriteLine($"Average throughput: {(bytes / 1024.0 / 1024.0) / elapsed:N2} MB/s");
        }

        Console.WriteLine();
        Console.WriteLine("=== Memory Statistics ===");
        Console.WriteLine($"Total allocated: {allocatedBytes / 1024.0 / 1024.0:N2} MB");
        Console.WriteLine($"GC collections: Gen0={gen0}, Gen1={gen1}, Gen2={gen2}");
        if (messages > 0)
        {
            Console.WriteLine($"Bytes allocated per message: {(double)allocatedBytes / messages:N2}");
        }

        Console.WriteLine($"Peak connections: {_peakConnections}");

        if (rioRing != null)
        {
            Console.WriteLine();
            Console.WriteLine("=== RIO Ring Info ===");
            Console.WriteLine($"Active connections: {rioRing.ActiveConnections}");
            Console.WriteLine($"Max connections: {rioRing.MaxConnections}");

            Console.WriteLine();
            Console.WriteLine("=== Lazy Commit Stats ===");
            Console.WriteLine($"Reserved bytes: {rioRing.ReservedBytes / 1024.0 / 1024.0:N2} MB (virtual address space)");
            Console.WriteLine($"Committed bytes: {rioRing.CommittedBytes / 1024.0 / 1024.0:N2} MB (actual physical memory)");
            Console.WriteLine($"Committed slabs: {rioRing.CommittedSlabs} (256 connections each)");
            Console.WriteLine($"Committed connections: {rioRing.CommittedConnections}");
        }
    }

    #endregion

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(nint socket);
}
