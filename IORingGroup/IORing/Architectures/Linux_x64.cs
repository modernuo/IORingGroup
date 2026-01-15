// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;

namespace System.Network.IORing.Architectures;

/// <summary>
/// Linux x64 syscall bindings for io_uring.
/// </summary>
public sealed partial class Linux_x64 : ILinuxArch
{
    public static readonly Linux_x64 Instance = new();

    private Linux_x64() { }

    // ILinuxArch property implementations - mmap
    int ILinuxArch.PROT_READ => PROT_READ;
    int ILinuxArch.PROT_WRITE => PROT_WRITE;
    int ILinuxArch.MAP_SHARED => MAP_SHARED;
    int ILinuxArch.MAP_POPULATE => MAP_POPULATE;

    // ILinuxArch property implementations - io_uring offsets
    ulong ILinuxArch.IORING_OFF_SQ_RING => IORING_OFF_SQ_RING;
    ulong ILinuxArch.IORING_OFF_CQ_RING => IORING_OFF_CQ_RING;
    ulong ILinuxArch.IORING_OFF_SQES => IORING_OFF_SQES;

    // ILinuxArch property implementations - socket constants
    int ILinuxArch.AF_INET => AF_INET;
    int ILinuxArch.SOCK_STREAM => SOCK_STREAM;
    int ILinuxArch.SOCK_NONBLOCK => SOCK_NONBLOCK;
    int ILinuxArch.IPPROTO_TCP => IPPROTO_TCP;
    int ILinuxArch.SOL_SOCKET => SOL_SOCKET;
    int ILinuxArch.SO_REUSEADDR => SO_REUSEADDR;
    int ILinuxArch.SO_LINGER => SO_LINGER;
    int ILinuxArch.TCP_NODELAY => TCP_NODELAY;
    int ILinuxArch.F_SETFL => F_SETFL;
    int ILinuxArch.F_GETFL => F_GETFL;
    int ILinuxArch.O_NONBLOCK => O_NONBLOCK;

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

    // Socket constants
    public const int AF_INET = 2;
    public const int SOCK_STREAM = 1;
    public const int SOCK_NONBLOCK = 0x800;
    public const int IPPROTO_TCP = 6;
    public const int SOL_SOCKET = 1;
    public const int SO_REUSEADDR = 2;
    public const int SO_LINGER = 13;
    public const int TCP_NODELAY = 1;  // IPPROTO_TCP level
    public const int F_SETFL = 4;
    public const int F_GETFL = 3;
    public const int O_NONBLOCK = 0x800;

    // libc bindings - memory
    [LibraryImport("libc", SetLastError = true)]
    private static partial nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(nint addr, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    // libc bindings - sockets
    [LibraryImport("libc", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int bind(int sockfd, nint addr, int addrlen);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int listen(int sockfd, int backlog);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsockopt(int sockfd, int level, int optname, nint optval, int optlen);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    /// <summary>
    /// io_uring_setup syscall wrapper.
    /// </summary>
    unsafe int ILinuxArch.io_uring_setup(uint entries, ref io_uring_params p)
    {
        fixed (io_uring_params* pp = &p)
        {
            return (int)syscall_raw(SYS_io_uring_setup, entries, (nint)pp);
        }
    }

    /// <summary>
    /// io_uring_enter syscall wrapper.
    /// </summary>
    int ILinuxArch.io_uring_enter(int fd, uint to_submit, uint min_complete, uint flags) =>
        (int)syscall_raw(SYS_io_uring_enter, fd, to_submit, min_complete, flags, 0);

    /// <summary>
    /// io_uring_register syscall wrapper.
    /// </summary>
    int ILinuxArch.io_uring_register(int fd, uint opcode, nint arg, uint nr_args) =>
        (int)syscall_raw(SYS_io_uring_register, fd, opcode, arg, nr_args);

    nint ILinuxArch.mmap(nint addr, nuint length, int prot, int flags, int fd, long offset) =>
        mmap(addr, length, prot, flags, fd, offset);

    int ILinuxArch.munmap(nint addr, nuint length) => munmap(addr, length);

    int ILinuxArch.close(int fd) => close(fd);

    int ILinuxArch.socket(int domain, int type, int protocol) => socket(domain, type, protocol);
    int ILinuxArch.bind(int sockfd, nint addr, int addrlen) => bind(sockfd, addr, addrlen);
    int ILinuxArch.listen(int sockfd, int backlog) => listen(sockfd, backlog);
    int ILinuxArch.setsockopt(int sockfd, int level, int optname, nint optval, int optlen)
        => setsockopt(sockfd, level, optname, optval, optlen);
    int ILinuxArch.fcntl(int fd, int cmd, int arg) => fcntl(fd, cmd, arg);

    // Raw syscall implementation
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall_raw(int number, uint arg1, nint arg2);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall_raw(int number, int arg1, uint arg2, uint arg3, uint arg4, nint arg5);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long syscall_raw(int number, int arg1, uint arg2, nint arg3, uint arg4);
}
