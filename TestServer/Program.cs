// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;

namespace TestServer;

public enum ServerBackend
{
    IORing,
    PollGroup
}

/// <summary>
/// High-performance single-threaded echo server using synchronous polling.
/// No async/await - tight event loop like nginx/redis for maximum throughput.
/// </summary>
public class Program
{
    private const int Port = 5000;
    private const int BufferSize = 4096;
    private const int MaxClients = 16384;
    private const int ListenBacklog = 4096;

    private static volatile bool _running = true;

    public static void Main(string[] args)
    {
        var backend = ServerBackend.IORing;
        var benchmarkMode = false;

        foreach (var arg in args)
        {
            if (arg.Equals("--pollgroup", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.PollGroup;
            }
            else if (arg.Equals("--ioring", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                backend = ServerBackend.IORing;
            }
            else if (arg.Equals("--benchmark", StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("-b", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkMode = true;
            }
        }

        Console.WriteLine("IORingGroup/PollGroup Benchmark Server (Single-threaded)");
        Console.WriteLine($"Backend: {backend}");
        Console.WriteLine($"Benchmark mode: {benchmarkMode}");
        Console.WriteLine($"Port: {Port}");
        Console.WriteLine("Usage: TestServer [--ioring|-i] [--pollgroup|-p] [--benchmark|-b]");
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

    #region IORing Server

    // User data encoding for IORing
    private const ulong OpAccept = 0x1000_0000_0000_0000UL;
    private const ulong OpPollRecv = 0x2000_0000_0000_0000UL;  // Poll for readable, then recv
    private const ulong OpRecv = 0x3000_0000_0000_0000UL;
    private const ulong OpSend = 0x4000_0000_0000_0000UL;
    private const ulong OpMask = 0xF000_0000_0000_0000UL;
    private const ulong IndexMask = 0x0FFF_FFFF_FFFF_FFFFUL;

    // Number of accepts to keep queued at all times (handles burst connections)
    private const int PendingAccepts = 128;

    private static readonly ClientState[] _ioringClients = new ClientState[MaxClients];
    private static readonly byte[][] _ioringRecvBuffers = new byte[MaxClients][];
    private static readonly byte[][] _ioringSendBuffers = new byte[MaxClients][];
    private static GCHandle[] _ioringRecvHandles = new GCHandle[MaxClients];
    private static GCHandle[] _ioringSendHandles = new GCHandle[MaxClients];

    // Benchmark stats
    private static long _totalMessages;
    private static long _totalBytes;
    private static long _lastReportedMessages;
    private static readonly Stopwatch _benchmarkStopwatch = new();

    private struct ClientState
    {
        public nint Socket;
        public int RecvLen;
        public bool Active;
    }

    // Track how many accepts are currently queued
    private static int _pendingAcceptCount;

    private static void RunIORingServer(bool benchmarkMode)
    {
        // Initialize buffers
        for (var i = 0; i < MaxClients; i++)
        {
            _ioringRecvBuffers[i] = new byte[BufferSize];
            _ioringSendBuffers[i] = new byte[BufferSize];
            _ioringRecvHandles[i] = GCHandle.Alloc(_ioringRecvBuffers[i], GCHandleType.Pinned);
            _ioringSendHandles[i] = GCHandle.Alloc(_ioringSendBuffers[i], GCHandleType.Pinned);
        }

        _pendingAcceptCount = 0;

        try
        {
            using var ring = IORingGroup.Create(16384);
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Blocking = false;
            listener.Bind(new IPEndPoint(IPAddress.Any, Port));
            listener.NoDelay = true;
            listener.Listen(ListenBacklog);

            Console.WriteLine($"IORing server listening on port {Port}");
            if (ring is System.Network.Windows.WindowsIORingGroup winRing)
            {
                Console.WriteLine($"Backend: {winRing.Backend}");
            }

            // Pre-post multiple accepts to handle burst connections
            for (var i = 0; i < PendingAccepts; i++)
            {
                ring.PrepareAccept(listener.Handle, 0, 0, OpAccept);
                _pendingAcceptCount++;
            }

            Span<Completion> completions = stackalloc Completion[256];
            var lastStatsMs = 0L;

            _benchmarkStopwatch.Start();

            while (_running)
            {
                ring.Submit();
                // Use short timeout (1ms) for responsive polling
                var count = ring.WaitCompletions(completions, 1, 1);

                for (var i = 0; i < count; i++)
                {
                    ProcessIORingCompletion(ring, listener.Handle, ref completions[i], benchmarkMode);
                }

                ring.AdvanceCompletionQueue(count);

                // Print stats every second in benchmark mode
                if (benchmarkMode)
                {
                    var elapsedMs = _benchmarkStopwatch.ElapsedMilliseconds;
                    if (elapsedMs - lastStatsMs >= 1000)
                    {
                        PrintStats("IORing");
                        lastStatsMs = elapsedMs;
                    }
                }
            }

            _benchmarkStopwatch.Stop();
            PrintFinalStats("IORing");
        }
        finally
        {
            for (var i = 0; i < MaxClients; i++)
            {
                if (_ioringRecvHandles[i].IsAllocated) _ioringRecvHandles[i].Free();
                if (_ioringSendHandles[i].IsAllocated) _ioringSendHandles[i].Free();
            }
        }
    }

    private static void ProcessIORingCompletion(IIORingGroup ring, nint listenerFd, ref Completion cqe, bool benchmarkMode)
    {
        var op = cqe.UserData & OpMask;
        var index = (int)(cqe.UserData & IndexMask);

        switch (op)
        {
            case OpAccept:
                HandleIORingAccept(ring, listenerFd, cqe.Result, benchmarkMode);
                break;
            case OpPollRecv:
                HandleIORingPollRecv(ring, index, cqe.Result, benchmarkMode);
                break;
            case OpRecv:
                HandleIORingRecv(ring, index, cqe.Result, benchmarkMode);
                break;
            case OpSend:
                HandleIORingSend(ring, index, cqe.Result, benchmarkMode);
                break;
        }
    }

    private static void HandleIORingAccept(IIORingGroup ring, nint listenerFd, int result, bool benchmarkMode)
    {
        // One accept completed
        _pendingAcceptCount--;

        if (result >= 0)
        {
            var clientIndex = FindFreeIORingSlot();
            if (clientIndex >= 0)
            {
                _ioringClients[clientIndex].Socket = result;
                _ioringClients[clientIndex].Active = true;
                if (!benchmarkMode)
                    Console.WriteLine($"[IORing] Client {clientIndex} connected (fd={result})");

                // Queue poll for readable - don't recv until we know data is available
                ring.PreparePollAdd(_ioringClients[clientIndex].Socket, PollMask.In, OpPollRecv | (uint)clientIndex);
            }
            else
            {
                Console.WriteLine($"[IORing] No free slot for client, closing socket {result}");
                closesocket(result);
            }
        }
        else
        {
            // Accept returned an error
            Console.WriteLine($"[IORing] Accept error: {result} (pending={_pendingAcceptCount})");
        }

        // Replenish accepts to maintain the pool
        while (_pendingAcceptCount < PendingAccepts)
        {
            ring.PrepareAccept(listenerFd, 0, 0, OpAccept);
            _pendingAcceptCount++;
        }
    }

    private static long _pollRecvCount;
    private static long _pollErrCount;

    // Linux-style poll event flags (our API uses these, C library translates to/from Windows)
    private const int POLLIN = 0x0001;
    private const int POLLPRI = 0x0002;
    private const int POLLOUT = 0x0004;
    private const int POLLERR = 0x0008;
    private const int POLLHUP = 0x0010;
    private const int POLLNVAL = 0x0020;

    private static void HandleIORingPollRecv(IIORingGroup ring, int index, int result, bool benchmarkMode)
    {
        _pollRecvCount++;
        if (_pollRecvCount <= 20)
            Console.WriteLine($"[POLL] Client {index} poll result=0x{result:X4} (total polls: {_pollRecvCount})");

        // Check for errors first - result is poll revents
        if (result < 0)
        {
            Console.WriteLine($"[IORing] Client {index} poll error: {result}");
            CloseIORingClient(index);
            return;
        }

        // Check for socket errors or hangup in the poll events
        if ((result & (POLLERR | POLLHUP | POLLNVAL)) != 0)
        {
            _pollErrCount++;
            if (_pollErrCount <= 20)
                Console.WriteLine($"[POLL] Client {index} error/hangup: 0x{result:X4} (total errs: {_pollErrCount})");
            CloseIORingClient(index);
            return;
        }

        // Socket is readable - now do the actual recv
        ring.PrepareRecv(_ioringClients[index].Socket, _ioringRecvHandles[index].AddrOfPinnedObject(), BufferSize, MsgFlags.None, OpRecv | (uint)index);
    }

    // WSAEWOULDBLOCK error code
    private const int WSAEWOULDBLOCK = 10035;

    private static long _wouldBlockRecvCount;
    private static long _wouldBlockSendCount;
    private static long _recvSuccessCount;
    private static long _sendSuccessCount;
    private static long _recvErrorCount;
    private static long _sendErrorCount;

    private static void HandleIORingRecv(IIORingGroup ring, int index, int result, bool benchmarkMode)
    {
        if (result == -WSAEWOULDBLOCK)
        {
            _wouldBlockRecvCount++;
            if (_wouldBlockRecvCount <= 10)
                Console.WriteLine($"[WOULDBLOCK] Recv on client {index} (total recv blocks: {_wouldBlockRecvCount})");
            // Poll said readable but recv got WOULDBLOCK - re-poll
            ring.PreparePollAdd(_ioringClients[index].Socket, PollMask.In, OpPollRecv | (uint)index);
            return;
        }

        if (result <= 0)
        {
            _recvErrorCount++;
            if (_recvErrorCount <= 20)
                Console.WriteLine($"[RECV] Client {index} error/disconnect (result={result}, total errors: {_recvErrorCount})");
            CloseIORingClient(index);
            return;
        }

        _recvSuccessCount++;
        if (_recvSuccessCount <= 10)
            Console.WriteLine($"[RECV] Client {index} received {result} bytes (total: {_recvSuccessCount})");

        _totalBytes += result;

        // Copy to send buffer and echo back
        Buffer.BlockCopy(_ioringRecvBuffers[index], 0, _ioringSendBuffers[index], 0, result);
        _ioringClients[index].RecvLen = result;

        ring.PrepareSend(_ioringClients[index].Socket, _ioringSendHandles[index].AddrOfPinnedObject(), result, MsgFlags.None, OpSend | (uint)index);
    }

    private static void HandleIORingSend(IIORingGroup ring, int index, int result, bool benchmarkMode)
    {
        if (result == -WSAEWOULDBLOCK)
        {
            _wouldBlockSendCount++;
            if (_wouldBlockSendCount <= 10)
                Console.WriteLine($"[WOULDBLOCK] Send on client {index} (total send blocks: {_wouldBlockSendCount})");
            // Can't send yet - re-queue send (send buffers are usually available quickly)
            ring.PrepareSend(_ioringClients[index].Socket, _ioringSendHandles[index].AddrOfPinnedObject(), _ioringClients[index].RecvLen, MsgFlags.None, OpSend | (uint)index);
            return;
        }

        if (result <= 0)
        {
            _sendErrorCount++;
            if (_sendErrorCount <= 20)
                Console.WriteLine($"[SEND] Client {index} error (result={result}, total errors: {_sendErrorCount})");
            CloseIORingClient(index);
            return;
        }

        _sendSuccessCount++;
        if (_sendSuccessCount <= 10)
            Console.WriteLine($"[SEND] Client {index} sent {result} bytes (total: {_sendSuccessCount})");

        _totalBytes += result;
        _totalMessages++;

        // Queue poll for next recv - wait until data available
        ring.PreparePollAdd(_ioringClients[index].Socket, PollMask.In, OpPollRecv | (uint)index);
    }

    private static int FindFreeIORingSlot()
    {
        for (var i = 0; i < MaxClients; i++)
            if (!_ioringClients[i].Active)
                return i;
        return -1;
    }

    private static void CloseIORingClient(int index)
    {
        if (_ioringClients[index].Active)
        {
            closesocket(_ioringClients[index].Socket);
            _ioringClients[index].Socket = 0;
            _ioringClients[index].Active = false;
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
        Console.WriteLine($"Backend: wepoll (Windows) / epoll (Linux) / kqueue (macOS)");

        var handles = new GCHandle[256];
        var lastStatsMs = 0L;

        // Track clients with pending sends (avoid O(n) scan)
        var pendingSendCount = 0;

        _benchmarkStopwatch.Start();

        while (_running)
        {
            var count = pollGroup.Poll(handles);

            for (var i = 0; i < count; i++)
            {
                var state = (PollClientState)handles[i].Target!;

                if (state.Socket == listener)
                {
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
                else
                {
                    // Handle client data - recv and immediate send attempt
                    HandlePollClient(pollGroup, state, ref pendingSendCount, benchmarkMode);
                }
            }

            // Retry pending sends if any
            if (pendingSendCount > 0)
            {
                for (var i = 0; i < MaxClients && pendingSendCount > 0; i++)
                {
                    var client = _pollClients[i];
                    if (client?.NeedsSend == true)
                    {
                        if (TryPollSend(pollGroup, client, ref pendingSendCount, benchmarkMode))
                        {
                            // Successfully sent, counter already decremented
                        }
                    }
                }
            }

            // Print stats every second in benchmark mode
            if (benchmarkMode)
            {
                var elapsedMs = _benchmarkStopwatch.ElapsedMilliseconds;
                if (elapsedMs - lastStatsMs >= 1000)
                {
                    PrintStats("PollGroup");
                    lastStatsMs = elapsedMs;
                }
            }

            // Brief yield when idle to avoid 100% CPU
            if (count == 0 && pendingSendCount == 0)
            {
                Thread.Sleep(1);
            }
        }

        _benchmarkStopwatch.Stop();
        PrintFinalStats("PollGroup");

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

            // Copy to send buffer
            Buffer.BlockCopy(state.RecvBuffer, 0, state.SendBuffer, 0, bytesRead);
            state.SendOffset = 0;
            state.SendLength = bytesRead;

            // Try immediate send
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

    #endregion

    #region Stats

    private static void PrintStats(string backend)
    {
        var messages = Interlocked.Read(ref _totalMessages);

        // Only print if there's been activity since last report
        if (messages == _lastReportedMessages)
            return;

        _lastReportedMessages = messages;

        var elapsed = _benchmarkStopwatch.Elapsed.TotalSeconds;
        var bytes = Interlocked.Read(ref _totalBytes);

        var msgPerSec = elapsed > 0 ? messages / elapsed : 0;
        var mbPerSec = elapsed > 0 ? (bytes / 1024.0 / 1024.0) / elapsed : 0;

        Console.WriteLine($"[{backend}] Messages: {messages:N0} | Rate: {msgPerSec:N0} msg/s | Throughput: {mbPerSec:N2} MB/s");
    }

    private static void PrintFinalStats(string backend)
    {
        var elapsed = _benchmarkStopwatch.Elapsed.TotalSeconds;
        var messages = Interlocked.Read(ref _totalMessages);
        var bytes = Interlocked.Read(ref _totalBytes);

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

        // Debug counters (IORing only)
        if (backend == "IORing")
        {
            Console.WriteLine();
            Console.WriteLine("=== Debug Counters ===");
            Console.WriteLine($"Poll completions: {_pollRecvCount:N0}");
            Console.WriteLine($"Poll errors: {_pollErrCount:N0}");
            Console.WriteLine($"Recv success: {_recvSuccessCount:N0}");
            Console.WriteLine($"Recv errors: {_recvErrorCount:N0}");
            Console.WriteLine($"Recv WOULDBLOCK: {_wouldBlockRecvCount:N0}");
            Console.WriteLine($"Send success: {_sendSuccessCount:N0}");
            Console.WriteLine($"Send errors: {_sendErrorCount:N0}");
            Console.WriteLine($"Send WOULDBLOCK: {_wouldBlockSendCount:N0}");
        }
    }

    #endregion

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(nint socket);
}
