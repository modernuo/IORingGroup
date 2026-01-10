using System.Runtime.InteropServices;

namespace System.Network.Windows;

/// <summary>
/// P/Invoke bindings for the ioring.dll native library.
/// </summary>
public static partial class Win_x64
{
    private const string LibraryName = "ioring.dll";

    /// <summary>
    /// Backend type returned by ioring_get_backend.
    /// </summary>
    public enum IORingBackend : int
    {
        None = 0,
        IoRing = 1,  // Windows 11+ IoRing
        RIO = 2,     // Windows 8+ Registered I/O
    }

    /// <summary>
    /// Submission queue entry structure matching C ioring_sqe_t.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IORingSqe
    {
        public byte Opcode;
        public byte Flags;
        public ushort IoPrio;
        public int Fd;
        public ulong Off;
        public ulong Addr;
        public uint Len;
        public uint OpFlags;  // Union: poll_events, msg_flags, accept_flags
        public ulong UserData;
        public ulong Addr3;
    }

    /// <summary>
    /// Completion queue entry structure matching C ioring_cqe_t.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IORingCqe
    {
        public ulong UserData;
        public int Res;
        public uint Flags;
    }

    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial nint ioring_create(uint entries);

    [LibraryImport(LibraryName)]
    public static partial void ioring_destroy(nint ring);

    [LibraryImport(LibraryName)]
    public static partial IORingBackend ioring_get_backend(nint ring);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_sq_space_left(nint ring);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_cq_ready(nint ring);

    [LibraryImport(LibraryName)]
    public static partial nint ioring_get_sqe(nint ring);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_poll_add(nint sqe, nint fd, uint pollMask, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_poll_remove(nint sqe, ulong userDataToCancel);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_accept(nint sqe, nint fd, nint addr, nint addrlen, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_connect(nint sqe, nint fd, nint addr, int addrlen, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_send(nint sqe, nint fd, nint buf, uint len, int flags, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_recv(nint sqe, nint fd, nint buf, uint len, int flags, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_close(nint sqe, nint fd, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_cancel(nint sqe, ulong userDataToCancel, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_shutdown(nint sqe, nint fd, int how, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial int ioring_submit(nint ring);

    [LibraryImport(LibraryName)]
    public static partial int ioring_submit_and_wait(nint ring, uint waitNr);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_peek_cqe(nint ring, nint cqes, uint count);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_wait_cqe(nint ring, nint cqes, uint count, uint minComplete, int timeoutMs);

    [LibraryImport(LibraryName)]
    public static partial void ioring_cq_advance(nint ring, uint count);

    [LibraryImport(LibraryName)]
    public static partial int ioring_get_last_error();
}
