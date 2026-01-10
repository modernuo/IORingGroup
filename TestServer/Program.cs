using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;

namespace TestServer;

/// <summary>
/// Simple echo server using IORingGroup for async I/O.
/// </summary>
public class Program
{
    private const int Port = 5000;
    private const int BufferSize = 4096;
    private const int MaxClients = 64;

    // User data encoding: high bits = operation type, low bits = client index
    private const ulong OpAccept = 0x1000_0000_0000_0000UL;
    private const ulong OpRecv = 0x2000_0000_0000_0000UL;
    private const ulong OpSend = 0x3000_0000_0000_0000UL;
    private const ulong OpMask = 0xF000_0000_0000_0000UL;
    private const ulong IndexMask = 0x0FFF_FFFF_FFFF_FFFFUL;

    private static readonly ClientState[] _clients = new ClientState[MaxClients];
    private static readonly byte[][] _recvBuffers = new byte[MaxClients][];
    private static readonly byte[][] _sendBuffers = new byte[MaxClients][];
    private static GCHandle[] _recvHandles = new GCHandle[MaxClients];
    private static GCHandle[] _sendHandles = new GCHandle[MaxClients];

    private struct ClientState
    {
        public nint Socket;
        public int RecvLen;
        public bool Active;
    }

    public static void Main(string[] args)
    {
        Console.WriteLine("IORingGroup Echo Server");
        Console.WriteLine($"Listening on port {Port}...");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        // Initialize buffers
        for (var i = 0; i < MaxClients; i++)
        {
            _recvBuffers[i] = new byte[BufferSize];
            _sendBuffers[i] = new byte[BufferSize];
            _recvHandles[i] = GCHandle.Alloc(_recvBuffers[i], GCHandleType.Pinned);
            _sendHandles[i] = GCHandle.Alloc(_sendBuffers[i], GCHandleType.Pinned);
        }

        try
        {
            RunServer();
        }
        finally
        {
            // Cleanup
            for (var i = 0; i < MaxClients; i++)
            {
                if (_recvHandles[i].IsAllocated) _recvHandles[i].Free();
                if (_sendHandles[i].IsAllocated) _sendHandles[i].Free();
            }
        }
    }

    private static void RunServer()
    {
        using var ring = IORingGroup.Create(256);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Blocking = false;  // Non-blocking mode for async operations
        listener.Bind(new IPEndPoint(IPAddress.Any, Port));
        listener.NoDelay = true;
        listener.Listen(128);

        Console.WriteLine($"Server listening on port {Port}");
        if (ring is System.Network.Windows.WindowsIORingGroup winRing)
        {
            Console.WriteLine($"Backend: {winRing.Backend}");
        }

        // Queue initial accept
        QueueAccept(ring, listener.Handle);

        Span<Completion> completions = stackalloc Completion[64];
        var running = true;
        var lastDebugLog = DateTime.MinValue;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            running = false;
        };

        while (running)
        {
            // Submit any pending operations and wait for completions
            var submitted = ring.Submit();
            var count = ring.WaitCompletions(completions, 1, 100);

            // Throttle polling debug output to once per second
            var now = DateTime.UtcNow;
            if (count > 0 || (now - lastDebugLog).TotalSeconds >= 1)
            {
                Console.WriteLine($"[DEBUG] Poll: submitted={submitted}, completions={count}, SQ space={ring.SubmissionQueueSpace}, CQ count={ring.CompletionQueueCount}");
                lastDebugLog = now;
            }

            for (var i = 0; i < count; i++)
            {
                ProcessCompletion(ring, listener.Handle, ref completions[i]);
            }

            ring.AdvanceCompletionQueue(count);
        }

        Console.WriteLine("\nShutting down...");

