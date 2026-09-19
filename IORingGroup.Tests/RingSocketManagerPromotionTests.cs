// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Net;
using System.Net.Sockets;
using System.Network;

namespace IORingGroup.Tests;

/// <summary>
/// Sockets start on the initial (pre-auth) pools and are promoted to the base pools on request.
/// </summary>
public class RingSocketManagerPromotionTests : IDisposable
{
    // 128 KiB keeps Initial strictly below Base even on the Windows legacy path (Initial = 64 KiB there)
    private const int Base = 128 * 1024;
    private static readonly int Initial = IORingBuffer.MinimumSize;

    private readonly IIORingGroup _ring;
    private readonly RingSocketManager _manager;
    private readonly nint _listener;
    private readonly int _listenerPort;
    private readonly RingSocketEvent[] _events = new RingSocketEvent[64];

    public RingSocketManagerPromotionTests()
    {
        var registered = RingSocketManager.RequiredRegisteredBuffers(64, Base, 2 * Base, 32L * Base, 4, Initial, Initial);
        _ring = System.Network.IORingGroup.Create(queueSize: 256, maxConnections: 64, maxRegisteredBuffers: registered);
        _manager = new RingSocketManager(
            _ring,
            maxSockets: 64,
            recvBufferSize: Base,
            sendBufferSize: Base,
            initialBufferSlabs: 1,
            maxBufferSlabs: 4,
            maxSendBufferSize: 2 * Base,
            sendBufferGrowthBudget: 32L * Base,
            sendBufferRetentionWindows: 1,
            initialRecvBufferSize: Initial,
            initialSendBufferSize: Initial
        );
        _listenerPort = 26000 + Random.Shared.Next(1000);
        _listener = _ring.CreateListener("127.0.0.1", (ushort)_listenerPort, 16);
        Assert.NotEqual(-1, _listener);
    }

    public void Dispose()
    {
        _ring.CloseListener(_listener);
        _manager.Dispose();
        _ring.Dispose();
    }

    private RingSocket Accept(out Socket client)
    {
        client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, _listenerPort);

        _ring.PrepareAccept(_listener, 0, 0, IORingUserData.EncodeAccept());
        _ring.Submit();

        var completions = new Completion[1];
        nint handle = -1;
        for (var i = 0; i < 100 && handle <= 0; i++)
        {
            var count = _ring.PeekCompletions(completions);
            if (count > 0)
            {
                _ring.AdvanceCompletionQueue(count);
                handle = completions[0].Result;
            }
            else
            {
                Thread.Sleep(10);
            }
        }

