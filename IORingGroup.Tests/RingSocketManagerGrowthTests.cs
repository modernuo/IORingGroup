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
    /// Pumps until the manager reaps the last send completion; the peer having every byte doesn't
    /// mean SendsInFlight is zero yet, so a shrink asked for too early is legitimately refused.
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
        // slabSize = max(16, 16/4) = 16, the same for both pools, and 16 sockets fill exactly one
        // slab of each.
        Assert.Equal(16 + 16, RingSocketManager.RequiredRegisteredBuffers(16, Base, Base, 0, 4));

        // 16 x 64 KB of budget buys 8 first-tier (128 KB) buffers.
        Assert.Equal(16 + 16 + 8, RingSocketManager.RequiredRegisteredBuffers(16, Base, 4 * Base, 16L * Base, 4));
    }

    [Fact]
    public void RequiredRegisteredBuffers_FitsTheLibraryDefaultTable()
    {
        // Boundary-exact: slab = max(16, 1024/128) = 16 divides 1024, so the no-growth requirement is
        // exactly 2048 and a table sized maxConnections x 2 still fits. Counts that the slab does not
        // divide need more, which is why the factory derives its default from the same rule.
        Assert.Equal(2048, RingSocketManager.RequiredRegisteredBuffers(1024));
        Assert.True(RingSocketManager.RequiredRegisteredBuffers(1024, 256 * 1024, 256 * 1024, 0) <= 2048);

        // The two overloads agree when growth is off
        Assert.Equal(
            RingSocketManager.RequiredRegisteredBuffers(1000),
            RingSocketManager.RequiredRegisteredBuffers(1000, 256 * 1024, 256 * 1024, 0)
        );

        // slab = max(16, 1000/128) = 16, and 1000 rounds up to 1008 buffers per pool
        Assert.Equal(2 * 1008, RingSocketManager.RequiredRegisteredBuffers(1000));

        // slabSize = max(16, 4096/128) = 32, so each base pool holds one buffer per socket, plus
        // 256 MiB of budget worth of 512 KB first-tier buffers.
        Assert.Equal(
            4096 + 4096 + 512,
            RingSocketManager.RequiredRegisteredBuffers(4096, 256 * 1024, 2 * 1024 * 1024, 256L * 1024 * 1024)
        );
    }

    [Fact]
    public void BasePools_AreSymmetric_AndReachMaxSockets()
    {
        // slab = max(16, 40 / 4) = 16; both pools need 3 slabs to hold 40 sockets
        Assert.Equal(16, RingSocketManager.BasePoolSlabSize(40, 4));
        Assert.Equal(2 * 48, RingSocketManager.RequiredRegisteredBuffers(40, Base, Base, 0, 4));

        // The floor keeps a tiny socket table off one-buffer slabs
        Assert.Equal(16, RingSocketManager.BasePoolSlabSize(8, 4));
    }

    [Fact]
    public void CreateSocket_SucceedsForEverySocketUpToMax()
    {
        // slab = max(16, 40 / 2) = 20, so the pools start at 20 buffers and grow one slab to reach 40
        const int maxSockets = 40;
        var registered = RingSocketManager.RequiredRegisteredBuffers(maxSockets, Base, Base, 0, 2);
        Assert.Equal(2 * maxSockets, registered);

        using var ring = System.Network.IORingGroup.Create(
            queueSize: 256, maxConnections: maxSockets, maxRegisteredBuffers: registered
        );
        using var manager = new RingSocketManager(
            ring, maxSockets: maxSockets, recvBufferSize: Base, sendBufferSize: Base,
            initialBufferSlabs: 1, maxBufferSlabs: 2
        );

        var port = 23000 + Random.Shared.Next(1000);
        var listener = ring.CreateListener("127.0.0.1", (ushort)port, 64);
        var clients = new List<Socket>(maxSockets);

        try
        {
            for (var i = 0; i < maxSockets; i++)
            {
                // The old send pool held maxBufferSlabs x (max(64, maxSockets / maxBufferSlabs) / 4)
                // = 2 x 16 = 32 buffers here, so this returned null on socket 33
                Assert.NotNull(manager.CreateSocket(AcceptOn(ring, listener, port, clients)));
                manager.Submit();
            }

            Assert.Equal(maxSockets, manager.ConnectedCount);
        }
        finally
        {
            CloseAll(manager, clients);
            ring.CloseListener(listener);
        }
    }

    [Fact]
    public void Maintain_TrimsIdleBaseSlabsDownToTheInitialCount()
    {
        const int maxSockets = 40;
        const int slab = 16;
        var registered = RingSocketManager.RequiredRegisteredBuffers(maxSockets, Base, Base, 0, 4);
        using var ring = System.Network.IORingGroup.Create(
            queueSize: 256, maxConnections: maxSockets, maxRegisteredBuffers: registered
        );
        using var manager = new RingSocketManager(
            ring, maxSockets: maxSockets, recvBufferSize: Base, sendBufferSize: Base,
            initialBufferSlabs: 1, maxBufferSlabs: 4, sendBufferRetentionWindows: 1
        );

        var port = 24000 + Random.Shared.Next(1000);
        var listener = ring.CreateListener("127.0.0.1", (ushort)port, 64);
        var clients = new List<Socket>(20);

        try
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.NotNull(manager.CreateSocket(AcceptOn(ring, listener, port, clients)));
                manager.Submit();
            }

            // Two slabs of each pool are live
            Assert.Equal(2L * 2 * slab * Base, manager.Maintain().BaseCapacityBytes);

            CloseAll(manager, clients);
            Assert.Equal(0, manager.ConnectedCount);

            manager.Maintain(); // window records the 20 that were live
            var trimmed = manager.Maintain(); // peak 0: the top slab of each pool goes back

            Assert.Equal(2 * slab, trimmed.BaseBuffersReleased);
            Assert.Equal(2L * slab * Base, trimmed.BaseCapacityBytes);
        }
        finally
        {
            CloseAll(manager, clients);
            ring.CloseListener(listener);
        }
    }

    /// <summary>
    /// Connects a client, accepts it on <paramref name="listener"/>, and returns the configured handle.
    /// </summary>
    private static nint AcceptOn(IIORingGroup ring, nint listener, int port, List<Socket> clients)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        clients.Add(client);

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
        return handle;
    }

    /// <summary>
    /// Closes every client and pumps until the manager has released their slots and buffers.
    /// </summary>
    private static void CloseAll(RingSocketManager manager, List<Socket> clients)
    {
        for (var i = 0; i < clients.Count; i++)
        {
            clients[i].Close();
        }

        clients.Clear();

        var events = new RingSocketEvent[64];
        for (var i = 0; i < 1000 && manager.ConnectedCount > 0; i++)
        {
            manager.ProcessCompletions(events);
            manager.Submit();
            Thread.Sleep(2);
        }

        // Buffers go back on the pass after the consumer has seen the disconnect events
        manager.ProcessCompletions(events);
        manager.Submit();
    }

    [Theory]
    [InlineData(1024)] // slab divides the count exactly
    [InlineData(1000)] // 1008 per pool: a table of maxConnections x 2 would be 16 short
    [InlineData(100)]  // the 16-buffer floor applies: 112 per pool against a x 2 table of 200
    public void Constructor_ComposesWithTheLibraryDefaults(int maxConnections)
    {
        // Create(maxConnections: n) + new RingSocketManager(ring, n) alone must work, so the ring's
        // default table comes from the manager's own rule rather than a duplicate of it.
        using var ring = System.Network.IORingGroup.Create(queueSize: 256, maxConnections: maxConnections);
        using var manager = new RingSocketManager(ring, maxConnections);

        Assert.Equal(RingSocketManager.RequiredRegisteredBuffers(maxConnections), ring.MaxRegisteredBuffers);
        Assert.Equal(maxConnections, manager.MaxSockets);
        Assert.Equal(0, manager.SendBufferTierCount); // growth off by default
    }

    [Fact]
    public void Constructor_DisposesEarlierPoolsWhenALaterOneFails()
    {
        // The cross-check compares table size, not what remains, so two managers on one ring can
        // both pass; the second's recv pool then claims the last entries and its send pool has
        // nowhere to register, stranding the recv pool.
        var needed = RingSocketManager.RequiredRegisteredBuffers(64, Base, Base, 0);
        Assert.Equal(64 + 64, needed);

        using var ring = System.Network.IORingGroup.Create(
            queueSize: 64, maxConnections: 64, maxRegisteredBuffers: needed + 64
        );

        // Four slabs up front on both sides: lazy pools would never reach the table's edge here.
        using var first = new RingSocketManager(
            ring, maxSockets: 64, recvBufferSize: Base, sendBufferSize: Base, initialBufferSlabs: 4
        );

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RingSocketManager(
                ring, maxSockets: 64, recvBufferSize: Base, sendBufferSize: Base, initialBufferSlabs: 4
            )
        );

        Assert.Contains("registration", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Free again - a leak would keep these 64 entries unavailable.
        using var proof = new IORingBufferPool(ring, slabSize: 64, bufferSize: Base, initialSlabs: 1, maxSlabs: 1);
        Assert.Equal(64, proof.TotalCapacity);
    }

    [Fact]
    public void Constructor_RejectsPositiveBudgetBelowOneTierSlab()
    {
        // This ring is also too small for the pools, but single-argument checks run before the
        // cross-check, so this ParamName fires first.
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
        // Explicit table: the default would be the 32 this configuration needs
        using var ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 8, maxRegisteredBuffers: 16);

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
        // 512 MiB overflows the int arithmetic that bounds a tier's slab; the ceiling is checked
        // before anything is allocated, so the ring's own size never comes into it.
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

        // Posted bytes stay readable until a completion is reaped, and nothing reaps one before the
        // grow, so the original genuinely has bytes in flight.
        Assert.Equal(1024, original.ReadableBytes);

        Assert.True(_manager.TryGrowSendBuffer(socket)); // 64 -> 128, original retiring
        Assert.Same(original, socket.RetiringSendBuffer);

        var middle = socket.SendBuffer;
        Write(socket, Pattern(2048, 2));

        Assert.True(_manager.TryGrowSendBuffer(socket)); // 128 -> 256, middle had nothing in flight

        Assert.Equal(4 * Base, socket.SendBuffer.PhysicalSize);
        // Middle had nothing in flight, so it returned to its pool immediately; NotSame alone would also pass on null.
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
        // the original is genuinely outstanding at grow time.
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

        // The send is outstanding until a completion is reaped, so the grow below leaves the original retiring.
        Assert.Equal(payload.Length, original.ReadableBytes);

        Assert.True(_manager.TryGrowSendBuffer(socket));
        Assert.Same(original, socket.RetiringSendBuffer);
        Assert.Equal(2 * Base, socket.SendBuffer.PhysicalSize);
        Assert.Equal(1, _manager.GetSendBufferTierStats(0).InUse);

        // The peer never reads, so the retiring buffer is still attached when the socket finalizes -
        // the path that must release two send buffers, not one.
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
