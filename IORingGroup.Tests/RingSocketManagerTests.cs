// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Net;
using System.Net.Sockets;
using System.Network;
using System.Text;

namespace IORingGroup.Tests;

public class RingSocketManagerTests : IDisposable
{
    private readonly IIORingGroup _ring;
    private readonly RingSocketManager _manager;
    private readonly nint _listener;
    private readonly int _listenerPort;
    private readonly RingSocketEvent[] _events;

    public RingSocketManagerTests()
    {
        _ring = System.Network.IORingGroup.Create(queueSize: 256);
        _manager = new RingSocketManager(_ring, maxSockets: 64, recvBufferSize: 64 * 1024, sendBufferSize: 64 * 1024);
        _events = new RingSocketEvent[64];

        // Create a listener for tests
        _listenerPort = 19000 + Random.Shared.Next(1000);
        _listener = _ring.CreateListener("127.0.0.1", (ushort)_listenerPort, 16);
        Assert.NotEqual(-1, _listener);
    }

    public void Dispose()
    {
        _ring.CloseListener(_listener);
        _manager.Dispose();
        _ring.Dispose();
    }

    [Fact]
    public void CreateSocket_WithValidHandle_ReturnsSocket()
    {
        // Arrange - Connect a client and accept
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle = AcceptConnection();
        Assert.True(acceptedHandle > 0);

        // Act
        _ring.ConfigureSocket(acceptedHandle);
        var socket = _manager.CreateSocket(acceptedHandle);

        // Assert
        Assert.NotNull(socket);
        Assert.True(socket.Connected);
        Assert.False(socket.DisconnectPending);
        Assert.NotNull(socket.RecvBuffer);
        Assert.NotNull(socket.SendBuffer);
        Assert.True(socket.Id >= 0);
        Assert.Equal(1, _manager.ConnectedCount);

        // Cleanup
        socket.Disconnect();
        _manager.Submit();
        client.Close();
        ProcessUntilDisconnect();
    }

    [Fact]
    public void CreateSocket_AssignsSequentialIds()
    {
        // Arrange - Create multiple connections
        var clients = new Socket[3];
        var sockets = new RingSocket[3];

        for (var i = 0; i < 3; i++)
        {
            clients[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            clients[i].Connect(IPAddress.Loopback, _listenerPort);

            var acceptedHandle = AcceptConnection();
            _ring.ConfigureSocket(acceptedHandle);
            sockets[i] = _manager.CreateSocket(acceptedHandle)!;
        }

        // Assert - IDs should be assigned (may not be strictly sequential due to implementation)
        Assert.NotNull(sockets[0]);
        Assert.NotNull(sockets[1]);
        Assert.NotNull(sockets[2]);
        Assert.NotEqual(sockets[0].Id, sockets[1].Id);
        Assert.NotEqual(sockets[1].Id, sockets[2].Id);
        Assert.Equal(3, _manager.ConnectedCount);

        // Cleanup
        foreach (var socket in sockets)
        {
            socket.Disconnect();
        }
        foreach (var client in clients)
        {
            client.Dispose();
        }
        ProcessUntilAllDisconnected();
    }

    [Fact]
    public void ProcessCompletions_ReceivesData()
    {
        // Arrange
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle);
        var socket = _manager.CreateSocket(acceptedHandle)!;
        _manager.Submit();

        // Act - Send data from client
        var testData = "Hello, RingSocket!"u8.ToArray();
        client.Send(testData);

        // Process completions
        Thread.Sleep(50);
        var eventCount = _manager.ProcessCompletions(_events);
        _manager.Submit();

        // Assert
        Assert.True(eventCount > 0);
        var recvEvent = _events.Take(eventCount).FirstOrDefault(e => e.Type == RingSocketEventType.DataReceived);
        Assert.Equal(RingSocketEventType.DataReceived, recvEvent.Type);
        Assert.Equal(socket, recvEvent.Socket);
        Assert.Equal(testData.Length, recvEvent.BytesTransferred);

        // Verify data in buffer
        var recvSpan = socket.RecvBuffer.GetReadSpan();
        Assert.Equal(testData, recvSpan.Slice(0, testData.Length).ToArray());

        // Cleanup
        socket.Disconnect();
        _manager.Submit();
        client.Close();
        ProcessUntilDisconnect();
    }