        Assert.True(handle > 0);
        _ring.ConfigureSocket(handle);
        var socket = _manager.CreateSocket(handle);
        Assert.NotNull(socket);
        _manager.Submit();
        return socket;
    }

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void Write(RingSocket socket, ReadOnlySpan<byte> data)
    {
        data.CopyTo(socket.SendBuffer.GetWriteSpan());
        socket.SendBuffer.CommitWrite(data.Length);
        socket.QueueSend();
    }

    private byte[] ReadAll(Socket client, int length)
    {
        var received = new byte[length];
        var total = 0;
        for (var i = 0; i < 500 && total < length; i++)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            if (client.Poll(1000, SelectMode.SelectRead))
            {
                var read = client.Receive(received, total, length - total, SocketFlags.None);
                Assert.NotEqual(0, read);
                total += read;
            }
        }

        Assert.Equal(length, total);
        return received;
    }

    /// <summary>Pumps until the socket's recv buffer holds at least <paramref name="readable"/> bytes.</summary>
    private void PumpUntilReadable(RingSocket socket, int readable)
    {
        for (var i = 0; i < 500 && socket.RecvBuffer.ReadableBytes < readable; i++)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            Thread.Sleep(1);
        }

        Assert.True(socket.RecvBuffer.ReadableBytes >= readable, "the expected bytes did not arrive");
    }

    private void WaitForDrain(RingSocket socket)
    {
        var deadline = Environment.TickCount64 + 5000;
        var drained = false;

        while (!drained && Environment.TickCount64 - deadline < 0)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();

            drained = socket is { SendsInFlight: 0, RetiringSendBuffer: null } &&
                      socket.SendBuffer.ReadableBytes == 0;

            if (!drained)
            {
                Thread.Sleep(1);
            }
        }

        Assert.True(drained, "the socket's sends did not drain within the deadline");
    }

    private void CloseAndReap(Socket client)
    {
        client.Close();
        for (var i = 0; i < 500 && _manager.ConnectedCount > 0; i++)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            Thread.Sleep(5);
        }

        // Retired buffers go back on the pass after their Disconnected event
        _manager.ProcessCompletions(_events);
        Assert.Equal(0, _manager.ConnectedCount);
    }

    [Fact]
    public void Constructor_RejectsAnInitialSizeNotBelowBase()
    {
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 16, maxRegisteredBuffers: 128);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingSocketManager(
            ring, maxSockets: 16, recvBufferSize: Base, sendBufferSize: Base, initialSendBufferSize: Base
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingSocketManager(
            ring, maxSockets: 16, recvBufferSize: Base, sendBufferSize: Base, initialRecvBufferSize: 2 * Base
        ));
    }

    [Fact]
    public void Constructor_RejectsAnInitialSizeTheBufferCannotMap()
    {
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 16, maxRegisteredBuffers: 128);
        // Not a power of two: ValidateSize's own exception, named for this constructor's parameter
        var ex = Assert.Throws<ArgumentException>(() => new RingSocketManager(
            ring, maxSockets: 16, recvBufferSize: Base, sendBufferSize: Base, initialSendBufferSize: 3000
        ));
        Assert.Equal("initialSendBufferSize", ex.ParamName);
    }

    [Fact]
    public void CreateSocket_StartsOnTheInitialPools_AndLeavesBaseUntouched()
    {
        Assert.Equal(Initial, _manager.InitialRecvBufferSize);
        Assert.Equal(Initial, _manager.InitialSendBufferSize);

        var socket = Accept(out var client);

        Assert.Equal(Initial, socket.RecvBuffer.PhysicalSize);
        Assert.Equal(Initial, socket.SendBuffer.PhysicalSize);
        Assert.Equal(1, _manager.InitialRecvPool!.InUse);
        Assert.Equal(1, _manager.InitialSendPool!.InUse);
        Assert.Equal(0, _manager.RecvBufferPool.InUse);
        Assert.Equal(0, _manager.SendBufferPool.InUse);

        CloseAndReap(client);

        Assert.Equal(0, _manager.InitialRecvPool.InUse);
        Assert.Equal(0, _manager.InitialSendPool.InUse);
    }

    [Fact]
    public void Maintain_TrimsIdleInitialSlabs_AndCountsThemAsBase()
    {
        // slab = max(16, 64 / 4) = 16; 17 sockets force a second slab of each initial pool
        var clients = new List<Socket>(17);
        for (var i = 0; i < 17; i++)
        {
            Accept(out var client);
            clients.Add(client);
        }

        Assert.Equal(2, _manager.InitialRecvPool!.CurrentSlabs);
        Assert.Equal(2, _manager.InitialSendPool!.CurrentSlabs);
        Assert.Equal(1, _manager.RecvBufferPool.CurrentSlabs);

        // Base capacity is what both base pools and both initial pools have allocated
        var expected = 2L * 16 * Base + 2L * 2 * 16 * Initial;
        Assert.Equal(expected, _manager.Maintain().BaseCapacityBytes);

        for (var i = 0; i < clients.Count; i++)
        {
            CloseAndReap(clients[i]);
        }

        _manager.Maintain(); // window records the 17 that were live
        var trimmed = _manager.Maintain(); // peak 0: the top slab of each initial pool goes back

        Assert.Equal(2 * 16, trimmed.BaseBuffersReleased);
        Assert.Equal(1, _manager.InitialRecvPool.CurrentSlabs);
        Assert.Equal(1, _manager.InitialSendPool.CurrentSlabs);
    }
}