        // Close all client sockets
        for (var i = 0; i < MaxClients; i++)
        {
            if (_clients[i].Active && _clients[i].Socket != 0)
            {
                CloseSocket(_clients[i].Socket);
            }
        }
    }

    private static void ProcessCompletion(IIORingGroup ring, nint listenerFd, ref Completion cqe)
    {
        var op = cqe.UserData & OpMask;
        var index = (int)(cqe.UserData & IndexMask);

        var opName = op switch
        {
            OpAccept => "ACCEPT",
            OpRecv => "RECV",
            OpSend => "SEND",
            _ => $"UNKNOWN(0x{op:X})"
        };
        Console.WriteLine($"[DEBUG] Completion: op={opName}, index={index}, result={cqe.Result}, userData=0x{cqe.UserData:X16}");

        switch (op)
        {
            case OpAccept:
                HandleAccept(ring, listenerFd, cqe.Result);
                break;
            case OpRecv:
                HandleRecv(ring, index, cqe.Result);
                break;
            case OpSend:
                HandleSend(ring, index, cqe.Result);
                break;
        }
    }

    private static void HandleAccept(IIORingGroup ring, nint listenerFd, int result)
    {
        if (result < 0)
        {
            Console.WriteLine($"Accept error: {result}");
        }
        else
        {
            var clientIndex = FindFreeClientSlot();
            if (clientIndex >= 0)
            {
                _clients[clientIndex].Socket = result;
                _clients[clientIndex].Active = true;
                Console.WriteLine($"Client {clientIndex} connected (fd={result})");

                // Queue recv for new client
                QueueRecv(ring, clientIndex);
            }
            else
            {
                Console.WriteLine("Max clients reached, rejecting connection");
                CloseSocket(result);
            }
        }

        // Queue next accept
        QueueAccept(ring, listenerFd);
    }

    private static void HandleRecv(IIORingGroup ring, int index, int result)
    {
        if (result <= 0)
        {
            if (result == 0)
            {
                Console.WriteLine($"Client {index} disconnected");
            }
            else
            {
                Console.WriteLine($"Client {index} recv error: {result}");
            }
            CloseClient(index);
            return;
        }

        Console.WriteLine($"Client {index} received {result} bytes");

        // Copy to send buffer and echo back
        Buffer.BlockCopy(_recvBuffers[index], 0, _sendBuffers[index], 0, result);
        _clients[index].RecvLen = result;

        QueueSend(ring, index, result);
    }

    private static void HandleSend(IIORingGroup ring, int index, int result)
    {
        if (result <= 0)
        {
            Console.WriteLine($"Client {index} send error: {result}");
            CloseClient(index);
            return;
        }

        Console.WriteLine($"Client {index} sent {result} bytes");

        // Queue next recv
        QueueRecv(ring, index);
    }

    private static void QueueAccept(IIORingGroup ring, nint listenerFd)
    {
        Console.WriteLine($"[DEBUG] QueueAccept: listenerFd={listenerFd}");
        ring.PrepareAccept(listenerFd, 0, 0, OpAccept);
    }

    private static void QueueRecv(IIORingGroup ring, int index)
    {
        var bufPtr = _recvHandles[index].AddrOfPinnedObject();
        Console.WriteLine($"[DEBUG] QueueRecv: index={index}, socket={_clients[index].Socket}, bufPtr=0x{bufPtr:X}");
        ring.PrepareRecv(_clients[index].Socket, bufPtr, BufferSize, MsgFlags.None, OpRecv | (uint)index);
    }

    private static void QueueSend(IIORingGroup ring, int index, int len)
    {
        var bufPtr = _sendHandles[index].AddrOfPinnedObject();
        Console.WriteLine($"[DEBUG] QueueSend: index={index}, socket={_clients[index].Socket}, bufPtr=0x{bufPtr:X}, len={len}");
        ring.PrepareSend(_clients[index].Socket, bufPtr, len, MsgFlags.None, OpSend | (uint)index);
    }

    private static int FindFreeClientSlot()
    {
        for (var i = 0; i < MaxClients; i++)
        {
            if (!_clients[i].Active)
                return i;
        }
        return -1;
    }

    private static void CloseClient(int index)
    {
        if (_clients[index].Active)
        {
            CloseSocket(_clients[index].Socket);
            _clients[index].Socket = 0;
            _clients[index].Active = false;
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

    [DllImport("ws2_32.dll")]
    private static extern int closesocket(nint socket);

    [DllImport("libc")]
    private static extern int close(int fd);
}
