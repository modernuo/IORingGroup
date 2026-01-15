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
    /// <param name="maxConnections">Maximum concurrent connections (Windows RIO only, ignored on other platforms).</param>
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
            return CreateLinuxRing(queueSize);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            return CreateDarwinRing(queueSize);
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

    private static IIORingGroup CreateLinuxRing(int queueSize)
    {
        // Use architecture-specific implementation via singleton instances
        IORing.ILinuxArch arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6
                => IORing.Architectures.Linux_arm64.Instance,
            _ => IORing.Architectures.Linux_x64.Instance,
        };
        return new IORing.LinuxIORingGroup(arch, queueSize);
    }

    private static IIORingGroup CreateDarwinRing(int queueSize)
    {
        return new Darwin.DarwinIORingGroup(queueSize);
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
    /// Returns true if this is a power of 2 (used to validate queue size).
    /// </summary>
    internal static bool IsPowerOfTwo(int x) => x > 0 && (x & (x - 1)) == 0;
}
