// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.CompilerServices;

namespace System.Network;

/// <summary>
/// A multi-slab pool of pre-allocated IORingBuffer instances for zero-allocation buffer management.
/// </summary>
/// <remarks>
/// <para>
/// Buffers are organized into slabs that are allocated on-demand. Each slab contains a fixed
/// number of buffers that are pre-registered with the ring when the slab is created.
/// </para>
/// <para>
/// Memory growth model:
/// - Start with <c>initialSlabs</c> worth of buffers
/// - Grow by adding slabs as demand increases
/// - Cap at <c>maxSlabs</c> to prevent OOM
/// - Fall back to individual allocations beyond max (with stats tracking)
/// </para>
/// <para>
/// Use <see cref="GetStats"/> to monitor pool health and detect when more slabs are needed.
/// </para>
/// </remarks>
public sealed class IORingBufferPool : IDisposable
{
    private readonly IIORingGroup _ring;
    private readonly List<PoolSlab> _slabs;
    private readonly int[] _windows;
    private int _firstNonFullSlab;
    private int _windowIndex;
    private int _registeredBuffers;
    private bool _disposed;

    // Fallback tracking
    private int _fallbackAllocations;
    private int _currentFallbackCount;
    private int _peakFallbackCount;

    /// <summary>
    /// Represents a slab of pre-allocated buffers.
    /// </summary>
    private sealed class PoolSlab
    {
        public readonly IORingBuffer[] Buffers;
        public readonly int[] FreeStack;
        public int FreeCount;

        public PoolSlab(int size)
        {
            Buffers = new IORingBuffer[size];
            FreeStack = new int[size];
            FreeCount = 0;
        }
    }

    /// <summary>
    /// Gets the number of buffers per slab.
    /// </summary>
    public int SlabSize { get; }

    /// <summary>
    /// Gets the physical size of each buffer in bytes.
    /// </summary>
    public int BufferSize { get; }

    /// <summary>
    /// Gets the maximum number of slabs allowed.
    /// </summary>
    public int MaxSlabs { get; }

    /// <summary>
    /// Gets the current number of allocated slabs.
    /// </summary>
    public int CurrentSlabs => _slabs.Count;

    /// <summary>
    /// Gets the total capacity (current slabs × slab size).
    /// </summary>
    public int TotalCapacity => CurrentSlabs * SlabSize;

    /// <summary>Buffers currently handed out.</summary>
    public int InUse { get; private set; }

    /// <summary>Peak of <see cref="InUse"/> since the last <see cref="Maintain"/>.</summary>
    public int PeakInUse { get; private set; }

    /// <summary>Highest peak across the last <see cref="RetentionWindows"/> maintenance windows.</summary>
    public int RetainFloor { get; private set; }

    /// <summary>Number of maintenance windows a peak stays in force.</summary>
    public int RetentionWindows { get; }

    /// <summary>
    /// Bytes one slab of this pool holds. Long because a large buffer size times a slab of them
    /// overflows an int well inside the sizes the manager's top tiers allow.
    /// </summary>
    public long SlabBytes => (long)SlabSize * BufferSize;

    public long CapacityBytes => (long)TotalCapacity * BufferSize;

