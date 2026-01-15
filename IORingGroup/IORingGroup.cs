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
    /// <param name="maxConnections">Maximum concurrent connections. Determines external buffer capacity (maxConnections * 3).</param>
    /// <returns>Platform-specific IIORingGroup implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if the current platform is not supported.</exception>
    public static IIORingGroup Create(int queueSize = DefaultQueueSize, int maxConnections = DefaultMaxConnections)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindowsRing(queueSize, maxConnections);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return CreateLinuxRing(queueSize, maxConnections);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            return CreateDarwinRing(queueSize, maxConnections);
        }

        throw new PlatformNotSupportedException(
            $"IORingGroup is not supported on platform: {RuntimeInformation.OSDescription}");
    }

    private static IIORingGroup CreateWindowsRing(int queueSize, int maxConnections)
    {
        // Windows uses RIO (Registered I/O) for high-performance socket I/O with zero-copy buffers.
        // Buffer sizes are unused (external buffers are managed via IORingBufferPool).
        const int unusedBufferSize = 4096;
        return new Windows.WindowsRIOGroup(queueSize, maxConnections, unusedBufferSize, unusedBufferSize);
    }

    private static IIORingGroup CreateLinuxRing(int queueSize, int maxConnections)
    {
        // Try io_uring first, fall back to epoll if unavailable
        IORing.ILinuxArch ioUringArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6
                => IORing.Architectures.Linux_arm64.Instance,
            _ => IORing.Architectures.Linux_x64.Instance,
        };

        if (IORing.LinuxIORingGroup.IsAvailable(ioUringArch))
        {
            return new IORing.LinuxIORingGroup(ioUringArch, queueSize, maxConnections);
        }

        // Fall back to epoll
        EPoll.ILinuxEpollArch epollArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6
                => EPoll.Architectures.LinuxEpoll_arm64.Instance,
            _ => EPoll.Architectures.LinuxEpoll_x64.Instance,
        };

        return new EPoll.LinuxEpollGroup(epollArch, queueSize, maxConnections);
    }

    private static IIORingGroup CreateDarwinRing(int queueSize, int maxConnections)
    {
        return new Darwin.DarwinIORingGroup(queueSize, maxConnections);
    }

    /// <summary>
    /// Checks if IIORingGroup is supported on the current platform.
    /// </summary>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);

    /// <summary>
    /// Checks if Linux io_uring is available and functional.
    /// Returns false on non-Linux platforms, or if io_uring is blocked by seccomp/security policy.
    /// </summary>
    public static bool IsIOUringAvailable
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            IORing.ILinuxArch arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm or Architecture.Arm64 or Architecture.Armv6
                    => IORing.Architectures.Linux_arm64.Instance,
                _ => IORing.Architectures.Linux_x64.Instance,
            };

            return IORing.LinuxIORingGroup.IsAvailable(arch);
        }
    }

    /// <summary>
    /// Returns true if Linux epoll fallback is being used (io_uring not available).
    /// </summary>
    public static bool IsUsingEpollFallback =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !IsIOUringAvailable;

    /// <summary>
    /// Creates a Linux io_uring implementation. Throws if io_uring is not available.
    /// </summary>
    /// <param name="queueSize">Size of the submission and completion queues. Must be power of 2.</param>
    /// <param name="maxConnections">Maximum concurrent connections.</param>
    /// <returns>Linux io_uring IIORingGroup implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if not running on Linux.</exception>
    /// <exception cref="InvalidOperationException">Thrown if io_uring is blocked by seccomp or kernel.</exception>
    public static IIORingGroup CreateLinuxIOUring(int queueSize = DefaultQueueSize, int maxConnections = DefaultMaxConnections)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("io_uring requires Linux");

        IORing.ILinuxArch arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6
                => IORing.Architectures.Linux_arm64.Instance,
            _ => IORing.Architectures.Linux_x64.Instance,
        };

        if (!IORing.LinuxIORingGroup.IsAvailable(arch))
            throw new InvalidOperationException("io_uring is not available (blocked by seccomp or kernel too old)");

        return new IORing.LinuxIORingGroup(arch, queueSize, maxConnections);
    }

    /// <summary>
    /// Creates a Linux epoll implementation. Always available on Linux.
    /// </summary>
    /// <param name="queueSize">Size of the submission and completion queues. Must be power of 2.</param>
    /// <param name="maxConnections">Maximum concurrent connections.</param>
    /// <returns>Linux epoll IIORingGroup implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if not running on Linux.</exception>
    public static IIORingGroup CreateLinuxEpoll(int queueSize = DefaultQueueSize, int maxConnections = DefaultMaxConnections)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("epoll requires Linux");

        EPoll.ILinuxEpollArch arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6
                => EPoll.Architectures.LinuxEpoll_arm64.Instance,
            _ => EPoll.Architectures.LinuxEpoll_x64.Instance,
        };

        return new EPoll.LinuxEpollGroup(arch, queueSize, maxConnections);
    }

    /// <summary>
    /// Returns true if this is a power of 2 (used to validate queue size).
    /// </summary>
    internal static bool IsPowerOfTwo(int x) => x > 0 && (x & (x - 1)) == 0;
}
