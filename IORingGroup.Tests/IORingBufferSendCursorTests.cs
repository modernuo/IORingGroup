// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Network;

namespace IORingGroup.Tests;

/// <summary>
/// Send cursor behaviour: write space is released by CommitRead (completion), never by CommitSend
/// (post). Zero-copy I/O may re-read posted bytes to retransmit, so releasing them early corrupts
/// live connections.
/// </summary>
public class IORingBufferSendCursorTests
{
    private const int BufferSize = 65536; // valid on every platform

    [Fact]
    public void CommitSendDoesNotReleaseWriteSpace()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        buffer.CommitWrite(1000);
        var writableBeforeSend = buffer.WritableBytes;

        buffer.CommitSend(1000);

        Assert.Equal(writableBeforeSend, buffer.WritableBytes);
        Assert.Equal(1000, buffer.InFlightBytes);
        Assert.Equal(0, buffer.SendableBytes);
        Assert.Equal(1000, buffer.ReadableBytes);
    }

    [Fact]
    public void CommitReadReleasesWriteSpace()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        buffer.CommitWrite(1000);
        buffer.CommitSend(1000);
        var writableWhileInFlight = buffer.WritableBytes;

        buffer.CommitRead(1000);

        Assert.Equal(writableWhileInFlight + 1000, buffer.WritableBytes);
        Assert.Equal(0, buffer.InFlightBytes);
        Assert.Equal(0, buffer.ReadableBytes);
    }

    [Fact]
    public void SupportsMultipleSendsInFlight()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        buffer.CommitWrite(500);
        var firstOffset = buffer.SendOffset;
        buffer.CommitSend(500);

        buffer.CommitWrite(300);
        Assert.Equal(300, buffer.SendableBytes);
        var secondOffset = buffer.SendOffset;
        buffer.CommitSend(300);

        // The second send starts where the first ended - no overlap, no gap.
        Assert.Equal(firstOffset + 500, secondOffset);
        Assert.Equal(800, buffer.InFlightBytes);
        Assert.Equal(0, buffer.SendableBytes);

        buffer.CommitRead(500);
        Assert.Equal(300, buffer.InFlightBytes);

        buffer.CommitRead(300);
        Assert.Equal(0, buffer.InFlightBytes);
        Assert.Equal(0, buffer.ReadableBytes);
    }

    [Fact]
    public void RecvPathCommitReadKeepsSendCursorWithHead()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        // Recv buffers never call CommitSend; the cursor must follow the head or SendableBytes
        // would wrap negative.
        buffer.CommitWrite(1000);
        buffer.CommitRead(400);

        Assert.Equal(0, buffer.InFlightBytes);
        Assert.Equal(600, buffer.ReadableBytes);
        Assert.Equal(600, buffer.SendableBytes);

        buffer.CommitRead(600);

        Assert.Equal(0, buffer.InFlightBytes);
        Assert.Equal(0, buffer.SendableBytes);
    }

    [Fact]
    public void SendCursorWrapsCorrectly()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        var chunk = BufferSize / 4;
        for (var i = 0; i < 3; i++)
        {
            buffer.CommitWrite(chunk);
            buffer.CommitSend(chunk);
            buffer.CommitRead(chunk);
        }

        buffer.CommitWrite(chunk);
        buffer.CommitSend(chunk);

        Assert.Equal(chunk, buffer.InFlightBytes);

        buffer.CommitRead(chunk);

        Assert.Equal(0, buffer.InFlightBytes);
        Assert.Equal(0, buffer.ReadableBytes);
        Assert.Equal(BufferSize - 1, buffer.WritableBytes);
    }

    [Fact]
    public void ShortSendMakesTheRemainderSendableAgain()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        buffer.CommitWrite(1000);
        buffer.CommitSend(1000);

        // send() semantics (epoll, io_uring, kqueue) routinely accept less than offered.
        buffer.CommitShortSend(600);

        Assert.Equal(0, buffer.InFlightBytes);
        Assert.Equal(400, buffer.SendableBytes);
        Assert.Equal(400, buffer.ReadableBytes);

        buffer.CommitSend(400);
        Assert.Equal(400, buffer.InFlightBytes);
        Assert.Equal(0, buffer.SendableBytes);

        buffer.CommitRead(400);
        Assert.Equal(0, buffer.ReadableBytes);
    }

    [Fact]
    public void CommitSendRejectsMoreThanQueued()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        buffer.CommitWrite(100);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.CommitSend(101));
    }

    [Fact]
    public void CommitShortSendRejectsMoreThanInFlight()
    {
        using var buffer = IORingBuffer.Create(BufferSize);

        buffer.CommitWrite(100);
        buffer.CommitSend(100);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.CommitShortSend(101));
    }
}
