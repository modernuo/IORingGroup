// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Net;
using System.Net.Sockets;
using System.Network;
using System.Runtime.InteropServices;

namespace IORingGroup.Tests;

public class NativeSocketAddressTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.20")]
    public void TryParseBindAddress_AcceptsDottedQuad(string? address)
    {
        Assert.True(NativeSocketAddress.TryParseBindAddress(address, out var parsed));
        Assert.Equal(AddressFamily.InterNetwork, parsed.AddressFamily);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("not-an-ip")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("1.2.3.256")]
    [InlineData("01.2.3.4")]
    [InlineData(" 127.0.0.1")]
    [InlineData("127.0.0.1:2593")]
    [InlineData("[::1]:2593")]
    [InlineData("[::1")]
    [InlineData("::1]")]
    [InlineData("[[::1]]")]
    [InlineData("::g")]
    [InlineData("[127.0.0.1]")]
    public void TryParseBindAddress_RejectsAnythingElse(string address)
    {
        Assert.False(NativeSocketAddress.TryParseBindAddress(address, out _));
    }

    [Theory]
    [InlineData("::", "::")]
    [InlineData("::1", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("2001:db8::7", "2001:db8::7")]
    [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1")]
    [InlineData("fe80::1%3", "fe80::1%3")]
    public void TryParseBindAddress_AcceptsIPv6(string address, string expected)
    {
        Assert.True(NativeSocketAddress.TryParseBindAddress(address, out var parsed));
        Assert.Equal(AddressFamily.InterNetworkV6, parsed.AddressFamily);
        Assert.Equal(IPAddress.Parse(expected), parsed);
    }

    [Fact]
    public void TryCreate_EncodesPortAndAddress()
    {
        Assert.True(NativeSocketAddress.TryCreate("127.0.0.1", 2593, SocketAddressLayout.Linux, out var addr));

        // Family bytes differ by platform; port and address sit at the same offsets everywhere.
        Assert.Equal(16, addr.Bytes.Length);
        byte[] expected = [0x0A, 0x21, 127, 0, 0, 1];
        Assert.Equal(expected, addr.Bytes.Slice(2, 6).ToArray());
    }

    public static TheoryData<string, int> V6Layouts() => new()
    {
        { "windows", 23 },
        { "linux", 10 },
        { "macos", 30 },
        { "freebsd", 28 }
    };

    [Theory]
    [MemberData(nameof(V6Layouts))]
    public void TryCreate_IPv6UsesThePlatformFamily(string platform, int family)
    {
        var layout = platform switch
        {
            "windows" => SocketAddressLayout.Windows,
            "linux" => SocketAddressLayout.Linux,
            "macos" => SocketAddressLayout.MacOS,
            _ => SocketAddressLayout.FreeBsd
        };

        Assert.True(NativeSocketAddress.TryCreate("fe80::1%3", 2593, layout, out var addr));

        Assert.True(addr.IsIPv6);
        Assert.Equal(family, addr.Family);
    }

    [Fact]
    public void TryCreate_EncodesSockaddrIn6()
    {
        Assert.True(NativeSocketAddress.TryCreate("fe80::1%3", 2593, SocketAddressLayout.Linux, out var addr));

        // sockaddr_in6: family(2) port(2) flowinfo(4) addr(16) scope_id(4, host order)
        var bytes = addr.Bytes;
        Assert.Equal(28, bytes.Length);
        Assert.Equal([0x0A, 0x21], bytes.Slice(2, 2).ToArray());
        Assert.Equal(IPAddress.Parse("fe80::1").GetAddressBytes(), bytes.Slice(8, 16).ToArray());
        Assert.Equal(3u, BitConverter.ToUInt32(bytes.Slice(24, 4)));
    }

    [Fact]
    public void TryCreate_IPv4IsNotIPv6()
    {
        Assert.True(NativeSocketAddress.TryCreate("0.0.0.0", 1, SocketAddressLayout.Windows, out var addr));
        Assert.False(addr.IsIPv6);
        Assert.Equal(2, addr.Family);
    }

    [Fact]
    public void TryCreate_RejectsInvalidAddress()
    {
        Assert.False(NativeSocketAddress.TryCreate("localhost", 2593, SocketAddressLayout.Linux, out _));
    }
}

/// <summary>
/// Listener creation against every backend the host can run: the platform default, plus epoll on
/// Linux (where the default is io_uring when available).
/// </summary>
public class ListenerAddressTests
{
    public static TheoryData<string> Backends()
    {
        var data = new TheoryData<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Named explicitly so a host without io_uring reports a skip rather than silently
            // testing the epoll fallback twice.
            data.Add("iouring");
            data.Add("epoll");
        }
        else
        {
            data.Add("default");
        }

        return data;
    }

    internal static IIORingGroup CreateRing(string backend)
    {
        switch (backend)
        {
            case "iouring":
                Skip.IfNot(System.Network.IORing.LinuxIORingGroup.IsAvailable(), "io_uring unavailable");
                return new System.Network.IORing.LinuxIORingGroup(256);
            case "epoll":
                return System.Network.IORingGroup.CreateLinuxEpoll(queueSize: 256);
            default:
                return System.Network.IORingGroup.Create(queueSize: 256);
        }
    }

    /// <summary>Reads the bound endpoint of a listener created with port 0.</summary>
    internal static IPEndPoint GetLocalEndPoint(nint listener)
    {
        using var socket = new Socket(new SafeSocketHandle(listener, ownsHandle: false));
        return (IPEndPoint)socket.LocalEndPoint!;
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_RejectsInvalidAddress(string backend)
    {
        using var ring = CreateRing(backend);

        foreach (var address in (string[])["localhost", "not-an-ip", "127.1", "1.2.3.256"])
        {
            var listener = ring.CreateListener(address, 0, 16);
            if (listener != -1)
            {
                ring.CloseListener(listener);
            }

            Assert.True(listener == -1, $"'{address}' should be rejected");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_Loopback_BindsLoopbackOnly(string backend)
    {
        using var ring = CreateRing(backend);
        var listener = ring.CreateListener("127.0.0.1", 0, 16);
        Assert.NotEqual(-1, listener);

        try
        {
            var local = GetLocalEndPoint(listener);
            Assert.Equal(IPAddress.Loopback, local.Address);
            Assert.NotEqual(0, local.Port);
        }
        finally
        {
            ring.CloseListener(listener);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_Loopback_AcceptsConnection(string backend)
    {
        using var ring = CreateRing(backend);
        var listener = ring.CreateListener("127.0.0.1", 0, 16);
        Assert.NotEqual(-1, listener);

        try
        {
            var local = GetLocalEndPoint(listener);
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(local);

            AssertAccepts(ring, listener);
        }
        finally
        {
            ring.CloseListener(listener);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_IPv6Loopback_AcceptsConnection(string backend)
    {
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 unavailable");
        using var ring = CreateRing(backend);

        foreach (var address in (string[])["::1", "[::1]"])
        {
            var listener = ring.CreateListener(address, 0, 16);
            Assert.True(listener != -1, $"'{address}' failed to bind");

            try
            {
                var local = GetLocalEndPoint(listener);
                Assert.Equal(IPAddress.IPv6Loopback, local.Address);

                using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                client.Connect(local);

                // The peer address must read back as IPv6: on RIO an accept socket of the wrong
                // family still accepts, but getpeername on it fails.
                var accepted = Accept(ring, listener);
                try
                {
                    using var peer = new Socket(new SafeSocketHandle(accepted, ownsHandle: false));
                    Assert.Equal(IPAddress.IPv6Loopback, (peer.RemoteEndPoint as IPEndPoint)?.Address);
                }
                finally
                {
                    ring.CloseSocket(accepted);
                }
            }
            finally
            {
                ring.CloseListener(listener);
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_IPv6_DefaultsToV6Only(string backend)
    {
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 unavailable");
        using var ring = CreateRing(backend);
        var listener = ring.CreateListener("::1", 0, 16);
        Assert.NotEqual(-1, listener);

        try
        {
            // Socket.DualMode reads IPV6_V6ONLY back from the listener.
            using var socket = new Socket(new SafeSocketHandle(listener, ownsHandle: false));
            Assert.False(socket.DualMode);
        }
        finally
        {
            ring.CloseListener(listener);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_DualMode_AcceptsIPv4AsMapped(string backend)
    {
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 unavailable");
        using var ring = CreateRing(backend);

        // The IPv4-mapped loopback exercises dual mode without binding a wildcard address.
        var listener = ring.CreateListener("::ffff:127.0.0.1", 0, 16, dualMode: true);
        Assert.NotEqual(-1, listener);

        try
        {
            using (var socket = new Socket(new SafeSocketHandle(listener, ownsHandle: false)))
            {
                Assert.True(socket.DualMode);
            }

            var port = GetLocalEndPoint(listener).Port;
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, port);

            var accepted = Accept(ring, listener);
            try
            {
                using var peer = new Socket(new SafeSocketHandle(accepted, ownsHandle: false));
                var remote = peer.RemoteEndPoint as IPEndPoint;
                Assert.NotNull(remote);
                Assert.True(remote.Address.IsIPv4MappedToIPv6, $"remote was {remote.Address}");
                Assert.Equal(IPAddress.Loopback, remote.Address.MapToIPv4());
            }
            finally
            {
                ring.CloseSocket(accepted);
            }
        }
        finally
        {
            ring.CloseListener(listener);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_DualMode_IgnoredForIPv4(string backend)
    {
        using var ring = CreateRing(backend);
        var listener = ring.CreateListener("127.0.0.1", 0, 16, dualMode: true);
        Assert.NotEqual(-1, listener);
        ring.CloseListener(listener);
    }

    /// <summary>
    /// "0.0.0.0" and "::" on one port. Under Linux's usual bindv6only=0, "::" also claims IPv4, so
    /// this needs IPV6_V6ONLY set explicitly or the second bind fails with EADDRINUSE.
    /// </summary>
    /// <remarks>Linux only: wildcard binds on Windows raise a firewall prompt, and v6-only is already its default.</remarks>
    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void CreateListener_IPv4AndIPv6Wildcards_ShareAPort(string backend)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Linux only");
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 unavailable");
        using var ring = CreateRing(backend);

        var v4 = ring.CreateListener("0.0.0.0", 0, 16);
        Assert.NotEqual(-1, v4);
        var port = (ushort)GetLocalEndPoint(v4).Port;
        var v6 = ring.CreateListener("::", port, 16);

        try
        {
            Assert.NotEqual(-1, v6);

            using var client4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client4.Connect(IPAddress.Loopback, port);
            AssertAccepts(ring, v4);

            using var client6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            client6.Connect(IPAddress.IPv6Loopback, port);
            AssertAccepts(ring, v6);
        }
        finally
        {
            ring.CloseListener(v4);
            if (v6 != -1)
            {
                ring.CloseListener(v6);
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void RingSocketManager_EchoesOverIPv6(string backend)
    {
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 unavailable");
        using var ring = CreateRing(backend);
        using var manager = new RingSocketManager(ring, maxSockets: 4, recvBufferSize: 64 * 1024, sendBufferSize: 64 * 1024);
        var listener = ring.CreateListener("::1", 0, 16);
        Assert.NotEqual(-1, listener);

        try
        {
            using var client = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(GetLocalEndPoint(listener));

            var handle = Accept(ring, listener);
            ring.ConfigureSocket(handle);
            var socket = manager.CreateSocket(handle);
            Assert.NotNull(socket);
            manager.Submit();

            var payload = "hello over IPv6"u8.ToArray();
            client.Send(payload);

            var events = new RingSocketEvent[16];
            var received = 0;
            var deadline = Environment.TickCount64 + 5000;
            while (received < payload.Length && Environment.TickCount64 < deadline)
            {
                var count = manager.ProcessCompletions(events);
                for (var i = 0; i < count; i++)
                {
                    if (events[i].Type == RingSocketEventType.DataReceived)
                    {
                        received += events[i].BytesTransferred;
                    }
                }

                manager.Submit();
                Thread.Sleep(1);
            }

            Assert.Equal(payload.Length, received);
            socket.RecvBuffer.GetReadSpan()[..received].CopyTo(socket.SendBuffer.GetWriteSpan());
            socket.SendBuffer.CommitWrite(received);
            socket.RecvBuffer.CommitRead(received);
            socket.QueueSend();

            var echoed = new byte[payload.Length];
            var got = 0;
            deadline = Environment.TickCount64 + 5000;
            while (got < echoed.Length && Environment.TickCount64 < deadline)
            {
                manager.ProcessCompletions(events);
                manager.Submit();
                if (client.Available > 0)
                {
                    got += client.Receive(echoed, got, echoed.Length - got, SocketFlags.None);
                }

                Thread.Sleep(1);
            }

            Assert.Equal(payload, echoed);

            // A graceful disconnect retires once the peer closes too.
            socket.Disconnect();
            manager.Submit();
            client.Close();
            deadline = Environment.TickCount64 + 5000;
            while (manager.ConnectedCount > 0 && Environment.TickCount64 < deadline)
            {
                manager.ProcessCompletions(events);
                manager.Submit();
                Thread.Sleep(1);
            }

            Assert.Equal(0, manager.ConnectedCount);
        }
        finally
        {
            ring.CloseListener(listener);
        }
    }

    /// <summary>
    /// Two listeners on one port, one per family: the accept event says which one the connection
    /// came through, which the local port alone cannot.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public void AcceptEvent_ReportsListenerIndex(string backend)
    {
        Skip.IfNot(Socket.OSSupportsIPv6, "IPv6 unavailable");
        using var ring = CreateRing(backend);
        using var manager = new RingSocketManager(ring, maxSockets: 4, recvBufferSize: 64 * 1024, sendBufferSize: 64 * 1024);

        var v4 = ring.CreateListener("127.0.0.1", 0, 16);
        Assert.NotEqual(-1, v4);
        var port = (ushort)GetLocalEndPoint(v4).Port;
        var v6 = ring.CreateListener("::1", port, 16);

        try
        {
            Assert.NotEqual(-1, v6);
            ring.PrepareAccept(v4, 0, 0, IORingUserData.EncodeAccept(0));
            ring.PrepareAccept(v6, 0, 0, IORingUserData.EncodeAccept(1));

            using var client6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            client6.Connect(IPAddress.IPv6Loopback, port);
            Assert.Equal(1, WaitForAccept(manager));

            using var client4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client4.Connect(IPAddress.Loopback, port);
            Assert.Equal(0, WaitForAccept(manager));
        }
        finally
        {
            ring.CloseListener(v4);
            if (v6 != -1)
            {
                ring.CloseListener(v6);
            }
        }

        static int WaitForAccept(RingSocketManager manager)
        {
            var events = new RingSocketEvent[8];
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                manager.Submit();
                var count = manager.ProcessCompletions(events);
                for (var i = 0; i < count; i++)
                {
                    if (events[i].Type == RingSocketEventType.Accept)
                    {
                        Assert.True(events[i].AcceptedSocketHandle >= 0, $"accept failed: {events[i].AcceptedSocketHandle}");
                        manager.Ring.CloseSocket(events[i].AcceptedSocketHandle);
                        return events[i].ListenerIndex;
                    }
                }

                Thread.Sleep(1);
            }

            return -1;
        }
    }

    /// <summary>Posts an accept on <paramref name="listener"/> and waits for it to succeed.</summary>
    internal static void AssertAccepts(IIORingGroup ring, nint listener) => ring.CloseSocket(Accept(ring, listener));

    /// <summary>Posts an accept on <paramref name="listener"/> and returns the accepted handle.</summary>
    internal static nint Accept(IIORingGroup ring, nint listener)
    {
        var userData = IORingUserData.EncodeAccept();
        ring.PrepareAccept(listener, 0, 0, userData);

        Span<Completion> completions = stackalloc Completion[4];
        var count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);
        Assert.Equal(1, count);
        Assert.Equal(userData, completions[0].UserData);
        Assert.True(completions[0].Result >= 0, $"accept failed: {completions[0].Result}");

        ring.AdvanceCompletionQueue(count);
        return completions[0].Result;
    }
}
