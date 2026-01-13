// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

#ifndef IORING_H
#define IORING_H

#include <stdint.h>
#include <winsock2.h>
#include <windows.h>

#ifdef IORING_EXPORTS
#define IORING_API __declspec(dllexport)
#else
#define IORING_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Forward declarations
typedef struct ioring ioring_t;

// Operation codes (matching io_uring IORING_OP_*)
typedef enum ioring_op {
    IORING_OP_NOP = 0,
    IORING_OP_POLL_ADD = 6,
    IORING_OP_POLL_REMOVE = 7,
    IORING_OP_ACCEPT = 13,
    IORING_OP_CONNECT = 16,
    IORING_OP_CLOSE = 19,
    IORING_OP_SEND = 26,
    IORING_OP_RECV = 27,
    IORING_OP_CANCEL = 28,
    IORING_OP_SHUTDOWN = 34,
} ioring_op_t;

// Poll masks (matching Linux poll(2))
typedef enum ioring_poll_mask {
    IORING_POLL_IN = 0x0001,
    IORING_POLL_PRI = 0x0002,
    IORING_POLL_OUT = 0x0004,
    IORING_POLL_ERR = 0x0008,
    IORING_POLL_HUP = 0x0010,
    IORING_POLL_NVAL = 0x0020,
    IORING_POLL_RDNORM = 0x0040,
    IORING_POLL_RDHUP = 0x2000,
} ioring_poll_mask_t;

// Submission queue entry (modeled after io_uring_sqe)
typedef struct ioring_sqe {
    uint8_t opcode;         // ioring_op_t
    uint8_t flags;          // Submission flags
    uint16_t ioprio;        // I/O priority
    int32_t fd;             // File descriptor (socket) or connection ID
    uint64_t off;           // Offset (union with addr2)
    uint64_t addr;          // Buffer address
    uint32_t len;           // Buffer length
    union {
        uint32_t poll_events;   // For poll operations
        uint32_t msg_flags;     // For send/recv
        uint32_t accept_flags;  // For accept
    };
    uint64_t user_data;     // User data returned with completion
    union {
        uint64_t addr3;         // Additional address
        struct {
            uint16_t buf_index;
            uint16_t buf_group;
        };
    };
} ioring_sqe_t;

// Completion queue entry (modeled after io_uring_cqe)
typedef struct ioring_cqe {
    uint64_t user_data;     // User data from submission
    int32_t res;            // Result (bytes transferred or negative error)
    uint32_t flags;         // Completion flags
} ioring_cqe_t;

// Backend type
typedef enum ioring_backend {
    IORING_BACKEND_NONE = 0,
    IORING_BACKEND_IORING = 1,  // Windows 11+ IoRing (not used - no socket support)
    IORING_BACKEND_RIO = 2,     // Windows 8+ Registered I/O
} ioring_backend_t;

// =============================================================================
// LEGACY API (synchronous fallback, for compatibility)
// =============================================================================

// Create/destroy ring (legacy - no RIO buffer registration)
IORING_API ioring_t* ioring_create(uint32_t entries);
IORING_API void ioring_destroy(ioring_t* ring);

// Get backend type
IORING_API ioring_backend_t ioring_get_backend(ioring_t* ring);

// Get queue state
IORING_API uint32_t ioring_sq_space_left(ioring_t* ring);
IORING_API uint32_t ioring_cq_ready(ioring_t* ring);

// Get next SQE slot (returns NULL if queue full)
IORING_API ioring_sqe_t* ioring_get_sqe(ioring_t* ring);

// Prepare operations (convenience functions that get SQE and fill it)
IORING_API void ioring_prep_poll_add(ioring_sqe_t* sqe, SOCKET fd, uint32_t poll_mask, uint64_t user_data);
IORING_API void ioring_prep_poll_remove(ioring_sqe_t* sqe, uint64_t user_data_to_cancel);
IORING_API void ioring_prep_accept(ioring_sqe_t* sqe, SOCKET fd, struct sockaddr* addr, int* addrlen, uint64_t user_data);
IORING_API void ioring_prep_connect(ioring_sqe_t* sqe, SOCKET fd, const struct sockaddr* addr, int addrlen, uint64_t user_data);
IORING_API void ioring_prep_send(ioring_sqe_t* sqe, SOCKET fd, const void* buf, uint32_t len, int flags, uint64_t user_data);
IORING_API void ioring_prep_recv(ioring_sqe_t* sqe, SOCKET fd, void* buf, uint32_t len, int flags, uint64_t user_data);
IORING_API void ioring_prep_close(ioring_sqe_t* sqe, SOCKET fd, uint64_t user_data);
IORING_API void ioring_prep_cancel(ioring_sqe_t* sqe, uint64_t user_data_to_cancel, uint64_t user_data);
IORING_API void ioring_prep_shutdown(ioring_sqe_t* sqe, SOCKET fd, int how, uint64_t user_data);

