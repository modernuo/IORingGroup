// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;

namespace System.Network.EPoll.Architectures;

/// <summary>
/// Linux x64 syscall bindings for epoll and sockets.
/// </summary>
internal sealed partial class LinuxEpoll_x64 : ILinuxEpollArch
{
    public static readonly LinuxEpoll_x64 Instance = new();

    private LinuxEpoll_x64() { }

    // =============================================================================
    // Socket constants (same as Linux x64 io_uring)
    // =============================================================================

    public const int AF_INET = 2;
    public const int SOCK_STREAM = 1;
    public const int SOCK_NONBLOCK = 0x800;
    public const int IPPROTO_TCP = 6;
    public const int SOL_SOCKET = 1;
    public const int SO_REUSEADDR = 2;
    public const int SO_LINGER = 13;
    public const int TCP_NODELAY = 1;
    public const int F_SETFL = 4;
    public const int F_GETFL = 3;
    public const int O_NONBLOCK = 0x800;

    // epoll_event size for x64 (packed: 4 bytes events + 8 bytes data = 12 bytes)
    public const int EPOLL_EVENT_SIZE = 12;

    // =============================================================================
    // ILinuxEpollArch property implementations
    // =============================================================================

    int ILinuxEpollArch.AF_INET => AF_INET;
    int ILinuxEpollArch.SOCK_STREAM => SOCK_STREAM;
    int ILinuxEpollArch.SOCK_NONBLOCK => SOCK_NONBLOCK;
    int ILinuxEpollArch.IPPROTO_TCP => IPPROTO_TCP;
    int ILinuxEpollArch.SOL_SOCKET => SOL_SOCKET;
    int ILinuxEpollArch.SO_REUSEADDR => SO_REUSEADDR;
    int ILinuxEpollArch.SO_LINGER => SO_LINGER;
    int ILinuxEpollArch.TCP_NODELAY => TCP_NODELAY;
    int ILinuxEpollArch.F_SETFL => F_SETFL;
    int ILinuxEpollArch.F_GETFL => F_GETFL;
    int ILinuxEpollArch.O_NONBLOCK => O_NONBLOCK;
    int ILinuxEpollArch.EpollEventSize => EPOLL_EVENT_SIZE;

    // =============================================================================
    // epoll P/Invoke bindings
    // =============================================================================

    [LibraryImport("libc", SetLastError = true)]
    private static partial int epoll_create1(int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int epoll_ctl(int epfd, int op, int fd, nint ev);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int epoll_wait(int epfd, nint events, int maxevents, int timeout);

    // =============================================================================
    // Socket P/Invoke bindings
    // =============================================================================

    [LibraryImport("libc", SetLastError = true)]
    private static partial int accept(int sockfd, nint addr, nint addrlen);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint recv(int sockfd, nint buf, nuint len, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint send(int sockfd, nint buf, nuint len, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int connect(int sockfd, nint addr, int addrlen);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int shutdown(int sockfd, int how);

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

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    // =============================================================================
    // ILinuxEpollArch method implementations
    // =============================================================================

    int ILinuxEpollArch.epoll_create1(int flags) => epoll_create1(flags);
    int ILinuxEpollArch.epoll_ctl(int epfd, int op, int fd, nint ev) => epoll_ctl(epfd, op, fd, ev);
    int ILinuxEpollArch.epoll_wait(int epfd, nint events, int maxevents, int timeout)
        => epoll_wait(epfd, events, maxevents, timeout);

    int ILinuxEpollArch.accept(int sockfd, nint addr, nint addrlen) => accept(sockfd, addr, addrlen);
    nint ILinuxEpollArch.recv(int sockfd, nint buf, nuint len, int flags) => recv(sockfd, buf, len, flags);
    nint ILinuxEpollArch.send(int sockfd, nint buf, nuint len, int flags) => send(sockfd, buf, len, flags);
    int ILinuxEpollArch.connect(int sockfd, nint addr, int addrlen) => connect(sockfd, addr, addrlen);
    int ILinuxEpollArch.shutdown(int sockfd, int how) => shutdown(sockfd, how);

    int ILinuxEpollArch.socket(int domain, int type, int protocol) => socket(domain, type, protocol);
    int ILinuxEpollArch.bind(int sockfd, nint addr, int addrlen) => bind(sockfd, addr, addrlen);
    int ILinuxEpollArch.listen(int sockfd, int backlog) => listen(sockfd, backlog);
    int ILinuxEpollArch.setsockopt(int sockfd, int level, int optname, nint optval, int optlen)
        => setsockopt(sockfd, level, optname, optval, optlen);
    int ILinuxEpollArch.fcntl(int fd, int cmd, int arg) => fcntl(fd, cmd, arg);
    int ILinuxEpollArch.close(int fd) => close(fd);
}