    public bool HasFreeBuffer
    {
        get
        {
            for (var i = _firstNonFullSlab; i < _slabs.Count; i++)
            {
                if (_slabs[i].FreeCount > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Creates a buffer pool with on-demand slab allocation.
    /// </summary>
    /// <param name="ring">The ring to register buffers with.</param>
    /// <param name="slabSize">Number of buffers per slab (e.g., 64 or 256).</param>
    /// <param name="bufferSize">Physical size of each buffer (must be power of 2, page-aligned).</param>
    /// <param name="initialSlabs">Number of slabs to pre-allocate (default: 1).</param>
    /// <param name="maxSlabs">Maximum number of slabs allowed (default: 16).</param>
    /// <param name="retentionWindows">Number of maintenance windows a peak stays in force (default: 15).</param>
    /// <exception cref="ArgumentNullException">If ring is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If sizes are invalid.</exception>
    public IORingBufferPool(
        IIORingGroup ring,
        int slabSize,
        int bufferSize,
        int initialSlabs = 1,
        int maxSlabs = 16,
        int retentionWindows = 15)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));

        if (slabSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slabSize), "Slab size must be positive");
        }

        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "Buffer size must be positive");
        }

        if (initialSlabs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialSlabs), "Initial slabs cannot be negative");
        }

        if (maxSlabs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSlabs), "Max slabs must be at least 1");
        }

        if (initialSlabs > maxSlabs)
        {
            throw new ArgumentOutOfRangeException(nameof(initialSlabs), "Initial slabs cannot exceed max slabs");
        }

        if (retentionWindows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionWindows), "Retention windows must be at least 1");
        }

        SlabSize = slabSize;
        BufferSize = bufferSize;
        MaxSlabs = maxSlabs;
        RetentionWindows = retentionWindows;
        _windows = new int[retentionWindows];
        _slabs = new List<PoolSlab>(maxSlabs);
        _firstNonFullSlab = 0;

        // Pre-allocate initial slabs. A slab that fails part way through has already unwound itself;
        // the slabs before it are this constructor's to release, since nothing will ever see the
        // half-built pool to dispose it.
        for (var i = 0; i < initialSlabs; i++)
        {
            PoolSlab slab;
            try
            {
                slab = CreateSlab(i);
            }
            catch
            {
                for (var j = 0; j < _slabs.Count; j++)
                {
                    DisposeSlab(_slabs[j]);
                }

                _slabs.Clear();
                throw;
            }

            _slabs.Add(slab);
        }
    }

    /// <summary>
    /// Creates and initializes a new slab with all buffers registered.
    /// </summary>
    private PoolSlab CreateSlab(int slabId)
    {
        var slab = new PoolSlab(SlabSize);
        var basePoolIndex = slabId * SlabSize;

        for (var i = 0; i < SlabSize; i++)
        {
            var poolIndex = basePoolIndex + i;

            // Allocate and register inside one try. An unregistered buffer would be accepted here
            // and then fail every operation posted against it, which reads as a random disconnect,
            // so fail where the cause is visible - but unwind first, or the slab's mappings and the
            // registrations of the buffers before it leak. The mapping itself can fail the same way
            // part way through a growing pool (address space, a locked-memory rlimit), and the slab
            // is not published until it is whole, so nothing else would ever release them. Backends
            // signal a registration failure either way: RIO returns a negative id, the Unix
            // backends throw.
            IORingBuffer? buffer = null;
            int bufferId;
            try
            {
                buffer = IORingBuffer.Create(BufferSize, isPooled: true, poolIndex: poolIndex);
                bufferId = _ring.RegisterBuffer(buffer);
            }
            catch (Exception ex)
            {
                // The message reads the registration count, so build it before unwinding drops it.
                var message = buffer == null
                    ? AllocationFailureMessage(slabId, i)
                    : RegistrationFailureMessage(slabId, i);

                // Null only when Create itself threw, in which case there is nothing to dispose.
                buffer?.Dispose();
                UnwindSlab(slab, i);

                throw new InvalidOperationException(message, ex);
            }

            if (bufferId < 0)
            {
                var message = RegistrationFailureMessage(slabId, i);
                buffer.Dispose();
                UnwindSlab(slab, i);

                throw new InvalidOperationException(message);
            }

            buffer.BufferId = bufferId;
            _registeredBuffers++;

            slab.Buffers[i] = buffer;
            slab.FreeStack[i] = i;
        }

        slab.FreeCount = SlabSize;
        return slab;
    }

    /// <summary>
    /// Explains a failed mapping. The inner exception carries the real cause; all this adds is
    /// where in the pool it happened, which the allocation itself knows nothing about.
    /// </summary>
    private string AllocationFailureMessage(int slabId, int index) =>
        $"Buffer allocation failed for buffer {index} of slab {slabId} ({BufferSize} byte buffers): " +
        "the double mapping could not be created. Look at the buffer size, at available address " +
        "space, or at a locked-memory rlimit.";

    /// <summary>
    /// Explains a failed registration without guessing at its cause. The table size is offered as
    /// the remediation only when this pool has demonstrably filled it; otherwise the failure is a
    /// native one - a locked-memory rlimit, exhausted address space - or another pool sharing the
    /// ring, and telling the reader to resize the table would send them to the wrong knob. The
    /// count is this pool's own, so it can only prove the table full, never prove it is not.
    /// </summary>
    private string RegistrationFailureMessage(int slabId, int index)
    {
        var max = _ring.MaxRegisteredBuffers;
        var where = $"Buffer registration failed for buffer {index} of slab {slabId} ({BufferSize} byte buffers)";

        return max > 0 && _registeredBuffers >= max
            ? $"{where}: the ring's registration table is full ({max} entries). Size it with " +
              "RingSocketManager.RequiredRegisteredBuffers and pass the result to IORingGroup.Create(maxRegisteredBuffers:)."
            : $"{where}: the ring rejected the registration" +
              (max > 0 ? $", with {_registeredBuffers} of the ring's {max} table entries held by this pool" : "") +
              ". This pool has not filled the table on its own, so look at a native limit - a locked-memory " +
              "rlimit, exhausted address space - or at other pools registering against the same ring.";
    }

    /// <summary>
    /// Releases the buffers of a slab that failed part way through creation. The slab was never
    /// published, so nothing else will ever unregister them.
    /// </summary>
    private void UnwindSlab(PoolSlab slab, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var buffer = slab.Buffers[i];
            _ring.UnregisterBuffer(buffer.BufferId);
            _registeredBuffers--;
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Unregisters and disposes every buffer of a fully built slab. The one place that releases a
    /// slab, shared by the constructor's unwind, <see cref="Maintain"/>'s trim, and <see cref="Dispose"/>.
    /// </summary>
    private void DisposeSlab(PoolSlab slab)
    {
        for (var i = 0; i < slab.Buffers.Length; i++)
        {
            var buffer = slab.Buffers[i];
            if (buffer == null)
            {
                continue;
            }

            if (buffer.BufferId >= 0)
            {
                _ring.UnregisterBuffer(buffer.BufferId);
                _registeredBuffers--;
            }

            buffer.Dispose();
        }
    }

    /// <summary>
    /// Acquires a buffer from the pool.
    /// If all slabs are full and we haven't hit max, a new slab is allocated.
    /// If at max slabs, a fallback buffer is allocated dynamically.
    /// </summary>
    /// <returns>An IORingBuffer ready for use.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IORingBuffer Acquire()
    {
        // Start from first potentially non-full slab (optimization)
        for (var i = _firstNonFullSlab; i < _slabs.Count; i++)
        {
            var slab = _slabs[i];
            if (slab.FreeCount > 0)
            {
                _firstNonFullSlab = i;
                return AcquireFromSlab(slab);
            }
        }

        // At max slabs - create fallback buffer
        if (_slabs.Count >= MaxSlabs)
        {
            var buffer = CreateFallbackBuffer();
            InUse++;
            if (InUse > PeakInUse)
            {
                PeakInUse = InUse;
            }

            return buffer;
        }

        var newSlab = CreateSlab(_slabs.Count);
        _slabs.Add(newSlab);
        _firstNonFullSlab = _slabs.Count - 1;
        return AcquireFromSlab(newSlab);
    }

    /// <summary>
    /// Acquires a buffer from a specific slab.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IORingBuffer AcquireFromSlab(PoolSlab slab)
    {
        var slotIndex = slab.FreeStack[--slab.FreeCount];
        var buffer = slab.Buffers[slotIndex];
        buffer.Reset();
        InUse++;
        if (InUse > PeakInUse)
        {
            PeakInUse = InUse;
        }

        return buffer;
    }

    /// <summary>
    /// Tries to acquire a buffer from the pool without fallback allocation.
    /// </summary>
    /// <param name="buffer">The acquired buffer, or null if pool is exhausted.</param>
    /// <returns>True if a buffer was acquired, false if pool is at max capacity.</returns>
    public bool TryAcquire(out IORingBuffer? buffer)
    {
        // Try existing slabs
        for (var i = _firstNonFullSlab; i < _slabs.Count; i++)
        {
            var slab = _slabs[i];
            if (slab.FreeCount > 0)
            {
                _firstNonFullSlab = i;
                buffer = AcquireFromSlab(slab);
                return true;
            }
        }

        // Try to create new slab
        if (_slabs.Count < MaxSlabs)
        {
            var newSlab = CreateSlab(_slabs.Count);
            _slabs.Add(newSlab);
            _firstNonFullSlab = _slabs.Count - 1;
            buffer = AcquireFromSlab(newSlab);
            return true;
        }

        buffer = null;
        return false;
    }

    /// <summary>
    /// Releases a buffer back to the pool or disposes it if it's a fallback buffer.
    /// </summary>
    /// <param name="buffer">The buffer to release.</param>
    public void Release(IORingBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        InUse--;

        if (buffer.IsPooled)
        {
            // Decode slab and slot from PoolIndex
            var slabId = buffer.PoolIndex / SlabSize;
            var slotIndex = buffer.PoolIndex % SlabSize;

            if (slabId < _slabs.Count)
            {
                var slab = _slabs[slabId];
                slab.FreeStack[slab.FreeCount++] = slotIndex;

                // Update hint if releasing to earlier slab
                if (slabId < _firstNonFullSlab)
                {
                    _firstNonFullSlab = slabId;
                }
            }
        }
        else
        {
            // Fallback buffer - unregister and dispose
            if (buffer.BufferId >= 0)
            {
                _ring.UnregisterBuffer(buffer.BufferId);
                _registeredBuffers--;
            }

            buffer.Dispose();
            _currentFallbackCount--;
        }
    }

    /// <summary>
    /// Rotates the usage window, recomputes the retention floor, and returns at most one fully
    /// free top slab to the OS if the remaining capacity still covers the floor.
    /// </summary>
    /// <returns>Buffers released (0 or <see cref="SlabSize"/>).</returns>
    public int Maintain()
    {
        _windows[_windowIndex] = PeakInUse;
        _windowIndex = (_windowIndex + 1) % _windows.Length;
        PeakInUse = InUse;

        var floor = 0;
        for (var i = 0; i < _windows.Length; i++)
        {
            if (_windows[i] > floor)
            {
                floor = _windows[i];
            }
        }

        RetainFloor = floor;

        if (_slabs.Count == 0)
        {
            return 0;
        }

        var top = _slabs[^1];
        if (top.FreeCount < SlabSize || (_slabs.Count - 1) * SlabSize < floor)
        {
            return 0;
        }

        DisposeSlab(top);

        _slabs.RemoveAt(_slabs.Count - 1);
        if (_firstNonFullSlab > _slabs.Count)
        {
            _firstNonFullSlab = _slabs.Count;
        }

        return SlabSize;
    }

    /// <summary>
    /// Creates a fallback buffer when pool is at max capacity.
    /// </summary>
    private IORingBuffer CreateFallbackBuffer()
    {
        _fallbackAllocations++;
        _currentFallbackCount++;

        // Track peak fallback usage
        if (_currentFallbackCount > _peakFallbackCount)
        {
            _peakFallbackCount = _currentFallbackCount;
        }

        var buffer = IORingBuffer.Create(BufferSize, isPooled: false, poolIndex: -1);

        // Register with ring
        var bufferId = _ring.RegisterBuffer(buffer);
        buffer.BufferId = bufferId;
        if (bufferId >= 0)
        {
            _registeredBuffers++;
        }

        return buffer;
    }

    /// <summary>
    /// Gets statistics about pool usage.
    /// </summary>
    public SlabPoolStats GetStats()
    {
        var totalCapacity = _slabs.Count * SlabSize;
        var freeCount = 0;
        for (var i = 0; i < _slabs.Count; i++)
        {
            var slab = _slabs[i];
            freeCount += slab.FreeCount;
        }

        return new SlabPoolStats
        {
            SlabSize = SlabSize,
            BufferSize = BufferSize,
            CurrentSlabs = _slabs.Count,
            MaxSlabs = MaxSlabs,
            TotalCapacity = totalCapacity,
            InUseCount = totalCapacity - freeCount,
            FreeCount = freeCount,
            TotalFallbackAllocations = _fallbackAllocations,
            CurrentFallbackCount = _currentFallbackCount,
            PeakFallbackCount = _peakFallbackCount,
            CommittedBytes = (long)_slabs.Count * SlabSize * BufferSize * 2, // ×2 for double-mapping
        };
    }

    /// <summary>
    /// Resets the fallback statistics counters.
    /// </summary>
    public void ResetStats()
    {
        _fallbackAllocations = 0;
        _peakFallbackCount = _currentFallbackCount;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Unregister and dispose all pooled buffers in all slabs
        for (var i = 0; i < _slabs.Count; i++)
        {
            DisposeSlab(_slabs[i]);
        }

        _slabs.Clear();
    }
}