// Submit queued operations
IORING_API int ioring_submit(ioring_t* ring);
IORING_API int ioring_submit_and_wait(ioring_t* ring, uint32_t wait_nr);

// Peek completions (non-blocking)
IORING_API uint32_t ioring_peek_cqe(ioring_t* ring, ioring_cqe_t* cqes, uint32_t count);

// Wait for completions
IORING_API uint32_t ioring_wait_cqe(ioring_t* ring, ioring_cqe_t* cqes, uint32_t count, uint32_t min_complete, int timeout_ms);

// Mark completions as seen (advance CQ head)
IORING_API void ioring_cq_advance(ioring_t* ring, uint32_t count);

// Error handling
IORING_API int ioring_get_last_error(void);
IORING_API const char* ioring_strerror(int error);

// =============================================================================
// HIGH-PERFORMANCE RIO API (true async with registered buffers)
// =============================================================================

/*
 * RIO (Registered I/O) provides high-performance async socket I/O with:
 * - Pre-registered buffer pools (zero-copy between user/kernel)
 * - Batched submissions and completions (fewer syscalls)
 * - True async operations (don't block waiting for data)
 *
 * Usage pattern:
 * 1. Create ring with ioring_create_rio() specifying max connections and buffer sizes
 * 2. Accept connections normally (listener doesn't need RIO)
 * 3. Register each connected socket with ioring_rio_register() to get buffer pointers
 * 4. Post recv/send ops with ioring_prep_recv_registered/ioring_prep_send_registered
 * 5. Submit with ioring_submit(), wait/peek completions as usual
 * 6. Unregister socket before closing with ioring_rio_unregister()
 * 7. Destroy ring with ioring_destroy()
 */

// Create ring with RIO buffer pool pre-allocated
// - entries: SQ/CQ size (power of 2 recommended)
// - max_connections: maximum concurrent registered sockets
// - recv_buffer_size: size of receive buffer per connection
// - send_buffer_size: size of send buffer per connection
// Returns NULL on failure (check ioring_get_last_error())
IORING_API ioring_t* ioring_create_rio(
    uint32_t entries,
    uint32_t max_connections,
    uint32_t recv_buffer_size,
    uint32_t send_buffer_size
);

// Extended version with configurable outstanding operations
// - outstanding_per_socket: max outstanding ops per direction (recv/send) per socket
//   Default is 2 (enough for request-response patterns like echo)
//   Higher values (4-8) for pipelined protocols
//   CQ is auto-sized to max_connections * outstanding_per_socket * 2
IORING_API ioring_t* ioring_create_rio_ex(
    uint32_t entries,
    uint32_t max_connections,
    uint32_t recv_buffer_size,
    uint32_t send_buffer_size,
    uint32_t outstanding_per_socket
);

// Register a connected socket for RIO operations
// - socket: the connected socket (not the listener!)
// - recv_buf: output - pointer to pre-allocated receive buffer
// - send_buf: output - pointer to pre-allocated send buffer
// Returns: connection ID (>= 0) on success, -1 on error
// The returned buffers are valid until ioring_rio_unregister() is called
IORING_API int ioring_rio_register(
    ioring_t* ring,
    SOCKET socket,
    void** recv_buf,
    void** send_buf
);

// Unregister a connection (call BEFORE closing the socket)
// This frees the connection slot for reuse
IORING_API void ioring_rio_unregister(ioring_t* ring, int conn_id);

// Get buffer sizes for this ring
IORING_API uint32_t ioring_rio_get_recv_buffer_size(ioring_t* ring);
IORING_API uint32_t ioring_rio_get_send_buffer_size(ioring_t* ring);

// Prepare receive on registered connection
// Data will be placed in the recv_buf returned from ioring_rio_register()
IORING_API void ioring_prep_recv_registered(
    ioring_sqe_t* sqe,
    int conn_id,        // Connection ID from ioring_rio_register()
    uint32_t max_len,   // Maximum bytes to receive (up to recv_buffer_size)
    uint64_t user_data  // User data returned with completion
);

