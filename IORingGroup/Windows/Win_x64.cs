// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

using System.Runtime.InteropServices;

namespace System.Network.Windows;

/// <summary>
/// P/Invoke bindings for the ioring.dll native library.
/// </summary>
public static partial class Win_x64
{
    private const string LibraryName = "ioring.dll";

    /// <summary>
    /// Backend type returned by ioring_get_backend.
    /// </summary>
    public enum IORingBackend : int
    {
        None = 0,
        IoRing = 1,  // Windows 11+ IoRing
        RIO = 2,     // Windows 8+ Registered I/O
    }

    /// <summary>
    /// Completion queue entry structure matching C ioring_cqe_t.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IORingCqe
    {
        public ulong UserData;
        public int Res;
        public uint Flags;
    }

    [LibraryImport(LibraryName)]
    public static partial void ioring_destroy(nint ring);

    [LibraryImport(LibraryName)]
    public static partial IORingBackend ioring_get_backend(nint ring);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_sq_space_left(nint ring);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_cq_ready(nint ring);

    [LibraryImport(LibraryName)]
    public static partial nint ioring_get_sqe(nint ring);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_poll_add(nint sqe, nint fd, uint pollMask, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_poll_remove(nint sqe, ulong userDataToCancel);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_accept(nint sqe, nint fd, nint addr, nint addrlen, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_connect(nint sqe, nint fd, nint addr, int addrlen, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_send(nint sqe, nint fd, nint buf, uint len, int flags, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_recv(nint sqe, nint fd, nint buf, uint len, int flags, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_close(nint sqe, nint fd, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_cancel(nint sqe, ulong userDataToCancel, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_shutdown(nint sqe, nint fd, int how, ulong userData);

    [LibraryImport(LibraryName)]
    public static partial int ioring_submit(nint ring);

    [LibraryImport(LibraryName)]
    public static partial int ioring_submit_and_wait(nint ring, uint waitNr);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_peek_cqe(nint ring, nint cqes, uint count);

    [LibraryImport(LibraryName)]
    public static partial uint ioring_wait_cqe(nint ring, nint cqes, uint count, uint minComplete, int timeoutMs);

    [LibraryImport(LibraryName)]
    public static partial void ioring_cq_advance(nint ring, uint count);

    [LibraryImport(LibraryName)]
    public static partial int ioring_get_last_error();

    // =============================================================================
    // High-Performance RIO API
    // =============================================================================

    /// <summary>
    /// Create a ring with RIO support and configurable outstanding operations.
    /// </summary>
    /// <param name="entries">SQ/CQ size (power of 2 recommended)</param>
    /// <param name="maxConnections">Maximum concurrent registered sockets</param>
    /// <param name="recvBufferSize">Unused - buffers now managed externally</param>
    /// <param name="sendBufferSize">Unused - buffers now managed externally</param>
    /// <param name="outstandingPerSocket">Max outstanding ops per direction per socket (default 2, max 16)</param>
    /// <returns>Ring handle, or IntPtr.Zero on failure</returns>
    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial nint ioring_create_rio_ex(uint entries, uint maxConnections, uint recvBufferSize, uint sendBufferSize, uint outstandingPerSocket);

    /// <summary>
    /// Register a connected socket for RIO operations.
    /// </summary>
    /// <param name="ring">Ring handle from ioring_create_rio</param>
    /// <param name="socket">Connected socket handle</param>
    /// <returns>Connection ID (>= 0) on success, -1 on error</returns>
    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial int ioring_rio_register(nint ring, nint socket);

    /// <summary>
    /// Unregister a connection (call BEFORE closing the socket).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void ioring_rio_unregister(nint ring, int connId);

    /// <summary>
    /// Check if ring was created with RIO support.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_is_rio(nint ring);

    /// <summary>
    /// Get number of active (registered) connections.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_active_connections(nint ring);

    /// <summary>
    /// Get the socket handle for a connection ID.
    /// Returns INVALID_SOCKET (-1) if the connection is not active or conn_id is invalid.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial nint ioring_rio_get_socket(nint ring, int connId);

    /// <summary>
    /// Create a regular accept socket (can use legacy recv/send, CANNOT use RIO).
    /// Use this for legacy I/O mode.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial nint ioring_create_accept_socket();

    // =============================================================================
    // RIO AcceptEx Support (for server-side RIO)
    // =============================================================================

    /// <summary>
    /// Create a socket suitable for AcceptEx that has WSA_FLAG_REGISTERED_IO.
    /// Returns INVALID_SOCKET (-1) on failure.
    /// </summary>
    /// <remarks>
    /// IMPORTANT: Sockets returned by accept() do NOT inherit WSA_FLAG_REGISTERED_IO.
    /// For server-side RIO, you must:
    /// 1. Create sockets with CreateAcceptSocket()
    /// 2. Use AcceptEx to accept into these pre-created sockets
    /// 3. After AcceptEx completes, call setsockopt with SO_UPDATE_ACCEPT_CONTEXT
    /// 4. Then register them with ioring_rio_register()
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial nint ioring_rio_create_accept_socket();

    /// <summary>
    /// Get AcceptEx function pointer (convenience helper).
    /// Returns null (IntPtr.Zero) on failure.
    /// </summary>
    /// <param name="listenSocket">The listening socket (can be IntPtr.Zero to use a temp socket)</param>
    [LibraryImport(LibraryName)]
    public static partial nint ioring_get_acceptex(nint listenSocket);

    // =============================================================================
    // RIO Listener Support (library-owned sockets to avoid IOCP conflicts)
    // =============================================================================

    /// <summary>
    /// Create a listener socket owned by the ring (automatically associated with ring's IOCP).
    /// </summary>
    /// <param name="ring">Ring handle from ioring_create_rio</param>
    /// <param name="bindAddr">IP address to bind to (e.g., "0.0.0.0" or "127.0.0.1")</param>
    /// <param name="port">Port number to listen on</param>
    /// <param name="backlog">Listen queue size (e.g., 128 or SOMAXCONN)</param>
    /// <returns>Listener socket on success, INVALID_SOCKET (-1) on failure</returns>
    /// <remarks>
    /// For RIO mode, the library should own the listener socket to avoid IOCP
    /// association conflicts with .NET's runtime. Use this API instead of creating
    /// a Socket in C# and passing its handle.
    /// </remarks>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint ioring_rio_create_listener(nint ring, string bindAddr, ushort port, int backlog);

    /// <summary>
    /// Close a listener socket created by ioring_rio_create_listener.
    /// This properly cleans up any pending AcceptEx operations.
    /// </summary>
    /// <param name="ring">Ring handle from ioring_create_rio</param>
    /// <param name="listener">Listener socket to close</param>
    [LibraryImport(LibraryName)]
    public static partial void ioring_rio_close_listener(nint ring, nint listener);

    /// <summary>
    /// Configures an accepted socket with optimal settings (TCP_NODELAY, non-blocking, etc.).
    /// </summary>
    /// <param name="socket">Socket to configure</param>
    [LibraryImport(LibraryName)]
    public static partial void ioring_rio_configure_socket(nint socket);

    // =============================================================================
    // RIO External Buffer Support (for zero-copy from user-owned memory like Pipe)
    // =============================================================================

    /// <summary>
    /// Register external buffer (e.g., VirtualAlloc'd Pipe memory) with RIO.
    /// </summary>
    /// <param name="ring">Ring handle from ioring_create_rio</param>
    /// <param name="buffer">Pointer to buffer memory (e.g., Pipe.GetBufferPointer())</param>
    /// <param name="length">Size in bytes (for double-mapped buffers, use 2x physical size)</param>
    /// <returns>Buffer ID on success (>= 0), -1 on failure</returns>
    /// <remarks>
    /// External buffers allow zero-copy I/O from user-owned memory. This is useful
    /// for circular buffers like ModernUO's Pipe where you want to send directly
    /// from the application's buffer without copying to RIO's internal buffers.
    /// The buffer must remain valid until ioring_rio_unregister_external_buffer() is called.
    /// </remarks>
    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial int ioring_rio_register_external_buffer(nint ring, nint buffer, uint length);

    /// <summary>
    /// Unregister an external buffer.
    /// </summary>
    /// <param name="ring">Ring handle from ioring_create_rio</param>
    /// <param name="bufferId">Buffer ID returned by ioring_rio_register_external_buffer</param>
    [LibraryImport(LibraryName)]
    public static partial void ioring_rio_unregister_external_buffer(nint ring, int bufferId);

    /// <summary>
    /// Prepare send from external registered buffer.
    /// </summary>
    /// <param name="sqe">Submission queue entry pointer</param>
    /// <param name="connId">Connection ID from ioring_rio_register</param>
    /// <param name="bufferId">External buffer ID from ioring_rio_register_external_buffer</param>
    /// <param name="offset">Offset within the registered buffer to start sending from</param>
    /// <param name="len">Number of bytes to send</param>
    /// <param name="userData">User data returned with completion</param>
    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_send_external(nint sqe, int connId, int bufferId, uint offset, uint len, ulong userData);

    /// <summary>
    /// Prepare a receive into an external buffer for zero-copy I/O.
    /// </summary>
    /// <param name="sqe">Submission queue entry pointer</param>
    /// <param name="connId">Connection ID from ioring_rio_register</param>
    /// <param name="bufferId">External buffer ID from ioring_rio_register_external_buffer</param>
    /// <param name="offset">Offset within the registered buffer to receive into</param>
    /// <param name="len">Maximum number of bytes to receive</param>
    /// <param name="userData">User data returned with completion</param>
    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_recv_external(nint sqe, int connId, int bufferId, uint offset, uint len, ulong userData);

    /// <summary>
    /// Get the number of registered external buffers.
    /// </summary>
    /// <param name="ring">Ring handle</param>
    /// <returns>Number of registered external buffers</returns>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_external_buffer_count(nint ring);

    /// <summary>
    /// Get the maximum number of external buffers (based on max_connections * 3).
    /// </summary>
    /// <param name="ring">Ring handle</param>
    /// <returns>Maximum number of external buffers</returns>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_max_external_buffers(nint ring);

    // =============================================================================
    // Winsock2 Helpers
    // =============================================================================

    /// <summary>
    /// Close a socket handle.
    /// </summary>
    /// <param name="socket">The socket handle to close</param>
    /// <returns>0 on success, SOCKET_ERROR (-1) on failure</returns>
    [LibraryImport("ws2_32.dll")]
    public static partial int closesocket(nint socket);

    /// <summary>
    /// WSAPOLLFD structure for WSAPoll.
    /// Must match Windows WSAPOLLFD layout exactly.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct WSAPOLLFD
    {
        public nint fd;      // SOCKET = UINT_PTR = 8 bytes on x64
        public short events;  // SHORT = 2 bytes
        public short revents; // SHORT = 2 bytes
        // Total: 12 bytes on x64
    }

    /// <summary>
    /// Set the last Winsock error.
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    internal static partial void WSASetLastError(int iError);

    /// <summary>
    /// Poll events.
    /// </summary>
    public const short POLLIN = 0x0001;
    public const short POLLOUT = 0x0004;
    public const short POLLERR = 0x0008;

    /// <summary>
    /// Poll a socket for readability.
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    public static partial int WSAPoll(ref WSAPOLLFD fdarray, uint nfds, int timeout);

    /// <summary>
    /// Receive data from a socket (non-RIO).
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    public static partial int recv(nint socket, nint buf, int len, int flags);

    /// <summary>
    /// Get the last Winsock error.
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    internal static partial int WSAGetLastError();

    /// <summary>
    /// Get socket option (simplified for SO_TYPE check).
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    public static partial int getsockopt(nint socket, int level, int optname, nint optval, ref int optlen);

    /// <summary>
    /// Get the local address of a socket.
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    public static partial int getsockname(nint socket, nint addr, ref int addrlen);

    // Socket option constants
    public const int SOL_SOCKET = 0xFFFF;
    public const int SO_TYPE = 0x1008;

    // Address family
    public const short AF_INET = 2;

    /// <summary>
    /// IPv4 socket address structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct sockaddr_in
    {
        public short sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    /// <summary>
    /// Convert IP address string to network byte order.
    /// </summary>
    [LibraryImport("ws2_32.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int inet_pton(short family, string src, out uint dst);

    /// <summary>
    /// Convert host short to network byte order.
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    public static partial ushort htons(ushort hostshort);

    /// <summary>
    /// Connect a socket to the specified address.
    /// </summary>
    [LibraryImport("ws2_32.dll")]
    public static partial int connect(nint socket, ref sockaddr_in name, int namelen);
}
