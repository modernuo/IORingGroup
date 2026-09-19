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

    /// <summary>Closes every client, then pumps until the manager has released every slot and buffer.</summary>
    private void CloseAllAndReap(List<Socket> clients)
    {
        for (var i = 0; i < clients.Count; i++)
        {
            clients[i].Close();
        }

        clients.Clear();

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

        CloseAllAndReap(clients);

        _manager.Maintain(); // window records the 17 that were live
        var trimmed = _manager.Maintain(); // peak 0: the top slab of each initial pool goes back

        Assert.Equal(2 * 16, trimmed.BaseBuffersReleased);
        Assert.Equal(1, _manager.InitialRecvPool.CurrentSlabs);
        Assert.Equal(1, _manager.InitialSendPool.CurrentSlabs);
    }

    [Fact]
    public void PromoteSend_WithNothingInFlight_ReleasesTheInitialBuffer()
    {
        var socket = Accept(out var client);
        var queued = Pattern(100, 1);
        Write(socket, queued); // sendable, never posted

        Assert.True(_manager.TryPromoteSendBuffer(socket));

        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);
        Assert.Null(socket.RetiringSendBuffer);
        Assert.Equal(queued.Length, socket.SendBuffer.ReadableBytes);
        Assert.Equal(0, _manager.InitialSendPool!.InUse);
        Assert.Equal(1, _manager.SendBufferPool.InUse);

        Assert.Equal(queued, ReadAll(client, queued.Length));
        CloseAndReap(client);
    }

    [Fact]
    public void PromoteSend_WithBytesInFlight_RetiresTheInitialBufferAndKeepsOrder()
    {
        var socket = Accept(out var client);
        var first = Pattern(Initial / 2, 1);
        Write(socket, first);
        _manager.ProcessSendQueue();
        _manager.Submit(); // in flight from the initial buffer

        var queued = Pattern(Initial / 4, 2);
        Write(socket, queued);
        var original = socket.SendBuffer;

        Assert.True(_manager.TryPromoteSendBuffer(socket));

        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);
        Assert.Same(original, socket.RetiringSendBuffer);
        Assert.Equal(0, original.SendableBytes);
        Assert.Equal(queued.Length, socket.SendBuffer.ReadableBytes);

        var after = Pattern(Base / 2, 3); // more than the initial buffer could ever hold
        Write(socket, after);

        var expected = new byte[first.Length + queued.Length + after.Length];
        first.CopyTo(expected, 0);
        queued.CopyTo(expected, first.Length);
        after.CopyTo(expected, first.Length + queued.Length);

        Assert.Equal(expected, ReadAll(client, expected.Length));
        WaitForDrain(socket);
        Assert.Null(socket.RetiringSendBuffer);
        Assert.Equal(0, _manager.InitialSendPool!.InUse);

        CloseAndReap(client);
    }

    [Fact]
    public void PromoteSend_IsANoOpOncePromoted_AndRefusedWhileClosing()
    {
        var socket = Accept(out var client);
        Assert.True(_manager.TryPromoteSendBuffer(socket));
        Assert.False(_manager.TryPromoteSendBuffer(socket));
        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);
        CloseAndReap(client);

        var closing = Accept(out var second);
        closing.Disconnect();
        Assert.False(_manager.TryPromoteSendBuffer(closing));
        Assert.Equal(Initial, closing.SendBuffer.PhysicalSize);
        CloseAndReap(second);
    }

    [Fact]
    public void Grow_PromotesFirst_ThenGrowsThroughTheTiers()
    {
        var socket = Accept(out var client);

        Assert.True(_manager.TryGrowSendBuffer(socket));
        Assert.Equal(Base, socket.SendBuffer.PhysicalSize); // promotion, not a tier

        Assert.True(_manager.TryGrowSendBuffer(socket));
        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);

        CloseAndReap(client);
    }

    [Fact]
    public void Grow_WhileTheInitialBufferIsRetiring_ReleasesTheBaseBufferAndKeepsTheRetiringOne()
    {
        var socket = Accept(out var client);
        var first = Pattern(512, 1);
        Write(socket, first);
        _manager.ProcessSendQueue();
        _manager.Submit(); // in flight from the initial buffer
        var initial = socket.SendBuffer;

        Assert.True(_manager.TryPromoteSendBuffer(socket));
        Assert.Same(initial, socket.RetiringSendBuffer);
        var baseBuffer = socket.SendBuffer;

        var queued = Pattern(1024, 2);
        Write(socket, queued); // sendable on base, never posted: base has nothing in flight

        Assert.True(_manager.TryGrowSendBuffer(socket)); // base -> first tier while the initial buffer still retires

        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);
        Assert.Same(initial, socket.RetiringSendBuffer); // the single retiring slot is untouched
        Assert.NotSame(baseBuffer, socket.SendBuffer);
        Assert.Equal(0, _manager.SendBufferPool.InUse); // the base buffer went straight back
        Assert.Equal(queued.Length, socket.SendBuffer.ReadableBytes);

        var expected = new byte[first.Length + queued.Length];
        first.CopyTo(expected, 0);
        queued.CopyTo(expected, first.Length);
        Assert.Equal(expected, ReadAll(client, expected.Length));

        WaitForDrain(socket);
        Assert.Null(socket.RetiringSendBuffer);
        Assert.Equal(0, _manager.InitialSendPool!.InUse);

        CloseAndReap(client);
    }

    [Fact]
    public void Shrink_ReturnsToBase_NeverToInitial()
    {
        var socket = Accept(out var client);
        Assert.True(_manager.TryPromoteSendBuffer(socket));
        Assert.True(_manager.TryGrowSendBuffer(socket));
        WaitForDrain(socket);

        Assert.True(_manager.TryShrinkSendBuffer(socket));
        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);
        Assert.False(_manager.TryShrinkSendBuffer(socket));

        CloseAndReap(client);
    }

    [Fact]
    public void Abort_WithARetiringInitialBuffer_ReleasesBothToTheirPools()
    {
        var socket = Accept(out var client);
        Write(socket, Pattern(256, 1));
        _manager.ProcessSendQueue();
        _manager.Submit();
        Assert.True(_manager.TryPromoteSendBuffer(socket));
        Assert.NotNull(socket.RetiringSendBuffer);

        _manager.DisconnectImmediate(socket);
        for (var i = 0; i < 500 && _manager.ConnectedCount > 0; i++)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            Thread.Sleep(5);
        }

        _manager.ProcessCompletions(_events);
        Assert.Equal(0, _manager.InitialSendPool!.InUse);
        Assert.Equal(0, _manager.SendBufferPool.InUse);
        client.Close();
    }

    [Fact]
    public void PromoteRecv_WithARecvPending_SwapsAtTheNextCompletion_KeepingEveryByte()
    {
        var socket = Accept(out var client);
        var first = Pattern(100, 1);
        client.Send(first);
        PumpUntilReadable(socket, first.Length); // delivered, not consumed: a partial packet
        var original = socket.RecvBuffer;

        Assert.True(_manager.TryPromoteRecvBuffer(socket));
        Assert.Same(original, socket.RecvBuffer); // a recv is armed against it
        Assert.True(socket.RecvPromotionPending);

        var second = Pattern(50, 2);
        client.Send(second);
        PumpUntilReadable(socket, first.Length + second.Length);

        Assert.Equal(Base, socket.RecvBuffer.PhysicalSize);
        Assert.False(socket.RecvPromotionPending);
        Assert.Equal(0, _manager.InitialRecvPool!.InUse);
        Assert.Equal(1, _manager.RecvBufferPool.InUse);

        var expected = new byte[first.Length + second.Length];
        first.CopyTo(expected, 0);
        second.CopyTo(expected, first.Length);
        Assert.Equal(expected, socket.RecvBuffer.GetReadSpan().ToArray());

        // The next recv is armed against the new buffer
        var third = Pattern(200, 3);
        client.Send(third);
        PumpUntilReadable(socket, expected.Length + third.Length);
        Assert.Equal(third, socket.RecvBuffer.GetReadSpan()[expected.Length..].ToArray());

        CloseAndReap(client);
    }

    [Fact]
    public void PromoteRecv_WhenTheInitialBufferIsFull_SwapsImmediatelyAndRearmsRecv()
    {
        var socket = Accept(out var client);
        var capacity = Initial - 1; // a ring buffer keeps one slot to tell full from empty
        var fill = Pattern(capacity, 1);
        client.Send(fill);
        PumpUntilReadable(socket, capacity); // WritableBytes == 0, so nothing is armed
        Assert.False(socket.RecvPending);

        Assert.True(_manager.TryPromoteRecvBuffer(socket));

        Assert.Equal(Base, socket.RecvBuffer.PhysicalSize);
        Assert.Equal(fill, socket.RecvBuffer.GetReadSpan().ToArray());
        Assert.True(socket.RecvPending);
        Assert.False(socket.RecvPromotionPending);
        _manager.Submit();

        var more = Pattern(10, 2);
        client.Send(more);
        PumpUntilReadable(socket, capacity + more.Length);
        Assert.Equal(more, socket.RecvBuffer.GetReadSpan()[capacity..].ToArray());

        CloseAndReap(client);
    }

    [Fact]
    public void PromoteRecv_IsANoOpOncePromoted_AndRefusedWhileClosing()
    {
        var socket = Accept(out var client);
        Assert.True(_manager.TryPromoteRecvBuffer(socket));
        Assert.False(_manager.TryPromoteRecvBuffer(socket)); // already pending
        client.Send(Pattern(8, 1));
        PumpUntilReadable(socket, 8);
        Assert.Equal(Base, socket.RecvBuffer.PhysicalSize);
        Assert.False(_manager.TryPromoteRecvBuffer(socket)); // already promoted
        CloseAndReap(client);

        var closing = Accept(out var second);
        closing.Disconnect();
        Assert.False(_manager.TryPromoteRecvBuffer(closing));
        CloseAndReap(second);
    }

    [Fact]
    public void Disconnect_WithAPromotionPending_ReleasesTheInitialBuffer()
    {
        var socket = Accept(out var client);
        Assert.True(_manager.TryPromoteRecvBuffer(socket));

        CloseAndReap(client); // peer EOF completes the armed recv; nothing to promote onto

        Assert.Equal(0, _manager.InitialRecvPool!.InUse);
        Assert.Equal(0, _manager.RecvBufferPool.InUse);
    }

    [Fact]
    public void PromoteRecv_WhenTheInitialBufferIsFull_AndBaseCannotSupply_ReportsFalseAndStaysRetryable()
    {
        var registered = RingSocketManager.RequiredRegisteredBuffers(8, Base, Base, 0, 2, Initial, Initial);
        using var ring = new FailingRegistrationRing(
            System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8, maxRegisteredBuffers: registered)
        );
        // No slabs up front: the base recv slab is created on the first promotion, which is where it fails
        using var manager = new RingSocketManager(
            ring, maxSockets: 8, recvBufferSize: Base, sendBufferSize: Base,
            initialBufferSlabs: 0, maxBufferSlabs: 2,
            initialRecvBufferSize: Initial, initialSendBufferSize: Initial
        );

        var port = 27000 + Random.Shared.Next(1000);
        var listener = ring.CreateListener("127.0.0.1", (ushort)port, 4);
        var events = new RingSocketEvent[16];
        Socket? client = null;

        try
        {
            client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, port);
            ring.PrepareAccept(listener, 0, 0, IORingUserData.EncodeAccept());
            ring.Submit();

            var completions = new Completion[1];
            nint handle = -1;
            for (var i = 0; i < 200 && handle <= 0; i++)
            {
                if (ring.PeekCompletions(completions) > 0)
                {
                    ring.AdvanceCompletionQueue(1);
                    handle = completions[0].Result;
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            Assert.True(handle > 0);
            ring.ConfigureSocket(handle);
            var socket = manager.CreateSocket(handle)!;
            manager.Submit();

            var capacity = Initial - 1;
            var fill = Pattern(capacity, 1);
            client.Send(fill);
            for (var i = 0; i < 500 && socket.RecvBuffer.ReadableBytes < capacity; i++)
            {
                manager.ProcessCompletions(events);
                manager.Submit();
                Thread.Sleep(1);
            }

            Assert.Equal(capacity, socket.RecvBuffer.ReadableBytes);
            Assert.False(socket.RecvPending);

            ring.FailRegistrationsAfter(0); // the base recv slab cannot be registered
            var refusedBefore = ring.RefusedRegistrations;

            Assert.False(manager.TryPromoteRecvBuffer(socket));
            Assert.False(socket.RecvPromotionPending);
            Assert.Equal(Initial, socket.RecvBuffer.PhysicalSize);
            Assert.Equal(fill, socket.RecvBuffer.GetReadSpan().ToArray()); // nothing lost
            Assert.True(ring.RefusedRegistrations > refusedBefore);

            // A second call tries again rather than short-circuiting on a stale flag
            var refusedAfterFirst = ring.RefusedRegistrations;
            Assert.False(manager.TryPromoteRecvBuffer(socket));
            Assert.True(ring.RefusedRegistrations > refusedAfterFirst);
        }
        finally
        {
            client?.Close();
            for (var i = 0; i < 500 && manager.ConnectedCount > 0; i++)
            {
                manager.ProcessCompletions(events);
                manager.Submit();
                Thread.Sleep(5);
            }

            manager.ProcessCompletions(events);
            ring.CloseListener(listener);
        }
    }
}
