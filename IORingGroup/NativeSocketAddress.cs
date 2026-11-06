// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Net;
using System.Net.Sockets;

namespace System.Network;

/// <summary>
/// Address family numbers of one platform's socket API.
/// </summary>
internal readonly struct SocketAddressLayout
{
    /// <summary>AF_INET.</summary>
    public readonly int InterNetwork;

    private SocketAddressLayout(int interNetwork) => InterNetwork = interNetwork;

    public static SocketAddressLayout Windows => new(2);

    public static SocketAddressLayout Linux => new(2);

    public static SocketAddressLayout Bsd => new(2);
}

/// <summary>
/// A listener's bind address as a native sockaddr.
/// </summary>
internal readonly struct NativeSocketAddress
{
    private readonly SocketAddress _socketAddress;

    /// <summary>Address family to create the socket with.</summary>
    public int Family { get; }

    private NativeSocketAddress(SocketAddress socketAddress, int family)
    {
        _socketAddress = socketAddress;
        Family = family;
    }

    /// <summary>The sockaddr bytes to pass to bind.</summary>
    public ReadOnlySpan<byte> Bytes => _socketAddress.Buffer.Span[.._socketAddress.Size];

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
            !IsCanonical(parsed, bindAddress))
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
        if (!TryParseBindAddress(bindAddress, out var address))
        {
            result = default;
            return false;
        }

        // Serialize() writes the running platform's own sockaddr layout: native family number,
        // and on BSD the sin_len byte.
        result = new NativeSocketAddress(new IPEndPoint(address, port).Serialize(), layout.InterNetwork);
        return true;
    }

    // The round trip rejects every non-canonical spelling IPAddress.TryParse still accepts.
    private static bool IsCanonical(IPAddress address, string text)
    {
        Span<char> formatted = stackalloc char[15]; // "255.255.255.255"
        return address.TryFormat(formatted, out var written) && formatted[..written].SequenceEqual(text);
    }
}
