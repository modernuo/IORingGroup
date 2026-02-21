// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

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
}
