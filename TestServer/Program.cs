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
    RIO,        // Windows: True RIO with registered buffers
    PollGroup,  // Cross-platform: wepoll (Windows) / epoll (Linux) / kqueue (macOS)
    IoUring     // Linux: Native io_uring
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
    private const int PipeBufferSize = 64 * 1024;  // 64KB send pipe per connection

    private static volatile bool _running = true;
    private static bool _quietMode;
    private static bool _usePipeMode;  // Use Pipe with external buffer for zero-copy sends
    private static int _benchmarkDuration; // 0 = run until clients disconnect

    public static void Main(string[] args)
    {
        // Default backend: RIO on Windows, io_uring on Linux, PollGroup elsewhere
        var backend = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ServerBackend.RIO
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? ServerBackend.IoUring
                : ServerBackend.PollGroup;

        var benchmarkMode = false;
        var benchmarkDuration = 0; // 0 = run until all clients disconnect
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
            else if (arg.Equals("--rio", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-r", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.RIO;
            }
            else if (arg.Equals("--iouring", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-u", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.IoUring;
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

        Console.WriteLine("IORingGroup Benchmark Server (Single-threaded)");
        Console.WriteLine($"Platform: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Backend: {backend}");
        Console.WriteLine($"Benchmark mode: {benchmarkMode}, Duration: {(_benchmarkDuration > 0 ? $"{_benchmarkDuration}s" : "unlimited")}, Quiet: {_quietMode}");
        Console.WriteLine($"Port: {Port}");
        Console.WriteLine("Usage: TestServer [--rio|-r] [--pollgroup|-p|--epoll] [--iouring|-u] [--benchmark|-b] [--duration|-d <seconds>] [--quiet|-q] [--pipe] [--affinity|-a <mask>]");
        Console.WriteLine("  --rio: Windows Registered I/O (Windows only)");
        Console.WriteLine("  --pollgroup/--epoll: wepoll (Windows) / epoll (Linux) / kqueue (macOS)");
        Console.WriteLine("  --iouring: Linux io_uring (Linux only)");
        Console.WriteLine("  --duration: Run for N seconds then exit (for timed benchmarks)");
        Console.WriteLine("  --pipe: Use Pipe circular buffer with external buffer registration for zero-copy sends (RIO only)");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        switch (backend)
        {
            case ServerBackend.RIO when RuntimeInformation.IsOSPlatform(OSPlatform.Windows):
                RunRIOServer(benchmarkMode);
                break;
            case ServerBackend.IoUring when RuntimeInformation.IsOSPlatform(OSPlatform.Linux):
                RunIoUringServer(benchmarkMode);
                break;
            case ServerBackend.RIO:
            case ServerBackend.IoUring:
                Console.WriteLine($"Warning: {backend} not supported on this platform, falling back to PollGroup");
                RunPollGroupServer(benchmarkMode);
                break;
            default:
                RunPollGroupServer(benchmarkMode);
                break;
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
        public nint SendBuffer;     // Pointer to registered send buffer (not used in Pipe mode)
        public bool Active;

        // Pipe mode fields (for zero-copy sends from user-owned memory)
        public Pipe? SendPipe;       // Circular buffer for send data
        public int ExternalBufferId; // RIO buffer ID for the pipe memory (-1 if not registered)
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

    // Syscall timing stats
    private static long _pollCallCount;
    private static long _pollTotalTicks;
    private static long _pollTotalCompletions;

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
                        Console.WriteLine($"[RIO] Benchmark duration ({_benchmarkDuration}s) reached - stopping...");
                        break;
                    }

                    ring.Submit();

                    // Time the PeekCompletions syscall (non-blocking, like PollGroup)
                    var pollStart = Stopwatch.GetTimestamp();
                    var count = ring.PeekCompletions(completions);
                    var pollEnd = Stopwatch.GetTimestamp();

                    _pollCallCount++;
                    _pollTotalTicks += pollEnd - pollStart;
                    _pollTotalCompletions += count;

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

                    // Yield when no work (like PollGroup does)
                    if (count == 0)
                    {
                        Thread.Sleep(1);
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
                        Active = true,
                        SendPipe = null,
                        ExternalBufferId = -1
                    };

                    // In pipe mode, create a Pipe and register it as an external buffer
                    if (_usePipeMode)
                    {
                        var pipe = new Pipe(PipeBufferSize);
                        var extBufId = ring.RegisterExternalBuffer(pipe.GetBufferPointer(), pipe.GetVirtualSize());
                        if (extBufId >= 0)
                        {
                            _rioClients[clientIndex].SendPipe = pipe;
                            _rioClients[clientIndex].ExternalBufferId = extBufId;
                        }
                        else
                        {
                            pipe.Dispose();
                            Console.WriteLine($"[RIO] Warning: Failed to register external buffer for client {clientIndex}");
                        }
                    }

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

        // In pipe mode, use zero-copy send from Pipe's circular buffer
        if (_usePipeMode && client.SendPipe != null && client.ExternalBufferId >= 0)
        {
            // Write received data to the pipe's write side
            var writeSpan = client.SendPipe.Writer.AvailableToWrite();
            if (writeSpan.Length >= result)
            {
                unsafe
                {
                    new Span<byte>((void*)client.RecvBuffer, result).CopyTo(writeSpan);
                }
                client.SendPipe.Writer.Advance((uint)result);

                // Get the read position and send directly from pipe memory (zero-copy!)
                var readSpan = client.SendPipe.Reader.AvailableToRead();
                var offset = client.SendPipe.Reader.GetReadOffset();

                // Queue send using external buffer (zero-copy from Pipe memory)
                ring.PrepareSendExternal(client.ConnId, client.ExternalBufferId, offset, readSpan.Length, OpSend | (uint)index);
            }
            else
            {
                // Pipe is full - shouldn't happen in echo server, but handle gracefully
                Console.WriteLine($"[RIO] Warning: Pipe full for client {index}");
                CloseRIOClient(ring, index, benchmarkMode);
            }
        }
        else
        {
            // Standard mode: Copy from recv buffer to send buffer for echo
            // Both buffers are in the RIO registered memory pool
            unsafe
            {
                Buffer.MemoryCopy((void*)client.RecvBuffer, (void*)client.SendBuffer, BufferSize, result);
            }

            // Queue send using registered buffer
            ring.PrepareSendRegistered(client.ConnId, result, OpSend | (uint)index);
        }
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

        // In pipe mode, advance the pipe reader to consume the sent data
        if (_usePipeMode && client.SendPipe != null)
        {
            client.SendPipe.Reader.Advance((uint)result);
        }

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

        // Clean up Pipe and external buffer if in pipe mode
        if (client.SendPipe != null)
        {
            if (client.ExternalBufferId >= 0)
            {
                ring.UnregisterExternalBuffer(client.ExternalBufferId);
            }
            client.SendPipe.Dispose();
        }

        // Unregister from RIO before closing socket
        ring.UnregisterSocket(client.ConnId);
        closesocket(client.Socket);

        client = default;

        // Stop benchmark when all clients disconnect (only if not using duration mode)
        if (benchmarkMode && _benchmarkStarted && ring.ActiveConnections == 0 && !_finalStatsPrinted && _benchmarkDuration == 0)
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
        _pollCallCount = 0;
        _pollTotalTicks = 0;
        _pollTotalCompletions = 0;
        _benchmarkStopwatch.Reset();

        var handles = new GCHandle[1024];
        var lastStatsMs = 0L;

        var pendingSendCount = 0;

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

                // Stop benchmark when all clients disconnect (only if not using duration mode)
                if (benchmarkMode && _benchmarkStarted && CountActivePollClients() == 0 && !_finalStatsPrinted && _benchmarkDuration == 0)
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

        // Syscall timing stats
        Console.WriteLine();
        Console.WriteLine($"=== I/O Syscall Statistics ({(rioRing != null ? "WaitCompletions" : "Poll")}) ===");
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

            Console.WriteLine();
            Console.WriteLine("=== Lazy Commit Stats ===");
            Console.WriteLine($"Reserved bytes: {rioRing.ReservedBytes / 1024.0 / 1024.0:N2} MB (virtual address space)");
            Console.WriteLine($"Committed bytes: {rioRing.CommittedBytes / 1024.0 / 1024.0:N2} MB (actual physical memory)");
            Console.WriteLine($"Committed slabs: {rioRing.CommittedSlabs} (256 connections each)");
            Console.WriteLine($"Committed connections: {rioRing.CommittedConnections}");

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

    #region io_uring Server (Linux)

    // Client state for io_uring
    private struct IoUringClientState
    {
        public nint Socket;
        public nint RecvBuffer;   // Pinned buffer for recv
        public nint SendBuffer;   // Pinned buffer for send
        public bool Active;
        public GCHandle RecvHandle;
        public GCHandle SendHandle;
    }

    private static readonly IoUringClientState[] _ioUringClients = new IoUringClientState[MaxClients];

    private static void RunIoUringServer(bool benchmarkMode)
    {
        try
        {
            using var ring = IORingGroup.Create(queueSize: 16384);

            Console.WriteLine($"io_uring ring created");

            // Create and bind listener socket using .NET Socket
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Blocking = false;
            listener.Bind(new IPEndPoint(IPAddress.Any, Port));
            listener.NoDelay = true;
            listener.Listen(ListenBacklog);

            var listenerFd = listener.Handle;
            Console.WriteLine($"io_uring server listening on port {Port}");

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

            // Pre-post accepts
            for (var i = 0; i < PendingAccepts; i++)
            {
                ring.PrepareAccept(listenerFd, 0, 0, OpAccept);
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
                    Console.WriteLine($"[io_uring] Benchmark duration ({_benchmarkDuration}s) reached - stopping...");
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
                    ProcessIoUringCompletion(ring, listenerFd, ref completions[i], benchmarkMode);
                }

                ring.AdvanceCompletionQueue(count);

                // Print stats every second in benchmark mode
                if (benchmarkMode)
                {
                    var elapsedMs = _benchmarkStopwatch.ElapsedMilliseconds;
                    if (elapsedMs - lastStatsMs >= 1000)
                    {
                        PrintStats("io_uring");
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
                PrintFinalStats("io_uring", null);
            }

            // Cleanup clients
            for (var i = 0; i < MaxClients; i++)
            {
                if (_ioUringClients[i].Active)
                {
                    CloseIoUringClient(ring, i, benchmarkMode);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"io_uring server error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static void ProcessIoUringCompletion(IIORingGroup ring, nint listenerFd, ref Completion cqe, bool benchmarkMode)
    {
        var op = cqe.UserData & OpMask;
        var index = (int)(cqe.UserData & IndexMask);

        switch (op)
        {
            case OpAccept:
                HandleIoUringAccept(ring, listenerFd, cqe.Result, benchmarkMode);
                break;
            case OpRecv:
                HandleIoUringRecv(ring, index, cqe.Result, benchmarkMode);
                break;
            case OpSend:
                HandleIoUringSend(ring, index, cqe.Result, benchmarkMode);
                break;
        }
    }

    private static void HandleIoUringAccept(IIORingGroup ring, nint listenerFd, int result, bool benchmarkMode)
    {
        _pendingAcceptCount--;

        if (result >= 0)
        {
            var clientSocket = (nint)result;
            var clientIndex = FindFreeIoUringSlot();

            if (clientIndex >= 0)
            {
                // Allocate pinned buffers for this client
                var recvBuffer = new byte[BufferSize];
                var sendBuffer = new byte[BufferSize];
                var recvHandle = GCHandle.Alloc(recvBuffer, GCHandleType.Pinned);
                var sendHandle = GCHandle.Alloc(sendBuffer, GCHandleType.Pinned);

                _ioUringClients[clientIndex] = new IoUringClientState
                {
                    Socket = clientSocket,
                    RecvBuffer = recvHandle.AddrOfPinnedObject(),
                    SendBuffer = sendHandle.AddrOfPinnedObject(),
                    RecvHandle = recvHandle,
                    SendHandle = sendHandle,
                    Active = true
                };

                // Set socket non-blocking and TCP_NODELAY
                // Note: On Linux we'd use fcntl and setsockopt, but .NET Socket wraps this
                try
                {
                    // Wrap in Socket temporarily to set options
                    using var tempSocket = new Socket(new SafeSocketHandle(clientSocket, ownsHandle: false));
                    tempSocket.Blocking = false;
                    tempSocket.NoDelay = true;
                }
                catch
                {
                    // Best effort
                }

                // Start timing on first connection
                if (!_benchmarkStarted)
                {
                    _benchmarkStarted = true;
                    CaptureStartMemoryStats();
                    _benchmarkStopwatch.Start();
                    if (benchmarkMode)
                        Console.WriteLine("[io_uring] First client connected - benchmark started!");
                }

                // Track peak connections
                var activeCount = CountActiveIoUringClients();
                if (activeCount > _peakConnections)
                    _peakConnections = activeCount;

                if (!benchmarkMode)
                    Console.WriteLine($"[io_uring] Client {clientIndex} connected (fd={result})");

                // Post initial receive
                ring.PrepareRecv(clientSocket, _ioUringClients[clientIndex].RecvBuffer, BufferSize, MsgFlags.None, OpRecv | (uint)clientIndex);
            }
            else
            {
                Console.WriteLine($"[io_uring] No free slot for client, closing fd {result}");
                CloseSocket(clientSocket);
            }
        }
        else
        {
            // Accept error
            if (result != -11 && result != -4) // EAGAIN, EINTR
                Console.WriteLine($"[io_uring] Accept error: {result}");
        }

        // Replenish accepts
        while (_pendingAcceptCount < PendingAccepts)
        {
            ring.PrepareAccept(listenerFd, 0, 0, OpAccept);
            _pendingAcceptCount++;
        }
    }

    private static void HandleIoUringRecv(IIORingGroup ring, int index, int result, bool benchmarkMode)
    {
        if (result <= 0)
        {
            CloseIoUringClient(ring, index, benchmarkMode);
            return;
        }

        _totalBytes += result;

        ref var client = ref _ioUringClients[index];

        // Copy data from recv buffer to send buffer for echo
        unsafe
        {
            Buffer.MemoryCopy((void*)client.RecvBuffer, (void*)client.SendBuffer, BufferSize, result);
        }

        // Queue send
        ring.PrepareSend(client.Socket, client.SendBuffer, result, MsgFlags.None, OpSend | (uint)index);
    }

    private static void HandleIoUringSend(IIORingGroup ring, int index, int result, bool benchmarkMode)
    {
        if (result <= 0)
        {
            CloseIoUringClient(ring, index, benchmarkMode);
            return;
        }

        _totalBytes += result;
        _totalMessages++;

        ref var client = ref _ioUringClients[index];

        // Queue next receive
        ring.PrepareRecv(client.Socket, client.RecvBuffer, BufferSize, MsgFlags.None, OpRecv | (uint)index);
    }

    private static int FindFreeIoUringSlot()
    {
        for (var i = 0; i < MaxClients; i++)
            if (!_ioUringClients[i].Active)
                return i;
        return -1;
    }

    private static int CountActiveIoUringClients()
    {
        var count = 0;
        for (var i = 0; i < MaxClients; i++)
            if (_ioUringClients[i].Active)
                count++;
        return count;
    }

    private static void CloseIoUringClient(IIORingGroup ring, int index, bool benchmarkMode)
    {
        ref var client = ref _ioUringClients[index];
        if (!client.Active) return;

        if (!benchmarkMode)
            Console.WriteLine($"[io_uring] Client {index} disconnected");

        // Free pinned buffers
        if (client.RecvHandle.IsAllocated)
            client.RecvHandle.Free();
        if (client.SendHandle.IsAllocated)
            client.SendHandle.Free();

        CloseSocket(client.Socket);
        client = default;

        // Stop benchmark when all clients disconnect (after benchmark started)
        if (benchmarkMode && _benchmarkStarted && CountActiveIoUringClients() == 0 && !_finalStatsPrinted && _benchmarkDuration == 0)
        {
            _benchmarkStopwatch.Stop();
            Console.WriteLine("[io_uring] All clients disconnected - benchmark stopped!");
            _finalStatsPrinted = true;
            PrintFinalStats("io_uring", null);
            _running = false;
        }
    }

    private static void CloseSocket(nint socket)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            closesocket(socket);
        }
        else
        {
            close((int)socket);
        }
    }

    #endregion

    #region Platform Interop

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(nint socket);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    #endregion
}
