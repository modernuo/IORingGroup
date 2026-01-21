// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

#ifndef IORING_H
#define IORING_H

#include <stdint.h>
#include <winsock2.h>

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

// =============================================================================
// Core API
// =============================================================================

// Destroy a ring created by ioring_create_rio_ex()
IORING_API void ioring_destroy(ioring_t* ring);

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

// =============================================================================
// HIGH-PERFORMANCE RIO API (true async with external buffers)
// =============================================================================

/*
 * RIO (Registered I/O) provides high-performance async socket I/O with:
 * - Externally registered buffers (zero-copy between user/kernel)
 * - Batched submissions and completions (fewer syscalls)
 * - True async operations (don't block waiting for data)
 *
 * Usage pattern:
 * 1. Create ring with ioring_create_rio_ex() specifying max connections
 * 2. Register external buffers with ioring_rio_register_external_buffer()
 * 3. Register each connected socket with ioring_rio_register()
 * 4. Post recv/send ops with ioring_prep_recv_external/ioring_prep_send_external
 * 5. Submit with ioring_submit(), wait/peek completions as usual
 * 6. Unregister socket before closing with ioring_rio_unregister()
 * 7. Destroy ring with ioring_destroy()
 *
 * NOTE: Buffers are managed externally (e.g., via IORingBufferPool in C#).
 */

// Create ring with RIO support
// - max_connections: max concurrent registered sockets
// SQ/CQ auto-sized to max_connections * 2 (1 recv + 1 send per connection)
IORING_API ioring_t* ioring_create_rio_ex(uint32_t max_connections);

// Register a connected socket for RIO operations
// - socket: the connected socket (not the listener!)
// Returns: connection ID (>= 0) on success, -1 on error
IORING_API int ioring_rio_register(
    ioring_t* ring,
    SOCKET socket
);

// Unregister a connection (call BEFORE closing the socket)
// This frees the connection slot for reuse
IORING_API void ioring_rio_unregister(ioring_t* ring, int conn_id);

// =============================================================================
// RIO Listener Support
// =============================================================================

/*
 * For RIO mode, the library owns the listener socket and uses AcceptEx internally
 * to create RIO-compatible accepted sockets. Usage:
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

// Create a listener socket with WSA_FLAG_REGISTERED_IO
// - ring: the RIO ring
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

// Configure an accepted socket with optimal settings (TCP_NODELAY, non-blocking, etc.)
// Call this on sockets from accept completions if not using PrepareAccept
IORING_API void ioring_rio_configure_socket(SOCKET socket);

// Close a socket gracefully (sends FIN, then closes)
// This ensures the client receives proper disconnect notification
IORING_API void ioring_rio_close_socket_graceful(SOCKET socket);

// =============================================================================
// RIO External Buffer Support (for zero-copy from user-owned memory like Pipe)
// =============================================================================

/*
 * External buffers allow zero-copy I/O from user-owned memory (e.g., ModernUO's
 * Pipe circular buffer). This avoids copying data between the application's
 * buffers and RIO's internal buffers.
 *
 * Usage pattern:
 *   // Create a Pipe (double-mapped circular buffer)
 *   Pipe sendPipe = new Pipe(256 * 1024);
 *
 *   // Register its memory with RIO (full virtual range = 2x physical for double-mapping)
 *   int bufId = ioring_rio_register_external_buffer(ring, pipe.GetBufferPointer(),
 *                                                   pipe.GetVirtualSize());
 *
 *   // Write data to pipe using normal Pipe.Writer
 *   var span = sendPipe.Writer.AvailableToWrite();
 *   // ... write packet data ...
 *   sendPipe.Writer.Advance(bytesWritten);
 *
 *   // Send directly from pipe memory (zero-copy!)
 *   var toSend = sendPipe.Reader.AvailableToRead();
 *   int offset = sendPipe.Reader.GetReadOffset();
 *   ioring_prep_send_external(sqe, conn_id, bufId, offset, toSend.Length, user_data);
 *
 *   // After send completes, advance the pipe
 *   sendPipe.Reader.Advance(bytesSent);
 *
 *   // Cleanup
 *   ioring_rio_unregister_external_buffer(ring, bufId);
 */

// Register external buffer (e.g., VirtualAlloc'd Pipe memory) with RIO
// - ring: the RIO ring
// - buffer: pointer to buffer memory (e.g., Pipe.GetBufferPointer())
// - length: size in bytes (for double-mapped buffers, use 2x physical size)
// Returns: buffer ID on success (>= 0), -1 on failure
// NOTE: The buffer must remain valid until ioring_rio_unregister_external_buffer() is called
IORING_API int ioring_rio_register_external_buffer(
    ioring_t* ring,
    void* buffer,
    uint32_t length
);

// Unregister an external buffer
// - ring: the RIO ring
// - buffer_id: ID returned by ioring_rio_register_external_buffer()
IORING_API void ioring_rio_unregister_external_buffer(ioring_t* ring, int buffer_id);

// Prepare send from external registered buffer
// - sqe: SQE to fill
// - conn_id: connection ID from ioring_rio_register()
// - buffer_id: external buffer ID from ioring_rio_register_external_buffer()
// - offset: offset within the registered buffer to start sending from
// - len: number of bytes to send
// - user_data: user data returned with completion
IORING_API void ioring_prep_send_external(
    ioring_sqe_t* sqe,
    int conn_id,
    int buffer_id,
    uint32_t offset,
    uint32_t len,
    uint64_t user_data
);

// Prepare a recv into an external buffer (e.g., VirtualAlloc'd Pipe memory)
// Parameters:
// - sqe: submission queue entry
// - conn_id: connection ID from ioring_rio_register
// - buffer_id: external buffer ID from ioring_rio_register_external_buffer
// - offset: byte offset within the external buffer
// - len: maximum number of bytes to receive
// - user_data: user data returned with completion
IORING_API void ioring_prep_recv_external(
    ioring_sqe_t* sqe,
    int conn_id,
    int buffer_id,
    uint32_t offset,
    uint32_t len,
    uint64_t user_data
);

// Get the number of registered external buffers
IORING_API uint32_t ioring_rio_get_external_buffer_count(ioring_t* ring);

// Get the maximum number of external buffers (based on max_connections * 3)
IORING_API uint32_t ioring_rio_get_max_external_buffers(ioring_t* ring);

#ifdef __cplusplus
}
#endif

#endif // IORING_H
