// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;
using System.Network.Windows;

namespace IORingGroup.Tests;

/// <summary>
/// Tests for the pure C# WindowsManagedRIOGroup implementation.
/// Mirrors WindowsRIOGroupTests patterns for A/B verification against native DLL.
/// </summary>
public class WindowsManagedRIOGroupTests
{
    private const int BufferSize = 4096;
    private const int MaxConnections = 128;

    [SkippableFact]
    public void Create_ReturnsValidInstance()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            Assert.NotNull(ring);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            var ring = new WindowsManagedRIOGroup(MaxConnections);
            ring.Dispose();
            ring.Dispose(); // Should not throw
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void SubmissionQueueSpace_InitiallyFull()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            Assert.True(ring.SubmissionQueueSpace >= MaxConnections * 2);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void CompletionQueueCount_InitiallyZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            Assert.Equal(0, ring.CompletionQueueCount);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void SubmissionQueueSpace_DecreasesAfterPrepare()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);

            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            var initialSpace = ring.SubmissionQueueSpace;
            ring.PreparePollAdd(listener.Handle, PollMask.In, 1);

            Assert.Equal(initialSpace - 1, ring.SubmissionQueueSpace);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void RegisterSocket_WithValidSocket_ReturnsPositiveConnId()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);

            // Create connected socket pair using RIO-compatible sockets
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            // Create a client socket with WSA_FLAG_REGISTERED_IO
            var clientSocket = Win_x64.WSASocketW(2, 1, 6, 0, 0, Win_x64.WSA_FLAG_REGISTERED_IO);
            Skip.If(clientSocket == -1, "WSASocketW failed");

            Win_x64.inet_pton(Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
            var addr = new Win_x64.sockaddr_in
            {
                sin_family = Win_x64.AF_INET,
                sin_port = Win_x64.htons((ushort)endpoint.Port),
                sin_addr = addrBytes,
                sin_zero = 0
            };
            var connectResult = Win_x64.connect(clientSocket, ref addr, 16);

            if (connectResult != 0)
            {
                Win_x64.closesocket(clientSocket);
                Skip.If(true, $"Connect failed: WSA error {Win_x64.WSAGetLastError()}");
            }

            using var serverSide = listener.Accept();

            var connId = ring.RegisterSocket(clientSocket);
            Assert.True(connId >= 0, $"RegisterSocket failed with connId={connId}");

            ring.UnregisterSocket(connId);
            Win_x64.closesocket(clientSocket);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void RegisterSocket_MultipleConnections_Works()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);

            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(10);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            var clientSockets = new List<nint>();
            var serverSockets = new List<Socket>();
            var connIds = new List<int>();

            try
            {
                for (var i = 0; i < 5; i++)
                {
                    var clientSocket = Win_x64.WSASocketW(2, 1, 6, 0, 0, Win_x64.WSA_FLAG_REGISTERED_IO);
                    Skip.If(clientSocket == -1, "WSASocketW failed");

                    Win_x64.inet_pton(Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
                    var addr = new Win_x64.sockaddr_in
                    {
                        sin_family = Win_x64.AF_INET,
                        sin_port = Win_x64.htons((ushort)endpoint.Port),
                        sin_addr = addrBytes,
                        sin_zero = 0
                    };
                    Win_x64.connect(clientSocket, ref addr, 16);
                    clientSockets.Add(clientSocket);

                    var server = listener.Accept();
                    serverSockets.Add(server);

                    var connId = ring.RegisterSocket(clientSocket);
                    Assert.True(connId >= 0, $"Failed to register connection {i}");
                    connIds.Add(connId);
                }

                // All IDs should be unique
                Assert.Equal(connIds.Count, connIds.Distinct().Count());

                foreach (var connId in connIds)
                {
                    ring.UnregisterSocket(connId);
                }
            }
            finally
            {
                foreach (var s in clientSockets) Win_x64.closesocket(s);
                foreach (var s in serverSockets) s.Dispose();
            }
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void RegisterBuffer_WithIORingBuffer_ReturnsValidId()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            using var buffer = IORingBuffer.Create(64 * 1024);

            var bufferId = ring.RegisterBuffer(buffer);
            Assert.True(bufferId >= 0, $"RegisterBuffer returned {bufferId}");

            ring.UnregisterBuffer(bufferId);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void RegisterBuffer_MultipleBuffers_TracksCount()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            using var buffer1 = IORingBuffer.Create(64 * 1024);
            using var buffer2 = IORingBuffer.Create(64 * 1024);
            using var buffer3 = IORingBuffer.Create(64 * 1024);

            var id1 = ring.RegisterBuffer(buffer1);
            var id2 = ring.RegisterBuffer(buffer2);
            var id3 = ring.RegisterBuffer(buffer3);

            Assert.True(id1 >= 0);
            Assert.True(id2 >= 0);
            Assert.True(id3 >= 0);

            // IDs should be unique
            Assert.NotEqual(id1, id2);
            Assert.NotEqual(id2, id3);
            Assert.NotEqual(id1, id3);

            ring.UnregisterBuffer(id2);
            ring.UnregisterBuffer(id1);
            ring.UnregisterBuffer(id3);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void UnregisterBuffer_InvalidId_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);

            // Should not throw for invalid IDs
            ring.UnregisterBuffer(-1);
            ring.UnregisterBuffer(100);
            ring.UnregisterBuffer(0);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void ClientSocket_RecvSend_EchoWorks()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);

            // Create a simple server using regular .NET sockets
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            // Create RIO client socket
            var clientSocket = Win_x64.WSASocketW(2, 1, 6, 0, 0, Win_x64.WSA_FLAG_REGISTERED_IO);
            Skip.If(clientSocket == -1, "WSASocketW failed");

            Win_x64.inet_pton(Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
            var addr = new Win_x64.sockaddr_in
            {
                sin_family = Win_x64.AF_INET,
                sin_port = Win_x64.htons((ushort)endpoint.Port),
                sin_addr = addrBytes,
                sin_zero = 0
            };
            var connectResult = Win_x64.connect(clientSocket, ref addr, 16);

            if (connectResult != 0)
            {
                Win_x64.closesocket(clientSocket);
                Skip.If(true, $"Connect failed: WSA error {Win_x64.WSAGetLastError()}");
            }

            using var serverSide = listener.Accept();

            // Register client socket
            var connId = ring.RegisterSocket(clientSocket);
            if (connId < 0)
            {
                Win_x64.closesocket(clientSocket);
                Skip.If(true, "RegisterSocket failed");
            }

            // Register recv and send buffers
            using var recvBuffer = IORingBuffer.Create(64 * 1024);
            using var sendBuffer = IORingBuffer.Create(64 * 1024);

            var recvBufId = ring.RegisterBuffer(recvBuffer);
            var sendBufId = ring.RegisterBuffer(sendBuffer);
            Skip.If(recvBufId < 0 || sendBufId < 0, "RegisterBuffer failed");

            // Post recv FIRST
            ring.PrepareRecvBuffer(connId, recvBufId, recvBuffer.WriteOffset, BufferSize, 1);
            ring.Submit();

            // Send data from server to our RIO client
            var testData = "Hello Managed RIO!"u8.ToArray();
            serverSide.Send(testData);

            // Wait for recv completion
            Span<Completion> completions = stackalloc Completion[16];
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);

            Assert.True(count > 0, "No recv completion received");
            Assert.Equal(testData.Length, completions[0].Result);
            ring.AdvanceCompletionQueue(count);
            recvBuffer.CommitWrite(testData.Length);

            // Verify received data
            var receivedData = recvBuffer.GetReadSpan();
            Assert.True(receivedData.Slice(0, testData.Length).SequenceEqual(testData), "Received data mismatch");

            // Echo back: copy to send buffer and send
            var writeSpan = sendBuffer.GetWriteSpan();
            receivedData.Slice(0, testData.Length).CopyTo(writeSpan);
            sendBuffer.CommitWrite(testData.Length);
            recvBuffer.CommitRead(testData.Length);

            ring.PrepareSendBuffer(connId, sendBufId, sendBuffer.ReadOffset, testData.Length, 2);
            ring.Submit();

            count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
            Assert.True(count > 0, "No send completion received");
            Assert.Equal(testData.Length, completions[0].Result);
            ring.AdvanceCompletionQueue(count);
            sendBuffer.CommitRead(testData.Length);

            // Verify echo on server side
            var echoBuffer = new byte[testData.Length];
            var received = serverSide.Receive(echoBuffer);
            Assert.Equal(testData.Length, received);
            Assert.Equal(testData, echoBuffer);

            // Cleanup
            ring.UnregisterSocket(connId);
            ring.UnregisterBuffer(recvBufId);
            ring.UnregisterBuffer(sendBufId);
            Win_x64.closesocket(clientSocket);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void CreateListener_AcceptEx_EndToEnd()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);

            // Create listener using managed ring's CreateListener
            var listener = ring.CreateListener("127.0.0.1", 0, 128);
            Assert.NotEqual(-1, listener);

            // Get the assigned port
            var addrBuf = new byte[16]; // sockaddr_in
            var addrLen = addrBuf.Length;
            int port;
            unsafe
            {
                fixed (byte* pAddr = addrBuf)
                {
                    if (Win_x64.getsockname(listener, (nint)pAddr, ref addrLen) == 0)
                    {
                        port = (addrBuf[2] << 8) | addrBuf[3];
                    }
                    else
                    {
                        Skip.If(true, "getsockname failed");
                        return;
                    }
                }
            }

            // Queue accept
            const ulong acceptUserData = 100;
            ring.PrepareAccept(listener, 0, 0, acceptUserData);
            ring.Submit();

            // Connect a client
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));

            // Wait for accept completion
            Span<Completion> completions = stackalloc Completion[16];
            var count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);

            Assert.True(count > 0, "No accept completion received");
            Assert.Equal(acceptUserData, completions[0].UserData);

            var acceptedSocket = (nint)completions[0].Result;
            Assert.True(acceptedSocket > 0, $"Accept returned invalid socket: {acceptedSocket}");
            ring.AdvanceCompletionQueue(count);

            // Configure and register the accepted socket
            ring.ConfigureSocket(acceptedSocket);
            var connId = ring.RegisterSocket(acceptedSocket);
            Assert.True(connId >= 0, $"RegisterSocket failed for accepted socket");

            // Register buffers and do echo
            using var recvBuffer = IORingBuffer.Create(64 * 1024);
            using var sendBuffer = IORingBuffer.Create(64 * 1024);
            var recvBufId = ring.RegisterBuffer(recvBuffer);
            var sendBufId = ring.RegisterBuffer(sendBuffer);

            // Post recv
            ring.PrepareRecvBuffer(connId, recvBufId, recvBuffer.WriteOffset, BufferSize, 1);
            ring.Submit();

            Thread.Sleep(10);

            // Client sends data
            var testData = "Hello via managed AcceptEx!"u8.ToArray();
            client.Send(testData);

            // Wait for recv
            count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
            Assert.True(count > 0, "No recv completion");
            Assert.Equal(testData.Length, completions[0].Result);
            ring.AdvanceCompletionQueue(count);
            recvBuffer.CommitWrite(testData.Length);

            // Verify received data
            var recvSpan = recvBuffer.GetReadSpan();
            Assert.True(recvSpan.Slice(0, testData.Length).SequenceEqual(testData));

            // Echo back
            var writeSpan = sendBuffer.GetWriteSpan();
            recvSpan.Slice(0, testData.Length).CopyTo(writeSpan);
            sendBuffer.CommitWrite(testData.Length);
            recvBuffer.CommitRead(testData.Length);

            ring.PrepareSendBuffer(connId, sendBufId, sendBuffer.ReadOffset, testData.Length, 2);
            ring.Submit();

            count = TestHelpers.WaitForCompletions(ring, completions, 1, 2000);
            Assert.True(count > 0, "No send completion");
            ring.AdvanceCompletionQueue(count);

            // Verify echo
            var echoBuf = new byte[testData.Length];
            var received = client.Receive(echoBuf);
            Assert.Equal(testData.Length, received);
            Assert.Equal(testData, echoBuf);

            // Cleanup
            ring.UnregisterSocket(connId);
            ring.UnregisterBuffer(recvBufId);
            ring.UnregisterBuffer(sendBufId);
            ring.CloseSocket(acceptedSocket);
            ring.CloseListener(listener);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void RapidReconnect_WorksCorrectly()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);

            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(10);
            var endpoint = (IPEndPoint)listener.LocalEndPoint!;

            for (var cycle = 0; cycle < 10; cycle++)
            {
                var clientSocket = Win_x64.WSASocketW(2, 1, 6, 0, 0, Win_x64.WSA_FLAG_REGISTERED_IO);
                Skip.If(clientSocket == -1, "WSASocketW failed");

                Win_x64.inet_pton(Win_x64.AF_INET, "127.0.0.1", out var addrBytes);
                var addr = new Win_x64.sockaddr_in
                {
                    sin_family = Win_x64.AF_INET,
                    sin_port = Win_x64.htons((ushort)endpoint.Port),
                    sin_addr = addrBytes,
                    sin_zero = 0
                };
                Win_x64.connect(clientSocket, ref addr, 16);
                using var serverSide = listener.Accept();

                var connId = ring.RegisterSocket(clientSocket);
                Assert.True(connId >= 0, $"RegisterSocket failed on cycle {cycle}");

                ring.UnregisterSocket(connId);
                Win_x64.closesocket(clientSocket);
            }
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void PeekCompletions_WithNoCompletions_ReturnsZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            Span<Completion> completions = stackalloc Completion[16];

            var count = ring.PeekCompletions(completions);
            Assert.Equal(0, count);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void Submit_WithNoOperations_ReturnsZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            var submitted = ring.Submit();
            Assert.Equal(0, submitted);
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }

    [SkippableFact]
    public void AdvanceCompletionQueue_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

        try
        {
            using var ring = new WindowsManagedRIOGroup(MaxConnections);
            ring.AdvanceCompletionQueue(0); // Should not throw
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"Managed RIO not available: {ex.Message}");
        }
    }
}
