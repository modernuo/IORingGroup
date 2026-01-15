// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

namespace System.Network.EPoll;

/// <summary>
/// Interface for architecture-specific Linux epoll and socket syscalls.
/// Used by LinuxEpollGroup for epoll-based I/O when io_uring is not available.
/// </summary>
internal interface ILinuxEpollArch
{
    // =============================================================================
    // epoll syscalls
    // =============================================================================

    /// <summary>
    /// Creates an epoll instance.
    /// </summary>
    int epoll_create1(int flags);

    /// <summary>
    /// Controls an epoll instance (add/modify/delete file descriptors).
    /// </summary>
    int epoll_ctl(int epfd, int op, int fd, nint ev);

    /// <summary>
    /// Waits for events on an epoll instance.
    /// </summary>
    int epoll_wait(int epfd, nint events, int maxevents, int timeout);

    // =============================================================================
    // Socket syscalls (not in ILinuxArch - io_uring handles these in-kernel)
    // =============================================================================

    /// <summary>
    /// Accepts a connection on a socket.
    /// </summary>
    int accept(int sockfd, nint addr, nint addrlen);

    /// <summary>
    /// Receives data from a socket.
    /// </summary>
    nint recv(int sockfd, nint buf, nuint len, int flags);

    /// <summary>
    /// Sends data on a socket.
    /// </summary>
    nint send(int sockfd, nint buf, nuint len, int flags);

    /// <summary>
    /// Connects a socket to a remote address.
    /// </summary>
    int connect(int sockfd, nint addr, int addrlen);

    /// <summary>
    /// Shuts down part of a full-duplex connection.
    /// </summary>
    int shutdown(int sockfd, int how);

    // =============================================================================
    // Shared socket syscalls (same as ILinuxArch)
    // =============================================================================

    /// <summary>
    /// Creates a socket.
    /// </summary>
    int socket(int domain, int type, int protocol);

    /// <summary>
    /// Binds a socket to an address.
    /// </summary>
    int bind(int sockfd, nint addr, int addrlen);

    /// <summary>
    /// Marks a socket as listening for connections.
    /// </summary>
    int listen(int sockfd, int backlog);

    /// <summary>
    /// Sets a socket option.
    /// </summary>
    int setsockopt(int sockfd, int level, int optname, nint optval, int optlen);

    /// <summary>
    /// Performs control operations on a file descriptor.
    /// </summary>
    int fcntl(int fd, int cmd, int arg);

    /// <summary>
    /// Closes a file descriptor.
    /// </summary>
    int close(int fd);

    // =============================================================================
    // Socket constants
    // =============================================================================

    int AF_INET { get; }
    int SOCK_STREAM { get; }
    int SOCK_NONBLOCK { get; }
    int IPPROTO_TCP { get; }
    int SOL_SOCKET { get; }
    int SO_REUSEADDR { get; }
    int SO_LINGER { get; }
    int TCP_NODELAY { get; }
    int F_SETFL { get; }
    int F_GETFL { get; }
    int O_NONBLOCK { get; }

    /// <summary>
    /// Size of epoll_event structure for this architecture (12 for x64, 16 for ARM64).
    /// </summary>
    int EpollEventSize { get; }
}
