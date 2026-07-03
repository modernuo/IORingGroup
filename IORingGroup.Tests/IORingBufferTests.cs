// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;
using System.Network;

namespace IORingGroup.Tests;

public class IORingBufferTests
{
    private const int Granularity = 65536; // valid on every platform (64 KB on Windows, page-multiple elsewhere)

    [Fact]
    public void Create_NonPowerOfTwo_Throws()
    {
        Assert.Throws<ArgumentException>(() => IORingBuffer.Create(1000));
    }

    [Fact]
    public void Create_NonPositive_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IORingBuffer.Create(0));
    }

    [SkippableFact]
    public void Create_BelowWindowsAllocationGranularity_ThrowsOnWindows()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        // 32 KB is a page-aligned power of 2 but below the 64 KB Windows allocation
        // granularity, so the second view could not be placed at offset physicalSize.
        // This must fail fast with ArgumentException, not fault inside the mapping call.
        Assert.Throws<ArgumentException>(() => IORingBuffer.Create(32768));
    }

    [Fact]
    public void Create_And_Dispose_ReportsSizes()
    {
        using var buffer = IORingBuffer.Create(Granularity);
        Assert.Equal(Granularity, buffer.PhysicalSize);
        Assert.Equal(Granularity * 2, buffer.VirtualSize);
        Assert.NotEqual(nint.Zero, buffer.Pointer);
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        var buffer = IORingBuffer.Create(Granularity);
        buffer.Dispose();
        buffer.Dispose();
    }

    [Fact]
    public void WrappingWrite_IsContiguous_ViaDoubleMapping()
    {
        using var buffer = IORingBuffer.Create(Granularity);
        AssertWrapRoundTrips(buffer);
    }

    [Fact]
    public void CommitWrite_WrapsTailWithMask()
    {
        using var buffer = IORingBuffer.Create(Granularity);
        // Move the write position to 16 bytes before the physical end.
        Advance(buffer, Granularity - 16);
        Assert.Equal(Granularity - 16, buffer.WriteOffset);

        // Writing 32 bytes crosses the physical boundary; the tail must wrap via the
        // power-of-2 mask to (N - 16 + 32) & (N - 1) == 16, matching the old modulo.
        buffer.GetWriteSpan()[..32].Clear();
        buffer.CommitWrite(32);
        Assert.Equal(16, buffer.WriteOffset);
    }

    [SkippableFact]
    public void LegacyWindowsPath_RoundTripsThroughDoubleMapping()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // Force the pre-1803 MapViewOfFileEx reserve/free/map fallback (the Server 2012/2016
        // path) even on a modern Windows box, and prove it produces a working double mapping.
        IORingBuffer.ForceLegacyWindowsPath = true;
        try
        {
            using var buffer = IORingBuffer.Create(Granularity);
            AssertWrapRoundTrips(buffer);
        }
        finally
        {
            IORingBuffer.ForceLegacyWindowsPath = null;
        }
    }

    // Advances head and tail together by count, leaving the buffer empty with the
    // window positioned at offset `count`.
    private static void Advance(IORingBuffer buffer, int count)
    {
        buffer.CommitWrite(count);
        buffer.CommitRead(count);
    }

    // Writes a byte pattern across the physical wrap boundary and reads it back through the
    // (contiguous) read span. Only succeeds if both halves alias the same physical pages.
    private static void AssertWrapRoundTrips(IORingBuffer buffer)
    {
        var size = buffer.PhysicalSize;
        Advance(buffer, size - 16);

        var write = buffer.GetWriteSpan();
        Span<byte> pattern = stackalloc byte[32];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i + 1);
        }

        pattern.CopyTo(write);
        buffer.CommitWrite(32);

        var read = buffer.GetReadSpan();
        Assert.Equal(32, read.Length);
        Assert.True(read[..32].SequenceEqual(pattern), "wrapped bytes did not round-trip contiguously");

        buffer.CommitRead(32);
        Assert.Equal(0, buffer.ReadableBytes);
    }
}
