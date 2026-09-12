// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2026, ModernUO

using System.Network;

namespace IORingGroup.Tests;

public class IORingBufferPoolRetentionTests : IDisposable
{
    private readonly IIORingGroup _ring = System.Network.IORingGroup.Create(queueSize: 64, maxConnections: 64);

    public void Dispose() => _ring.Dispose();

    private IORingBufferPool CreatePool(int slabSize = 4, int maxSlabs = 4, int retentionWindows = 3) =>
        new(_ring, slabSize: slabSize, bufferSize: 65536, initialSlabs: 0, maxSlabs: maxSlabs, retentionWindows: retentionWindows);

    [Fact]
    public void InUse_TracksAcquireAndRelease()
    {
        using var pool = CreatePool();

        Assert.True(pool.TryAcquire(out var a));
        Assert.True(pool.TryAcquire(out var b));
        Assert.Equal(2, pool.InUse);
        Assert.Equal(2, pool.PeakInUse);

        pool.Release(a!);
        Assert.Equal(1, pool.InUse);
        Assert.Equal(2, pool.PeakInUse);

        pool.Release(b!);
    }

    [Fact]
    public void Maintain_FloorIsMaxOfRecentWindows()
    {
        using var pool = CreatePool(retentionWindows: 3);

        Assert.True(pool.TryAcquire(out var a));
        Assert.True(pool.TryAcquire(out var b));
        Assert.True(pool.TryAcquire(out var c));
        pool.Release(a!);
        pool.Release(b!);
        pool.Release(c!);

        pool.Maintain(); // window 1: peak 3
        Assert.Equal(3, pool.RetainFloor);

        pool.Maintain(); // window 2: peak 0
        pool.Maintain(); // window 3: peak 0
        Assert.Equal(3, pool.RetainFloor);

        pool.Maintain(); // window 1 overwritten: the 3 ages out
        Assert.Equal(0, pool.RetainFloor);
    }

    [Fact]
    public void Maintain_ReleasesOneFreeTopSlabPerCallAboveTheFloor()
    {
        using var pool = CreatePool(slabSize: 2, maxSlabs: 4, retentionWindows: 1);
        var held = new IORingBuffer[5];
        for (var i = 0; i < held.Length; i++)
        {
            Assert.True(pool.TryAcquire(out var buffer));
            held[i] = buffer!;
        }

        Assert.Equal(3, pool.CurrentSlabs);

        for (var i = 0; i < held.Length; i++)
        {
            pool.Release(held[i]);
        }

        Assert.Equal(0, pool.Maintain()); // window records peak 5: floor 5, nothing trimmed
        Assert.Equal(3, pool.CurrentSlabs);

        Assert.Equal(2, pool.Maintain()); // peak 0: floor 0, one slab trimmed
        Assert.Equal(2, pool.CurrentSlabs);

        Assert.Equal(2, pool.Maintain());
        Assert.Equal(1, pool.CurrentSlabs);
    }

    [Fact]
    public void Maintain_NeverTrimsBelowTheFloor()
    {
        using var pool = CreatePool(slabSize: 2, maxSlabs: 4, retentionWindows: 2);
        Assert.True(pool.TryAcquire(out var a));
        Assert.True(pool.TryAcquire(out var b));
        Assert.True(pool.TryAcquire(out var c)); // second slab
        pool.Release(c!);

        pool.Maintain(); // peak 3: floor 3, capacity 4 - 2 < 3, keep
        Assert.Equal(2, pool.CurrentSlabs);

        pool.Release(a!);
        pool.Release(b!);
    }

    [Fact]
    public void Maintain_DoesNotTrimASlabStillInUse()
    {
        using var pool = CreatePool(slabSize: 2, maxSlabs: 4, retentionWindows: 1);
        Assert.True(pool.TryAcquire(out var a));
        Assert.True(pool.TryAcquire(out var b));
        Assert.True(pool.TryAcquire(out var c)); // lives in slab 1
        pool.Release(a!);
        pool.Release(b!);

        pool.Maintain(); // floor 3
        pool.Maintain(); // floor 1, top slab has c in use: keep
        Assert.Equal(2, pool.CurrentSlabs);

        pool.Release(c!);
    }

    [Fact]
    public void HasFreeBuffer_ReflectsSlabState()
    {
        using var pool = CreatePool(slabSize: 1, maxSlabs: 1);
        Assert.False(pool.HasFreeBuffer);
        Assert.True(pool.TryAcquire(out var a));
        Assert.False(pool.HasFreeBuffer);
        pool.Release(a!);
        Assert.True(pool.HasFreeBuffer);
    }
}
