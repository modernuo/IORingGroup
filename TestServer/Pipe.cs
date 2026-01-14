// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// Adapted from ModernUO's Pipe.cs for IORingGroup testing

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TestServer;

/// <summary>
/// A double-mapped circular buffer using VirtualAlloc2.
/// The same physical memory is mapped twice in virtual address space,
/// allowing reads/writes to wrap around without special handling.
/// </summary>
public partial class Pipe : IDisposable
{
    public class PipeWriter
    {
        private readonly Pipe _pipe;

        internal PipeWriter(Pipe pipe) => _pipe = pipe;

        public unsafe Span<byte> AvailableToWrite()
        {
            var read = _pipe._readIdx;
            var write = _pipe._writeIdx;

            uint sz;
            if (read <= write)
            {
                sz = _pipe.Size - write + read - 1;
            }
            else
            {
                sz = read - write - 1;
            }

            return new Span<byte>((void*)(_pipe._buffer + write), (int)sz);
        }

        /// <summary>
        /// Gets the current write offset from the buffer base.
        /// Used for zero-copy receives with registered buffers.
        /// </summary>
        public int GetWriteOffset() => (int)_pipe._writeIdx;

        public void Advance(uint count)
        {
            var read = _pipe._readIdx;
            var write = _pipe._writeIdx;

            if (count == 0)
            {
                return;
            }

            if (count > _pipe.Size - 1)
            {
                throw new EndOfPipeException("Unable to advance beyond the end of the pipe.");
            }

            if (read <= write)
            {
                if (count > read + _pipe.Size - write - 1)
                {
                    throw new EndOfPipeException("Unable to advance beyond the end of the pipe.");
                }

                var sz = Math.Min(count, _pipe.Size - write);

                write += sz;
                if (write > _pipe.Size - 1)
                {
                    write = 0;
                }
                count -= sz;

                if (count > 0)
                {
                    if (count >= read)
                    {
                        throw new EndOfPipeException("Unable to advance beyond the end of the pipe.");
                    }

                    write = count;
                }
            }
            else
            {
                if (count > read - write - 1)
                {
                    throw new EndOfPipeException("Unable to advance beyond the end of the pipe.");
                }

                write += count;
            }

            if (write == read)
            {
                throw new EndOfPipeException("Unable to advance beyond the end of the pipe.");
            }

            _pipe._writeIdx = write;
        }

        public void Close() => _pipe._closed = true;

        public bool IsClosed => _pipe._closed;
    }

    public class PipeReader
    {
        private readonly Pipe _pipe;

        internal PipeReader(Pipe pipe) => _pipe = pipe;

        public unsafe Span<byte> AvailableToRead()
        {
            var read = _pipe._readIdx;
            var write = _pipe._writeIdx;

            uint sz;
            if (read <= write)
            {
                sz = write - read;
            }
            else
            {
                sz = _pipe.Size - read + write;
            }

            return new Span<byte>((void*)(_pipe._buffer + read), (int)sz);
        }

        /// <summary>
        /// Gets the current read offset from the buffer base.
        /// Used for zero-copy sends with registered buffers.
        /// </summary>
        public int GetReadOffset() => (int)_pipe._readIdx;

        public void Advance(uint count)
        {
            var read = _pipe._readIdx;
            var write = _pipe._writeIdx;

            if (read <= write)
            {
                if (count > write - read)
                {
                    throw new EndOfPipeException("Unable to advance beyond the end of the pipe.");
                }

                read += count;
            }
            else
            {
                var sz = Math.Min(count, _pipe.Size - read);

                read += sz;
                if (read > _pipe.Size - 1)
                {
                    read = 0;
                }
                count -= sz;

                if (count > 0)
                {
                    if (count > write)
                    {
                        throw new EndOfPipeException("Unable to advance beyond the end of the pipe.");
                    }

                    read = count;
                }
            }

            if (read == write)
            {
                // If the read pointer catches up to the write pointer, then the pipe is empty.
                // As a performance optimization, set both to 0.
                _pipe._readIdx = 0;
                _pipe._writeIdx = 0;
            }
            else
            {
                _pipe._readIdx = read;
            }
        }

        public void Close() => _pipe._closed = true;

        public bool IsClosed => _pipe._closed;
    }

    private nint _handle;
    private nint _buffer;
    private readonly uint _bufferSize;
    private uint _writeIdx;
    private uint _readIdx;
    private bool _closed;

    public PipeWriter Writer { get; }
    public PipeReader Reader { get; }

    public uint Size => _bufferSize;

    public bool Closed => _closed;

    /// <summary>
    /// Gets the base pointer of the buffer for RIO registration.
    /// The full virtual range is 2x Size (double-mapped).
    /// </summary>
    public nint GetBufferPointer() => _buffer;

    /// <summary>
    /// Gets the total virtual address range size (2x physical size).
    /// </summary>
    public uint GetVirtualSize() => _bufferSize * 2;

