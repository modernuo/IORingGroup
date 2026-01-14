// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// Adapted from ModernUO's PipeTests.cs

using System;
using System.Runtime.InteropServices;
using TestServer;
using Xunit;

namespace IORingGroup.Tests;

/// <summary>
/// Tests for the double-mapped circular buffer Pipe implementation.
/// </summary>
public class PipeTests
{
    [SkippableFact]
    public void Create_WithSmallSize_RoundsUpToPageSize()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        var pageSize = (uint)Environment.SystemPageSize;
        using var pipe = new Pipe(128);

        // Size should be rounded up to page size
        Assert.Equal(pageSize, pipe.Size);
        // Available to write is Size - 1 (sentinel byte)
        Assert.Equal((int)(pageSize - 1), pipe.Writer.AvailableToWrite().Length);
    }

    [SkippableFact]
    public void Create_WithExactPageSize_HasCorrectSize()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        var pageSize = (uint)Environment.SystemPageSize;
        using var pipe = new Pipe(pageSize);

        Assert.Equal(pageSize, pipe.Size);
    }

    [SkippableFact]
    public void GetBufferPointer_ReturnsNonZero()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        Assert.NotEqual(nint.Zero, pipe.GetBufferPointer());
    }

    [SkippableFact]
    public void GetVirtualSize_IsTwicePhysicalSize()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        var pageSize = (uint)Environment.SystemPageSize;
        using var pipe = new Pipe(pageSize);

        // Virtual size is 2x physical size (double-mapped)
        Assert.Equal(pageSize * 2, pipe.GetVirtualSize());
    }

    [SkippableFact]
    public void Writer_AvailableToWrite_InitiallyFull()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        var pageSize = (uint)Environment.SystemPageSize;
        using var pipe = new Pipe(pageSize);

        // Should have Size - 1 bytes available (sentinel byte reserved)
        Assert.Equal((int)(pageSize - 1), pipe.Writer.AvailableToWrite().Length);
    }

    [SkippableFact]
    public void Reader_AvailableToRead_InitiallyEmpty()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        Assert.Equal(0, pipe.Reader.AvailableToRead().Length);
    }

    [SkippableFact]
    public void WriteAndRead_SimpleData_MatchesExpected()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        // Write some test data
        var testData = "Hello, Pipe!"u8.ToArray();
        var writeSpan = pipe.Writer.AvailableToWrite();
        testData.CopyTo(writeSpan);
        pipe.Writer.Advance((uint)testData.Length);

        // Read it back
        var readSpan = pipe.Reader.AvailableToRead();
        Assert.Equal(testData.Length, readSpan.Length);
        Assert.True(readSpan.SequenceEqual(testData));

        // Advance reader
        pipe.Reader.Advance((uint)testData.Length);
        Assert.Equal(0, pipe.Reader.AvailableToRead().Length);
    }

    [SkippableFact]
    public void WriteAndRead_MultipleChunks_Works()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        for (int i = 0; i < 10; i++)
        {
            var data = new byte[100];
            Array.Fill(data, (byte)i);

            var writeSpan = pipe.Writer.AvailableToWrite();
            data.CopyTo(writeSpan);
            pipe.Writer.Advance(100);

            var readSpan = pipe.Reader.AvailableToRead();
            Assert.Equal(100, readSpan.Length);
            Assert.True(readSpan.SequenceEqual(data));

            pipe.Reader.Advance(100);
        }
    }

    [SkippableFact]
    public void WriteAndRead_WrapAround_MaintainsDataIntegrity()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        var pageSize = (uint)Environment.SystemPageSize;
        using var pipe = new Pipe(pageSize);

        // Fill most of the buffer
        var fillSize = (int)(pageSize - 100);
        var fillData = new byte[fillSize];
        for (int i = 0; i < fillSize; i++)
            fillData[i] = (byte)(i % 256);

        var writeSpan = pipe.Writer.AvailableToWrite();
        fillData.CopyTo(writeSpan);
        pipe.Writer.Advance((uint)fillSize);

        // Read half
        var halfSize = fillSize / 2;
        var readSpan = pipe.Reader.AvailableToRead();
        Assert.Equal(fillSize, readSpan.Length);
        pipe.Reader.Advance((uint)halfSize);

        // Now write more data that wraps around
        var wrapData = new byte[200];
        Array.Fill(wrapData, (byte)0xAB);

        writeSpan = pipe.Writer.AvailableToWrite();
        Assert.True(writeSpan.Length >= 200, $"Expected >= 200 bytes, got {writeSpan.Length}");
        wrapData.CopyTo(writeSpan);
        pipe.Writer.Advance(200);

        // Read remaining original data
        readSpan = pipe.Reader.AvailableToRead();
        Assert.Equal(fillSize - halfSize + 200, readSpan.Length);

        // The magic of double-mapping: even wrapped data appears contiguous!
        // First part should be the remaining fill data
        for (int i = 0; i < fillSize - halfSize; i++)
        {
            Assert.Equal((byte)((i + halfSize) % 256), readSpan[i]);
        }

        // Second part should be the wrap data
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal((byte)0xAB, readSpan[fillSize - halfSize + i]);
        }
    }

    [SkippableFact]
    public void Reader_GetReadOffset_TracksPosition()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        Assert.Equal(0, pipe.Reader.GetReadOffset());

        // Write and advance
        pipe.Writer.Advance(100);
        Assert.Equal(0, pipe.Reader.GetReadOffset()); // Read offset unchanged

        pipe.Reader.Advance(50);
        Assert.Equal(50, pipe.Reader.GetReadOffset());

        pipe.Reader.Advance(50);
        // When reader catches up to writer, both reset to 0
        Assert.Equal(0, pipe.Reader.GetReadOffset());
    }

    [SkippableFact]
    public void Writer_Advance_BeyondAvailable_ThrowsEndOfPipeException()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        var available = pipe.Writer.AvailableToWrite().Length;
        Assert.Throws<EndOfPipeException>(() => pipe.Writer.Advance((uint)(available + 1)));
    }

    [SkippableFact]
    public void Reader_Advance_BeyondAvailable_ThrowsEndOfPipeException()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        pipe.Writer.Advance(100);
        Assert.Throws<EndOfPipeException>(() => pipe.Reader.Advance(101));
    }

    [SkippableFact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        var pipe = new Pipe(4096);
        pipe.Dispose();
        pipe.Dispose(); // Should not throw
    }

    [SkippableFact]
    public void Close_SetsClosedFlag()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        using var pipe = new Pipe(4096);

        Assert.False(pipe.Closed);
        Assert.False(pipe.Writer.IsClosed);
        Assert.False(pipe.Reader.IsClosed);

        pipe.Writer.Close();

        Assert.True(pipe.Closed);
        Assert.True(pipe.Writer.IsClosed);
        Assert.True(pipe.Reader.IsClosed);
    }

    [SkippableFact]
    public void DoubleMappedMemory_WritesAreVisible_InBothMappings()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Pipe uses Windows VirtualAlloc2");

        var pageSize = (uint)Environment.SystemPageSize;
        using var pipe = new Pipe(pageSize);

        // Write at the beginning
        var writeSpan = pipe.Writer.AvailableToWrite();
        writeSpan[0] = 0xDE;
        writeSpan[1] = 0xAD;
        writeSpan[2] = 0xBE;
        writeSpan[3] = 0xEF;

        // The magic of double-mapping: the same physical memory appears at two virtual addresses
        // First mapping: buffer[0..pageSize)
        // Second mapping: buffer[pageSize..2*pageSize) maps to same physical memory
        unsafe
        {
            var ptr = (byte*)pipe.GetBufferPointer();

            // First mapping
            Assert.Equal(0xDE, ptr[0]);
            Assert.Equal(0xAD, ptr[1]);
            Assert.Equal(0xBE, ptr[2]);
            Assert.Equal(0xEF, ptr[3]);

            // Second mapping (same physical memory)
            Assert.Equal(0xDE, ptr[pageSize + 0]);
            Assert.Equal(0xAD, ptr[pageSize + 1]);
            Assert.Equal(0xBE, ptr[pageSize + 2]);
            Assert.Equal(0xEF, ptr[pageSize + 3]);

            // Modify through second mapping
            ptr[pageSize + 0] = 0xCA;
            ptr[pageSize + 1] = 0xFE;

            // Verify change is visible through first mapping
            Assert.Equal(0xCA, ptr[0]);
            Assert.Equal(0xFE, ptr[1]);
        }
    }
}
