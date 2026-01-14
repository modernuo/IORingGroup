// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

namespace System.Network.IORing;

/// <summary>
/// Interface for architecture-specific Linux io_uring syscalls.
/// </summary>
public interface ILinuxArch
{
    // io_uring syscalls
    int io_uring_setup(uint entries, ref io_uring_params p);
    int io_uring_enter(int fd, uint to_submit, uint min_complete, uint flags);
    int io_uring_register(int fd, uint opcode, nint arg, uint nr_args);

    // Memory mapping
    nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);
    int munmap(nint addr, nuint length);

    // File/socket operations
    int close(int fd);

    // Socket syscalls
    int socket(int domain, int type, int protocol);
    int bind(int sockfd, nint addr, int addrlen);
    int listen(int sockfd, int backlog);
    int setsockopt(int sockfd, int level, int optname, nint optval, int optlen);
    int fcntl(int fd, int cmd, int arg);

    // Memory mapping constants
    int PROT_READ { get; }
    int PROT_WRITE { get; }
    int MAP_SHARED { get; }
    int MAP_POPULATE { get; }

    // io_uring offsets
    ulong IORING_OFF_SQ_RING { get; }
    ulong IORING_OFF_CQ_RING { get; }
    ulong IORING_OFF_SQES { get; }

    // Socket constants
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
}
