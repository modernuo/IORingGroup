// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Net;
using System.Net.Sockets;

namespace System.Network;

/// <summary>
/// Address family numbers and IPv6 option names of one platform's socket API.
/// </summary>
internal readonly struct SocketAddressLayout
{
    /// <summary>IPPROTO_IPV6, the option level for <see cref="Ipv6V6Only"/>; 41 on every supported platform.</summary>
    public const int IPPROTO_IPV6 = 41;

    /// <summary>AF_INET.</summary>
    public readonly int InterNetwork;

    /// <summary>AF_INET6.</summary>
    public readonly int InterNetworkV6;

    /// <summary>IPV6_V6ONLY option name.</summary>
    public readonly int Ipv6V6Only;

    private SocketAddressLayout(int interNetwork, int interNetworkV6, int ipv6V6Only)
    {
        InterNetwork = interNetwork;
        InterNetworkV6 = interNetworkV6;
        Ipv6V6Only = ipv6V6Only;
    }

    public static SocketAddressLayout Windows => new(2, 23, 27);

    public static SocketAddressLayout Linux => new(2, 10, 26);

    /// <summary>The kqueue backend's platform: macOS uses AF_INET6 30, FreeBSD 28.</summary>
    public static SocketAddressLayout Bsd => OperatingSystem.IsFreeBSD() ? FreeBsd : MacOS;

    public static SocketAddressLayout MacOS => new(2, 30, 27);

    public static SocketAddressLayout FreeBsd => new(2, 28, 27);
}

/// <summary>
/// A listener's bind address as a native sockaddr.
/// </summary>
internal readonly struct NativeSocketAddress
{
    private readonly SocketAddress _socketAddress;

    /// <summary>Address family to create the socket with.</summary>
    public int Family { get; }

    /// <summary>Whether this is an IPv6 address, so IPV6_V6ONLY applies.</summary>
    public bool IsIPv6 { get; }

    private NativeSocketAddress(SocketAddress socketAddress, int family, bool isIPv6)
    {
        _socketAddress = socketAddress;
        Family = family;
        IsIPv6 = isIPv6;
    }

    /// <summary>The sockaddr bytes to pass to bind.</summary>
    public ReadOnlySpan<byte> Bytes => _socketAddress.Buffer.Span[.._socketAddress.Size];

    /// <summary>
    /// Parses a bind address. Null or empty means every IPv4 interface.
    /// </summary>
    /// <remarks>
    /// IPv4 must be a canonical dotted quad. Host names and the shorthand forms
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> allows ("127.1", "2130706433") are
    /// rejected, so a mistyped address fails instead of binding somewhere unexpected.
    /// IPv6 may be bracketed ("[::1]") and may carry a scope ("fe80::1%3"), but not a port.
    /// </remarks>
    public static bool TryParseBindAddress(string? bindAddress, out IPAddress address)
    {
        if (string.IsNullOrEmpty(bindAddress))
        {
            address = IPAddress.Any;
            return true;
        }

        address = IPAddress.None;

        if (bindAddress.Contains(':'))
        {
            var text = bindAddress.AsSpan();
            if (text.Length > 2 && text[0] == '[' && text[^1] == ']')
            {
                text = text[1..^1];
            }

            // Leftover brackets mean "[::1]:2593" or similar; IPAddress would drop the port.
            if (text.ContainsAny('[', ']') ||
                !IPAddress.TryParse(text, out var v6) ||
                v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            address = v6;
            return true;
        }

        if (!IPAddress.TryParse(bindAddress, out var v4) ||
            v4.AddressFamily != AddressFamily.InterNetwork ||
            !IsCanonical(v4, bindAddress))
        {
            return false;
        }

        address = v4;
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
        // sin6_scope_id, and on BSD the sin_len byte.
        var isIPv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        result = new NativeSocketAddress(
            new IPEndPoint(address, port).Serialize(),
            isIPv6 ? layout.InterNetworkV6 : layout.InterNetwork,
            isIPv6
        );
        return true;
    }

    // The round trip rejects every non-canonical spelling IPAddress.TryParse still accepts.
    private static bool IsCanonical(IPAddress address, string text)
    {
        Span<char> formatted = stackalloc char[15]; // "255.255.255.255"
        return address.TryFormat(formatted, out var written) && formatted[..written].SequenceEqual(text);
    }
}
