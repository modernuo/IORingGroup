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
    /// Check if RIO is properly initialized.
    /// Returns 0 if all RIO functions are available, negative error code otherwise.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_check_init();

    /// <summary>
    /// Test complete RIO setup with a fresh socket we create internally.
    /// Returns 0 on success, negative error code on failure.
    /// Use ioring_get_last_error() to get the WSA error code.
    /// </summary>
    /// <returns>
    /// 0: Success
    /// -1: RIO init failed
    /// -2: Socket creation failed
    /// -3: Bind failed
    /// -4: Buffer allocation failed
    /// -5: Buffer registration failed
    /// -6: Completion queue creation failed
    /// -7: Request queue creation failed
    /// </returns>
    [LibraryImport(LibraryName)]
    public static partial int ioring_rio_test_setup();

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
    // Winsock2 Helpers
    // =============================================================================

    /// <summary>
    /// Close a socket handle.
    /// </summary>
    /// <param name="socket">The socket handle to close</param>
    /// <returns>0 on success, SOCKET_ERROR (-1) on failure</returns>
    [LibraryImport("ws2_32.dll")]
    public static partial int closesocket(nint socket);
}
