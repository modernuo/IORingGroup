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
    public void TryParseBindAddress_RejectsAnythingElse(string address)
    {
        Assert.False(NativeSocketAddress.TryParseBindAddress(address, out _));
    }

    [Fact]
    public unsafe void TryCreate_Linux_EncodesSockaddrIn()
    {
        Assert.True(NativeSocketAddress.TryCreate("127.0.0.1", 2593, SocketAddressLayout.Linux, out var addr));

        Assert.Equal(16, addr.Length);
        Assert.Equal(2, addr.Family);
        byte[] expected = [0x02, 0x00, 0x0A, 0x21, 127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(expected, new ReadOnlySpan<byte>(addr.Data, addr.Length).ToArray());
    }

    [Fact]
    public unsafe void TryCreate_Bsd_PrefixesLength()
    {
        Assert.True(NativeSocketAddress.TryCreate("10.1.2.3", 80, SocketAddressLayout.Bsd, out var addr));

        byte[] expected = [16, 0x02, 0x00, 0x50, 10, 1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(expected, new ReadOnlySpan<byte>(addr.Data, addr.Length).ToArray());
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

    /// <summary>Posts an accept on <paramref name="listener"/> and waits for it to succeed.</summary>
    internal static void AssertAccepts(IIORingGroup ring, nint listener)
    {
        var userData = IORingUserData.EncodeAccept();
        ring.PrepareAccept(listener, 0, 0, userData);

        Span<Completion> completions = stackalloc Completion[4];
        var count = TestHelpers.WaitForCompletions(ring, completions, 1, 5000);
        Assert.Equal(1, count);
        Assert.Equal(userData, completions[0].UserData);
        Assert.True(completions[0].Result >= 0, $"accept failed: {completions[0].Result}");

        ring.AdvanceCompletionQueue(count);
        ring.CloseSocket(completions[0].Result);
    }
}
