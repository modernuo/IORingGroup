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
    /// Submission queue entry structure matching C ioring_sqe_t.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IORingSqe
    {
        public byte Opcode;
        public byte Flags;
        public ushort IoPrio;
        public int Fd;
        public ulong Off;
        public ulong Addr;
        public uint Len;
        public uint OpFlags;  // Union: poll_events, msg_flags, accept_flags
        public ulong UserData;
        public ulong Addr3;
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

    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial nint ioring_create(uint entries);

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
    /// Create a ring with RIO buffer pool pre-allocated for true async I/O.
    /// </summary>
    /// <param name="entries">SQ/CQ size (power of 2 recommended)</param>
    /// <param name="maxConnections">Maximum concurrent registered sockets</param>
    /// <param name="recvBufferSize">Size of receive buffer per connection</param>
    /// <param name="sendBufferSize">Size of send buffer per connection</param>
    /// <returns>Ring handle, or IntPtr.Zero on failure</returns>
    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial nint ioring_create_rio(uint entries, uint maxConnections, uint recvBufferSize, uint sendBufferSize);

    /// <summary>
    /// Create a ring with RIO buffer pool and configurable outstanding operations.
    /// </summary>
    /// <param name="entries">SQ/CQ size (power of 2 recommended)</param>
    /// <param name="maxConnections">Maximum concurrent registered sockets</param>
    /// <param name="recvBufferSize">Size of receive buffer per connection</param>
    /// <param name="sendBufferSize">Size of send buffer per connection</param>
    /// <param name="outstandingPerSocket">Max outstanding ops per direction per socket (default 2, max 16)</param>
    /// <returns>Ring handle, or IntPtr.Zero on failure</returns>
    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial nint ioring_create_rio_ex(uint entries, uint maxConnections, uint recvBufferSize, uint sendBufferSize, uint outstandingPerSocket);

    /// <summary>
    /// Register a connected socket for RIO operations.
    /// </summary>
    /// <param name="ring">Ring handle from ioring_create_rio</param>
    /// <param name="socket">Connected socket handle</param>
    /// <param name="recvBuf">Output: pointer to pre-allocated receive buffer</param>
    /// <param name="sendBuf">Output: pointer to pre-allocated send buffer</param>
    /// <returns>Connection ID (>= 0) on success, -1 on error</returns>
    [LibraryImport(LibraryName, SetLastError = true)]
    public static partial int ioring_rio_register(nint ring, nint socket, out nint recvBuf, out nint sendBuf);

    /// <summary>
    /// Unregister a connection (call BEFORE closing the socket).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void ioring_rio_unregister(nint ring, int connId);

    /// <summary>
    /// Get receive buffer size for this RIO ring.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_recv_buffer_size(nint ring);

    /// <summary>
    /// Get send buffer size for this RIO ring.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_send_buffer_size(nint ring);

    /// <summary>
    /// Check if ring was created with RIO support.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_is_rio(nint ring);

    /// <summary>
    /// Get maximum connections for RIO ring.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_max_connections(nint ring);

    /// <summary>
    /// Get number of active (registered) connections.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_active_connections(nint ring);

    /// <summary>
    /// Diagnostic: Get connection state for debugging.
    /// Returns packed value: (active) | (rq_valid &lt;&lt; 1) | (socket_valid &lt;&lt; 2)
    /// Value 7 = fully valid (active + rq_valid + socket_valid)
    /// Negative value indicates error.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_get_conn_state(nint ring, int connId);

    /// <summary>
    /// Diagnostic: Check if RIOReceive parameters are valid (does NOT actually post).
    /// Returns: 1 if all parameters valid, negative error code if invalid.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_check_recv_params(nint ring, int connId);

    /// <summary>
    /// Diagnostic: Get number of completions waiting in user-space CQ.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_peek_cq_count(nint ring);

    /// <summary>
    /// Diagnostic: Get RIO recv stats (packed: recv_calls | recv_success &lt;&lt; 16).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_recv_stats();

    /// <summary>
    /// Diagnostic: Get last RIOReceive error.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_get_last_recv_error();

    /// <summary>
    /// Diagnostic: Get dequeue stats (packed: dequeue_calls | dequeue_total &lt;&lt; 16).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_dequeue_stats();

    /// <summary>
    /// Diagnostic: Reset all diagnostic counters.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void ioring_rio_reset_stats();

    /// <summary>
    /// Diagnostic: Get build version (to verify correct DLL is loaded).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_get_build_version();

    /// <summary>
    /// Diagnostic: Get the CQ handle being used.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_cq_handle(nint ring);

    /// <summary>
    /// Diagnostic: Get the RQ handle that was created (for AcceptEx sockets).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_rq_created();

    /// <summary>
    /// Diagnostic: Get the RQ handle used in RIOReceive.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_rq_used();

    /// <summary>
    /// Diagnostic: Get the buffer ID used in RIOReceive.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_buf_id_used();

    /// <summary>
    /// Diagnostic: Get the buffer offset used in RIOReceive.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_buf_offset_used();

    /// <summary>
    /// Diagnostic: Verify buffer registration is valid for the ring.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_check_buffer_registration(nint ring);

    /// <summary>
    /// Diagnostic: Get the connection slot that was created (during AcceptEx).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_get_conn_slot_created();

    /// <summary>
    /// Diagnostic: Get the connection ID used in RIOReceive.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_get_conn_id_used();

    /// <summary>
    /// Diagnostic: Get the CQ used when creating the RQ (to compare with polling CQ).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_cq_at_rq_create();

    /// <summary>
    /// Diagnostic: Check if socket has data available using WSAPoll.
    /// Returns: 1 if readable, 0 if not, -error on error.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_check_socket_readable(nint ring, int connId);

    /// <summary>
    /// Diagnostic: Get the raw socket handle stored for a connection.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_conn_socket(nint ring, int connId);

    /// <summary>
    /// Diagnostic: Verify socket is valid using getsockopt (SO_TYPE).
    /// Returns: socket type (1=SOCK_STREAM, 2=SOCK_DGRAM) if valid, -error if invalid.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_verify_socket_valid(nint ring, int connId);

    /// <summary>
    /// Diagnostic: Check how much data is pending on the socket using FIONREAD.
    /// Returns: bytes available, or -error on failure.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_get_socket_pending_bytes(nint ring, int connId);

    /// <summary>
    /// Diagnostic: Get the buffer ID registered with the ring (to compare with bufIdUsed).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_buffer_id(nint ring);

    /// <summary>
    /// Diagnostic: Get SO_UPDATE_ACCEPT_CONTEXT result (0=success, >0=WSA error).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_get_update_accept_ctx_result();

    /// <summary>
    /// Diagnostic: Get the listener socket used in SO_UPDATE_ACCEPT_CONTEXT.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_listen_socket_used();

    /// <summary>
    /// Diagnostic: Get the accept socket used in SO_UPDATE_ACCEPT_CONTEXT.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial ulong ioring_rio_get_accept_socket_at_update();

    /// <summary>
    /// Diagnostic: Get RIONotify call count.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_get_notify_calls();

    /// <summary>
    /// Diagnostic: Check if any data was written to the receive buffer.
    /// Returns: first 4 bytes of buffer as int (e.g., "Hell" = 0x6C6C6548).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_peek_recv_buffer(nint ring, int connId);

    /// <summary>
    /// Prepare receive on registered connection.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_recv_registered(nint sqe, int connId, uint maxLen, ulong userData);

    /// <summary>
    /// Prepare send on registered connection.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void ioring_prep_send_registered(nint sqe, int connId, uint len, ulong userData);

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
    /// Get the number of owned listener sockets.
    /// </summary>
    /// <param name="ring">Ring handle</param>
    /// <returns>Number of listener sockets created by ioring_rio_create_listener</returns>
    [LibraryImport(LibraryName)]
    public static partial uint ioring_rio_get_listener_count(nint ring);

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
    /// Connect a socket to the specified address.
    /// </summary>
    /// <param name="socket">Socket to connect</param>
    /// <param name="ipAddress">IP address string (e.g., "127.0.0.1")</param>
    /// <param name="port">Port number</param>
    /// <returns>0 on success, -1 on failure</returns>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ioring_connect_socket(nint socket, string ipAddress, ushort port);

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
    public static partial void WSASetLastError(int iError);

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
    public static partial int WSAGetLastError();

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
}
