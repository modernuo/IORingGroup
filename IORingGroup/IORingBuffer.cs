// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Network;

/// <summary>
/// A double-mapped circular buffer for zero-copy I/O operations.
/// The same physical memory is mapped twice consecutively in virtual address space,
/// allowing reads/writes that wrap around to appear contiguous.
/// </summary>
/// <remarks>
/// This buffer is designed for use with RIO (Windows) and io_uring (Linux) registered buffers.
/// The double-mapping eliminates the need for wrap-around handling in hot paths.
/// </remarks>
public sealed partial class IORingBuffer : IDisposable
{
    private nint _handle;
    private nint _buffer;
    private readonly int _physicalSize;
    private int _head;  // Read position (0 to physicalSize-1)
    private int _tail;  // Write position (0 to physicalSize-1)
    private bool _disposed;

    /// <summary>
    /// Gets the base pointer of the buffer.
    /// The full virtual range is 2x PhysicalSize (double-mapped).
    /// </summary>
    public nint Pointer => _buffer;

    /// <summary>
    /// Gets the physical size of the buffer in bytes.
    /// </summary>
    public int PhysicalSize => _physicalSize;

    /// <summary>
    /// Gets the virtual size of the buffer (2x physical for double-mapping).
    /// </summary>
    public int VirtualSize => _physicalSize * 2;

    /// <summary>
    /// Gets or sets the registered buffer ID (RIO BufferId or io_uring buffer index).
    /// Set by the pool/ring when the buffer is registered.
    /// </summary>
    public int BufferId { get; internal set; } = -1;

    /// <summary>
    /// Indicates whether this buffer belongs to a pool (vs fallback allocation).
    /// </summary>
    internal bool IsPooled { get; init; }

    /// <summary>
    /// Index within the pool's buffer array (-1 for fallback buffers).
    /// </summary>
    internal int PoolIndex { get; init; }

