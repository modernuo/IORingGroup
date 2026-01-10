using System.Runtime.InteropServices;

namespace System.Network.IORing.Architectures;

/// <summary>
/// Linux x64 syscall bindings for io_uring.
/// </summary>
public sealed partial class Linux_x64 : ILinuxArch
{
    public static readonly Linux_x64 Instance = new();

    private Linux_x64() { }

    // ILinuxArch property implementations
    int ILinuxArch.PROT_READ => PROT_READ;
    int ILinuxArch.PROT_WRITE => PROT_WRITE;
    int ILinuxArch.MAP_SHARED => MAP_SHARED;
    int ILinuxArch.MAP_POPULATE => MAP_POPULATE;
    ulong ILinuxArch.IORING_OFF_SQ_RING => IORING_OFF_SQ_RING;
    ulong ILinuxArch.IORING_OFF_CQ_RING => IORING_OFF_CQ_RING;
    ulong ILinuxArch.IORING_OFF_SQES => IORING_OFF_SQES;

    // Syscall numbers for x86_64
    private const int SYS_io_uring_setup = 425;
    private const int SYS_io_uring_enter = 426;
    private const int SYS_io_uring_register = 427;

    // mmap constants
    public const int PROT_READ = 0x1;
    public const int PROT_WRITE = 0x2;
    public const int MAP_SHARED = 0x01;
    public const int MAP_POPULATE = 0x8000;

    // io_uring_register opcodes
    public const uint IORING_REGISTER_BUFFERS = 0;
    public const uint IORING_UNREGISTER_BUFFERS = 1;
    public const uint IORING_REGISTER_FILES = 2;
    public const uint IORING_UNREGISTER_FILES = 3;

    // mmap offsets for io_uring
    public const ulong IORING_OFF_SQ_RING = 0;
    public const ulong IORING_OFF_CQ_RING = 0x8000000;
    public const ulong IORING_OFF_SQES = 0x10000000;

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint mmap_native(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap_native(nint addr, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close_native(int fd);

    /// <summary>
    /// io_uring_setup syscall wrapper.
    /// </summary>
    int ILinuxArch.io_uring_setup(uint entries, ref io_uring_params p)
    {
        unsafe
        {
            fixed (io_uring_params* pp = &p)
            {
                return (int)syscall_raw(SYS_io_uring_setup, entries, (nint)pp);
            }
        }
    }

    /// <summary>
    /// io_uring_enter syscall wrapper.
    /// </summary>
    int ILinuxArch.io_uring_enter(int fd, uint to_submit, uint min_complete, uint flags)
    {
        return (int)syscall_raw(SYS_io_uring_enter, fd, to_submit, min_complete, flags, 0);
    }

    /// <summary>
    /// io_uring_register syscall wrapper.
    /// </summary>
    int ILinuxArch.io_uring_register(int fd, uint opcode, nint arg, uint nr_args)
    {
        return (int)syscall_raw(SYS_io_uring_register, fd, opcode, arg, nr_args);
    }

    nint ILinuxArch.mmap(nint addr, nuint length, int prot, int flags, int fd, long offset)
        => mmap_native(addr, length, prot, flags, fd, offset);

    int ILinuxArch.munmap(nint addr, nuint length) => munmap_native(addr, length);

    int ILinuxArch.close(int fd) => close_native(fd);

    // Raw syscall implementation
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall_raw(int number, uint arg1, nint arg2);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall_raw(int number, int arg1, uint arg2, uint arg3, uint arg4, nint arg5);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall_raw(int number, int arg1, uint arg2, nint arg3, uint arg4);
}