// Prepare send on registered connection
// Data must already be written to send_buf from ioring_rio_register()
IORING_API void ioring_prep_send_registered(
    ioring_sqe_t* sqe,
    int conn_id,        // Connection ID from ioring_rio_register()
    uint32_t len,       // Bytes to send (already in send_buf)
    uint64_t user_data  // User data returned with completion
);

// Check if ring was created with RIO support
IORING_API int ioring_is_rio(ioring_t* ring);

// Get maximum connections for RIO ring
IORING_API uint32_t ioring_rio_get_max_connections(ioring_t* ring);

// Get number of active (registered) connections
IORING_API uint32_t ioring_rio_get_active_connections(ioring_t* ring);

// Diagnostic: Get connection state for debugging
// Returns packed value: (active << 0) | (rq_valid << 1) | (socket_valid << 2)
// Value 7 = fully valid (active + rq_valid + socket_valid)
// Negative value indicates error
IORING_API int ioring_rio_get_conn_state(ioring_t* ring, int conn_id);

// Diagnostic: Check if RIOReceive parameters are valid (does NOT actually post)
// Returns: 1 if all parameters valid, negative error code if invalid
IORING_API int ioring_rio_check_recv_params(ioring_t* ring, int conn_id);

// Diagnostic: Get number of completions waiting in user-space CQ
IORING_API int ioring_rio_peek_cq_count(ioring_t* ring);

// Diagnostic: Get RIO recv stats (packed: recv_calls | recv_success << 16)
IORING_API uint32_t ioring_rio_get_recv_stats(void);

// Diagnostic: Get last RIOReceive error
IORING_API int ioring_rio_get_last_recv_error(void);

// Diagnostic: Get dequeue stats (packed: dequeue_calls | dequeue_total << 16)
IORING_API uint32_t ioring_rio_get_dequeue_stats(void);

// Diagnostic: Reset all counters
IORING_API void ioring_rio_reset_stats(void);

// Diagnostic: Get build version (to verify correct DLL is loaded)
IORING_API int ioring_get_build_version(void);

// Diagnostic: Get the CQ handle being used
IORING_API uint64_t ioring_rio_get_cq_handle(ioring_t* ring);

// Diagnostic: Get the RQ handle that was created (for AcceptEx sockets)
IORING_API uint64_t ioring_rio_get_rq_created(void);

// Diagnostic: Get the RQ handle used in RIOReceive
IORING_API uint64_t ioring_rio_get_rq_used(void);

// Diagnostic: Get the buffer ID used in RIOReceive
IORING_API uint64_t ioring_rio_get_buf_id_used(void);

// Diagnostic: Get the buffer offset used in RIOReceive
IORING_API uint32_t ioring_rio_get_buf_offset_used(void);

// Diagnostic: Verify buffer registration is valid for the ring
IORING_API int ioring_rio_check_buffer_registration(ioring_t* ring);

// Diagnostic: Get the connection slot that was created (during AcceptEx)
IORING_API int ioring_rio_get_conn_slot_created(void);

// Diagnostic: Get the connection ID used in RIOReceive
IORING_API int ioring_rio_get_conn_id_used(void);

// Diagnostic: Get the CQ used when creating the RQ (to compare with polling CQ)
IORING_API uint64_t ioring_rio_get_cq_at_rq_create(void);

// Diagnostic: Check if socket has data available using WSAPoll
// Returns: 1 if readable, 0 if not, -error on error
IORING_API int ioring_rio_check_socket_readable(ioring_t* ring, int conn_id);

// Diagnostic: Get the raw socket handle stored for a connection
IORING_API uint64_t ioring_rio_get_conn_socket(ioring_t* ring, int conn_id);

// Diagnostic: Verify socket is valid using getsockopt (SO_TYPE)
// Returns: socket type (SOCK_STREAM=1, SOCK_DGRAM=2) if valid, -error if invalid
IORING_API int ioring_rio_verify_socket_valid(ioring_t* ring, int conn_id);

// Diagnostic: Check how much data is pending on the socket using FIONREAD
// Returns: bytes available, or -error on failure
IORING_API int ioring_rio_get_socket_pending_bytes(ioring_t* ring, int conn_id);

