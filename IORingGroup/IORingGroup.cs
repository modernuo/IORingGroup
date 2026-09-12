// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;

namespace System.Network;

/// <summary>
/// Factory for creating platform-appropriate IIORingGroup implementations.
/// </summary>
public static class IORingGroup
{
    /// <summary>
    /// Default size for the submission and completion queues.
    /// </summary>
    public const int DefaultQueueSize = 4096;

    /// <summary>
    /// Default maximum connections for Windows RIO mode.
    /// </summary>
    public const int DefaultMaxConnections = 1024;

    /// <summary>
    /// Creates an IIORingGroup instance appropriate for the current platform.
    /// </summary>
    /// <param name="queueSize">Size of the submission and completion queues. Must be power of 2.</param>
    /// <param name="maxConnections">
    /// Maximum concurrent connections. It also supplies the default registration table size
    /// (<c>maxConnections * 2</c>) when <paramref name="maxRegisteredBuffers"/> is 0, but only then.
    /// </param>
    /// <param name="maxRegisteredBuffers">
    /// Size of the buffer registration table. 0 (default) means <c>maxConnections * 2</c>; pass a
    /// larger value for headroom beyond the default one recv + one send buffer per connection.
    /// <see cref="RingSocketManager.RequiredRegisteredBuffers"/> computes what a
    /// <see cref="RingSocketManager"/> configuration needs, which is well above that default.
    /// </param>
    /// <returns>Platform-specific IIORingGroup implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if the current platform is not supported.</exception>
    public static IIORingGroup Create(
        int queueSize = DefaultQueueSize,
        int maxConnections = DefaultMaxConnections,
        int maxOutstandingSends = 1,
        int maxRegisteredBuffers = 0
    )
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindowsRing(maxConnections, maxOutstandingSends, maxRegisteredBuffers);
        }

        // Other backends ignore maxOutstandingSends and report 1 via MaxOutstandingSendsPerSocket:
        // they complete sends on copy, so extra sends in flight gain nothing and cost ordering
        // guarantees (io_uring) or correctness (epoll/kqueue hold one pending send per connection).

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return CreateLinuxRing(queueSize, maxConnections, maxRegisteredBuffers);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            return CreateDarwinRing(queueSize, maxConnections, maxRegisteredBuffers);
        }

        throw new PlatformNotSupportedException(
            $"IORingGroup is not supported on platform: {RuntimeInformation.OSDescription}");
    }

    private static IIORingGroup CreateWindowsRing(int maxConnections, int maxOutstandingSends, int maxRegisteredBuffers) =>
        new Windows.WindowsManagedRIOGroup(maxConnections, maxOutstandingSends, maxRegisteredBuffers);

    private static IIORingGroup CreateLinuxRing(int queueSize, int maxConnections, int maxRegisteredBuffers)
    {
        if (IORing.LinuxIORingGroup.IsAvailable())
        {
            return new IORing.LinuxIORingGroup(queueSize, maxConnections, maxRegisteredBuffers);
        }

        // Fallback to epoll when io_uring is unavailable
        return new EPoll.LinuxEpollGroup(queueSize, maxConnections, maxRegisteredBuffers);
    }

    /// <summary>
    /// Creates an epoll-based IIORingGroup for Linux explicitly.
    /// Useful for testing or when io_uring should be bypassed.
    /// </summary>
    /// <param name="queueSize">Size of the submission and completion queues. Must be power of 2.</param>
    /// <param name="maxConnections">Maximum concurrent connections.</param>
    /// <param name="maxRegisteredBuffers">
    /// Size of the buffer registration table. 0 (default) means <c>maxConnections * 2</c>.
    /// </param>
    /// <returns>Epoll-based IIORingGroup implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if not running on Linux.</exception>
    public static IIORingGroup CreateLinuxEpoll(
        int queueSize = DefaultQueueSize,
        int maxConnections = DefaultMaxConnections,
        int maxRegisteredBuffers = 0
    )
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("epoll requires Linux");
        }

        return new EPoll.LinuxEpollGroup(queueSize, maxConnections, maxRegisteredBuffers);
    }

    private static Darwin.DarwinIORingGroup CreateDarwinRing(int queueSize, int maxConnections, int maxRegisteredBuffers) =>
        new Darwin.DarwinIORingGroup(queueSize, maxConnections, maxRegisteredBuffers);

    /// <summary>
    /// Returns true if this is a power of 2 (used to validate queue size).
    /// </summary>
    internal static bool IsPowerOfTwo(int x) => x > 0 && (x & (x - 1)) == 0;
}
