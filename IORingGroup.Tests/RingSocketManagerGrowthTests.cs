// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026, ModernUO

using System.Net;
using System.Net.Sockets;
using System.Network;

namespace IORingGroup.Tests;

public class RingSocketManagerGrowthTests : IDisposable
{
    private const int Base = 64 * 1024;

    private readonly IIORingGroup _ring;
    private readonly RingSocketManager _manager;
    private readonly nint _listener;
    private readonly int _listenerPort;
    private readonly RingSocketEvent[] _events = new RingSocketEvent[64];

    public RingSocketManagerGrowthTests()
    {
        var registered = RingSocketManager.RequiredRegisteredBuffers(64, Base, 4 * Base, 128L * Base);
        _ring = System.Network.IORingGroup.Create(queueSize: 256, maxConnections: 64, maxRegisteredBuffers: registered);
        _manager = new RingSocketManager(
            _ring,
            maxSockets: 64,
            recvBufferSize: Base,
            sendBufferSize: Base,
            initialBufferSlabs: 1,
            maxBufferSlabs: 4,
            maxSendBufferSize: 4 * Base,
            sendBufferGrowthBudget: 128L * Base
        );
        _listenerPort = 20000 + Random.Shared.Next(1000);
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
        new System.Random(seed).NextBytes(data);
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

    [Fact]
    public void Constructor_TiersFollowPowersOfTwoUpToMax()
    {
        Assert.Equal(2, _manager.SendBufferTierCount); // 128 KB, 256 KB
        Assert.Equal(4 * Base, _manager.MaxSendBufferSize);
    }

    [Fact]
    public void RequiredRegisteredBuffers_AddsBudgetWorthOfFirstTier()
    {
        Assert.Equal(32, RingSocketManager.RequiredRegisteredBuffers(16, Base, Base, 0));
        Assert.Equal(32 + 8, RingSocketManager.RequiredRegisteredBuffers(16, Base, 4 * Base, 16L * Base));
    }

    [Fact]
    public void Grow_WithSendsInFlight_KeepsStreamOrderAndRetiresOldBuffer()
    {
        var socket = Accept(out var client);
        var first = Pattern(Base / 2, 1);
        Write(socket, first);
        _manager.ProcessSendQueue();
        _manager.Submit(); // first half is in flight from the original buffer

        var queued = Pattern(Base / 4, 2);
        Write(socket, queued); // sendable, not yet posted
        var original = socket.SendBuffer;

        Assert.True(_manager.TryGrowSendBuffer(socket));

        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);
        Assert.Same(original, socket.RetiringSendBuffer);
        Assert.Equal(0, original.SendableBytes);
        Assert.Equal(queued.Length, socket.SendBuffer.ReadableBytes);

        var after = Pattern(Base, 3); // more than the original could ever hold
        Write(socket, after);

        var expected = new byte[first.Length + queued.Length + after.Length];
        first.CopyTo(expected, 0);
        queued.CopyTo(expected, first.Length);
        after.CopyTo(expected, first.Length + queued.Length);

        Assert.Equal(expected, ReadAll(client, expected.Length));
        Assert.Null(socket.RetiringSendBuffer);

        client.Close();
        while (_manager.ConnectedCount > 0)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Grow_WhileRetiring_ReleasesTheMiddleBufferImmediately()
    {
        var socket = Accept(out var client);
        Write(socket, Pattern(1024, 1));
        _manager.ProcessSendQueue();
        _manager.Submit();

        Assert.True(_manager.TryGrowSendBuffer(socket)); // 64 -> 128, original retiring
        var middle = socket.SendBuffer;
        Write(socket, Pattern(2048, 2));

        Assert.True(_manager.TryGrowSendBuffer(socket)); // 128 -> 256, middle had nothing in flight

        Assert.Equal(4 * Base, socket.SendBuffer.PhysicalSize);
        Assert.NotSame(middle, socket.RetiringSendBuffer);
        Assert.Equal(2048, socket.SendBuffer.ReadableBytes);

        var expected = new byte[3072];
        Pattern(1024, 1).CopyTo(expected, 0);
        Pattern(2048, 2).CopyTo(expected, 1024);
        Assert.Equal(expected, ReadAll(client, expected.Length));

        client.Close();
        while (_manager.ConnectedCount > 0)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Grow_RefusedAtTopTier()
    {
        var socket = Accept(out var client);
        Assert.True(_manager.TryGrowSendBuffer(socket));
        Assert.True(_manager.TryGrowSendBuffer(socket));
        Assert.Equal(4 * Base, socket.SendBuffer.PhysicalSize);

        Assert.False(_manager.TryGrowSendBuffer(socket));
        Assert.Equal(4 * Base, socket.SendBuffer.PhysicalSize);

        client.Close();
    }

    [Fact]
    public void Grow_RefusedWhenBudgetExhausted()
    {
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8, maxRegisteredBuffers: 128);
        using var tight = new RingSocketManager(
            ring, maxSockets: 8, recvBufferSize: Base, sendBufferSize: Base,
            initialBufferSlabs: 1, maxBufferSlabs: 2,
            maxSendBufferSize: 2 * Base, sendBufferGrowthBudget: 1L * Base // less than one tier slab
        );
        var port = 21000 + Random.Shared.Next(1000);
        var listener = ring.CreateListener("127.0.0.1", (ushort)port, 4);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        ring.PrepareAccept(listener, 0, 0, IORingUserData.EncodeAccept());
        ring.Submit();
        var completions = new Completion[1];
        nint handle = -1;
        for (var i = 0; i < 100 && handle <= 0; i++)
        {
            if (ring.PeekCompletions(completions) > 0)
            {
                ring.AdvanceCompletionQueue(1);
                handle = completions[0].Result;
            }
            else
            {
                Thread.Sleep(10);
            }
        }
        ring.ConfigureSocket(handle);
        var socket = tight.CreateSocket(handle)!;

        Assert.False(tight.TryGrowSendBuffer(socket));
        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);
        Assert.Equal(1, tight.Maintain().GrowthRefusals);

        ring.CloseListener(listener);
    }