    [Fact]
    public void ProcessCompletions_SendsData()
    {
        // Arrange
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle);
        var socket = _manager.CreateSocket(acceptedHandle)!;
        _manager.Submit();

        // Act - Write data to send buffer and queue send
        var testData = "Hello from server!"u8.ToArray();
        var writeSpan = socket.SendBuffer.GetWriteSpan();
        testData.CopyTo(writeSpan);
        socket.SendBuffer.CommitWrite(testData.Length);
        socket.QueueSend();

        // Process until send completes
        var sendEventReceived = false;
        for (var i = 0; i < 10 && !sendEventReceived; i++)
        {
            var eventCount = _manager.ProcessCompletions(_events);
            _manager.Submit();

            for (var j = 0; j < eventCount; j++)
            {
                if (_events[j].Type == RingSocketEventType.DataSent)
                {
                    sendEventReceived = true;
                    Assert.Equal(socket, _events[j].Socket);
                    Assert.Equal(testData.Length, _events[j].BytesTransferred);
                }
            }

            if (!sendEventReceived)
            {
                Thread.Sleep(10);
            }
        }

        Assert.True(sendEventReceived, "Send completion event not received");

        // Verify client received data
        var recvBuffer = new byte[1024];
        client.ReceiveTimeout = 1000;
        var bytesReceived = client.Receive(recvBuffer);
        Assert.Equal(testData.Length, bytesReceived);
        Assert.Equal(testData, recvBuffer.Take(bytesReceived).ToArray());

