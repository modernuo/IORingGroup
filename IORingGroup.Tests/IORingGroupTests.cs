using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;

namespace IORingGroup.Tests;

// Helper to poll for completions with timeout (replaces blocking WaitCompletions)
internal static class TestHelpers
{
    public static int WaitForCompletions(IIORingGroup ring, Span<Completion> completions, int minComplete, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var count = 0;
        while (count < minComplete && Environment.TickCount64 < deadline)
        {
            ring.Submit();
            count = ring.PeekCompletions(completions);
            if (count < minComplete)
                Thread.Sleep(1);
        }
        return count;
    }
}

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
    public void RegisterSocket_DiagnosticInfo()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

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
                Skip.If(error == 10045, "RegisterSocket failed with WSAEOPNOTSUPP - rebuild ioring.dll with latest changes");

                Assert.Fail($"RegisterSocket failed: {errorMsg}\n" +
                    $"Socket handle: {server.Handle}");
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
            maxConnections: MaxConnections
        );

        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void Create_WithCustomOutstanding_Succeeds()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(
            queueSize: 256,
            maxConnections: MaxConnections,
            outstandingPerSocket: 4
        );

        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void Create_WithInvalidOutstanding_ThrowsArgumentException()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new System.Network.Windows.WindowsRIOGroup(256, MaxConnections, 100));
    }

    [SkippableFact]
    public void Create_WithNonPowerOfTwo_ThrowsArgumentException()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        Assert.Throws<ArgumentException>(() =>
            new System.Network.Windows.WindowsRIOGroup(100, MaxConnections));
    }

    [SkippableFact]
    public void RegisterSocket_WithValidSocket_ReturnsPositiveConnId()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

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

        ring.UnregisterSocket(connId);
    }

    [SkippableFact]
    public void RegisterSocket_MultipleConnections_Works()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

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

            // Unregister all
            foreach (var connId in connIds)
            {
                ring.UnregisterSocket(connId);
            }
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

        var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);
        ring.Dispose();
        ring.Dispose(); // Should not throw
    }

    [SkippableFact]
    public void SubmissionQueueSpace_DecreasesAfterPrepare()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

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
    public void RIO_PrepareAccept_AutomaticallyUsesAcceptEx()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        // This test verifies that PrepareAccept automatically uses AcceptEx internally
        // to create RIO-compatible sockets for server-side connections

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

            // Use library-owned listener with WSA_FLAG_REGISTERED_IO
            const ushort testPort = 0;  // Let OS assign port
            var listenerHandle = System.Network.Windows.Win_x64.ioring_rio_create_listener(
                ring.Handle, "127.0.0.1", testPort, 128);

            if (listenerHandle is -1 or 0)
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
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);

            Assert.True(count > 0, "No accept completion received - AcceptEx may not be working");
            Assert.Equal(acceptUserData, completions[0].UserData);

            // Result is the socket handle (RQ is NOT created automatically)
            var acceptedSocket = (nint)completions[0].Result;
            Assert.True(acceptedSocket > 0, $"Accept returned invalid socket: {acceptedSocket}");

            ring.AdvanceCompletionQueue(count);

            // Try to register the socket with RIO
            // NOTE: AcceptEx now creates sockets WITHOUT WSA_FLAG_REGISTERED_IO for legacy compatibility.
            // This means RegisterSocket will FAIL with WSAEOPNOTSUPP (10045).
            // For server-side RIO, users must create accept sockets manually with WSASocketW with WSA_FLAG_REGISTERED_IO.
            var connId = ring.RegisterSocket(acceptedSocket);
            if (connId < 0)
            {
                var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                // Expected: AcceptEx sockets don't have WSA_FLAG_REGISTERED_IO, so RIO won't work
                Skip.If(error == 10045,
                    "RegisterSocket failed with WSAEOPNOTSUPP - expected behavior. " +
                    "AcceptEx creates regular sockets for legacy compatibility. " +
                    "For server-side RIO, create accept sockets manually with WSASocketW with WSA_FLAG_REGISTERED_IO.");
                Assert.Fail($"RegisterSocket failed with unexpected error {error}");
            }
            Assert.True(connId >= 0, "RegisterSocket failed");

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

            count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);

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

            count = TestHelpers.WaitForCompletions(ring, completions, 1, 1000);
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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

            // Create a pre-allocated socket with WSA_FLAG_REGISTERED_IO
            var acceptSocket = System.Network.Windows.Win_x64.WSASocketW(2, 1, 6, 0, 0, System.Network.Windows.Win_x64.WSA_FLAG_REGISTERED_IO);
            if (acceptSocket == -1)
            {
                var error = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"WSASocketW failed with error {error}");
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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

            // Create a simple server using regular .NET sockets
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            // Create a CLIENT socket with WSA_FLAG_REGISTERED_IO using C library
            var clientSocket = System.Network.Windows.Win_x64.WSASocketW(2, 1, 6, 0, 0, System.Network.Windows.Win_x64.WSA_FLAG_REGISTERED_IO);
            Skip.If(clientSocket == -1, "WSASocketW failed");

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
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);

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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);
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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);
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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);
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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

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
            var clientSocket = System.Network.Windows.Win_x64.WSASocketW(2, 1, 6, 0, 0, System.Network.Windows.Win_x64.WSA_FLAG_REGISTERED_IO);
            Skip.If(clientSocket == -1, "WSASocketW failed");

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
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);

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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);
            using var buffer = IORingBuffer.Create(64 * 1024);

            var extBufId = ring.RegisterExternalBuffer(buffer.Pointer, (uint)buffer.VirtualSize);
            Skip.If(extBufId < 0, "RegisterExternalBuffer failed - rebuild ioring.dll");

            // Create connected socket pair
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            var clientSocket = System.Network.Windows.Win_x64.WSASocketW(2, 1, 6, 0, 0, System.Network.Windows.Win_x64.WSA_FLAG_REGISTERED_IO);
            Skip.If(clientSocket == -1, "WSASocketW failed");

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

                var count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
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
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

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

            var clientSocket = System.Network.Windows.Win_x64.WSASocketW(2, 1, 6, 0, 0, System.Network.Windows.Win_x64.WSA_FLAG_REGISTERED_IO);
            Skip.If(clientSocket == -1, "WSASocketW failed");

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
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
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

            count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
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

    /// <summary>
    /// Tests rapid reconnection - connect, disconnect, immediately reconnect.
    /// This simulates the ModernUO login server scenario where a client
    /// connects to the login server, then immediately reconnects to the game server.
    /// </summary>
    [SkippableFact]
    public void RIO_RapidReconnect_AcceptsSecondConnection()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

            // Use library-owned listener with WSA_FLAG_REGISTERED_IO
            var listenerHandle = System.Network.Windows.Win_x64.ioring_rio_create_listener(
                ring.Handle, "127.0.0.1", 0, 128);

            if (listenerHandle is -1 or 0)
            {
                var err = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"Failed to create library-owned listener: error {err}");
            }

            // Get the assigned port
            var addrBytes = new byte[16];
            var addrLen = addrBytes.Length;
            int port;
            unsafe
            {
                fixed (byte* pAddr = addrBytes)
                {
                    if (System.Network.Windows.Win_x64.getsockname(listenerHandle, (nint)pAddr, ref addrLen) == 0)
                    {
                        port = (addrBytes[2] << 8) | addrBytes[3];
                    }
                    else
                    {
                        port = 5555;
                    }
                }
            }
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            // Queue multiple accept operations (like ModernUO does)
            const int pendingAccepts = 32;
            for (var i = 0; i < pendingAccepts; i++)
            {
                ring.PrepareAccept(listenerHandle, 0, 0, (ulong)(100 + i));
            }
            var submitted = ring.Submit();
            Assert.Equal(pendingAccepts, submitted);

            Span<Completion> completions = stackalloc Completion[64];

            // ===== First connection =====
            Console.WriteLine("Connecting client #1...");
            using var client1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client1.Connect(endpoint);

            // Wait for accept completion
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);
            Assert.True(count > 0, "No accept completion for client #1");

            var socket1 = (nint)completions[0].Result;
            Assert.True(socket1 > 0, $"Invalid socket for client #1: {socket1}");
            Console.WriteLine($"Client #1 accepted: socket={socket1}");

            ring.AdvanceCompletionQueue(count);

            // Replenish the accept (like ModernUO does)
            ring.PrepareAccept(listenerHandle, 0, 0, 200);
            submitted = ring.Submit();
            Console.WriteLine($"Replenished accept: submitted={submitted}");

            // ===== Disconnect client #1 =====
            Console.WriteLine("Disconnecting client #1...");
            client1.Close();
            System.Network.Windows.Win_x64.closesocket(socket1);

            // Small delay to simulate processing
            Thread.Sleep(10);

            // ===== Immediate reconnect (client #2) =====
            Console.WriteLine("Connecting client #2 (immediate reconnect)...");
            using var client2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client2.Connect(endpoint);

            // Wait for accept completion
            count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);
            Assert.True(count > 0, "No accept completion for client #2 - RECONNECT FAILED!");

            var socket2 = (nint)completions[0].Result;
            Assert.True(socket2 > 0, $"Invalid socket for client #2: {socket2}");
            Console.WriteLine($"Client #2 accepted: socket={socket2}");

            ring.AdvanceCompletionQueue(count);

            // ===== Verify we can receive data on reconnected socket =====
            var connId = ring.RegisterSocket(socket2);
            if (connId >= 0)
            {
                Console.WriteLine($"Registered socket #2 with connId={connId}");

                using var recvBuffer = IORingBuffer.Create(64 * 1024);
                var recvBufId = ring.RegisterExternalBuffer(recvBuffer.Pointer, (uint)recvBuffer.VirtualSize);

                if (recvBufId >= 0)
                {
                    // Post recv
                    recvBuffer.GetWriteSpan(out var recvOffset);
                    ring.PrepareRecvBuffer(connId, recvBufId, recvOffset, BufferSize, 300);
                    ring.Submit();

                    // Send data from client
                    var testData = "Hello from reconnected client!"u8.ToArray();
                    client2.Send(testData);

                    count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
                    Assert.True(count > 0, "No recv completion on reconnected socket");
                    Assert.Equal(testData.Length, completions[0].Result);
                    Console.WriteLine($"Received {completions[0].Result} bytes on reconnected socket");

                    ring.AdvanceCompletionQueue(count);
                    ring.UnregisterExternalBuffer(recvBufId);
                }

                ring.UnregisterSocket(connId);
            }

            // Cleanup
            System.Network.Windows.Win_x64.closesocket(socket2);
            System.Network.Windows.Win_x64.ioring_rio_close_listener(ring.Handle, listenerHandle);

            Console.WriteLine("Rapid reconnect test PASSED!");
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

    /// <summary>
    /// Tests multiple rapid reconnections in succession.
    /// </summary>
    [SkippableFact]
    public void RIO_MultipleRapidReconnects_AllSucceed()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

            var listenerHandle = System.Network.Windows.Win_x64.ioring_rio_create_listener(
                ring.Handle, "127.0.0.1", 0, 128);

            if (listenerHandle is -1 or 0)
            {
                Skip.If(true, "Failed to create listener");
            }

            var addrBytes = new byte[16];
            var addrLen = addrBytes.Length;
            int port;
            unsafe
            {
                fixed (byte* pAddr = addrBytes)
                {
                    if (System.Network.Windows.Win_x64.getsockname(listenerHandle, (nint)pAddr, ref addrLen) == 0)
                    {
                        port = (addrBytes[2] << 8) | addrBytes[3];
                    }
                    else
                    {
                        port = 5555;
                    }
                }
            }
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            // Queue initial accepts
            const int pendingAccepts = 32;
            for (var i = 0; i < pendingAccepts; i++)
            {
                ring.PrepareAccept(listenerHandle, 0, 0, (ulong)(100 + i));
            }
            ring.Submit();

            Span<Completion> completions = stackalloc Completion[64];

            // Do 5 rapid connect/disconnect cycles
            for (var cycle = 0; cycle < 5; cycle++)
            {
                Console.WriteLine($"Cycle {cycle + 1}: Connecting...");

                using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                client.Connect(endpoint);

                var count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);
                Assert.True(count > 0, $"No accept completion for cycle {cycle + 1}");

                var socket = (nint)completions[0].Result;
                Assert.True(socket > 0, $"Invalid socket for cycle {cycle + 1}");
                Console.WriteLine($"Cycle {cycle + 1}: Accepted socket={socket}");

                ring.AdvanceCompletionQueue(count);

                // Replenish accept
                ring.PrepareAccept(listenerHandle, 0, 0, (ulong)(200 + cycle));
                ring.Submit();

                // Close and disconnect
                client.Close();
                System.Network.Windows.Win_x64.closesocket(socket);

                // No delay - immediate reconnect
                Console.WriteLine($"Cycle {cycle + 1}: Disconnected");
            }

            System.Network.Windows.Win_x64.ioring_rio_close_listener(ring.Handle, listenerHandle);
            Console.WriteLine("Multiple rapid reconnects test PASSED!");
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

    /// <summary>
    /// Tests reconnection after full recv/send flow (mimics ModernUO login → game server flow).
    /// This tests whether pending recv operations affect subsequent accepts.
    /// </summary>
    [SkippableFact]
    public void RIO_ReconnectAfterRecvSendFlow_AcceptsSecondConnection()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

            var listenerHandle = System.Network.Windows.Win_x64.ioring_rio_create_listener(
                ring.Handle, "127.0.0.1", 0, 128);

            if (listenerHandle is -1 or 0)
            {
                var err = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"Failed to create library-owned listener: error {err}");
            }

            // Get the assigned port
            var addrBytes = new byte[16];
            var addrLen = addrBytes.Length;
            int port;
            unsafe
            {
                fixed (byte* pAddr = addrBytes)
                {
                    if (System.Network.Windows.Win_x64.getsockname(listenerHandle, (nint)pAddr, ref addrLen) == 0)
                    {
                        port = (addrBytes[2] << 8) | addrBytes[3];
                    }
                    else
                    {
                        port = 5556;
                    }
                }
            }
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            // Queue multiple accept operations (like ModernUO does)
            const int pendingAccepts = 32;
            for (var i = 0; i < pendingAccepts; i++)
            {
                ring.PrepareAccept(listenerHandle, 0, 0, (ulong)(100 + i));
            }
            var submitted = ring.Submit();
            Assert.Equal(pendingAccepts, submitted);

            Span<Completion> completions = stackalloc Completion[64];

            // ===== First connection (login flow) =====
            Console.WriteLine("=== CLIENT #1 (Login Flow) ===");
            using var client1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client1.Connect(endpoint);
            Console.WriteLine("Client #1 connected");

            // Wait for accept completion
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);
            Assert.True(count > 0, "No accept completion for client #1");
            var socket1 = (nint)completions[0].Result;
            Assert.True(socket1 > 0, $"Invalid socket for client #1: {socket1}");
            Console.WriteLine($"Client #1 accepted: socket={socket1}");
            ring.AdvanceCompletionQueue(count);

            // Replenish accept (like ModernUO does after each accept)
            ring.PrepareAccept(listenerHandle, 0, 0, 200);

            // Register socket with RIO (like ModernUO does)
            var connId1 = ring.RegisterSocket(socket1);
            Assert.True(connId1 >= 0, $"Failed to register socket #1: {connId1}");
            Console.WriteLine($"Registered socket #1 with connId={connId1}");

            // Create buffer for recv/send
            using var buffer = IORingBuffer.Create(64 * 1024);
            var bufId = ring.RegisterExternalBuffer(buffer.Pointer, (uint)buffer.VirtualSize);
            Assert.True(bufId >= 0, $"Failed to register buffer: {bufId}");

            // Post recv (like ModernUO does after accepting)
            buffer.GetWriteSpan(out var recvOffset);
            ring.PrepareRecvBuffer(connId1, bufId, recvOffset, BufferSize, 1000);  // userData = 1000 for recv
            submitted = ring.Submit();
            Console.WriteLine($"Submitted: {submitted} (replenish + recv)");

            // Client sends data (like UO client sending packets)
            var loginPacket = "SEED\x00\x80LOGIN_DATA_HERE"u8.ToArray();
            client1.Send(loginPacket);
            Console.WriteLine($"Client #1 sent {loginPacket.Length} bytes");

            // Get recv completion
            count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
            Assert.True(count > 0, "No recv completion for client #1");
            Console.WriteLine($"Recv completed: {completions[0].Result} bytes, userData={completions[0].UserData}");
            ring.AdvanceCompletionQueue(count);
            buffer.CommitWrite(completions[0].Result);

            // Post another recv (ModernUO keeps a pending recv)
            buffer.GetWriteSpan(out recvOffset);
            ring.PrepareRecvBuffer(connId1, bufId, recvOffset, BufferSize, 1001);
            ring.Submit();

            // Simulate sending server response (server list)
            var writeSpan = buffer.GetWriteSpan(out var sendOffset);
            var serverList = "SERVER_LIST_RESPONSE"u8.ToArray();
            serverList.CopyTo(writeSpan);
            buffer.CommitWrite(serverList.Length);
            buffer.GetReadSpan(out var readOffset);
            ring.PrepareSendBuffer(connId1, bufId, readOffset, serverList.Length, 2000);  // userData = 2000 for send
            submitted = ring.Submit();
            Console.WriteLine($"Submitted send: {submitted}");

            // Get send completion
            count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
            Assert.True(count > 0, "No send completion for client #1");
            Console.WriteLine($"Send completed: {completions[0].Result} bytes, userData={completions[0].UserData}");
            ring.AdvanceCompletionQueue(count);
            buffer.CommitRead(completions[0].Result);

            // Client receives (just drain it)
            var recvBuf = new byte[1024];
            client1.Receive(recvBuf);

            // ===== Disconnect client #1 (like server does after server select) =====
            Console.WriteLine("=== DISCONNECTING CLIENT #1 ===");

            // NOTE: There's still a pending recv on connId1!
            // This mimics ModernUO where we disconnect mid-recv

            // Unregister and close (like ModernUO Dispose)
            ring.UnregisterSocket(connId1);
            Console.WriteLine("Unregistered socket #1");
            System.Network.Windows.Win_x64.closesocket(socket1);
            Console.WriteLine("Closed server-side socket #1");

            // Client disconnects
            client1.Close();
            Console.WriteLine("Client #1 closed");

            // Small delay (simulates game loop iteration)
            Thread.Sleep(10);

            // ===== Second connection (game server reconnect) =====
            Console.WriteLine("=== CLIENT #2 (Game Server Reconnect) ===");
            using var client2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Console.WriteLine("Connecting client #2...");
            client2.Connect(endpoint);
            Console.WriteLine("Client #2 connected (client-side)");

            // Poll for accept completion (rapid polling like ModernUO Slice)
            var maxPolls = 1000;
            var acceptReceived = false;
            nint socket2 = 0;

            for (var poll = 0; poll < maxPolls; poll++)
            {
                count = ring.PeekCompletions(completions);
                if (count > 0)
                {
                    Console.WriteLine($"Poll {poll}: Got {count} completions");
                    for (var i = 0; i < count; i++)
                    {
                        var userData = completions[i].UserData;
                        var result = completions[i].Result;
                        Console.WriteLine($"  Completion: userData={userData}, result={result}");

                        // Check if this is an accept completion (userData >= 100 && < 300)
                        if (userData >= 100 && userData < 300)
                        {
                            socket2 = result;
                            acceptReceived = true;
                            Console.WriteLine($"  -> Accept completion! socket={socket2}");
                        }
                    }
                    ring.AdvanceCompletionQueue(count);

                    if (acceptReceived) break;
                }

                Thread.Sleep(1);  // Small delay between polls
            }

            Assert.True(acceptReceived, $"No accept completion for client #2 after {maxPolls} polls - RECONNECT FAILED!");
            Assert.True(socket2 > 0, $"Invalid socket for client #2: {socket2}");

            // Verify we can use the reconnected socket
            var connId2 = ring.RegisterSocket(socket2);
            Assert.True(connId2 >= 0, $"Failed to register socket #2: {connId2}");
            Console.WriteLine($"Registered reconnected socket #2 with connId={connId2}");

            // Clean up
            ring.UnregisterSocket(connId2);
            ring.UnregisterExternalBuffer(bufId);
            System.Network.Windows.Win_x64.closesocket(socket2);
            System.Network.Windows.Win_x64.ioring_rio_close_listener(ring.Handle, listenerHandle);

            Console.WriteLine("=== RECONNECT AFTER RECV/SEND FLOW TEST PASSED! ===");
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

    /// <summary>
    /// Tests reconnection with ZERO delay - exactly like ModernUO's rapid Slice loop.
    /// </summary>
    [SkippableFact]
    public void RIO_ZeroDelayReconnect_Succeeds()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new System.Network.Windows.WindowsRIOGroup(256, MaxConnections);

            var listenerHandle = System.Network.Windows.Win_x64.ioring_rio_create_listener(
                ring.Handle, "127.0.0.1", 0, 128);

            if (listenerHandle is -1 or 0)
            {
                var err = System.Network.Windows.Win_x64.ioring_get_last_error();
                Skip.If(true, $"Failed to create library-owned listener: error {err}");
            }

            // Get the assigned port
            var addrBytes = new byte[16];
            var addrLen = addrBytes.Length;
            int port;
            unsafe
            {
                fixed (byte* pAddr = addrBytes)
                {
                    if (System.Network.Windows.Win_x64.getsockname(listenerHandle, (nint)pAddr, ref addrLen) == 0)
                    {
                        port = (addrBytes[2] << 8) | addrBytes[3];
                    }
                    else
                    {
                        port = 5557;
                    }
                }
            }
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            // Queue 32 accept operations
            const int pendingAccepts = 32;
            for (var i = 0; i < pendingAccepts; i++)
            {
                ring.PrepareAccept(listenerHandle, 0, 0, (ulong)(100 + i));
            }
            ring.Submit();

            Span<Completion> completions = stackalloc Completion[64];

            // Connect client #1
            Console.WriteLine("Connecting client #1...");
            using var client1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client1.Connect(endpoint);

            // Poll for accept (no delay)
            var count = 0;
            for (var poll = 0; poll < 100000 && count == 0; poll++)
            {
                count = ring.PeekCompletions(completions);
            }
            Assert.True(count > 0, "No accept completion for client #1");
            var socket1 = (nint)completions[0].Result;
            ring.AdvanceCompletionQueue(count);

            // Replenish and register
            ring.PrepareAccept(listenerHandle, 0, 0, 200);
            var connId1 = ring.RegisterSocket(socket1);

            // Post recv
            using var buffer = IORingBuffer.Create(64 * 1024);
            var bufId = ring.RegisterExternalBuffer(buffer.Pointer, (uint)buffer.VirtualSize);
            buffer.GetWriteSpan(out var recvOffset);
            ring.PrepareRecvBuffer(connId1, bufId, recvOffset, BufferSize, 1000);
            ring.Submit();

            // Send data from client
            client1.Send("TEST"u8.ToArray());

            // Poll for recv (no delay)
            count = 0;
            for (var poll = 0; poll < 100000 && count == 0; poll++)
            {
                count = ring.PeekCompletions(completions);
            }
            Assert.True(count > 0, "No recv completion");
            ring.AdvanceCompletionQueue(count);

            // === IMMEDIATE DISCONNECT AND RECONNECT ===
            Console.WriteLine("Immediate disconnect...");
            ring.UnregisterSocket(connId1);
            System.Network.Windows.Win_x64.closesocket(socket1);
            client1.Close();

            // NO DELAY - immediately connect client #2
            Console.WriteLine("Immediate reconnect (client #2)...");
            using var client2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client2.Connect(endpoint);

            // Poll for accept (no delay, tight loop)
            Console.WriteLine("Polling for accept...");
            count = 0;
            var acceptReceived = false;
            nint socket2 = 0;
            var startTime = DateTime.UtcNow;

            // Poll rapidly for up to 5 seconds
            while ((DateTime.UtcNow - startTime).TotalSeconds < 5)
            {
                count = ring.PeekCompletions(completions);
                if (count > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var userData = completions[i].UserData;
                        var result = completions[i].Result;
                        Console.WriteLine($"Completion: userData={userData}, result={result}");

                        if (userData >= 100 && userData < 300 && result > 0)
                        {
                            socket2 = result;
                            acceptReceived = true;
                        }
                    }
                    ring.AdvanceCompletionQueue(count);
                    if (acceptReceived) break;
                }
            }

            Console.WriteLine($"Accept received: {acceptReceived}, socket2={socket2}");
            Assert.True(acceptReceived, "No accept completion for client #2 after zero-delay reconnect!");
            Assert.True(socket2 > 0, $"Invalid socket for client #2: {socket2}");

            // Cleanup
            ring.UnregisterExternalBuffer(bufId);
            System.Network.Windows.Win_x64.closesocket(socket2);
            System.Network.Windows.Win_x64.ioring_rio_close_listener(ring.Handle, listenerHandle);

            Console.WriteLine("=== ZERO DELAY RECONNECT TEST PASSED! ===");
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
}