    [Fact]
    public void Shrink_ReturnsDrainedSocketToBase_AndRefusesWhileBusy()
    {
        var socket = Accept(out var client);
        Assert.True(_manager.TryGrowSendBuffer(socket));

        Write(socket, Pattern(512, 1));
        _manager.ProcessSendQueue();
        _manager.Submit();
        Assert.False(_manager.TryShrinkSendBuffer(socket)); // in flight

        ReadAll(client, 512);
        Assert.True(_manager.TryShrinkSendBuffer(socket));
        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);
        Assert.False(_manager.TryShrinkSendBuffer(socket)); // already base

        client.Close();
    }

    [Fact]
    public void Maintain_ReportsTierUsageAndTrimsAfterQuietWindows()
    {
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8, maxRegisteredBuffers: 128);
        using var quick = new RingSocketManager(
            ring, maxSockets: 8, recvBufferSize: Base, sendBufferSize: Base,
            initialBufferSlabs: 1, maxBufferSlabs: 2,
            maxSendBufferSize: 2 * Base, sendBufferGrowthBudget: 64L * Base, sendBufferRetentionWindows: 1
        );
        var port = 22000 + Random.Shared.Next(1000);
        var listener = ring.CreateListener("127.0.0.1", (ushort)port, 4);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        ring.PrepareAccept(listener, 0, 0, IORingUserData.EncodeAccept());
        ring.Submit();
        var completions = new Completion[1];
        nint handle = -1;
        for (var i = 0; i < 100 && handle <= 0; i++)
        {
            if (ring.PeekCompletions(completions) > 0)
            {
                ring.AdvanceCompletionQueue(1);
                handle = completions[0].Result;
            }
            else
            {
                Thread.Sleep(10);
            }
        }
        ring.ConfigureSocket(handle);
        var socket = quick.CreateSocket(handle)!;

        Assert.True(quick.TryGrowSendBuffer(socket));
        var stats = quick.Maintain();
        Assert.Equal(1, stats.TierInUse);
        Assert.True(stats.TierCapacityBytes > 0);

        Assert.True(quick.TryShrinkSendBuffer(socket));
        quick.Maintain(); // window: peak 1 (from before the shrink)
        var trimmed = quick.Maintain(); // window: peak 0, floor 0, slab released
        Assert.True(trimmed.BuffersReleased > 0);
        Assert.Equal(0, trimmed.TierCapacityBytes);

        ring.CloseListener(listener);
    }
}