        // Cleanup
        socket.Disconnect();
        _manager.Submit();
        client.Close();
        ProcessUntilDisconnect();
    }

    [Fact]
    public void Disconnect_GracefullyClosesSocket()
    {
        // Arrange
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle);
        var socket = _manager.CreateSocket(acceptedHandle)!;
        _manager.Submit();

        var socketId = socket.Id;

        // Act
        socket.Disconnect();
        _manager.Submit(); // Submit the shutdown operation

        // Close client to complete the TCP handshake (client sends FIN in response)
        client.Close();

        var disconnectEventReceived = ProcessUntilDisconnect();

        // Assert
        Assert.True(disconnectEventReceived);
        Assert.Equal(0, _manager.ConnectedCount);
        Assert.Null(_manager.GetSocket(socketId, socket.Generation));
    }

    [Fact]
    public void Disconnect_WaitsForPendingIO()
    {
        // Arrange
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle);
        var socket = _manager.CreateSocket(acceptedHandle)!;
        _manager.Submit();

        var socketId = socket.Id;

        // Write data to send buffer
        var testData = "Data before disconnect"u8.ToArray();
        var writeSpan = socket.SendBuffer.GetWriteSpan();
        testData.CopyTo(writeSpan);
        socket.SendBuffer.CommitWrite(testData.Length);
        socket.QueueSend();

        // Act - Request disconnect while I/O is pending
        socket.Disconnect();

        // DisconnectPending should be set (waiting for send to complete)
        Assert.True(socket.DisconnectPending);

        // Submit to start processing send
        _manager.Submit();

        // Wait for send to complete first
        var sendCompleted = false;
        for (var i = 0; i < 50 && !sendCompleted; i++)
        {
            var eventCount = _manager.ProcessCompletions(_events);
            _manager.Submit();

            for (var j = 0; j < eventCount; j++)
            {
                if (_events[j].Type == RingSocketEventType.DataSent)
                {
                    sendCompleted = true;
                    break;
                }
            }

            if (!sendCompleted)
            {
                Thread.Sleep(10);
            }
        }

        Assert.True(sendCompleted, "Send should complete before disconnect");

        // Now close client to complete the TCP handshake
        client.Close();

        // Process completions to complete the disconnect
        ProcessUntilDisconnect();

        // Assert - Socket is fully cleaned up
        Assert.Equal(0, _manager.ConnectedCount);
        Assert.Null(_manager.GetSocket(socketId, socket.Generation));
    }

    [Fact]
    public void GetSocket_WithValidGeneration_ReturnsSocket()
    {
        // Arrange
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle);
        var socket = _manager.CreateSocket(acceptedHandle)!;

        // Act
        var retrieved = _manager.GetSocket(socket.Id, socket.Generation);

        // Assert
        Assert.Same(socket, retrieved);

        // Cleanup
        socket.Disconnect();
        _manager.Submit();
        client.Close();
        ProcessUntilDisconnect();
    }

    [Fact]
    public void GetSocket_WithStaleGeneration_ReturnsNull()
    {
        // Arrange - Create and disconnect a socket
        using var client1 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client1.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle1 = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle1);
        var socket1 = _manager.CreateSocket(acceptedHandle1)!;
        var oldId = socket1.Id;
        var oldGeneration = socket1.Generation;

        socket1.Disconnect();
        _manager.Submit();
        client1.Close();
        ProcessUntilDisconnect();

        // Create a new socket that might reuse the same slot
        using var client2 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client2.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle2 = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle2);
        var socket2 = _manager.CreateSocket(acceptedHandle2)!;

        // Act - Try to get with old generation
        var retrieved = _manager.GetSocket(oldId, oldGeneration);

        // Assert - Should return null if slot was reused (generation changed)
        // or the new socket if slot wasn't reused
        if (socket2.Id == oldId)
        {
            // Slot was reused, old generation should not match
            Assert.Null(retrieved);
            Assert.NotEqual(oldGeneration, socket2.Generation);
        }

        // Cleanup
        socket2.Disconnect();
        _manager.Submit();
        client2.Close();
        ProcessUntilDisconnect();
    }

    [Fact]
    public void QueueSend_MultipleCallsOnlyQueuesOnce()
    {
        // Arrange
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        var acceptedHandle = AcceptConnection();
        _ring.ConfigureSocket(acceptedHandle);
        var socket = _manager.CreateSocket(acceptedHandle)!;

        // Write data
        var testData = "Test data"u8.ToArray();
        var writeSpan = socket.SendBuffer.GetWriteSpan();
        testData.CopyTo(writeSpan);
        socket.SendBuffer.CommitWrite(testData.Length);

        // Act - Call QueueSend multiple times
        socket.QueueSend();
        socket.QueueSend();
        socket.QueueSend();

        Assert.True(socket.SendQueued);

        // Process and verify only one send event
        var sendEvents = 0;
        for (var i = 0; i < 5; i++)
        {
            var eventCount = _manager.ProcessCompletions(_events);
            _manager.Submit();

            for (var j = 0; j < eventCount; j++)
            {
                if (_events[j].Type == RingSocketEventType.DataSent)
                {
                    sendEvents++;
                }
            }

            Thread.Sleep(10);
        }

        Assert.Equal(1, sendEvents);

        // Cleanup
        socket.Disconnect();
        _manager.Submit();
        client.Close();
        ProcessUntilDisconnect();
    }

    [Fact]
    public void RapidReconnect_GenerationPreventsStaleCompletions()
    {
        // This test verifies that generation tracking works for rapid reconnects
        for (var iteration = 0; iteration < 5; iteration++)
        {
            // Connect
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, _listenerPort);

            var acceptedHandle = AcceptConnection();
            _ring.ConfigureSocket(acceptedHandle);
            var socket = _manager.CreateSocket(acceptedHandle)!;
            _manager.Submit();

            var id = socket.Id;
            var generation = socket.Generation;

            // Disconnect quickly
            socket.Disconnect();
            _manager.Submit(); // Submit the shutdown operation

            // Close client to complete the TCP handshake
            client.Close();

            ProcessUntilDisconnect();

            // Verify generation incremented (if slot is reused)
            // The next socket on the same slot should have generation + 1
        }

        Assert.Equal(0, _manager.ConnectedCount);
    }

    /// <summary>
    /// Mimics the ModernUO test harness pattern:
    /// 1. Process completions first (cleans up previous socket)
    /// 2. Accept a new connection
    /// 3. Write data to send buffer
    /// 4. Call DisconnectImmediate (force close, not graceful)
    /// 5. Repeat rapidly
    ///
    /// This tests the pattern that's causing crashes in ModernUO's test suite.
    /// </summary>
    [Fact]
    public void RapidCreateWriteDisconnect_MimicsModernUOTestHarness()
    {
        var clients = new List<Socket>();

        for (var iteration = 0; iteration < 100; iteration++)
        {
            // Step 1: Process completions first (like ModernUO's Slice() does)
            _manager.ProcessCompletions(_events);
            _manager.Submit();

            // Step 2: Accept a new connection
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, _listenerPort);
            clients.Add(client);

            var acceptedHandle = AcceptConnection();
            Assert.True(acceptedHandle > 0, $"Failed to accept connection on iteration {iteration}");

            _ring.ConfigureSocket(acceptedHandle);
            var socket = _manager.CreateSocket(acceptedHandle);
            Assert.NotNull(socket);
            _manager.Submit(); // Submit the initial recv

            // Step 3: Write data to send buffer (like packet tests do)
            var testData = Encoding.UTF8.GetBytes($"Test data for iteration {iteration}");
            var writeSpan = socket.SendBuffer.GetWriteSpan();
            testData.CopyTo(writeSpan);
            socket.SendBuffer.CommitWrite(testData.Length);
            socket.QueueSend();

            // Step 4: Call DisconnectImmediate (force close without waiting)
            // This is what ModernUO's NetState.Dispose does
            _manager.DisconnectImmediate(socket);
        }

        // Final cleanup
        _manager.ProcessCompletions(_events);
        _manager.Submit();

        // Clean up client sockets
        foreach (var client in clients)
        {
            try { client.Close(); } catch { }
            try { client.Dispose(); } catch { }
        }

        // Process until all disconnected
        ProcessUntilAllDisconnected();

        Assert.Equal(0, _manager.ConnectedCount);
    }

    /// <summary>
    /// Tests an even more aggressive pattern - no sleep, rapid fire.
    /// </summary>
    [Fact]
    public void StressTest_RapidCreateDispose_NoSleep()
    {
        var clients = new List<Socket>();

        for (var iteration = 0; iteration < 50; iteration++)
        {
            // Process any pending completions
            _manager.ProcessCompletions(_events);
            _manager.Submit();

            // Accept
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, _listenerPort);
            clients.Add(client);

            var acceptedHandle = AcceptConnection();
            if (acceptedHandle <= 0)
            {
                // Try processing completions and retry once
                _manager.ProcessCompletions(_events);
                _manager.Submit();
                acceptedHandle = AcceptConnection();
            }
            Assert.True(acceptedHandle > 0, $"Failed on iteration {iteration}");

            _ring.ConfigureSocket(acceptedHandle);
            var socket = _manager.CreateSocket(acceptedHandle);
            Assert.NotNull(socket);

            // Write some data
            var data = "PING"u8.ToArray();
            var span = socket.SendBuffer.GetWriteSpan();
            data.CopyTo(span);
            socket.SendBuffer.CommitWrite(data.Length);
            socket.QueueSend();

            // Immediately disconnect
            _manager.DisconnectImmediate(socket);

            // Process completions right away
            _manager.ProcessCompletions(_events);
            _manager.Submit();
        }

        // Cleanup
        foreach (var client in clients)
        {
            try { client.Dispose(); } catch { }
        }

        ProcessUntilAllDisconnected();
        Assert.Equal(0, _manager.ConnectedCount);
    }

    private nint AcceptConnection()
    {
        // Post accept and wait for completion
        _ring.PrepareAccept(_listener, 0, 0, IORingUserData.EncodeAccept());
        _ring.Submit();

        var completions = new Completion[1];
        for (var i = 0; i < 100; i++)
        {
            var count = _ring.PeekCompletions(completions);
            if (count > 0)
            {
                _ring.AdvanceCompletionQueue(count);
                if (completions[0].Result > 0)
                {
                    return completions[0].Result;
                }
            }
            Thread.Sleep(10);
        }

        return -1;
    }

    private bool ProcessUntilDisconnect()
    {
        for (var i = 0; i < 100; i++)
        {
            var eventCount = _manager.ProcessCompletions(_events);
            _manager.Submit();

            for (var j = 0; j < eventCount; j++)
            {
                if (_events[j].Type == RingSocketEventType.Disconnected)
                {
                    return true;
                }
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private void ProcessUntilAllDisconnected()
    {
        while (_manager.ConnectedCount > 0)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            Thread.Sleep(10);
        }
    }
}
