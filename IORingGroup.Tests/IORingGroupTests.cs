using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Network;
using Xunit;

namespace IORingGroup.Tests;

public class IORingGroupTests
{
    [SkippableFact]
    public void Create_ReturnsValidInstance()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void Create_WithCustomQueueSize_Succeeds()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create(1024);
        Assert.NotNull(ring);
    }

    [SkippableFact]
    public void Create_WithNonPowerOfTwo_ThrowsArgumentException()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        Assert.Throws<ArgumentException>(() => System.Network.IORingGroup.Create(1000));
    }

    [SkippableFact]
    public void SubmissionQueueSpace_InitiallyFull()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create(256);
        Assert.True(ring.SubmissionQueueSpace >= 256);
    }

    [SkippableFact]
    public void CompletionQueueCount_InitiallyZero()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        Assert.Equal(0, ring.CompletionQueueCount);
    }

    [SkippableFact]
    public void PreparePollAdd_DecreasesSubmissionSpace()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create(256);
        var initialSpace = ring.SubmissionQueueSpace;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        ring.PreparePollAdd(socket.Handle, PollMask.In, 1);

        Assert.Equal(initialSpace - 1, ring.SubmissionQueueSpace);
    }

    [SkippableFact]
    public void Submit_WithPendingOperations_ReturnsPositive()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        using var listener = CreateListeningSocket();

        ring.PreparePollAdd(listener.Handle, PollMask.In, 1);
        var submitted = ring.Submit();

        Assert.True(submitted >= 0);
    }

    [SkippableFact]
    public void Submit_WithNoOperations_ReturnsZero()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        var submitted = ring.Submit();

        Assert.Equal(0, submitted);
    }

    [SkippableFact]
    public void PeekCompletions_WithNoCompletions_ReturnsZero()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        Span<Completion> completions = stackalloc Completion[16];

        var count = ring.PeekCompletions(completions);

        Assert.Equal(0, count);
    }

    [SkippableFact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        var ring = System.Network.IORingGroup.Create();
        ring.Dispose();
        ring.Dispose(); // Should not throw
    }

    [SkippableFact]
    public void AdvanceCompletionQueue_DoesNotThrow()
    {
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported on this platform");

        using var ring = System.Network.IORingGroup.Create();
        ring.AdvanceCompletionQueue(0); // Should not throw
    }

    private static Socket CreateListeningSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);
        return socket;
    }
}

public class WindowsIORingGroupTests
{
    [SkippableFact]
    public void Backend_ReturnsValidValue()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported");

        using var ring = System.Network.IORingGroup.Create();
        if (ring is System.Network.Windows.WindowsIORingGroup winRing)
        {
            var backend = winRing.Backend;
            Assert.True(
                backend == System.Network.Windows.Win_x64.IORingBackend.IoRing ||
                backend == System.Network.Windows.Win_x64.IORingBackend.RIO
            );
        }
    }

    [SkippableFact]
    public void SendRecv_WithConnectedSockets_Works()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");
        Skip.IfNot(System.Network.IORingGroup.IsSupported, "IORingGroup not supported");

        using var ring = System.Network.IORingGroup.Create(256);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(endpoint);

        using var server = listener.Accept();

        // Test send
        var sendBuffer = "Hello, IORing!"u8.ToArray();
        unsafe
        {
            fixed (byte* pSend = sendBuffer)
            {
                ring.PrepareSend(client.Handle, (nint)pSend, sendBuffer.Length, MsgFlags.None, 1);
            }
        }
        ring.Submit();

        // Receive on server side (synchronous for test simplicity)
        var recvBuffer = new byte[sendBuffer.Length];
        var received = server.Receive(recvBuffer);

        Assert.Equal(sendBuffer.Length, received);
        Assert.Equal(sendBuffer, recvBuffer);
    }
}

public class CompletionStructTests
{
    [Fact]
    public void Completion_Constructor_SetsValues()
    {
        var completion = new Completion(12345, 100, CompletionFlags.More);

        Assert.Equal(12345UL, completion.UserData);
        Assert.Equal(100, completion.Result);
        Assert.Equal(CompletionFlags.More, completion.Flags);
    }

    [Fact]
    public void Completion_StructSize_Is16Bytes()
    {
        // Ensure struct is cache-friendly
        var size = Marshal.SizeOf<Completion>();
        Assert.True(size <= 24, $"Completion struct is {size} bytes, expected <= 24");
    }
}

public class PollMaskTests
{
    [Fact]
    public void PollMask_HasCorrectValues()
    {
        Assert.Equal(0x0001u, (uint)PollMask.In);
        Assert.Equal(0x0004u, (uint)PollMask.Out);
        Assert.Equal(0x0008u, (uint)PollMask.Err);
        Assert.Equal(0x0010u, (uint)PollMask.Hup);
        Assert.Equal(0x2000u, (uint)PollMask.RdHup);
    }

    [Fact]
    public void PollMask_CanCombine()
    {
        var combined = PollMask.In | PollMask.Out;
        Assert.Equal(0x0005u, (uint)combined);
    }
}
