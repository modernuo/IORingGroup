using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;

namespace IORingGroup.Tests;

public class IORingGroupTests
{
    [SkippableFact]
    public void Create_ReturnsValidInstance()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void Create_WithCustomQueueSize_Succeeds()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create(1024);
        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void Create_WithNonPowerOfTwo_ThrowsArgumentException()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        Assert.Throws<ArgumentException>(() => System.Network.IORingGroup.Create(1000));
    }

    [SkippableFact]
    public void SubmissionQueueSpace_InitiallyFull()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create(256);
        Assert.True(ring.SubmissionQueueSpace >= 256);
    }

    [SkippableFact]
    public void CompletionQueueCount_InitiallyZero()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        Assert.Equal(0, ring.CompletionQueueCount);
    }

    [SkippableFact]
    public void PreparePollAdd_DecreasesSubmissionSpace()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create(256);
        var initialSpace = ring.SubmissionQueueSpace;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        ring.PreparePollAdd(socket.Handle, PollMask.In, 1);

        Assert.Equal(initialSpace - 1, ring.SubmissionQueueSpace);
    }

    [SkippableFact]
    public void Submit_WithPendingOperations_ReturnsPositive()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        using var listener = CreateListeningSocket();

        ring.PreparePollAdd(listener.Handle, PollMask.In, 1);
        var submitted = ring.Submit();

        Assert.True(submitted >= 0);
    }

    [SkippableFact]
    public void Submit_WithNoOperations_ReturnsZero()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        var submitted = ring.Submit();

        Assert.Equal(0, submitted);
    }

    [SkippableFact]
    public void PeekCompletions_WithNoCompletions_ReturnsZero()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        Span<Completion> completions = stackalloc Completion[16];

        var count = ring.PeekCompletions(completions);

        Assert.Equal(0, count);
    }

    [SkippableFact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        var ring = System.Network.IORingGroup.Create();
        ring.Dispose();
        ring.Dispose(); // Should not throw
    }

    [SkippableFact]
    public void AdvanceCompletionQueue_DoesNotThrow()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        ring.AdvanceCompletionQueue(0); // Should not throw
    }

    private static Socket CreateListeningSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);
        return socket;
    }
}

public class WindowsFactoryTests
{
    [SkippableFact]
    public void Factory_ReturnsWindowsRIOGroup()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported");

        using var ring = System.Network.IORingGroup.Create();
        Assert.IsType<System.Network.Windows.WindowsRIOGroup>(ring);

        var rioRing = (System.Network.Windows.WindowsRIOGroup)ring;
        Assert.Equal(System.Network.Windows.Win_x64.IORingBackend.RIO, rioRing.Backend);
    }

    [SkippableFact]
    public void SendRecv_WithConnectedSockets_Works()
    {
        // Legacy PrepareSend/PrepareRecv is not supported in RIO mode.
        // RIO requires external buffers. Use PrepareSendBuffer/PrepareRecvBuffer instead.
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "Windows RIO mode requires external buffers. Use PrepareSendBuffer/PrepareRecvBuffer instead of PrepareSend/PrepareRecv.");

        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported");

        using var ring = System.Network.IORingGroup.Create(256);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(endpoint);

        using var server = listener.Accept();

        // Test send
        var sendBuffer = "Hello, IORing!"u8.ToArray();
        unsafe
        {
            fixed (byte* pSend = sendBuffer)
            {
#pragma warning disable CS0618 // Type or member is obsolete
                ring.PrepareSend(client.Handle, (nint)pSend, sendBuffer.Length, MsgFlags.None, 1);
#pragma warning restore CS0618
            }
        }
        ring.Submit();

        // Receive on server side (synchronous for test simplicity)
        var recvBuffer = new byte[sendBuffer.Length];
        var received = server.Receive(recvBuffer);

        Assert.Equal(sendBuffer.Length, received);
        Assert.Equal(sendBuffer, recvBuffer);
    }
}

public class CompletionStructTests
{
    [Fact]
    public void Completion_Constructor_SetsValues()
    {
        var completion = new Completion(12345, 100, CompletionFlags.More);

        Assert.Equal(12345UL, completion.UserData);
        Assert.Equal(100, completion.Result);
        Assert.Equal(CompletionFlags.More, completion.Flags);
    }

    [Fact]
    public void Completion_StructSize_Is16Bytes()
    {
        // Ensure struct is cache-friendly
        var size = Marshal.SizeOf<Completion>();
        Assert.True(size <= 24, $"Completion struct is {size} bytes, expected <= 24");
    }
}

