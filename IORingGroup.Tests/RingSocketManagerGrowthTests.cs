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
        var registered = RingSocketManager.RequiredRegisteredBuffers(64, Base, 4 * Base, 128L * Base, 4);
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

    /// <summary>
    /// Pumps until the manager has actually reaped the last send completion. The peer having every
    /// byte does not mean the DataSent completion has been processed: SendsInFlight is still
    /// non-zero until it is, and a shrink asked for in that window is legitimately refused.
    /// </summary>
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

    [Fact]
    public void Constructor_TiersFollowPowersOfTwoUpToMax()
    {
        Assert.Equal(2, _manager.SendBufferTierCount); // 128 KB, 256 KB
        Assert.Equal(4 * Base, _manager.MaxSendBufferSize);
    }

    [Fact]
    public void RequiredRegisteredBuffers_CoversBothPoolsPlusBudgetWorthOfFirstTier()
    {
        // slabSize = max(64, 16 / 4) = 64 and the send pool takes a quarter of that. Each base pool
        // is bounded by what 16 sockets can actually pull -- one buffer each, rounded up to whole
        // slabs -- rather than by the 4-slab ceiling, which neither pool could ever fill: one recv
        // slab (64) and one send slab (16).
        Assert.Equal(64 + 16, RingSocketManager.RequiredRegisteredBuffers(16, Base, Base, 0, 4));

        // 16 x 64 KB of budget buys 8 first-tier (128 KB) buffers.
        Assert.Equal(64 + 16 + 8, RingSocketManager.RequiredRegisteredBuffers(16, Base, 4 * Base, 16L * Base, 4));
    }

    [Fact]
    public void RequiredRegisteredBuffers_FitsTheLibraryDefaultTable()
    {
        // IORingGroup.Create's default table is maxConnections x 2. The manager's defaults have to
        // fit inside it, or the library cannot be used without an explicit table size.
        Assert.True(RingSocketManager.RequiredRegisteredBuffers(1024, 256 * 1024, 256 * 1024, 0) <= 2048);

        // ModernUO's shape: recv 32 x 128 = 4096 and send 32 x 32 = 1024, both at their slab
        // ceiling here, plus 256 MiB of budget worth of 512 KB first-tier buffers.
        Assert.Equal(
            4096 + 1024 + 512,
            RingSocketManager.RequiredRegisteredBuffers(4096, 256 * 1024, 2 * 1024 * 1024, 256L * 1024 * 1024)
        );
    }

    [Fact]
    public void Constructor_ComposesWithTheLibraryDefaults()
    {
        // Create(maxConnections: n) + new RingSocketManager(ring, n) with nothing else specified has
        // to work: the default table is maxConnections x 2, and the defaults must fit it.
        using var ring = System.Network.IORingGroup.Create(queueSize: 256, maxConnections: 1024);
        using var manager = new RingSocketManager(ring, 1024);

        Assert.Equal(1024, manager.MaxSockets);
        Assert.Equal(0, manager.SendBufferTierCount); // growth off by default
    }

    [Fact]
    public void Constructor_DisposesEarlierPoolsWhenALaterOneFails()
    {
        // Two managers on one ring: the cross-check only compares the table's size, not what is
        // left of it, so the second one passes and then runs out part way through. Its recv pool
        // takes the last 64 entries and its send pool has nowhere to register -- the case where the
        // recv pool is stranded, since nothing outside the constructor holds the half-built manager
        // to dispose it.
        var needed = RingSocketManager.RequiredRegisteredBuffers(64, Base, Base, 0);
        Assert.Equal(64 + 64, needed);

        using var ring = System.Network.IORingGroup.Create(
            queueSize: 64, maxConnections: 64, maxRegisteredBuffers: needed + 64
        );

        using var first = new RingSocketManager(ring, maxSockets: 64, recvBufferSize: Base, sendBufferSize: Base);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RingSocketManager(ring, maxSockets: 64, recvBufferSize: Base, sendBufferSize: Base)
        );

        Assert.Contains("registration", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The 64 entries the failed manager's recv pool held are free again, which they would not be
        // if it had leaked.
        using var proof = new IORingBufferPool(ring, slabSize: 64, bufferSize: Base, initialSlabs: 1, maxSlabs: 1);
        Assert.Equal(64, proof.TotalCapacity);
    }

    [Fact]
    public void Constructor_RejectsPositiveBudgetBelowOneTierSlab()
    {
        // The default registration table here (maxConnections x 2 = 16) is far too small for the
        // pools, so the constructor's ring cross-check would reject this ring too. The single-
        // argument checks run first and the cross-check last, because the cross-check needs every
        // sizing argument to already be known good; that ordering is what makes this ParamName
        // deterministic, and the assertion below pins it.
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RingSocketManager(
                ring, maxSockets: 8, recvBufferSize: Base, sendBufferSize: Base,
                initialBufferSlabs: 1, maxBufferSlabs: 2,
                maxSendBufferSize: 2 * Base,
                sendBufferGrowthBudget: RingSocketManager.MinimumSendBufferGrowthBudget(Base) - 1
            )
        );

        Assert.Equal("sendBufferGrowthBudget", ex.ParamName);
    }

    [Fact]
    public void Constructor_RejectsRingTooSmallForTheConfiguration()
    {
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8); // table = 16

        var ex = Assert.Throws<ArgumentException>(
            () => new RingSocketManager(
                ring, maxSockets: 8, recvBufferSize: Base, sendBufferSize: Base,
                initialBufferSlabs: 1, maxBufferSlabs: 2
            )
        );

        Assert.Equal("ring", ex.ParamName);
        Assert.Contains("RequiredRegisteredBuffers", ex.Message);
    }

    [Fact]
    public void Constructor_RejectsMaxSendBufferSizeAboveTheCeiling()
    {
        // 512 MiB overflows the int arithmetic that bounds a tier's slab, so it is refused outright
        // rather than silently defeating the budget. The ceiling is checked before anything is
        // allocated, so the ring's own size never comes into it.
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RingSocketManager(
                ring, maxSockets: 8, recvBufferSize: Base, sendBufferSize: Base,
                initialBufferSlabs: 0, maxBufferSlabs: 2,
                maxSendBufferSize: 512 * 1024 * 1024,
                sendBufferGrowthBudget: 1L << 40
            )
        );

        Assert.Equal("maxSendBufferSize", ex.ParamName);
        Assert.Contains("256 MiB", ex.Message);
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
        WaitForDrain(socket);
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
        var original = socket.SendBuffer;
        Write(socket, Pattern(1024, 1));
        _manager.ProcessSendQueue();
        _manager.Submit();

        // Posted bytes stay readable until a completion is reaped, and nothing reaps one between
        // here and the grow, so the original genuinely has bytes in flight no matter how fast
        // loopback is.
        Assert.Equal(1024, original.ReadableBytes);

        Assert.True(_manager.TryGrowSendBuffer(socket)); // 64 -> 128, original retiring
        Assert.Same(original, socket.RetiringSendBuffer);

        var middle = socket.SendBuffer;
        Write(socket, Pattern(2048, 2));

        Assert.True(_manager.TryGrowSendBuffer(socket)); // 128 -> 256, middle had nothing in flight

        Assert.Equal(4 * Base, socket.SendBuffer.PhysicalSize);
        // The middle buffer had nothing in flight, so it went straight back to its pool and the
        // original is still the one retiring -- NotSame alone would also pass on a null.
        Assert.Same(original, socket.RetiringSendBuffer);
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
        Assert.Equal(0, _manager.Maintain().GrowthRefusals); // running out of tiers is not a budget refusal

        client.Close();
    }

    [Fact]
    public void Grow_RefusedWhenBudgetExhausted()
    {
        var budget = RingSocketManager.MinimumSendBufferGrowthBudget(Base); // exactly one first-tier slab
        var registered = RingSocketManager.RequiredRegisteredBuffers(8, Base, 4 * Base, budget, 2);
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8, maxRegisteredBuffers: registered);
        using var tight = new RingSocketManager(
            ring, maxSockets: 8, recvBufferSize: Base, sendBufferSize: Base,
            initialBufferSlabs: 1, maxBufferSlabs: 2,
            maxSendBufferSize: 4 * Base, sendBufferGrowthBudget: budget
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

        Assert.True(tight.TryGrowSendBuffer(socket)); // the first tier's slab fits the budget exactly
        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);

        Assert.False(tight.TryGrowSendBuffer(socket)); // the second tier's slab does not fit alongside it
        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);
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
        WaitForDrain(socket);
        Assert.True(_manager.TryShrinkSendBuffer(socket));
        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);
        Assert.False(_manager.TryShrinkSendBuffer(socket)); // already base

        client.Close();
    }

    [Fact]
    public void Shrink_RefusedWhileABufferIsRetiring()
    {
        var socket = Accept(out var client);
        var original = socket.SendBuffer;

        var payload = Pattern(1024, 7);
        Write(socket, payload);
        _manager.ProcessSendQueue();
        _manager.Submit();

        // Posted bytes stay readable until a completion is reaped, and nothing reaps one here, so
        // the original is genuinely outstanding at grow time whatever loopback did.
        Assert.Equal(payload.Length, original.ReadableBytes);

        Assert.True(_manager.TryGrowSendBuffer(socket));
        var grown = socket.SendBuffer;
        Assert.Same(original, socket.RetiringSendBuffer);
        Assert.Equal(2 * Base, grown.PhysicalSize);

        // Shrinking now would hand the base pool a buffer the transport is still reading from.
        Assert.False(_manager.TryShrinkSendBuffer(socket));
        Assert.Same(grown, socket.SendBuffer);
        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);

        // Let the peer drain it; the completion retires the original.
        Assert.Equal(payload, ReadAll(client, payload.Length));
        WaitForDrain(socket);
        Assert.Null(socket.RetiringSendBuffer);

        Assert.True(_manager.TryShrinkSendBuffer(socket));
        Assert.Equal(Base, socket.SendBuffer.PhysicalSize);

        client.Close();
        DrainUntilDisconnected();
    }

    [Fact]
    public void Abort_WithARetiringBuffer_ReleasesBothBuffersExactlyOnce()
    {
        var socket = Accept(out var client);
        var original = socket.SendBuffer;

        var payload = Pattern(4096, 11);
        Write(socket, payload);
        _manager.ProcessSendQueue();
        _manager.Submit();

        // Same reasoning as above: the send is outstanding as far as the manager is concerned until
        // a completion is reaped, so the grow below always leaves the original retiring.
        Assert.Equal(payload.Length, original.ReadableBytes);

        Assert.True(_manager.TryGrowSendBuffer(socket));
        Assert.Same(original, socket.RetiringSendBuffer);
        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);
        Assert.Equal(1, _manager.GetSendBufferTierStats(0).InUse);

        // The peer never reads: the retiring buffer is still attached when the socket is finalized,
        // which is the path that has to release two send buffers rather than one.
        _manager.DisconnectImmediate(socket);

        var disconnected = false;
        for (var i = 0; i < 500 && !disconnected; i++)
        {
            var count = _manager.ProcessCompletions(_events);
            for (var j = 0; j < count; j++)
            {
                disconnected |= _events[j].Type == RingSocketEventType.Disconnected;
            }

            _manager.Submit();
            if (!disconnected)
            {
                Thread.Sleep(10);
            }
        }

        Assert.True(disconnected);

        // Buffers go back on the pass after the consumer has seen the event that referenced them.
        _manager.ProcessCompletions(_events);
        _manager.Submit();

        // A double release would drive InUse negative, a leak would leave it at 1.
        Assert.Equal(0, _manager.GetSendBufferTierStats(0).InUse);
        Assert.Equal(0, _manager.Maintain().TierInUse);
        Assert.Equal(0, _manager.ConnectedCount);

        // The base buffer went back to its own pool too, so the manager can still hand one out.
        var next = Accept(out var secondClient);
        Assert.Equal(1, _manager.ConnectedCount);
        Assert.Equal(Base, next.SendBuffer.PhysicalSize);

        client.Close();
        secondClient.Close();
        DrainUntilDisconnected();
    }

    private void DrainUntilDisconnected()
    {
        for (var i = 0; i < 500 && _manager.ConnectedCount > 0; i++)
        {
            _manager.ProcessCompletions(_events);
            _manager.Submit();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Maintain_ReportsTierUsageAndTrimsAfterQuietWindows()
    {
        var registered = RingSocketManager.RequiredRegisteredBuffers(8, Base, 2 * Base, 64L * Base, 2);
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8, maxRegisteredBuffers: registered);
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