    /// <summary>
    /// Gets the number of bytes available for reading.
    /// </summary>
    public int ReadableBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var head = _head;
            var tail = _tail;
            return tail >= head ? tail - head : _physicalSize - head + tail;
        }
    }

    /// <summary>
    /// Gets the number of bytes available for writing.
    /// </summary>
    public int WritableBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _physicalSize - ReadableBytes - 1; // -1 to distinguish full from empty
    }

    /// <summary>
    /// Gets the current read offset (head position) in the buffer.
    /// Use this when preparing RIO/io_uring operations.
    /// </summary>
    public int ReadOffset
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _head;
    }

    /// <summary>
    /// Gets the current write offset (tail position) in the buffer.
    /// Use this when preparing RIO/io_uring operations.
    /// </summary>
    public int WriteOffset
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _tail;
    }

    /// <summary>
    /// Creates a new double-mapped circular buffer.
    /// </summary>
    /// <param name="physicalSize">Physical size in bytes (must be power of 2 and page-aligned).</param>
    /// <returns>A new IORingBuffer instance.</returns>
    public static IORingBuffer Create(int physicalSize)
    {
        return Create(physicalSize, isPooled: false, poolIndex: -1);
    }

    /// <summary>
    /// Creates a new double-mapped circular buffer with pool tracking.
    /// </summary>
    internal static IORingBuffer Create(int physicalSize, bool isPooled, int poolIndex)
    {
        if (physicalSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalSize), "Size must be positive");
        }

        // Verify power of 2
        if ((physicalSize & (physicalSize - 1)) != 0)
        {
            throw new ArgumentException("Size must be a power of 2", nameof(physicalSize));
        }

        var pageSize = Environment.SystemPageSize;
        if (physicalSize % pageSize != 0)
        {
            throw new ArgumentException($"Size must be a multiple of page size ({pageSize})", nameof(physicalSize));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindows(physicalSize, isPooled, poolIndex);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return CreateLinux(physicalSize, isPooled, poolIndex);
        }

        throw new PlatformNotSupportedException("IORingBuffer requires Windows or Linux");
    }

    private IORingBuffer(nint handle, nint buffer, int physicalSize, bool isPooled, int poolIndex)
    {
        _handle = handle;
        _buffer = buffer;
        _physicalSize = physicalSize;
        _head = 0;
        _tail = 0;
        IsPooled = isPooled;
        PoolIndex = poolIndex;
    }

    /// <summary>
    /// Gets a contiguous span for writing.
    /// Due to double-mapping, the span is always contiguous regardless of wrap-around.
    /// Use <see cref="WriteOffset"/> when preparing RIO/io_uring operations.
    /// </summary>
    /// <returns>A span representing the writable region.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe Span<byte> GetWriteSpan()
    {
        return new Span<byte>((byte*)_buffer + _tail, WritableBytes);
    }

    /// <summary>
    /// Advances the write position after writing data.
    /// </summary>
    /// <param name="count">Number of bytes written.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitWrite(int count)
    {
        if (count < 0 || count > WritableBytes)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(count));
        }

        _tail = (_tail + count) % _physicalSize;
    }

    /// <summary>
    /// Gets a contiguous span for reading.
    /// Due to double-mapping, the span is always contiguous regardless of wrap-around.
    /// Use <see cref="ReadOffset"/> when preparing RIO/io_uring operations.
    /// </summary>
    /// <returns>A span representing the readable region.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe Span<byte> GetReadSpan()
    {
        return new Span<byte>((byte*)_buffer + _head, ReadableBytes);
    }

    /// <summary>
    /// Advances the read position after consuming data.
    /// </summary>
    /// <param name="count">Number of bytes consumed.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CommitRead(int count)
    {
        if (count < 0 || count > ReadableBytes)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(count));
        }

        _head = (_head + count) % _physicalSize;

        // NOTE: We intentionally do NOT reset head/tail to 0 when empty.
        // With zero-copy I/O, a recv may already be posted at the current _tail offset.
        // If we reset to 0, the kernel will write data at the old offset, but we'll
        // read from offset 0 (which contains stale data).
        // The double-mapping handles wrap-around, so no reset is needed.
    }

    /// <summary>
    /// Resets the buffer to empty state.
    /// </summary>
    public void Reset()
    {
        _head = 0;
        _tail = 0;
    }

    #region Windows Implementation

    private static IORingBuffer CreateWindows(int physicalSize, bool isPooled, int poolIndex)
    {
        // Reserve a region of virtual memory (2x size for double-mapping)
        var region = WindowsNative.VirtualAlloc2(
            nint.Zero,
            nint.Zero,
            (ulong)physicalSize * 2,
            WindowsNative.MEM_RESERVE | WindowsNative.MEM_RESERVE_PLACEHOLDER,
            WindowsNative.PAGE_NOACCESS,
            nint.Zero,
            0
        );

        if (region == nint.Zero)
        {
            throw new InvalidOperationException($"VirtualAlloc2 failed: {Marshal.GetLastPInvokeError()}");
        }

        // Split the placeholder - release the first half to create two separate placeholders
        var freed = WindowsNative.VirtualFree(
            region,
            (uint)physicalSize,
            WindowsNative.MEM_RELEASE | WindowsNative.MEM_PRESERVE_PLACEHOLDER
        );

        if (!freed)
        {
            WindowsNative.VirtualFree(region, 0, WindowsNative.MEM_RELEASE);
            throw new InvalidOperationException($"VirtualFree placeholder failed: {Marshal.GetLastPInvokeError()}");
        }

        // Create a file mapping object backed by the paging file
        var handle = WindowsNative.CreateFileMappingW(
            WindowsNative.InvalidHandleValue,
            nint.Zero,
            WindowsNative.PAGE_READWRITE,
            0,
            (uint)physicalSize,
            null
        );

        if (handle == nint.Zero)
        {
            WindowsNative.VirtualFree(region, 0, WindowsNative.MEM_RELEASE);
            WindowsNative.VirtualFree(region + physicalSize, 0, WindowsNative.MEM_RELEASE);
            throw new InvalidOperationException($"CreateFileMapping failed: {Marshal.GetLastPInvokeError()}");
        }

        // Map the first view
        var buffer = WindowsNative.MapViewOfFile3(
            handle,
            nint.Zero,
            region,
            0,
            (ulong)physicalSize,
            WindowsNative.MEM_REPLACE_PLACEHOLDER,
            WindowsNative.PAGE_READWRITE,
            nint.Zero,
            0
        );

        if (buffer == nint.Zero)
        {
            WindowsNative.CloseHandle(handle);
            WindowsNative.VirtualFree(region, 0, WindowsNative.MEM_RELEASE);
            WindowsNative.VirtualFree(region + physicalSize, 0, WindowsNative.MEM_RELEASE);
            throw new InvalidOperationException($"MapViewOfFile3 (first) failed: {Marshal.GetLastPInvokeError()}");
        }

        // Map the second view (same physical memory, adjacent virtual address)
        var view2 = WindowsNative.MapViewOfFile3(
            handle,
            nint.Zero,
            buffer + physicalSize,
            0,
            (ulong)physicalSize,
            WindowsNative.MEM_REPLACE_PLACEHOLDER,
            WindowsNative.PAGE_READWRITE,
            nint.Zero,
            0
        );

        if (view2 == nint.Zero)
        {
            WindowsNative.UnmapViewOfFile(buffer);
            WindowsNative.CloseHandle(handle);
            throw new InvalidOperationException($"MapViewOfFile3 (second) failed: {Marshal.GetLastPInvokeError()}");
        }

        return new IORingBuffer(handle, buffer, physicalSize, isPooled, poolIndex);
    }

    private static partial class WindowsNative
    {
        public const nint InvalidHandleValue = -1;
        public const uint MEM_PRESERVE_PLACEHOLDER = 0x02;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint MEM_RESERVE_PLACEHOLDER = 0x40000;
        public const uint PAGE_NOACCESS = 0x01;
        public const uint PAGE_READWRITE = 0x04;

        [LibraryImport("kernelbase.dll", SetLastError = true)]
        public static partial nint VirtualAlloc2(
            nint process, nint address, ulong size, uint allocationType, uint protect,
            nint extendedParameters, uint parameterCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualFree(nint lpAddress, uint dwSize, uint dwFreeType);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint CreateFileMappingW(
            nint hFile, nint lpFileMappingAttributes, uint flProtect,
            uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string? lpName);

        [LibraryImport("kernelbase.dll", SetLastError = true)]
        public static partial nint MapViewOfFile3(
            nint hFileMappingObject, nint processHandle, nint pvBaseAddress,
            ulong ullOffset, ulong ullSize, uint allocFlags, uint dwDesiredAccess,
            nint hExtendedParameter, int parameterCount);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnmapViewOfFile(nint lpBaseAddress);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint hObject);
    }

    #endregion

    #region Linux Implementation

    private static IORingBuffer CreateLinux(int physicalSize, bool isPooled, int poolIndex)
    {
        // Create anonymous file descriptor
        var fd = LinuxNative.memfd_create("ioring_buffer", LinuxNative.MFD_CLOEXEC);
        if (fd < 0)
        {
            throw new InvalidOperationException($"memfd_create failed: {Marshal.GetLastPInvokeError()}");
        }

        // Set the file size
        if (LinuxNative.ftruncate(fd, physicalSize) < 0)
        {
            LinuxNative.close(fd);
            throw new InvalidOperationException($"ftruncate failed: {Marshal.GetLastPInvokeError()}");
        }

        // Reserve virtual address space for both mappings
        var region = LinuxNative.mmap(
            nint.Zero,
            (nuint)(physicalSize * 2),
            LinuxNative.PROT_NONE,
            LinuxNative.MAP_PRIVATE | LinuxNative.MAP_ANONYMOUS,
            -1,
            0
        );

        if (region == LinuxNative.MAP_FAILED)
        {
            LinuxNative.close(fd);
            throw new InvalidOperationException($"mmap (reserve) failed: {Marshal.GetLastPInvokeError()}");
        }

        // Map first view
        var buffer = LinuxNative.mmap(
            region,
            (nuint)physicalSize,
            LinuxNative.PROT_READ | LinuxNative.PROT_WRITE,
            LinuxNative.MAP_SHARED | LinuxNative.MAP_FIXED,
            fd,
            0
        );

        if (buffer == LinuxNative.MAP_FAILED)
        {
            LinuxNative.munmap(region, (nuint)(physicalSize * 2));
            LinuxNative.close(fd);
            throw new InvalidOperationException($"mmap (first) failed: {Marshal.GetLastPInvokeError()}");
        }

        // Map second view (same fd, same offset = same physical memory)
        var view2 = LinuxNative.mmap(
            region + physicalSize,
            (nuint)physicalSize,
            LinuxNative.PROT_READ | LinuxNative.PROT_WRITE,
            LinuxNative.MAP_SHARED | LinuxNative.MAP_FIXED,
            fd,
            0
        );

        if (view2 == LinuxNative.MAP_FAILED)
        {
            LinuxNative.munmap(region, (nuint)(physicalSize * 2));
            LinuxNative.close(fd);
            throw new InvalidOperationException($"mmap (second) failed: {Marshal.GetLastPInvokeError()}");
        }

        return new IORingBuffer(fd, buffer, physicalSize, isPooled, poolIndex);
    }

    private static partial class LinuxNative
    {
        public static readonly nint MAP_FAILED = -1;

        public const int MFD_CLOEXEC = 0x0001;
        public const int PROT_NONE = 0x0;
        public const int PROT_READ = 0x1;
        public const int PROT_WRITE = 0x2;
        public const int MAP_SHARED = 0x01;
        public const int MAP_PRIVATE = 0x02;
        public const int MAP_FIXED = 0x10;
        public const int MAP_ANONYMOUS = 0x20;

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int memfd_create(string name, uint flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int ftruncate(int fd, long length);

        [LibraryImport("libc", SetLastError = true)]
        public static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int munmap(nint addr, nuint length);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int close(int fd);
    }

    #endregion

    #region Disposal

    private void ReleaseUnmanagedResources()
    {
        if (_disposed || _buffer == nint.Zero)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (_handle != nint.Zero)
            {
                WindowsNative.CloseHandle(_handle);
                _handle = nint.Zero;
            }

            if (_buffer != nint.Zero)
            {
                WindowsNative.UnmapViewOfFile(_buffer);
                WindowsNative.UnmapViewOfFile(_buffer + _physicalSize);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (_buffer != nint.Zero)
            {
                LinuxNative.munmap(_buffer, (nuint)(_physicalSize * 2));
            }

            if (_handle != nint.Zero)
            {
                LinuxNative.close((int)_handle);
                _handle = nint.Zero;
            }
        }

        _buffer = nint.Zero;
        _disposed = true;
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~IORingBuffer()
    {
        ReleaseUnmanagedResources();
    }

    #endregion
}

internal static class ThrowHelper
{
    public static void ThrowArgumentOutOfRange(string paramName)
    {
        throw new ArgumentOutOfRangeException(paramName);
    }
}
