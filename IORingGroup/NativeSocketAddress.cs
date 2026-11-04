// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace System.Network;

/// <summary>
/// Address family numbers and sockaddr layout of one platform's socket API.
/// </summary>
internal readonly struct SocketAddressLayout
{
    /// <summary>AF_INET.</summary>
    public readonly int InterNetwork;

    /// <summary>BSD-derived systems prefix sockaddr with a length byte and use a one-byte family.</summary>
    public readonly bool LengthPrefixed;

    private SocketAddressLayout(int interNetwork, bool lengthPrefixed)
    {
        InterNetwork = interNetwork;
        LengthPrefixed = lengthPrefixed;
    }

    public static SocketAddressLayout Windows => new(2, false);

    public static SocketAddressLayout Linux => new(2, false);

    public static SocketAddressLayout Bsd => new(2, true);
}

/// <summary>
/// A listener's bind address encoded as a native sockaddr.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSocketAddress
{
    /// <summary>sizeof(sockaddr_in).</summary>
    public const int MaxLength = 16;

    /// <summary>The sockaddr bytes; pass <c>Data</c> and <see cref="Length"/> to bind.</summary>
    public fixed byte Data[MaxLength];

    /// <summary>Bytes of <see cref="Data"/> in use.</summary>
    public int Length;

    /// <summary>Address family to create the socket with.</summary>
    public int Family;

    /// <summary>
    /// Parses a bind address. Null or empty means every IPv4 interface.
    /// </summary>
    /// <remarks>
    /// Only the dotted-quad form is accepted. Host names and the shorthand forms
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> allows ("127.1", "2130706433") are
    /// rejected, so a mistyped address fails instead of binding somewhere unexpected.
    /// </remarks>
    public static bool TryParseBindAddress(string? bindAddress, out IPAddress address)
    {
        if (string.IsNullOrEmpty(bindAddress))
        {
            address = IPAddress.Any;
            return true;
        }

        if (!IPAddress.TryParse(bindAddress, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork ||
            parsed.ToString() != bindAddress)
        {
            address = IPAddress.None;
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// Encodes <paramref name="bindAddress"/> and <paramref name="port"/> for <paramref name="layout"/>.
    /// </summary>
    /// <returns>False when the address is not one <see cref="TryParseBindAddress"/> accepts.</returns>
    public static bool TryCreate(string? bindAddress, ushort port, in SocketAddressLayout layout, out NativeSocketAddress result)
    {
        result = default;
        if (!TryParseBindAddress(bindAddress, out var address))
        {
            return false;
        }

        var bytes = MemoryMarshal.CreateSpan(ref result.Data[0], MaxLength);

        // sockaddr_in: family(2) port(2) addr(4) zero(8); BSD splits family into len(1) family(1).
        result.Length = 16;
        result.Family = layout.InterNetwork;
        WriteFamily(bytes, layout, result.Length, result.Family);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[2..], port);
        address.TryWriteBytes(bytes.Slice(4, 4), out _);
        return true;
    }

    private static void WriteFamily(Span<byte> bytes, in SocketAddressLayout layout, int length, int family)
    {
        if (layout.LengthPrefixed)
        {
            bytes[0] = (byte)length;
            bytes[1] = (byte)family;
        }
        else
        {
            // sa_family_t is a host-order 16-bit field.
            MemoryMarshal.Write(bytes, (ushort)family);
        }
    }
}
