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
    /// Creates an IIORingGroup instance appropriate for the current platform.
    /// </summary>
    /// <param name="queueSize">Size of the submission and completion queues. Must be power of 2.</param>
    /// <returns>Platform-specific IIORingGroup implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if the current platform is not supported.</exception>
    public static IIORingGroup Create(int queueSize = DefaultQueueSize)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return CreateWindowsRing(queueSize);
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

    private static IIORingGroup CreateWindowsRing(int queueSize)
    {
        // Windows implementation uses ioring.dll which handles IoRing/RIO selection
        return new Windows.WindowsIORingGroup(queueSize);
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