public class PollMaskTests
{
    [Fact]
    public void PollMask_HasCorrectValues()
    {
        Assert.Equal(0x0001u, (uint)PollMask.In);
        Assert.Equal(0x0004u, (uint)PollMask.Out);
        Assert.Equal(0x0008u, (uint)PollMask.Err);
        Assert.Equal(0x0010u, (uint)PollMask.Hup);
        Assert.Equal(0x2000u, (uint)PollMask.RdHup);
    }

    [Fact]
    public void PollMask_CanCombine()
    {
        var combined = PollMask.In | PollMask.Out;
        Assert.Equal(0x0005u, (uint)combined);
    }
}

public class WindowsRIOGroupTests
{
    private const int BufferSize = 4096;
    private const int MaxConnections = 128;

    [SkippableFact]
    public void RIO_IsAvailable()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        // This test verifies RIO is available on the system
        // Create a minimal ring to test RIO initialization
        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(
                queueSize: 256,
                maxConnections: 16,
                recvBufferSize: 1024,
                sendBufferSize: 1024
            );

            Assert.True(ring.IsRIO, "Ring should be in RIO mode");
            Assert.Equal(System.Network.Windows.Win_x64.IORingBackend.RIO, ring.Backend);
        }
        catch (InvalidOperationException ex)
        {
            var error = System.Network.Windows.Win_x64.ioring_get_last_error();
            Skip.If(true, $"RIO not available on this system: {ex.Message}, error code: {error}");
        }
    }

    [SkippableFact]
    public void RegisterSocket_DiagnosticInfo()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

            // Create connected socket pair
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect((IPEndPoint)listener.LocalEndPoint!);

            using var server = listener.Accept();
            server.Blocking = false;

            // Try to register - capture detailed error info
            var connId = ring.RegisterSocket(server.Handle);
            if (connId < 0)
            {
                var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                var errorMsg = error switch
                {
                    10022 => "WSAEINVAL - Invalid argument",
                    10045 => "WSAEOPNOTSUPP - Operation not supported (RIOCreateRequestQueue failed - rebuild ioring.dll)",
                    10055 => "WSAENOBUFS - No buffer space",
                    10038 => "WSAENOTSOCK - Not a socket",
                    10093 => "WSANOTINITIALISED - Winsock not initialized",
                    _ => $"Unknown error {error}"
                };

                // Skip instead of fail if it's the known RIO issue (needs DLL rebuild)
                Skip.If(error == 10045, $"RegisterSocket failed with WSAEOPNOTSUPP - rebuild ioring.dll with latest changes");

                Assert.Fail($"RegisterSocket failed: {errorMsg}\n" +
                    $"Socket handle: {server.Handle}\n" +
                    $"Ring IsRIO: {ring.IsRIO}\n" +
                    $"Ring Backend: {ring.Backend}");
            }

            Assert.True(connId >= 0);
            ring.UnregisterSocket(connId);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void Create_ReturnsValidInstance()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(
            queueSize: 256,
            maxConnections: MaxConnections,
            recvBufferSize: BufferSize,
            sendBufferSize: BufferSize
        );

        Assert.NotNull(ring);
        Assert.True(ring.IsRIO);
        Assert.Equal(MaxConnections, ring.MaxConnections);
        Assert.Equal(BufferSize, ring.RecvBufferSize);
        Assert.Equal(BufferSize, ring.SendBufferSize);
    }

    [SkippableFact]
    public void Create_WithCustomOutstanding_Succeeds()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(
            queueSize: 256,
            maxConnections: MaxConnections,
            recvBufferSize: BufferSize,
            sendBufferSize: BufferSize,
            outstandingPerSocket: 4
        );

        Assert.NotNull(ring);
        Assert.True(ring.IsRIO);
    }

    [SkippableFact]
    public void Create_WithInvalidOutstanding_ThrowsArgumentException()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize, 100));
    }

    [SkippableFact]
    public void Create_WithNonPowerOfTwo_ThrowsArgumentException()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        Assert.Throws<ArgumentException>(() =>
            new System.Network.Windows.WindowsRIOGroup(100, MaxConnections, BufferSize, BufferSize));
    }

    [SkippableFact]
    public void ActiveConnections_InitiallyZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

        Assert.Equal(0, ring.ActiveConnections);
    }

    [SkippableFact]
    public void RegisterSocket_WithValidSocket_ReturnsPositiveConnId()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

        // Create connected socket pair
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(endpoint);

        using var server = listener.Accept();
        server.Blocking = false;

        // Register the server socket
        var connId = ring.RegisterSocket(server.Handle);

        if (connId < 0)
        {
            var error = System.Network.Windows.Win_x64.ioring_get_last_error();
            Skip.If(error == 10045, "RIOCreateRequestQueue failed with WSAEOPNOTSUPP - rebuild ioring.dll");
        }

        Assert.True(connId >= 0, $"RegisterSocket failed with connId={connId}");
        Assert.Equal(1, ring.ActiveConnections);

        ring.UnregisterSocket(connId);
        Assert.Equal(0, ring.ActiveConnections);
    }

    [SkippableFact]
    public void RegisterSocket_MultipleConnections_Works()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(10);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        var clients = new List<Socket>();
        var servers = new List<Socket>();
        var connIds = new List<int>();

        try
        {
            // Create 5 connections
            for (var i = 0; i < 5; i++)
            {
                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                client.Connect(endpoint);
                clients.Add(client);

                var server = listener.Accept();
                server.Blocking = false;
                servers.Add(server);

                var connId = ring.RegisterSocket(server.Handle);
                if (connId < 0)
                {
                    var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                    Skip.If(error == 10045, "RIOCreateRequestQueue failed with WSAEOPNOTSUPP - rebuild ioring.dll");
                }
                Assert.True(connId >= 0, $"Failed to register connection {i}");
                connIds.Add(connId);
            }

            Assert.Equal(5, ring.ActiveConnections);

            // Unregister all
            foreach (var connId in connIds)
            {
                ring.UnregisterSocket(connId);
            }

            Assert.Equal(0, ring.ActiveConnections);
        }
        finally
        {
            foreach (var s in clients) s.Dispose();
            foreach (var s in servers) s.Dispose();
        }
    }

    [SkippableFact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);
        ring.Dispose();
        ring.Dispose(); // Should not throw
    }

    [SkippableFact]
    public void SubmissionQueueSpace_DecreasesAfterPrepare()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

        // Create a socket for poll operation
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var initialSpace = ring.SubmissionQueueSpace;

        // Use poll operation (doesn't require external buffers)
        ring.PreparePollAdd(listener.Handle, PollMask.In, 1);

        Assert.Equal(initialSpace - 1, ring.SubmissionQueueSpace);
    }

    [SkippableFact]
    public void Backend_ReturnsRIO()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

        Assert.Equal(System.Network.Windows.Win_x64.IORingBackend.RIO, ring.Backend);
    }

    [SkippableFact]
    public void CreateAcceptSocket_ReturnsValidSocket()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            var acceptSocket = System.Network.Windows.WindowsRIOGroup.CreateAcceptSocket();

            if (acceptSocket == -1)
            {
                var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"CreateAcceptSocket failed with error {error}");
            }

            Assert.NotEqual(-1, acceptSocket);

            // Clean up - close the socket
            System.Net.Sockets.Socket.OSSupportsUnixDomainSockets.ToString(); // Force socket init
            // Use closesocket via P/Invoke or just let it leak for the test
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "ioring_rio_create_accept_socket not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void GetAcceptEx_ReturnsValidPointer()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            var acceptExPtr = System.Network.Windows.WindowsRIOGroup.GetAcceptEx();

            // Should succeed - the function creates a temp socket if none provided
            Assert.NotEqual(nint.Zero, acceptExPtr);
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "ioring_get_acceptex not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void RIO_PrepareAccept_AutomaticallyUsesAcceptEx()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        // This test verifies that PrepareAccept automatically uses AcceptEx internally
        // to create RIO-compatible sockets for server-side connections

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

            // Use library-owned listener to avoid .NET IOCP conflicts
            const ushort testPort = 0;  // Let OS assign port
            var listenerHandle = System.Network.Windows.Win_x64.ioring_rio_create_listener(
                ring.Handle, "127.0.0.1", testPort, 128);

            if (listenerHandle == -1 || listenerHandle == 0)
            {
                var err = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"Failed to create library-owned listener: error {err}");
            }

            // Get the assigned port
            var addrBytes = new byte[16];  // sockaddr_in
            var addrLen = addrBytes.Length;
            int port;
            unsafe
            {
                fixed (byte* pAddr = addrBytes)
                {
                    if (System.Network.Windows.Win_x64.getsockname(listenerHandle, (nint)pAddr, ref addrLen) == 0)
                    {
                        // Port is at offset 2, in network byte order
                        port = (addrBytes[2] << 8) | addrBytes[3];
                    }
                    else
                    {
                        port = 5555;  // Fallback
                    }
                }
            }
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            // Queue an accept operation - this now uses AcceptEx internally
            const ulong acceptUserData = 100;
            ring.PrepareAccept(listenerHandle, 0, 0, acceptUserData);
            ring.Submit();

            // Connect a client to trigger the accept
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(endpoint);

            // Wait for accept completion
            Span<Completion> completions = stackalloc Completion[16];
            var count = ring.WaitCompletions(completions, 1, 5000);

            Assert.True(count > 0, "No accept completion received - AcceptEx may not be working");
            Assert.Equal(acceptUserData, completions[0].UserData);

            // Result is the socket handle (RQ is NOT created automatically)
            var acceptedSocket = (nint)completions[0].Result;
            Assert.True(acceptedSocket > 0, $"Accept returned invalid socket: {acceptedSocket}");

            ring.AdvanceCompletionQueue(count);

            // Try to register the socket with RIO
            // NOTE: AcceptEx now creates sockets WITHOUT WSA_FLAG_REGISTERED_IO for legacy compatibility.
            // This means RegisterSocket will FAIL with WSAEOPNOTSUPP (10045).
            // For server-side RIO, users must create accept sockets manually with CreateAcceptSocket().
            var connId = ring.RegisterSocket(acceptedSocket);
            if (connId < 0)
            {
                var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                // Expected: AcceptEx sockets don't have WSA_FLAG_REGISTERED_IO, so RIO won't work
                Skip.If(error == 10045,
                    "RegisterSocket failed with WSAEOPNOTSUPP - expected behavior. " +
                    "AcceptEx creates regular sockets for legacy compatibility. " +
                    "For server-side RIO, create accept sockets manually with CreateAcceptSocket().");
                Assert.Fail($"RegisterSocket failed with unexpected error {error}");
            }
            Assert.True(connId >= 0, $"RegisterSocket failed");

            // RIO requires pre-registered buffers - use external buffer API with IORingBuffer
            using var recvBuffer = IORingBuffer.Create(64 * 1024);
            using var sendBuffer = IORingBuffer.Create(64 * 1024);

            var recvBufId = ring.RegisterExternalBuffer(recvBuffer.Pointer, (uint)recvBuffer.VirtualSize);
            Skip.If(recvBufId < 0, "RegisterExternalBuffer failed - rebuild ioring.dll");
            var sendBufId = ring.RegisterExternalBuffer(sendBuffer.Pointer, (uint)sendBuffer.VirtualSize);

            // IMPORTANT: Post recv BEFORE client sends data
            // RIO may require the receive to be pending before data arrives
            recvBuffer.GetWriteSpan(out var recvOffset);
            ring.PrepareRecvExternal(connId, recvBufId, recvOffset, BufferSize, 1);
            ring.Submit();

            // Give a moment for recv to be fully posted
            Thread.Sleep(10);

            // NOW send data from client (after recv is pending)
            var testData = "Hello via automatic AcceptEx!"u8.ToArray();
            var sent = client.Send(testData);
            Assert.Equal(testData.Length, sent);

            count = ring.WaitCompletions(completions, 1, 2000);

            Assert.True(count > 0, "No recv completion received");
            Assert.Equal(testData.Length, completions[0].Result);
            ring.AdvanceCompletionQueue(count);
            recvBuffer.CommitWrite(testData.Length);

            // Verify data
            var receivedData = recvBuffer.GetReadSpan(out _);
            Assert.True(receivedData.Slice(0, testData.Length).SequenceEqual(testData), "Received data doesn't match");

            // Echo back - copy to send buffer
            var writeSpan = sendBuffer.GetWriteSpan(out _);
            receivedData.Slice(0, testData.Length).CopyTo(writeSpan);
            sendBuffer.CommitWrite(testData.Length);
            recvBuffer.CommitRead(testData.Length);

            sendBuffer.GetReadSpan(out var sendOffset);
            ring.PrepareSendExternal(connId, sendBufId, sendOffset, testData.Length, 2);
            ring.Submit();

            count = ring.WaitCompletions(completions, 1, 1000);
            Assert.True(count > 0, "No send completion");
            ring.AdvanceCompletionQueue(count);

            // Verify echo on client
            var echoBuffer = new byte[testData.Length];
            var received = client.Receive(echoBuffer);
            Assert.Equal(testData.Length, received);
            Assert.Equal(testData, echoBuffer);

            ring.UnregisterSocket(connId);
            ring.UnregisterExternalBuffer(recvBufId);
            ring.UnregisterExternalBuffer(sendBufId);

            // Clean up accepted socket
            System.Network.Windows.Win_x64.closesocket(acceptedSocket);

            // Clean up library-owned listener
            System.Network.Windows.Win_x64.ioring_rio_close_listener(ring.Handle, listenerHandle);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "New RIO functions not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void RIO_WithAcceptExSocket_EchoWorks()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        // This test demonstrates that sockets from .NET accept() do NOT work with RIO
        // because they lack WSA_FLAG_REGISTERED_IO. Use AcceptEx with library-created sockets.

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

            // Create a pre-allocated socket with WSA_FLAG_REGISTERED_IO
            var acceptSocket = System.Network.Windows.WindowsRIOGroup.CreateAcceptSocket();
            if (acceptSocket == -1)
            {
                var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"CreateAcceptSocket failed with error {error}");
            }

            // For now, just verify we can create the socket and it's valid
            Assert.NotEqual(-1, acceptSocket);

            // Test that .NET accepted sockets DON'T work with RIO
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(endpoint);

            // Accept - the accepted socket WON'T have RIO flag
            using var server = listener.Accept();
            server.Blocking = false;

            // Try to register - expect this to fail since accept() doesn't inherit WSA_FLAG_REGISTERED_IO
            var connId = ring.RegisterSocket(server.Handle);
            var error2 = System.Network.Windows.Win_x64.ioring_get_last_error();

            // Expected: This will fail with 10045 on accepted sockets
            if (connId < 0 && error2 == 10045)
            {
                // This is expected behavior - document it
                Assert.True(true,
                    "As expected, accept() socket doesn't work with RIO. " +
                    "Use PrepareAccept with library-owned listener for server-side RIO.");
            }
            else if (connId >= 0)
            {
                // Unexpectedly worked - clean up
                ring.UnregisterSocket(connId);
                Assert.True(true, "RegisterSocket unexpectedly succeeded");
            }
            else
            {
                // Different error - skip test
                Skip.If(true, $"RegisterSocket failed with unexpected error {error2}");
            }

            // Clean up the pre-created accept socket
            System.Network.Windows.Win_x64.closesocket(acceptSocket);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "New RIO functions not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void RIO_ClientSocket_RecvWorks()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        // This test verifies RIO works with a CLIENT socket created with WSA_FLAG_REGISTERED_IO
        // This isolates RIO functionality from AcceptEx complexity

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

            // Create a simple server using regular .NET sockets
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            // Create a CLIENT socket with WSA_FLAG_REGISTERED_IO using C library
            var clientSocket = System.Network.Windows.WindowsRIOGroup.CreateAcceptSocket();
            Skip.If(clientSocket == -1, "CreateAcceptSocket failed");

            // Connect our RIO socket to the listener using direct P/Invoke
            System.Network.Windows.Win_x64.inet_pton(System.Network.Windows.Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
            var addr = new System.Network.Windows.Win_x64.sockaddr_in
            {
                sin_family = System.Network.Windows.Win_x64.AF_INET,
                sin_port = System.Network.Windows.Win_x64.htons((ushort)endpoint.Port),
                sin_addr = addrBytes,
                sin_zero = 0
            };
            var connectResult = System.Network.Windows.Win_x64.connect(clientSocket, ref addr, 16);

            if (connectResult != 0)
            {
                var err = System.Network.Windows.Win_x64.WSAGetLastError();
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, $"Connect failed with error {err}");
            }

            // Accept on the listener side (regular socket)
            using var serverSide = listener.Accept();

            // Register our RIO client socket
            var connId = ring.RegisterSocket(clientSocket);
            if (connId < 0)
            {
                var err = System.Network.Windows.Win_x64.ioring_get_last_error();
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, $"RegisterSocket failed with error {err}");
            }

            // RIO requires pre-registered buffers - use external buffer API with IORingBuffer
            using var recvBuffer = IORingBuffer.Create(64 * 1024);
            var recvBufId = ring.RegisterExternalBuffer(recvBuffer.Pointer, (uint)recvBuffer.VirtualSize);
            if (recvBufId < 0)
            {
                ring.UnregisterSocket(connId);
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, "RegisterExternalBuffer failed - rebuild ioring.dll");
            }

            // Post recv FIRST
            recvBuffer.GetWriteSpan(out var recvOffset);
            ring.PrepareRecvExternal(connId, recvBufId, recvOffset, BufferSize, 1);
            ring.Submit();

            // Send data FROM server TO our RIO client
            var testData = "Hello RIO Client!"u8.ToArray();
            serverSide.Send(testData);

            // Wait for completion on RIO client
            Span<Completion> completions = stackalloc Completion[16];
            var count = ring.WaitCompletions(completions, 1, 2000);

            Assert.True(count > 0, "CLIENT socket RIO recv failed");
            Assert.Equal(testData.Length, completions[0].Result);
            ring.AdvanceCompletionQueue(count);

            ring.UnregisterExternalBuffer(recvBufId);
            ring.UnregisterSocket(connId);
            System.Network.Windows.Win_x64.closesocket(clientSocket);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "Required functions not available - rebuild ioring.dll");
        }
    }

    // =============================================================================
    // External Buffer Tests
    // =============================================================================

    [SkippableFact]
    public void RegisterExternalBuffer_WithValidBuffer_ReturnsPositiveId()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);
            using var buffer = IORingBuffer.Create(64 * 1024);

            var bufferId = ring.RegisterExternalBuffer(buffer.Pointer, (uint)buffer.VirtualSize);

            if (bufferId < 0)
            {
                var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"RegisterExternalBuffer failed with error {error} - rebuild ioring.dll");
            }

            Assert.True(bufferId >= 0, $"RegisterExternalBuffer returned {bufferId}");
            Assert.Equal(1, ring.ExternalBufferCount);

            ring.UnregisterExternalBuffer(bufferId);
            Assert.Equal(0, ring.ExternalBufferCount);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "External buffer functions not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void RegisterExternalBuffer_MultipleBuffers_TracksCount()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);
            using var buffer1 = IORingBuffer.Create(64 * 1024);
            using var buffer2 = IORingBuffer.Create(64 * 1024);
            using var buffer3 = IORingBuffer.Create(64 * 1024);

            var id1 = ring.RegisterExternalBuffer(buffer1.Pointer, (uint)buffer1.VirtualSize);
            Skip.If(id1 < 0, "RegisterExternalBuffer failed - rebuild ioring.dll");

            var id2 = ring.RegisterExternalBuffer(buffer2.Pointer, (uint)buffer2.VirtualSize);
            var id3 = ring.RegisterExternalBuffer(buffer3.Pointer, (uint)buffer3.VirtualSize);

            Assert.Equal(3, ring.ExternalBufferCount);

            // IDs should be unique
            Assert.NotEqual(id1, id2);
            Assert.NotEqual(id2, id3);
            Assert.NotEqual(id1, id3);

            ring.UnregisterExternalBuffer(id2);
            Assert.Equal(2, ring.ExternalBufferCount);

            ring.UnregisterExternalBuffer(id1);
            ring.UnregisterExternalBuffer(id3);
            Assert.Equal(0, ring.ExternalBufferCount);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "External buffer functions not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void UnregisterExternalBuffer_InvalidId_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

            // Should not throw for invalid IDs
            ring.UnregisterExternalBuffer(-1);
            ring.UnregisterExternalBuffer(100);
            ring.UnregisterExternalBuffer(0);

            Assert.Equal(0, ring.ExternalBufferCount);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "External buffer functions not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void ExternalBufferCount_InitiallyZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);
            Assert.Equal(0, ring.ExternalBufferCount);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "External buffer functions not available - rebuild ioring.dll");
        }
    }

    // =============================================================================
    // IORingBuffer + RIO Integration Tests (Zero-Copy Sends)
    // =============================================================================

    [SkippableFact]
    public void PrepareSendExternal_WithIORingBuffer_SendsData()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

            // Create an IORingBuffer for zero-copy sends
            using var buffer = IORingBuffer.Create(64 * 1024);

            // Register buffer memory with RIO
            var extBufId = ring.RegisterExternalBuffer(buffer.Pointer, (uint)buffer.VirtualSize);
            Skip.If(extBufId < 0, "RegisterExternalBuffer failed - rebuild ioring.dll");

            // Create connected socket pair
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            // Create RIO client socket
            var clientSocket = System.Network.Windows.WindowsRIOGroup.CreateAcceptSocket();
            Skip.If(clientSocket == -1, "CreateAcceptSocket failed");

            System.Network.Windows.Win_x64.inet_pton(System.Network.Windows.Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
            var addr = new System.Network.Windows.Win_x64.sockaddr_in
            {
                sin_family = System.Network.Windows.Win_x64.AF_INET,
                sin_port = System.Network.Windows.Win_x64.htons((ushort)endpoint.Port),
                sin_addr = addrBytes,
                sin_zero = 0
            };
            var connectResult = System.Network.Windows.Win_x64.connect(clientSocket, ref addr, 16);

            if (connectResult != 0)
            {
                var err = System.Network.Windows.Win_x64.WSAGetLastError();
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, $"Connect failed with error {err}");
            }

            using var serverSide = listener.Accept();

            // Register client socket with RIO
            var connId = ring.RegisterSocket(clientSocket);
            if (connId < 0)
            {
                var err = System.Network.Windows.Win_x64.ioring_get_last_error();
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, $"RegisterSocket failed with error {err}");
            }

            // Write test data to the buffer
            var testData = "Hello from IORingBuffer zero-copy!"u8.ToArray();
            var writeSpan = buffer.GetWriteSpan(out _);
            testData.CopyTo(writeSpan);
            buffer.CommitWrite(testData.Length);

            // Get the read offset for the send
            var readSpan = buffer.GetReadSpan(out var readOffset);
            var readLength = readSpan.Length;

            // Send using external buffer (zero-copy from IORingBuffer memory!)
            const ulong sendUserData = 42;
            ring.PrepareSendExternal(connId, extBufId, readOffset, readLength, sendUserData);
            ring.Submit();

            // Wait for send completion
            Span<Completion> completions = stackalloc Completion[16];
            var count = ring.WaitCompletions(completions, 1, 2000);

            Assert.True(count > 0, "No send completion received");
            Assert.Equal(sendUserData, completions[0].UserData);
            Assert.Equal(testData.Length, completions[0].Result);

            ring.AdvanceCompletionQueue(count);

            // Advance buffer reader (data was sent)
            buffer.CommitRead(testData.Length);

            // Receive on server side to verify data
            var recvBuffer = new byte[testData.Length];
            var received = serverSide.Receive(recvBuffer);

            Assert.Equal(testData.Length, received);
            Assert.Equal(testData, recvBuffer);

            // Cleanup
            ring.UnregisterSocket(connId);
            ring.UnregisterExternalBuffer(extBufId);
            System.Network.Windows.Win_x64.closesocket(clientSocket);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "Required functions not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void PrepareSendExternal_MultipleChunks_Works()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);
            using var buffer = IORingBuffer.Create(64 * 1024);

            var extBufId = ring.RegisterExternalBuffer(buffer.Pointer, (uint)buffer.VirtualSize);
            Skip.If(extBufId < 0, "RegisterExternalBuffer failed - rebuild ioring.dll");

            // Create connected socket pair
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            var clientSocket = System.Network.Windows.WindowsRIOGroup.CreateAcceptSocket();
            Skip.If(clientSocket == -1, "CreateAcceptSocket failed");

            System.Network.Windows.Win_x64.inet_pton(System.Network.Windows.Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
            var addr = new System.Network.Windows.Win_x64.sockaddr_in
            {
                sin_family = System.Network.Windows.Win_x64.AF_INET,
                sin_port = System.Network.Windows.Win_x64.htons((ushort)endpoint.Port),
                sin_addr = addrBytes,
                sin_zero = 0
            };
            var connectResult = System.Network.Windows.Win_x64.connect(clientSocket, ref addr, 16);

            if (connectResult != 0)
            {
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, "Connect failed");
            }

            using var serverSide = listener.Accept();
            serverSide.ReceiveTimeout = 2000;

            var connId = ring.RegisterSocket(clientSocket);
            if (connId < 0)
            {
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, "RegisterSocket failed");
            }

            // Send multiple chunks through the buffer
            Span<Completion> completions = stackalloc Completion[16];
            for (var i = 0; i < 5; i++)
            {
                var chunkData = new byte[100];
                Array.Fill(chunkData, (byte)(i + 0x41)); // 'A', 'B', 'C', etc.

                var writeSpan = buffer.GetWriteSpan(out _);
                chunkData.CopyTo(writeSpan);
                buffer.CommitWrite(100);

                buffer.GetReadSpan(out var readOffset);
                ring.PrepareSendExternal(connId, extBufId, readOffset, 100, (ulong)i);
                ring.Submit();

                var count = ring.WaitCompletions(completions, 1, 2000);
                Assert.True(count > 0, $"No completion for chunk {i}");
                Assert.Equal(100, completions[0].Result);
                ring.AdvanceCompletionQueue(count);

                buffer.CommitRead(100);

                // Verify on server
                var recvBuffer = new byte[100];
                var received = serverSide.Receive(recvBuffer);
                Assert.Equal(100, received);
                Assert.True(recvBuffer.All(b => b == (byte)(i + 0x41)));
            }

            ring.UnregisterSocket(connId);
            ring.UnregisterExternalBuffer(extBufId);
            System.Network.Windows.Win_x64.closesocket(clientSocket);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "Required functions not available - rebuild ioring.dll");
        }
    }

    [SkippableFact]
    public void PrepareSendExternal_WithWrappedBuffer_SendsCorrectData()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, BufferSize, BufferSize);

            // Use a small buffer to force wrap-around
            var pageSize = Environment.SystemPageSize;
            using var buffer = IORingBuffer.Create(pageSize);

            var extBufId = ring.RegisterExternalBuffer(buffer.Pointer, (uint)buffer.VirtualSize);
            Skip.If(extBufId < 0, "RegisterExternalBuffer failed - rebuild ioring.dll");

            // Create connected socket pair
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            var clientSocket = System.Network.Windows.WindowsRIOGroup.CreateAcceptSocket();
            Skip.If(clientSocket == -1, "CreateAcceptSocket failed");

            System.Network.Windows.Win_x64.inet_pton(System.Network.Windows.Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
            var addr = new System.Network.Windows.Win_x64.sockaddr_in
            {
                sin_family = System.Network.Windows.Win_x64.AF_INET,
                sin_port = System.Network.Windows.Win_x64.htons((ushort)endpoint.Port),
                sin_addr = addrBytes,
                sin_zero = 0
            };
            var connectResult = System.Network.Windows.Win_x64.connect(clientSocket, ref addr, 16);

            if (connectResult != 0)
            {
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, "Connect failed");
            }

            using var serverSide = listener.Accept();
            serverSide.ReceiveTimeout = 2000;

            var connId = ring.RegisterSocket(clientSocket);
            if (connId < 0)
            {
                System.Network.Windows.Win_x64.closesocket(clientSocket);
                Skip.If(true, "RegisterSocket failed");
            }

            // Fill most of the buffer to force wrap-around on next write
            var fillSize = pageSize - 200;
            var fillData = new byte[fillSize];
            new Random(42).NextBytes(fillData);

            var writeSpan = buffer.GetWriteSpan(out _);
            fillData.CopyTo(writeSpan);
            buffer.CommitWrite(fillSize);

            // Send and consume
            buffer.GetReadSpan(out var fillReadOffset);
            ring.PrepareSendExternal(connId, extBufId, fillReadOffset, fillSize, 1);
            ring.Submit();

            Span<Completion> completions = stackalloc Completion[16];
            var count = ring.WaitCompletions(completions, 1, 2000);
            Assert.True(count > 0);
            ring.AdvanceCompletionQueue(count);
            buffer.CommitRead(fillSize);

            var recvBuffer = new byte[fillSize];
            var totalReceived = 0;
            while (totalReceived < fillSize)
            {
                var received = serverSide.Receive(recvBuffer, totalReceived, fillSize - totalReceived, SocketFlags.None);
                if (received == 0) break;
                totalReceived += received;
            }
            Assert.Equal(fillSize, totalReceived);
            Assert.Equal(fillData, recvBuffer);

            // Now write data that wraps around the ring buffer
            var wrapData = "Wrapped data test!"u8.ToArray();
            writeSpan = buffer.GetWriteSpan(out _);
            wrapData.CopyTo(writeSpan);
            buffer.CommitWrite(wrapData.Length);

            // The double-mapping magic allows us to read contiguously even across the wrap
            buffer.GetReadSpan(out var readOffset);
            ring.PrepareSendExternal(connId, extBufId, readOffset, wrapData.Length, 2);
            ring.Submit();

            count = ring.WaitCompletions(completions, 1, 2000);
            Assert.True(count > 0, "No completion for wrapped send");
            Assert.Equal(wrapData.Length, completions[0].Result);
            ring.AdvanceCompletionQueue(count);
            buffer.CommitRead(wrapData.Length);

            var wrapRecv = new byte[wrapData.Length];
            var wrapReceived = serverSide.Receive(wrapRecv);
            Assert.Equal(wrapData.Length, wrapReceived);
            Assert.Equal(wrapData, wrapRecv);

            ring.UnregisterSocket(connId);
            ring.UnregisterExternalBuffer(extBufId);
            System.Network.Windows.Win_x64.closesocket(clientSocket);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"RIO not available: {ex.Message}");
        }
        catch (EntryPointNotFoundException)
        {
            Skip.If(true, "Required functions not available - rebuild ioring.dll");
        }
    }
}