/// <summary>
/// Statistics about multi-slab buffer pool usage.
/// </summary>
public readonly struct SlabPoolStats
{
    /// <summary>
    /// Number of buffers per slab.
    /// </summary>
    public int SlabSize { get; init; }

    /// <summary>
    /// Physical size of each buffer in bytes.
    /// </summary>
    public int BufferSize { get; init; }

    /// <summary>
    /// Current number of allocated slabs.
    /// </summary>
    public int CurrentSlabs { get; init; }

    /// <summary>
    /// Maximum number of slabs allowed.
    /// </summary>
    public int MaxSlabs { get; init; }

    /// <summary>
    /// Total buffer capacity (CurrentSlabs × SlabSize).
    /// </summary>
    public int TotalCapacity { get; init; }

    /// <summary>
    /// Number of buffers currently in use.
    /// </summary>
    public int InUseCount { get; init; }

    /// <summary>
    /// Number of buffers available in pool.
    /// </summary>
    public int FreeCount { get; init; }

    /// <summary>
    /// Total number of fallback allocations that occurred (lifetime).
    /// A non-zero value indicates the pool was exhausted at some point.
    /// </summary>
    public int TotalFallbackAllocations { get; init; }

    /// <summary>
    /// Current number of fallback (non-pooled) buffers in use.
    /// </summary>
    public int CurrentFallbackCount { get; init; }

    /// <summary>
    /// Peak number of fallback buffers in use at any one time.
    /// </summary>
    public int PeakFallbackCount { get; init; }

    /// <summary>
    /// Total committed memory in bytes (including double-mapping overhead).
    /// </summary>
    public long CommittedBytes { get; init; }

    /// <summary>
    /// True if any fallback allocations have occurred.
    /// </summary>
    public bool HasFallbacks => TotalFallbackAllocations > 0;

    /// <summary>
    /// True if the pool can grow by adding more slabs.
    /// </summary>
    public bool CanGrow => CurrentSlabs < MaxSlabs;

    /// <summary>
    /// Pool utilization percentage (0-100).
    /// </summary>
    public double UtilizationPercent => TotalCapacity > 0
        ? (double)InUseCount / TotalCapacity * 100
        : 0;

    public override string ToString() =>
        $"Slabs: {CurrentSlabs}/{MaxSlabs}, " +
        $"Buffers: {InUseCount}/{TotalCapacity} ({UtilizationPercent:F1}%), " +
        $"Fallbacks: {CurrentFallbackCount} current, {TotalFallbackAllocations} total" +
        (CanGrow ? " [can grow]" : " [at max]");
}
