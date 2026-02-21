// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;

namespace System.Network.EPoll;

/// <summary>
/// epoll_event for x64 Linux — 12 bytes, packed to match kernel __attribute__((packed)).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct epoll_event_packed
{
    [FieldOffset(0)] public uint events;
    [FieldOffset(4)] public long data;
}

/// <summary>
/// epoll_event for ARM64 Linux — 16 bytes, natural alignment.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct epoll_event_aligned
{
    public uint events;
    public uint _padding;
    public long data;
}

/// <summary>
/// epoll event flags.
/// </summary>
[Flags]
internal enum epoll_events : uint
{
    EPOLLIN      = 0x001,
    EPOLLPRI     = 0x002,
    EPOLLOUT     = 0x004,
    EPOLLERR     = 0x008,
    EPOLLHUP     = 0x010,
    EPOLLRDNORM  = 0x040,
    EPOLLRDBAND  = 0x080,
    EPOLLWRNORM  = 0x100,
    EPOLLWRBAND  = 0x200,
    EPOLLRDHUP   = 0x2000,
    EPOLLET      = 0x80000000,
    EPOLLONESHOT = 0x40000000,
}

/// <summary>
/// epoll_ctl operations.
/// </summary>
internal enum epoll_op : int
{
    EPOLL_CTL_ADD = 1,
    EPOLL_CTL_DEL = 2,
    EPOLL_CTL_MOD = 3,
}
