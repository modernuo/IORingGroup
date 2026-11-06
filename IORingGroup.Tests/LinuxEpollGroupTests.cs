// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;

namespace IORingGroup.Tests;

public class LinuxEpollGroupTests
{
    [SkippableFact]
    public void CreateLinuxEpoll_OnLinux_ReturnsValidInstance()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void CreateLinuxEpoll_OnNonLinux_ThrowsPlatformNotSupported()
    {
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        Assert.Throws<PlatformNotSupportedException>(() => System.Network.IORingGroup.CreateLinuxEpoll());
    }

    [SkippableFact]
    public void SubmissionQueueSpace_InitiallyPositive()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll(256);
        Assert.True(ring.SubmissionQueueSpace > 0);
    }

    [SkippableFact]
    public void CompletionQueueCount_InitiallyZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        Assert.Equal(0, ring.CompletionQueueCount);
    }

    [SkippableFact]
    public void Submit_WithNoOperations_ReturnsZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        var submitted = ring.Submit();
        Assert.Equal(0, submitted);
    }

    [SkippableFact]
    public void PeekCompletions_WithNoCompletions_ReturnsZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        Span<Completion> completions = stackalloc Completion[16];
        var count = ring.PeekCompletions(completions);
        Assert.Equal(0, count);
    }

    [SkippableFact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        ring.Dispose();
        ring.Dispose();
    }

    [SkippableFact]
    public void CreateListener_BindsSuccessfully()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        var listener = ring.CreateListener("127.0.0.1", 0, 128);
        Assert.True(listener >= 0, $"CreateListener returned {listener}");
        ring.CloseListener(listener);
    }

    [SkippableFact]
    public void RegisterSocket_ReturnsValidConnId()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll(maxConnections: 16);
        var listener = ring.CreateListener("127.0.0.1", 0, 1);
        Assert.True(listener >= 0);
        ring.CloseListener(listener);
    }

    [SkippableFact]
    public void BufferRegistration_RegisterAndUnregister()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        using var buffer = IORingBuffer.Create(4096);
        var bufferId = ring.RegisterBuffer(buffer);
        Assert.True(bufferId >= 0);
        ring.UnregisterBuffer(bufferId);
    }
    [SkippableFact]
    public void Accept_CompletesEveryPostedAcceptInOneWait()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        var listener = ring.CreateListener("127.0.0.1", 0, 128);
        Assert.NotEqual(-1, listener);

        const int posted = 16;
        var clients = Connect(listener, posted);
        try
        {
            for (var i = 0; i < posted; i++)
            {
                ring.PrepareAccept(listener, 0, 0, IORingUserData.EncodeAccept());
            }

            ring.SubmitAndWait(1);

            Assert.Equal(posted, TakeAccepted(ring));
        }
        finally
        {
            Dispose(clients);
            ring.CloseListener(listener);
        }
    }

    [SkippableFact]
    public void Accept_TakesNoMoreThanPosted_AndKeepsTheRestQueued()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
        using var ring = System.Network.IORingGroup.CreateLinuxEpoll();
        var listener = ring.CreateListener("127.0.0.1", 0, 128);
        Assert.NotEqual(-1, listener);

        var clients = Connect(listener, 6);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                ring.PrepareAccept(listener, 0, 0, IORingUserData.EncodeAccept());
            }

            ring.SubmitAndWait(1);
            Assert.Equal(4, TakeAccepted(ring));

            // The two connections left in the backlog surface once accepts are posted again.
            for (var i = 0; i < 4; i++)
            {
                ring.PrepareAccept(listener, 0, 0, IORingUserData.EncodeAccept());
            }

            ring.SubmitAndWait(2);
            Assert.Equal(2, TakeAccepted(ring));

            // The two unused accepts stay posted and take the next connection.
            clients.AddRange(Connect(listener, 1));
            ring.SubmitAndWait(1);
            Assert.Equal(1, TakeAccepted(ring));
        }
        finally
        {
            Dispose(clients);
            ring.CloseListener(listener);
        }
    }

    private static List<Socket> Connect(nint listener, int count)
    {
        int port;
        using (var socket = new Socket(new SafeSocketHandle(listener, ownsHandle: false)))
        {
            port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        var clients = new List<Socket>(count);
        for (var i = 0; i < count; i++)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(IPAddress.Loopback, port);
            clients.Add(client);
        }

        return clients;
    }

    // Counts and closes accepted sockets among the ready completions.
    private static int TakeAccepted(IIORingGroup ring)
    {
        Span<Completion> completions = stackalloc Completion[64];
        var count = ring.PeekCompletions(completions);
        var accepted = 0;
        for (var i = 0; i < count; i++)
        {
            Assert.True(completions[i].Result >= 0, $"accept failed: {completions[i].Result}");
            ring.CloseSocket(completions[i].Result);
            accepted++;
        }

        ring.AdvanceCompletionQueue(count);
        return accepted;
    }

    private static void Dispose(List<Socket> clients)
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