// Diagnostic: Get the buffer ID registered with the ring (to compare with bufIdUsed)
IORING_API uint64_t ioring_rio_get_buffer_id(ioring_t* ring);

// Diagnostic: Get SO_UPDATE_ACCEPT_CONTEXT result (0=success, >0=WSA error)
IORING_API int ioring_rio_get_update_accept_ctx_result(void);

// Diagnostic: Get the listener socket used in SO_UPDATE_ACCEPT_CONTEXT
IORING_API uint64_t ioring_rio_get_listen_socket_used(void);

// Diagnostic: Get the accept socket used in SO_UPDATE_ACCEPT_CONTEXT
IORING_API uint64_t ioring_rio_get_accept_socket_at_update(void);

// Diagnostic: Get RIONotify call count
IORING_API int ioring_rio_get_notify_calls(void);

// Diagnostic: Check if any data was written to the receive buffer
// Returns: first 4 bytes of buffer as int (e.g., "Hell" = 0x6C6C6548)
IORING_API int ioring_rio_peek_recv_buffer(ioring_t* ring, int conn_id);

// =============================================================================
// RIO AcceptEx Support (for server-side RIO)
// =============================================================================

/*
 * IMPORTANT: Sockets returned by accept() do NOT inherit WSA_FLAG_REGISTERED_IO.
 * For server-side RIO, you must:
 * 1. Create sockets with ioring_rio_create_accept_socket()
 * 2. Use AcceptEx (from Windows) to accept into these pre-created sockets
 * 3. Then register them with ioring_rio_register()
 *
 * Example pattern:
 *   // Create accept socket pool
 *   SOCKET acceptSock = ioring_rio_create_accept_socket();
 *
 *   // Use AcceptEx (get via WSAIoctl with WSAID_ACCEPTEX)
 *   AcceptEx(listenSock, acceptSock, buffer, 0, addrLen, addrLen, &bytes, &overlapped);
 *
 *   // After AcceptEx completes, update socket context
 *   setsockopt(acceptSock, SOL_SOCKET, SO_UPDATE_ACCEPT_CONTEXT,
 *              (char*)&listenSock, sizeof(listenSock));
 *
 *   // Now register for RIO
 *   ioring_rio_register(ring, acceptSock, &recvBuf, &sendBuf);
 */

// Create a socket suitable for AcceptEx that has WSA_FLAG_REGISTERED_IO
// Returns INVALID_SOCKET on failure
IORING_API SOCKET ioring_rio_create_accept_socket(void);

// Connect a socket to the specified address (for testing RIO on client sockets)
// Returns 0 on success, -1 on failure (call ioring_get_last_error for details)
IORING_API int ioring_connect_socket(SOCKET sock, const char* ip_address, uint16_t port);

// Get AcceptEx function pointer (convenience helper)
// Returns NULL on failure
IORING_API void* ioring_get_acceptex(SOCKET listen_socket);

// =============================================================================
// RIO Listener Support (library-owned sockets to avoid IOCP conflicts)
// =============================================================================

/*
 * For RIO mode, the library should own the listener socket to avoid IOCP
 * association conflicts with .NET's runtime. Use these APIs:
 *
 *   // Create and start listening
 *   SOCKET listener = ioring_rio_create_listener(ring, "0.0.0.0", 5000, 128);
 *
 *   // Queue accept operation (uses AcceptEx internally)
 *   ioring_prep_accept(sqe, listener, NULL, NULL, user_data);
 *
 *   // When done
 *   ioring_rio_close_listener(ring, listener);
 */

// Create a listener socket owned by the ring (automatically associated with ring's IOCP)
// - ring: the RIO ring to associate with
// - bind_addr: IP address to bind to (e.g., "0.0.0.0" or "127.0.0.1")
// - port: port number to listen on
// - backlog: listen queue size (e.g., SOMAXCONN or 128)
// Returns: listener socket on success, INVALID_SOCKET on failure
IORING_API SOCKET ioring_rio_create_listener(
    ioring_t* ring,
    const char* bind_addr,
    uint16_t port,
    int backlog
);

// Close a listener socket created by ioring_rio_create_listener
// This properly cleans up any pending AcceptEx operations
IORING_API void ioring_rio_close_listener(ioring_t* ring, SOCKET listener);

// Get the number of owned listeners
IORING_API uint32_t ioring_rio_get_listener_count(ioring_t* ring);

#ifdef __cplusplus
}
#endif

#endif // IORING_H