    public Pipe(uint size)
    {
        var pageSize = (uint)Environment.SystemPageSize;

        // Virtual allocation requires multiples of system page size
        var adjustedSize = (size + pageSize - 1) & ~(pageSize - 1);

        // Reserve a region of virtual memory. We need twice the size so we can later mirror.
        var region = NativeMethods.VirtualAlloc2(
            nint.Zero,
            nint.Zero,
            adjustedSize * 2,
            NativeMethods.MEM_RESERVE | NativeMethods.MEM_RESERVE_PLACEHOLDER,
            NativeMethods.PAGE_NOACCESS,
            nint.Zero,
            0
        );

        if (region == nint.Zero)
        {
            throw new InvalidOperationException($"Allocating virtual memory failed. ({Marshal.GetLastPInvokeError()})");
        }

        // Releases half of the region so we can map the same memory region twice
        var freed = NativeMethods.VirtualFree(
            region,
            adjustedSize,
            NativeMethods.MEM_RELEASE | NativeMethods.MEM_PRESERVE_PLACEHOLDER
        );

        if (!freed)
        {
            throw new InvalidOperationException($"Creating virtual placeholder failed. ({Marshal.GetLastPInvokeError()})");
        }

        // Create a file descriptor
        _handle = NativeMethods.CreateFileMappingW(
            NativeMethods.InvalidHandleValue,
            nint.Zero,
            NativeMethods.PAGE_READWRITE,
            0,
            adjustedSize,
            null
        );

        if (_handle == nint.Zero)
        {
            throw new InvalidOperationException($"Creating file mapping failed. ({Marshal.GetLastPInvokeError()})");
        }

        // Map the region to the first half of the virtual space
        _buffer = NativeMethods.MapViewOfFile3(
            _handle,
            nint.Zero,
            region,
            0,
            adjustedSize,
            NativeMethods.MEM_REPLACE_PLACEHOLDER,
            NativeMethods.PAGE_READWRITE,
            nint.Zero,
            0
        );

        if (_buffer == nint.Zero)
        {
            throw new InvalidOperationException($"Mapping file view failed. ({Marshal.GetLastPInvokeError()})");
        }

        // Map the same region to the second half of the virtual space
        var view2 = NativeMethods.MapViewOfFile3(
            _handle,
            nint.Zero,
            _buffer + (nint)adjustedSize,
            0,
            adjustedSize,
            NativeMethods.MEM_REPLACE_PLACEHOLDER,
            NativeMethods.PAGE_READWRITE,
            nint.Zero,
            0
        );

        if (view2 == nint.Zero)
        {
            throw new InvalidOperationException($"Mapping file view mirror failed. ({Marshal.GetLastPInvokeError()})");
        }

        _bufferSize = adjustedSize;
        _writeIdx = 0;
        _readIdx = 0;
        _closed = false;

        Writer = new PipeWriter(this);
        Reader = new PipeReader(this);
    }

    private static partial class NativeMethods
    {
        private const string Kernel32 = "kernel32.dll";
        private const string KernelBase = "kernelbase.dll";
        public static readonly nint InvalidHandleValue = -1;

        [LibraryImport(Kernel32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint CreateFileMappingW(
            nint hFile, nint lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow,
            string? lpName
        );

        [LibraryImport(KernelBase, SetLastError = true)]
        public static partial nint MapViewOfFile3(
            nint hFileMappingObject, nint processHandle, nint pvBaseAddress, ulong ullOffset, ulong ullSize,
            uint allocFlags, uint dwDesiredAccess,
            nint hExtendedParameter, int parameterCount
        );

        [LibraryImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnmapViewOfFile(nint lpBaseAddress);

        [LibraryImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint hObject);

        [LibraryImport(KernelBase, SetLastError = true)]
        public static partial nint VirtualAlloc2(
            nint process,
            nint address,
            ulong size,
            uint allocationType,
            uint protect,
            nint extendedParameters,
            uint parameterCount
        );

        [LibraryImport(Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualFree(nint lpAddress, uint dwSize, uint dwFreeType);

        public const uint MEM_PRESERVE_PLACEHOLDER = 0x02;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint MEM_RESERVE_PLACEHOLDER = 0x40000;
        public const uint PAGE_NOACCESS = 0x01;
        public const uint PAGE_READWRITE = 0x04;
    }

    private void ReleaseUnmanagedResources()
    {
        if (_buffer == nint.Zero)
        {
            return;
        }

        if (_handle != nint.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = nint.Zero;
        }

        if (_buffer != nint.Zero)
        {
            NativeMethods.UnmapViewOfFile(_buffer);
            NativeMethods.UnmapViewOfFile(_buffer + (nint)_bufferSize);
        }

        _buffer = nint.Zero;
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~Pipe()
    {
        ReleaseUnmanagedResources();
    }
}

public class EndOfPipeException : IOException
{
    public EndOfPipeException(string message) : base(message)
    {
    }
}
