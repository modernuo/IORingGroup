// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

#include "ioring.h"

#include <ws2tcpip.h>
#include <mswsock.h>
#include <stdlib.h>
#include <string.h>
#include <intrin.h>  // For _mm_pause()

/*
 * Windows IORing Implementation with True RIO Support
 *
 * This library provides two modes:
 * 1. Legacy mode (ioring_create): Uses synchronous recv/send with WSAPoll
 * 2. RIO mode (ioring_create_rio): Uses true async RIO with registered buffers
 *
 * RIO mode provides:
 * - Zero-copy I/O with pre-registered buffers
 * - Batched submissions and completions
 * - True async operations that don't block
 */

// RIO function table
static RIO_EXTENSION_FUNCTION_TABLE rio = { 0 };
static BOOL rio_initialized = FALSE;

// Thread-local error
static __declspec(thread) int last_error = 0;

// Poll mask translation between Linux io_uring style and Windows WSAPoll
#define LINUX_POLLIN    0x0001
#define LINUX_POLLPRI   0x0002
#define LINUX_POLLOUT   0x0004
#define LINUX_POLLERR   0x0008
#define LINUX_POLLHUP   0x0010
#define LINUX_POLLNVAL  0x0020

static SHORT linux_to_windows_poll(uint32_t linux_mask) {
    SHORT win_mask = 0;
    if (linux_mask & LINUX_POLLIN)  win_mask |= POLLRDNORM;
    if (linux_mask & LINUX_POLLPRI) win_mask |= POLLRDBAND;
    if (linux_mask & LINUX_POLLOUT) win_mask |= POLLWRNORM;
    if (linux_mask & LINUX_POLLERR) win_mask |= POLLERR;
    if (linux_mask & LINUX_POLLHUP) win_mask |= POLLHUP;
    if (linux_mask & LINUX_POLLNVAL) win_mask |= POLLNVAL;
    return win_mask;
}

static uint32_t windows_to_linux_poll(SHORT win_mask) {
    uint32_t linux_mask = 0;
    if (win_mask & POLLRDNORM) linux_mask |= LINUX_POLLIN;
    if (win_mask & POLLRDBAND) linux_mask |= LINUX_POLLPRI;
    if (win_mask & POLLWRNORM) linux_mask |= LINUX_POLLOUT;
    if (win_mask & POLLERR)    linux_mask |= LINUX_POLLERR;
    if (win_mask & POLLHUP)    linux_mask |= LINUX_POLLHUP;
    if (win_mask & POLLNVAL)   linux_mask |= LINUX_POLLNVAL;
    return linux_mask;
}

// =============================================================================
// Connection State for RIO Mode
// =============================================================================

typedef struct rio_connection {
    SOCKET socket;
    RIO_RQ rq;                      // Request queue for this socket

    // Buffer location (lazy commit mode)
    RIO_BUFFERID buffer_id;         // Which slab this connection's buffers are in
    uint32_t recv_offset;           // Offset within the slab for recv buffer
    uint32_t send_offset;           // Offset within the slab for send buffer

    BOOL active;                    // Connection is fully registered and active
    BOOL reserved;                  // Slot is reserved (prevents AcceptEx collision)
} rio_connection_t;

// AcceptEx context for pending accept operations
// AcceptEx requires output buffer: local addr (16 + sizeof(sockaddr)) + remote addr (16 + sizeof(sockaddr))
#define ACCEPTEX_ADDR_SIZE (sizeof(SOCKADDR_STORAGE) + 16)
#define ACCEPTEX_BUFFER_SIZE (ACCEPTEX_ADDR_SIZE * 2)

typedef struct acceptex_context {
    SOCKET accept_socket;       // Pre-created socket with WSA_FLAG_REGISTERED_IO
    SOCKET listen_socket;       // The listener socket
    char buffer[ACCEPTEX_BUFFER_SIZE];  // AcceptEx output buffer (addresses)
    OVERLAPPED overlapped;      // Overlapped structure for async completion
    HANDLE event;               // Event for completion notification (non-IOCP mode)
    uint64_t user_data;         // User data to return with completion
    BOOL pending;               // TRUE if AcceptEx is pending
    BOOL completed;             // TRUE if AcceptEx has completed (check result)
    DWORD bytes_received;       // Bytes received by AcceptEx
    RIO_RQ rq;                  // Pre-created RQ (created before AcceptEx)
    int conn_slot;              // Connection slot for this socket
} acceptex_context_t;

// RIO configuration defaults
#define RIO_DEFAULT_OUTSTANDING_PER_SOCKET 2  // 1 recv + 1 send typical for echo
#define RIO_MAX_OUTSTANDING_PER_SOCKET 16     // Upper limit per direction

// Lazy commit slab configuration
#define RIO_MAX_SLABS 64                      // Max number of buffer slabs (64 * 256 = 16K connections)
#define RIO_CONNECTIONS_PER_SLAB 256          // Connections per slab (2MB per slab at 8KB/conn)

// Forward declarations
static void dequeue_rio_completions(ioring_t* ring);
static BOOL ensure_rio_capacity(ioring_t* ring, uint32_t needed_connections);

// Internal ring structure
struct ioring {
    ioring_backend_t backend;
    uint32_t sq_entries;
    uint32_t cq_entries;
    uint32_t sq_mask;
    uint32_t cq_mask;

    // Submission queue (user-space)
    ioring_sqe_t* sq;
    uint32_t sq_head;
    uint32_t sq_tail;

    // Completion queue (user-space)
    ioring_cqe_t* cq;
    uint32_t cq_head;
    uint32_t cq_tail;

    // RIO mode data
    BOOL rio_mode;                      // True if created with ioring_create_rio
    RIO_CQ rio_cq;                      // RIO completion queue
    uint32_t rio_max_connections;       // Max concurrent connections
    uint32_t rio_active_connections;    // Current active connections
    uint32_t rio_outstanding_per_socket;// Max outstanding ops per direction per socket
    uint32_t rio_cq_size;               // Actual CQ size
    rio_connection_t* rio_connections;  // Per-connection state
    HANDLE rio_event;                   // Event for completion notification
    HANDLE rio_iocp;                    // IOCP for RIO completions (if using IOCP mode)

    // Lazy-commit buffer mode (reserves full address space, commits on demand)
    char* rio_buffer;                   // VirtualAlloc'd with MEM_RESERVE (full capacity)
    size_t rio_buffer_reserved;         // Total reserved size
    size_t rio_buffer_committed;        // Currently committed size
    uint32_t rio_recv_buf_size;         // Receive buffer size per connection
    uint32_t rio_send_buf_size;         // Send buffer size per connection
    uint32_t rio_per_conn_size;         // recv + send size per connection

    // Slab management (each slab is committed and registered separately)
    RIO_BUFFERID rio_slab_ids[RIO_MAX_SLABS]; // Buffer ID per committed slab
    uint32_t rio_slab_size;             // Size of each slab in bytes
    uint32_t rio_committed_slabs;       // Number of slabs committed and registered
    uint32_t rio_committed_connections; // Number of connection slots committed

    // Legacy single-buffer ID (for backwards compat, points to first slab)
    RIO_BUFFERID rio_buf_id;            // = rio_slab_ids[0]

    // Owned listener sockets (library creates these)
    SOCKET* owned_listeners;            // Array of listener sockets we created
    uint32_t owned_listener_count;
    uint32_t owned_listener_capacity;

    // AcceptEx support (RIO mode only)
    acceptex_context_t* accept_pool;    // Pool of pending AcceptEx contexts
    uint32_t accept_pool_size;          // Size of accept pool
    LPFN_ACCEPTEX fn_acceptex;          // Cached AcceptEx function pointer
    LPFN_GETACCEPTEXSOCKADDRS fn_getacceptexsockaddrs;  // For extracting addresses
    HANDLE accept_iocp;                 // IOCP for AcceptEx completions

    // Legacy mode data (pending operations for sync fallback)
    struct pending_op {
        uint64_t user_data;
        uint8_t opcode;
        SOCKET fd;
        void* buf;
        uint32_t len;
        uint32_t flags;
        void* addr;
        int addrlen;
    }* pending_ops;
    uint32_t pending_count;
    uint32_t pending_capacity;

    // External buffer support (for zero-copy from user-owned memory like Pipe)
    #define RIO_MAX_EXTERNAL_BUFFERS 16
    RIO_BUFFERID external_buffer_ids[16];   // Registered buffer IDs
    void* external_buffer_ptrs[16];          // Pointers for tracking
    uint32_t external_buffer_lengths[16];    // Lengths for validation
    uint32_t external_buffer_count;          // Number of registered external buffers
};

// Static initialization
static BOOL initialized = FALSE;

static uint32_t next_power_of_two(uint32_t n) {
    n--;
    n |= n >> 1;
    n |= n >> 2;
    n |= n >> 4;
    n |= n >> 8;
    n |= n >> 16;
    return n + 1;
}

static void init_winsock(void) {
    if (initialized) return;
    initialized = TRUE;

    WSADATA wsaData;
    WSAStartup(MAKEWORD(2, 2), &wsaData);
}

static BOOL init_rio(void) {
    if (rio_initialized) return TRUE;

    init_winsock();

    // Use WSASocket with WSA_FLAG_REGISTERED_IO to ensure RIO compatibility
    SOCKET sock = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0, WSA_FLAG_REGISTERED_IO);
    if (sock == INVALID_SOCKET) {
        // Fall back to regular socket if WSA_FLAG_REGISTERED_IO isn't supported
        sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (sock == INVALID_SOCKET) return FALSE;
    }

    GUID rio_guid = WSAID_MULTIPLE_RIO;
    DWORD bytes = 0;
    if (WSAIoctl(sock, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER,
                 &rio_guid, sizeof(rio_guid),
                 &rio, sizeof(rio),
                 &bytes, NULL, NULL) == 0) {
        rio_initialized = TRUE;
    }
    closesocket(sock);
    return rio_initialized;
}

// =============================================================================
// Legacy API Implementation (synchronous fallback)
// =============================================================================

// Helper to complete an operation
static void complete_op(ioring_t* ring, uint64_t user_data, int32_t res, uint32_t flags) {
    ring->cq[ring->cq_tail & ring->cq_mask] = (ioring_cqe_t){
        .user_data = user_data,
        .res = res,
        .flags = flags
    };
    ring->cq_tail++;
}

// Add pending operation for legacy mode
static BOOL add_pending_op(ioring_t* ring, uint64_t user_data, uint8_t opcode,
                           SOCKET fd, void* buf, uint32_t len, uint32_t flags,
                           void* addr, int addrlen) {
    if (ring->pending_count >= ring->pending_capacity) return FALSE;
    ring->pending_ops[ring->pending_count++] = (struct pending_op){
        .user_data = user_data,
        .opcode = opcode,
        .fd = fd,
        .buf = buf,
        .len = len,
        .flags = flags,
        .addr = addr,
        .addrlen = addrlen
    };
    return TRUE;
}

IORING_API ioring_t* ioring_create(uint32_t entries) {
    init_winsock();
    init_rio();

    if (entries == 0) entries = 4096;
    entries = next_power_of_two(entries);

    ioring_t* ring = calloc(1, sizeof(ioring_t));
    if (!ring) {
        last_error = ERROR_OUTOFMEMORY;
        return NULL;
    }

    ring->sq_entries = entries;
    ring->cq_entries = entries * 2;
    ring->sq_mask = entries - 1;
    ring->cq_mask = ring->cq_entries - 1;
    ring->rio_mode = FALSE;
    ring->backend = IORING_BACKEND_RIO;

    ring->sq = (ioring_sqe_t*)calloc(entries, sizeof(ioring_sqe_t));
    ring->cq = (ioring_cqe_t*)calloc(ring->cq_entries, sizeof(ioring_cqe_t));
    ring->pending_ops = calloc(entries, sizeof(struct pending_op));
    ring->pending_capacity = entries;

    if (!ring->sq || !ring->cq || !ring->pending_ops) {
        free(ring->sq);
        free(ring->cq);
        free(ring->pending_ops);
        free(ring);
        last_error = ERROR_OUTOFMEMORY;
        return NULL;
    }

    return ring;
}

IORING_API void ioring_destroy(ioring_t* ring) {
    if (!ring) return;

    if (ring->rio_mode) {
        // Cleanup owned listener sockets
        if (ring->owned_listeners) {
            for (uint32_t i = 0; i < ring->owned_listener_count; i++) {
                if (ring->owned_listeners[i] != INVALID_SOCKET) {
                    closesocket(ring->owned_listeners[i]);
                }
            }
            free(ring->owned_listeners);
        }

        // Cleanup AcceptEx pool
        if (ring->accept_pool) {
            for (uint32_t i = 0; i < ring->accept_pool_size; i++) {
                if (ring->accept_pool[i].accept_socket != INVALID_SOCKET) {
                    closesocket(ring->accept_pool[i].accept_socket);
                }
                if (ring->accept_pool[i].event) {
                    CloseHandle(ring->accept_pool[i].event);
                }
            }
            free(ring->accept_pool);
        }
        if (ring->accept_iocp) {
            CloseHandle(ring->accept_iocp);
        }

        // Cleanup RIO connections (close sockets)
        if (ring->rio_connections) {
            for (uint32_t i = 0; i < ring->rio_max_connections; i++) {
                rio_connection_t* conn = &ring->rio_connections[i];
                if ((conn->active || conn->reserved) && conn->socket != INVALID_SOCKET) {
                    closesocket(conn->socket);
                }
            }
            free(ring->rio_connections);
        }

        // Close RIO completion queue
        if (ring->rio_cq != RIO_INVALID_CQ) {
            rio.RIOCloseCompletionQueue(ring->rio_cq);
        }

        // Cleanup all registered buffer slabs
        if (rio.RIODeregisterBuffer) {
            for (uint32_t i = 0; i < ring->rio_committed_slabs; i++) {
                if (ring->rio_slab_ids[i] != RIO_INVALID_BUFFERID) {
                    rio.RIODeregisterBuffer(ring->rio_slab_ids[i]);
                }
            }

            // Cleanup external buffers (user is responsible for freeing memory)
            for (uint32_t i = 0; i < 16; i++) {
                if (ring->external_buffer_ids[i] != RIO_INVALID_BUFFERID) {
                    rio.RIODeregisterBuffer(ring->external_buffer_ids[i]);
                }
            }
        }
        // Release the entire reserved region (includes all committed pages)
        if (ring->rio_buffer) {
            VirtualFree(ring->rio_buffer, 0, MEM_RELEASE);
        }

        // Close handles
        if (ring->rio_iocp) {
            CloseHandle(ring->rio_iocp);
        }
        if (ring->rio_event) {
            CloseHandle(ring->rio_event);
        }
    }

    free(ring->pending_ops);
    free(ring->sq);
    free(ring->cq);
    free(ring);
}

IORING_API ioring_backend_t ioring_get_backend(ioring_t* ring) {
    return ring ? ring->backend : IORING_BACKEND_NONE;
}

IORING_API uint32_t ioring_sq_space_left(ioring_t* ring) {
    if (!ring) return 0;
    return ring->sq_entries - (ring->sq_tail - ring->sq_head);
}

IORING_API uint32_t ioring_cq_ready(ioring_t* ring) {
    if (!ring) return 0;
    return ring->cq_tail - ring->cq_head;
}

IORING_API ioring_sqe_t* ioring_get_sqe(ioring_t* ring) {
    if (!ring) return NULL;
    if (ioring_sq_space_left(ring) == 0) return NULL;

    ioring_sqe_t* sqe = &ring->sq[ring->sq_tail & ring->sq_mask];
    memset(sqe, 0, sizeof(ioring_sqe_t));
    ring->sq_tail++;
    return sqe;
}

// Prepare functions
IORING_API void ioring_prep_poll_add(ioring_sqe_t* sqe, SOCKET fd, uint32_t poll_mask, uint64_t user_data) {
    sqe->opcode = IORING_OP_POLL_ADD;
    sqe->fd = (int32_t)fd;
    sqe->poll_events = poll_mask;
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_poll_remove(ioring_sqe_t* sqe, uint64_t user_data_to_cancel) {
    sqe->opcode = IORING_OP_POLL_REMOVE;
    sqe->addr = user_data_to_cancel;
    sqe->user_data = user_data_to_cancel;
}

IORING_API void ioring_prep_accept(ioring_sqe_t* sqe, SOCKET fd, struct sockaddr* addr, int* addrlen, uint64_t user_data) {
    sqe->opcode = IORING_OP_ACCEPT;
    sqe->fd = (int32_t)fd;
    sqe->addr = (uintptr_t)addr;
    sqe->off = (uintptr_t)addrlen;
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_connect(ioring_sqe_t* sqe, SOCKET fd, const struct sockaddr* addr, int addrlen, uint64_t user_data) {
    sqe->opcode = IORING_OP_CONNECT;
    sqe->fd = (int32_t)fd;
    sqe->addr = (uintptr_t)addr;
    sqe->off = addrlen;
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_send(ioring_sqe_t* sqe, SOCKET fd, const void* buf, uint32_t len, int flags, uint64_t user_data) {
    sqe->opcode = IORING_OP_SEND;
    sqe->fd = (int32_t)fd;
    sqe->addr = (uintptr_t)buf;
    sqe->len = len;
    sqe->msg_flags = flags;
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_recv(ioring_sqe_t* sqe, SOCKET fd, void* buf, uint32_t len, int flags, uint64_t user_data) {
    sqe->opcode = IORING_OP_RECV;
    sqe->fd = (int32_t)fd;
    sqe->addr = (uintptr_t)buf;
    sqe->len = len;
    sqe->msg_flags = flags;
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_close(ioring_sqe_t* sqe, SOCKET fd, uint64_t user_data) {
    sqe->opcode = IORING_OP_CLOSE;
    sqe->fd = (int32_t)fd;
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_cancel(ioring_sqe_t* sqe, uint64_t user_data_to_cancel, uint64_t user_data) {
    sqe->opcode = IORING_OP_CANCEL;
    sqe->addr = user_data_to_cancel;
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_shutdown(ioring_sqe_t* sqe, SOCKET fd, int how, uint64_t user_data) {
    sqe->opcode = IORING_OP_SHUTDOWN;
    sqe->fd = (int32_t)fd;
    sqe->len = how;
    sqe->user_data = user_data;
}

// RIO-specific prepare functions
IORING_API void ioring_prep_recv_registered(ioring_sqe_t* sqe, int conn_id, uint32_t max_len, uint64_t user_data) {
    sqe->opcode = IORING_OP_RECV;
    sqe->fd = conn_id;  // Store conn_id in fd field
    sqe->len = max_len;
    sqe->flags = 0x80;  // Flag to indicate registered buffer mode
    sqe->user_data = user_data;
}

IORING_API void ioring_prep_send_registered(ioring_sqe_t* sqe, int conn_id, uint32_t len, uint64_t user_data) {
    sqe->opcode = IORING_OP_SEND;
    sqe->fd = conn_id;  // Store conn_id in fd field
    sqe->len = len;
    sqe->flags = 0x80;  // Flag to indicate registered buffer mode
    sqe->user_data = user_data;
}

// Execute a single operation (legacy mode - synchronous)
static int legacy_execute_op(ioring_t* ring, ioring_sqe_t* sqe) {
    SOCKET fd = (SOCKET)sqe->fd;
    int err;

    switch (sqe->opcode) {
        case IORING_OP_POLL_ADD: {
            if (ring->pending_count < ring->pending_capacity) {
                add_pending_op(ring, sqe->user_data, sqe->opcode, fd, NULL, 0, sqe->poll_events, NULL, 0);
            }
            return 1;
        }
        case IORING_OP_ACCEPT: {
            struct sockaddr* addr = (struct sockaddr*)sqe->addr;
            int* addrlen = (int*)sqe->off;
            SOCKET client = accept(fd, addr, addrlen);
            if (client != INVALID_SOCKET) {
                u_long mode = 1;
                ioctlsocket(client, FIONBIO, &mode);
                struct linger lin = { 1, 0 };
                setsockopt(client, SOL_SOCKET, SO_LINGER, (char*)&lin, sizeof(lin));
                complete_op(ring, sqe->user_data, (int32_t)client, 0);
                return 1;
            }
            err = WSAGetLastError();
            if (err == WSAEWOULDBLOCK) {
                if (!add_pending_op(ring, sqe->user_data, sqe->opcode, fd, NULL, 0, 0, addr, addrlen ? *addrlen : 0)) {
                    complete_op(ring, sqe->user_data, -WSAENOBUFS, 0);
                    return 1;
                }
                return 0;
            }
            complete_op(ring, sqe->user_data, -err, 0);
            return 1;
        }
        case IORING_OP_SEND: {
            const char* buf = (const char*)sqe->addr;
            int len = (int)sqe->len;
            int flags = sqe->msg_flags;
            int sent = send(fd, buf, len, flags);
            if (sent >= 0) {
                complete_op(ring, sqe->user_data, sent, 0);
                return 1;
            }
            err = WSAGetLastError();
            complete_op(ring, sqe->user_data, -err, 0);
            return 1;
        }
        case IORING_OP_RECV: {
            char* buf = (char*)sqe->addr;
            int len = (int)sqe->len;
            int flags = sqe->msg_flags;
            int recvd = recv(fd, buf, len, flags);
            if (recvd >= 0) {
                complete_op(ring, sqe->user_data, recvd, 0);
                return 1;
            }
            err = WSAGetLastError();
            complete_op(ring, sqe->user_data, -err, 0);
            return 1;
        }
        case IORING_OP_CLOSE: {
            int res = closesocket(fd);
            complete_op(ring, sqe->user_data, res == 0 ? 0 : -WSAGetLastError(), 0);
            return 1;
        }
        case IORING_OP_SHUTDOWN: {
            int res = shutdown(fd, (int)sqe->len);
            complete_op(ring, sqe->user_data, res == 0 ? 0 : -WSAGetLastError(), 0);
            return 1;
        }
        default:
            complete_op(ring, sqe->user_data, -ENOSYS, 0);
            return 1;
    }
}

// Forward declarations
static void check_acceptex_completions(ioring_t* ring);
static BOOL post_acceptex(ioring_t* ring, SOCKET listen_socket, uint64_t user_data);

// Execute operation in RIO mode
static int rio_execute_op(ioring_t* ring, ioring_sqe_t* sqe) {
    // Handle ACCEPT specially - use AcceptEx for RIO-compatible sockets
    if (sqe->opcode == IORING_OP_ACCEPT && ring->accept_pool) {
        SOCKET listen_socket = (SOCKET)sqe->fd;
        if (post_acceptex(ring, listen_socket, sqe->user_data)) {
            return 0;  // Operation pending
        }
        // post_acceptex failed, fall through to legacy path
        complete_op(ring, sqe->user_data, -last_error, 0);
        return 1;
    }

    // Check if this is a registered buffer operation
    if (sqe->flags & 0x80) {
        int conn_id = sqe->fd;
        if (conn_id < 0 || (uint32_t)conn_id >= ring->rio_max_connections) {
            complete_op(ring, sqe->user_data, -EINVAL, 0);
            return 1;
        }

        rio_connection_t* conn = &ring->rio_connections[conn_id];
        if (!conn->active || conn->rq == RIO_INVALID_RQ) {
            complete_op(ring, sqe->user_data, -ENOTCONN, 0);
            return 1;
        }

        RIO_BUF buf;
        buf.BufferId = conn->buffer_id;  // Use per-connection buffer ID (lazy commit)

        BOOL success = FALSE;
        int retries = 0;
        const int max_retries = 3;

        if (sqe->opcode == IORING_OP_RECV) {
            buf.Offset = conn->recv_offset;
            buf.Length = sqe->len;

            while (!success && retries < max_retries) {
                WSASetLastError(0);
                success = rio.RIOReceive(conn->rq, &buf, 1, 0, (PVOID)sqe->user_data);
                if (!success) {
                    int err = WSAGetLastError();
                    if (err == WSAENOBUFS && retries < max_retries - 1) {
                        // CQ might be full, drain completions and retry
                        dequeue_rio_completions(ring);
                        retries++;
                    } else {
                        complete_op(ring, sqe->user_data, -err, 0);
                        return 1;
                    }
                }
            }
        }
        else if (sqe->opcode == IORING_OP_SEND) {
            // Check if this is an external buffer send (flag 0x01)
            if (sqe->flags & 0x01) {
                // External buffer send - use buffer_id from sqe
                int ext_buf_id = (int)sqe->buf_index;
                if (ext_buf_id < 0 || ext_buf_id >= 16 ||
                    ring->external_buffer_ids[ext_buf_id] == RIO_INVALID_BUFFERID) {
                    complete_op(ring, sqe->user_data, -EINVAL, 0);
                    return 1;
                }

                // Validate offset + length doesn't exceed buffer
                uint32_t offset = (uint32_t)sqe->addr;  // Offset stored in addr
                if (offset + sqe->len > ring->external_buffer_lengths[ext_buf_id]) {
                    complete_op(ring, sqe->user_data, -EINVAL, 0);
                    return 1;
                }

                buf.BufferId = ring->external_buffer_ids[ext_buf_id];
                buf.Offset = offset;
                buf.Length = sqe->len;
            } else {
                // Regular per-connection buffer send
                buf.Offset = conn->send_offset;
                buf.Length = sqe->len;
            }

            while (!success && retries < max_retries) {
                success = rio.RIOSend(conn->rq, &buf, 1, 0, (PVOID)sqe->user_data);
                if (!success) {
                    int err = WSAGetLastError();
                    if (err == WSAENOBUFS && retries < max_retries - 1) {
                        // CQ might be full, drain completions and retry
                        dequeue_rio_completions(ring);
                        retries++;
                    } else {
                        complete_op(ring, sqe->user_data, -err, 0);
                        return 1;
                    }
                }
            }
        }

        return 0;  // Operation pending, completion will come from RIO CQ
    }

    // Non-registered operations use legacy path
    return legacy_execute_op(ring, sqe);
}

// Process pending poll operations (legacy mode)
static void process_pending_polls(ioring_t* ring) {
    for (uint32_t i = 0; i < ring->pending_count; ) {
        struct pending_op* op = &ring->pending_ops[i];
        BOOL completed = FALSE;

        switch (op->opcode) {
            case IORING_OP_POLL_ADD: {
                WSAPOLLFD pfd = { op->fd, linux_to_windows_poll(op->flags), 0 };
                int result = WSAPoll(&pfd, 1, 0);
                if (result > 0) {
                    complete_op(ring, op->user_data, windows_to_linux_poll(pfd.revents), 0);
                    completed = TRUE;
                } else if (result < 0) {
                    complete_op(ring, op->user_data, -WSAGetLastError(), 0);
                    completed = TRUE;
                }
                break;
            }
            case IORING_OP_ACCEPT: {
                SOCKET client = accept(op->fd, op->addr, op->addr ? &op->addrlen : NULL);
                if (client != INVALID_SOCKET) {
                    u_long mode = 1;
                    ioctlsocket(client, FIONBIO, &mode);
                    struct linger lin = { 1, 0 };
                    setsockopt(client, SOL_SOCKET, SO_LINGER, (char*)&lin, sizeof(lin));
                    complete_op(ring, op->user_data, (int32_t)client, 0);
                    completed = TRUE;
                } else {
                    int err = WSAGetLastError();
                    if (err != WSAEWOULDBLOCK) {
                        complete_op(ring, op->user_data, -err, 0);
                        completed = TRUE;
                    }
                }
                break;
            }
        }

        if (completed) {
            ring->pending_ops[i] = ring->pending_ops[--ring->pending_count];
        } else {
            i++;
        }
    }
}

// =============================================================================
// Lazy Commit: Ensure we have enough committed buffer capacity
// =============================================================================
static BOOL ensure_rio_capacity(ioring_t* ring, uint32_t needed_connections) {
    if (!ring || !ring->rio_mode) return FALSE;

    // Already have enough capacity
    if (needed_connections <= ring->rio_committed_connections) {
        return TRUE;
    }

    // Check if we've hit the max
    if (needed_connections > ring->rio_max_connections) {
        last_error = WSAENOBUFS;
        return FALSE;
    }

    // Calculate how many slabs we need
    uint32_t conns_per_slab = RIO_CONNECTIONS_PER_SLAB;
    if (conns_per_slab > ring->rio_max_connections) conns_per_slab = ring->rio_max_connections;

    uint32_t slabs_needed = (needed_connections + conns_per_slab - 1) / conns_per_slab;
    if (slabs_needed > RIO_MAX_SLABS) {
        last_error = WSAENOBUFS;
        return FALSE;
    }

    // Commit and register new slabs as needed
    while (ring->rio_committed_slabs < slabs_needed) {
        uint32_t slab_index = ring->rio_committed_slabs;
        size_t slab_offset = (size_t)slab_index * ring->rio_slab_size;

        // Check we're still within reserved range
        if (slab_offset + ring->rio_slab_size > ring->rio_buffer_reserved) {
            last_error = WSAENOBUFS;
            return FALSE;
        }

        // Commit this slab's memory
        void* committed = VirtualAlloc(
            ring->rio_buffer + slab_offset,
            ring->rio_slab_size,
            MEM_COMMIT,
            PAGE_READWRITE
        );
        if (!committed) {
            last_error = GetLastError();
            return FALSE;
        }

        // Register with RIO
        ring->rio_slab_ids[slab_index] = rio.RIORegisterBuffer(
            ring->rio_buffer + slab_offset,
            ring->rio_slab_size
        );
        if (ring->rio_slab_ids[slab_index] == RIO_INVALID_BUFFERID) {
            last_error = WSAGetLastError();
            // Decommit the memory we just committed
            VirtualFree(ring->rio_buffer + slab_offset, ring->rio_slab_size, MEM_DECOMMIT);
            return FALSE;
        }

        ring->rio_committed_slabs++;
        ring->rio_buffer_committed += ring->rio_slab_size;
        ring->rio_committed_connections = ring->rio_committed_slabs * conns_per_slab;
        if (ring->rio_committed_connections > ring->rio_max_connections) {
            ring->rio_committed_connections = ring->rio_max_connections;
        }

        // Update buffer_id for connections in this new slab
        uint32_t first_conn = slab_index * conns_per_slab;
        uint32_t last_conn = first_conn + conns_per_slab;
        if (last_conn > ring->rio_max_connections) last_conn = ring->rio_max_connections;

        for (uint32_t i = first_conn; i < last_conn; i++) {
            ring->rio_connections[i].buffer_id = ring->rio_slab_ids[slab_index];
        }
    }

    return TRUE;
}

// Dequeue RIO completions (and AcceptEx completions)
static void dequeue_rio_completions(ioring_t* ring) {
    if (!ring->rio_mode) return;

    // First check for AcceptEx completions (now using event-based notification)
    if (ring->accept_pool) {
        check_acceptex_completions(ring);
    }

    if (ring->rio_cq == RIO_INVALID_CQ) return;

    // Check how much space we have in user-space CQ
    uint32_t cq_space = ring->cq_entries - (ring->cq_tail - ring->cq_head);
    if (cq_space == 0) {
        // User-space CQ is full, can't drain more
        // User must call AdvanceCompletionQueue to make room
        return;
    }

    // Only dequeue as many as we have room for
    uint32_t max_dequeue = cq_space < 256 ? cq_space : 256;

    RIORESULT results[256];
    ULONG count = rio.RIODequeueCompletion(ring->rio_cq, results, max_dequeue);

    if (count == RIO_CORRUPT_CQ) {
        // CQ is corrupt, nothing we can do
        return;
    }

    for (ULONG i = 0; i < count; i++) {
        uint64_t user_data = (uint64_t)results[i].RequestContext;
        int32_t res = (results[i].Status == 0) ? (int32_t)results[i].BytesTransferred : -(int32_t)results[i].Status;
        complete_op(ring, user_data, res, 0);
    }

}

IORING_API int ioring_submit(ioring_t* ring) {
    if (!ring) return -1;

    uint32_t submitted = 0;
    uint32_t to_submit = ring->sq_tail - ring->sq_head;

    for (uint32_t i = 0; i < to_submit; i++) {
        ioring_sqe_t* sqe = &ring->sq[ring->sq_head & ring->sq_mask];

        if (ring->rio_mode) {
            rio_execute_op(ring, sqe);
        } else {
            legacy_execute_op(ring, sqe);
        }

        ring->sq_head++;
        submitted++;
    }

    return submitted;
}

IORING_API int ioring_submit_and_wait(ioring_t* ring, uint32_t wait_nr) {
    int submitted = ioring_submit(ring);

    while (ioring_cq_ready(ring) < wait_nr) {
        if (ring->rio_mode) {
            dequeue_rio_completions(ring);
        }
        process_pending_polls(ring);

        if (ioring_cq_ready(ring) >= wait_nr) break;

        _mm_pause();
    }

    return submitted;
}

IORING_API uint32_t ioring_peek_cqe(ioring_t* ring, ioring_cqe_t* cqes, uint32_t count) {
    if (!ring || !cqes) return 0;

    // First, dequeue any RIO completions
    if (ring->rio_mode) {
        dequeue_rio_completions(ring);
    }

    uint32_t available = ioring_cq_ready(ring);
    uint32_t to_copy = count < available ? count : available;

    for (uint32_t i = 0; i < to_copy; i++) {
        cqes[i] = ring->cq[(ring->cq_head + i) & ring->cq_mask];
    }

    return to_copy;
}

IORING_API uint32_t ioring_wait_cqe(ioring_t* ring, ioring_cqe_t* cqes, uint32_t count, uint32_t min_complete, int timeout_ms) {
    if (!ring || !cqes) return 0;

    DWORD start = GetTickCount();
    DWORD elapsed = 0;
    int spin_count = 0;

    while (ioring_cq_ready(ring) < min_complete) {
        if (ring->rio_mode) {
            dequeue_rio_completions(ring);
        }
        process_pending_polls(ring);

        if (ioring_cq_ready(ring) >= min_complete) break;

        elapsed = GetTickCount() - start;
        if (timeout_ms >= 0 && elapsed >= (DWORD)timeout_ms) break;

        if (++spin_count < 100) {
            _mm_pause();
        } else {
            spin_count = 0;
            SwitchToThread();
        }
    }

    return ioring_peek_cqe(ring, cqes, count);
}

IORING_API void ioring_cq_advance(ioring_t* ring, uint32_t count) {
    if (ring) {
        ring->cq_head += count;
    }
}

IORING_API int ioring_get_last_error(void) {
    return last_error;
}

// =============================================================================
// High-Performance RIO API Implementation
// =============================================================================

IORING_API ioring_t* ioring_create_rio_ex(
    uint32_t entries,
    uint32_t max_connections,
    uint32_t recv_buffer_size,
    uint32_t send_buffer_size,
    uint32_t outstanding_per_socket
) {
    if (!init_rio()) {
        last_error = ERROR_NOT_SUPPORTED;
        return NULL;
    }

    // Verify RIO functions are loaded
    if (!rio.RIOCreateRequestQueue || !rio.RIOCreateCompletionQueue ||
        !rio.RIORegisterBuffer || !rio.RIODequeueCompletion ||
        !rio.RIOReceive || !rio.RIOSend) {
        last_error = ERROR_NOT_SUPPORTED;
        return NULL;
    }

    if (entries == 0) entries = 4096;
    entries = next_power_of_two(entries);

    if (max_connections == 0) max_connections = 1024;

    // Clamp outstanding_per_socket to valid range
    if (outstanding_per_socket == 0) outstanding_per_socket = RIO_DEFAULT_OUTSTANDING_PER_SOCKET;
    if (outstanding_per_socket > RIO_MAX_OUTSTANDING_PER_SOCKET) outstanding_per_socket = RIO_MAX_OUTSTANDING_PER_SOCKET;
    // Also clamp to MAX_OUTSTANDING_RECV/SEND
    if (outstanding_per_socket > MAX_OUTSTANDING_RECV) outstanding_per_socket = MAX_OUTSTANDING_RECV;

    ioring_t* ring = calloc(1, sizeof(ioring_t));
    if (!ring) {
        last_error = ERROR_OUTOFMEMORY;
        return NULL;
    }

    ring->sq_entries = entries;
    // User-space CQ must be large enough to hold all RIO completions
    // RIO CQ size = max_connections * outstanding * 2, so match that
    uint32_t min_cq_size = max_connections * outstanding_per_socket * 2;
    ring->cq_entries = (entries * 2 > min_cq_size) ? entries * 2 : min_cq_size;
    // Round up to power of 2 for efficient masking
    ring->cq_entries = next_power_of_two(ring->cq_entries);
    ring->sq_mask = entries - 1;
    ring->cq_mask = ring->cq_entries - 1;
    ring->rio_mode = TRUE;
    ring->backend = IORING_BACKEND_RIO;
    ring->rio_max_connections = max_connections;
    ring->rio_outstanding_per_socket = outstanding_per_socket;
    ring->rio_cq = RIO_INVALID_CQ;

    // Allocate user-space queues
    ring->sq = (ioring_sqe_t*)calloc(entries, sizeof(ioring_sqe_t));
    ring->cq = (ioring_cqe_t*)calloc(ring->cq_entries, sizeof(ioring_cqe_t));
    ring->pending_ops = calloc(entries, sizeof(struct pending_op));
    ring->pending_capacity = entries;

    if (!ring->sq || !ring->cq || !ring->pending_ops) {
        goto fail;
    }

    // Allocate connection state array
    ring->rio_connections = calloc(max_connections, sizeof(rio_connection_t));
    if (!ring->rio_connections) {
        goto fail;
    }
    // Initialize connections
    for (uint32_t i = 0; i < max_connections; i++) {
        ring->rio_connections[i].socket = INVALID_SOCKET;
        ring->rio_connections[i].rq = RIO_INVALID_RQ;
        ring->rio_connections[i].recv_slot_mask = 0;
        ring->rio_connections[i].send_slot_mask = 0;
        ring->rio_connections[i].current_recv_slot = -1;
        ring->rio_connections[i].active = FALSE;
        ring->rio_connections[i].reserved = FALSE;
    }

    // ==========================================================================
    // Lazy-Commit Buffer (reserves full address space, commits slabs on demand)
    // ==========================================================================
    ring->rio_recv_buf_size = recv_buffer_size;
    ring->rio_send_buf_size = send_buffer_size;
    uint32_t per_connection_size = recv_buffer_size + send_buffer_size;
    ring->rio_per_conn_size = per_connection_size;

    // Calculate slab sizing
    // Each slab covers RIO_CONNECTIONS_PER_SLAB connections
    uint32_t conns_per_slab = RIO_CONNECTIONS_PER_SLAB;
    if (conns_per_slab > max_connections) conns_per_slab = max_connections;
    ring->rio_slab_size = conns_per_slab * per_connection_size;

    // Total size to reserve (full capacity)
    size_t total_buffer_size = (size_t)max_connections * per_connection_size;
    ring->rio_buffer_reserved = total_buffer_size;

    // Initialize slab tracking
    ring->rio_committed_slabs = 0;
    ring->rio_committed_connections = 0;
    ring->rio_buffer_committed = 0;
    for (uint32_t i = 0; i < RIO_MAX_SLABS; i++) {
        ring->rio_slab_ids[i] = RIO_INVALID_BUFFERID;
    }

    // Initialize external buffer tracking
    ring->external_buffer_count = 0;
    for (uint32_t i = 0; i < 16; i++) {
        ring->external_buffer_ids[i] = RIO_INVALID_BUFFERID;
        ring->external_buffer_ptrs[i] = NULL;
        ring->external_buffer_lengths[i] = 0;
    }

    // RESERVE full address space (no physical memory yet - this is free)
    ring->rio_buffer = (char*)VirtualAlloc(NULL, total_buffer_size,
                                           MEM_RESERVE, PAGE_READWRITE);
    if (!ring->rio_buffer) {
        goto fail;
    }

    // COMMIT first slab only (this allocates physical memory)
    void* committed = VirtualAlloc(ring->rio_buffer, ring->rio_slab_size,
                                   MEM_COMMIT, PAGE_READWRITE);
    if (!committed) {
        goto fail;
    }
    ring->rio_buffer_committed = ring->rio_slab_size;
    ring->rio_committed_connections = conns_per_slab;

    // Register first slab with RIO
    ring->rio_slab_ids[0] = rio.RIORegisterBuffer(ring->rio_buffer, ring->rio_slab_size);
    if (ring->rio_slab_ids[0] == RIO_INVALID_BUFFERID) {
        goto fail;
    }
    ring->rio_committed_slabs = 1;
    ring->rio_buf_id = ring->rio_slab_ids[0];  // Legacy compatibility

    // Initialize per-connection buffer info (only for first slab initially)
    for (uint32_t i = 0; i < max_connections; i++) {
        uint32_t slab_index = i / conns_per_slab;
        uint32_t index_in_slab = i % conns_per_slab;
        uint32_t offset_in_slab = index_in_slab * per_connection_size;

        ring->rio_connections[i].buffer_id = (slab_index < ring->rio_committed_slabs)
            ? ring->rio_slab_ids[slab_index] : RIO_INVALID_BUFFERID;
        ring->rio_connections[i].recv_offset = offset_in_slab;
        ring->rio_connections[i].send_offset = offset_in_slab + recv_buffer_size;
    }

    // Create IOCP for RIO completions (optional, can use polling)
    ring->rio_iocp = CreateIoCompletionPort(INVALID_HANDLE_VALUE, NULL, 0, 1);
    // Not fatal if this fails - we can use polling

    // Create RIO completion queue
    // CQ must be large enough for all outstanding operations across all RQs
    uint32_t rio_cq_size = max_connections * outstanding_per_socket * 2;
    if (rio_cq_size < ring->cq_entries) {
        rio_cq_size = ring->cq_entries;
    }
    if (rio_cq_size > 2000000) {
        rio_cq_size = 2000000;
    }
    ring->rio_cq_size = rio_cq_size;

    // Use polling mode for RIO CQ - simpler and works without RIONotify
    ring->rio_cq = rio.RIOCreateCompletionQueue(rio_cq_size, NULL);

    if (ring->rio_cq == RIO_INVALID_CQ) {
        goto fail;
    }

    // Create event for fallback notification (not used in polling mode)
    ring->rio_event = CreateEventW(NULL, FALSE, FALSE, NULL);

    // Initialize AcceptEx pool for server-side RIO support
    uint32_t accept_pool_size = max_connections < 64 ? max_connections : 64;
    ring->accept_pool_size = accept_pool_size;
    ring->accept_pool = calloc(accept_pool_size, sizeof(acceptex_context_t));
    if (!ring->accept_pool) {
        goto fail;
    }
    for (uint32_t i = 0; i < accept_pool_size; i++) {
        ring->accept_pool[i].accept_socket = INVALID_SOCKET;
        ring->accept_pool[i].pending = FALSE;
        ring->accept_pool[i].completed = FALSE;
        ring->accept_pool[i].rq = RIO_INVALID_RQ;
        ring->accept_pool[i].conn_slot = -1;
        // Create event for this accept context (event-based notification instead of IOCP)
        ring->accept_pool[i].event = CreateEventW(NULL, TRUE, FALSE, NULL);  // Manual reset
        if (!ring->accept_pool[i].event) {
            goto fail;
        }
    }

    // NOTE: We no longer use IOCP for AcceptEx - using events instead
    // This avoids potential interference with RIO
    ring->accept_iocp = NULL;

    // Initialize owned listeners array
    ring->owned_listener_capacity = 16;
    ring->owned_listeners = calloc(ring->owned_listener_capacity, sizeof(SOCKET));
    if (!ring->owned_listeners) {
        goto fail;
    }
    ring->owned_listener_count = 0;

    return ring;

fail:
    last_error = GetLastError();
    if (last_error == 0) last_error = ERROR_OUTOFMEMORY;
    ioring_destroy(ring);
    return NULL;
}

IORING_API int ioring_rio_register(
    ioring_t* ring,
    SOCKET socket,
    void** recv_buf,
    void** send_buf
) {
    if (!ring || !ring->rio_mode) {
        last_error = EINVAL;
        return -1;
    }

    if (ring->rio_cq == RIO_INVALID_CQ) {
        last_error = EINVAL;
        return -1;
    }

    // Verify socket is valid
    if (socket == INVALID_SOCKET) {
        last_error = WSAENOTSOCK;
        return -1;
    }

    // Helper to calculate buffer pointers from slab-based offsets
    uint32_t conns_per_slab = RIO_CONNECTIONS_PER_SLAB;
    if (conns_per_slab > ring->rio_max_connections) conns_per_slab = ring->rio_max_connections;

    // Check if this socket is already registered (e.g., from AcceptEx)
    for (uint32_t i = 0; i < ring->rio_max_connections; i++) {
        if (ring->rio_connections[i].active && ring->rio_connections[i].socket == socket) {
            // Socket already registered - just return buffer pointers
            rio_connection_t* conn = &ring->rio_connections[i];
            uint32_t slab_index = i / conns_per_slab;
            size_t slab_base = (size_t)slab_index * ring->rio_slab_size;
            if (recv_buf) {
                *recv_buf = ring->rio_buffer + slab_base + conn->recv_offset;
            }
            if (send_buf) {
                *send_buf = ring->rio_buffer + slab_base + conn->send_offset;
            }
            return (int)i;
        }
    }

    // Find free slot
    int slot = -1;
    for (uint32_t i = 0; i < ring->rio_max_connections; i++) {
        if (!ring->rio_connections[i].active) {
            slot = (int)i;
            break;
        }
    }

    if (slot < 0) {
        last_error = WSAENOBUFS;
        return -1;
    }

    // Ensure we have capacity for this slot (lazy commit)
    if (!ensure_rio_capacity(ring, (uint32_t)slot + 1)) {
        // last_error set by ensure_rio_capacity
        return -1;
    }

    rio_connection_t* conn = &ring->rio_connections[slot];

    // Verify RIO function is available
    if (!rio.RIOCreateRequestQueue) {
        last_error = ERROR_NOT_SUPPORTED;
        return -1;
    }

    // Create RIO request queue for this socket
    // Parameters: socket, maxOutstandingReceive, maxReceiveDataBuffers,
    //             maxOutstandingSend, maxSendDataBuffers, completionQueue,
    //             completionQueue (same for both), socketContext
    WSASetLastError(0);  // Clear any previous error
    conn->rq = rio.RIOCreateRequestQueue(
        socket,
        ring->rio_outstanding_per_socket,   // Max outstanding receives
        1,                                   // Max receive data buffers per op
        ring->rio_outstanding_per_socket,   // Max outstanding sends
        1,                                   // Max send data buffers per op
        ring->rio_cq,                        // Receive completion queue
        ring->rio_cq,                        // Send completion queue
        (PVOID)(uintptr_t)slot               // Socket context
    );

    if (conn->rq == RIO_INVALID_RQ) {
        last_error = WSAGetLastError();
        // If no error was set, it might be an internal issue
        if (last_error == 0) {
            last_error = ERROR_INVALID_PARAMETER;
        }
        return -1;
    }

    conn->socket = socket;
    conn->active = TRUE;
    ring->rio_active_connections++;

    // Return buffer pointers (using slab-based offsets)
    uint32_t slab_index = (uint32_t)slot / conns_per_slab;
    size_t slab_base = (size_t)slab_index * ring->rio_slab_size;
    if (recv_buf) {
        *recv_buf = ring->rio_buffer + slab_base + conn->recv_offset;
    }
    if (send_buf) {
        *send_buf = ring->rio_buffer + slab_base + conn->send_offset;
    }

    return slot;
}

IORING_API void ioring_rio_unregister(ioring_t* ring, int conn_id) {
    if (!ring || !ring->rio_mode) return;
    if (conn_id < 0 || (uint32_t)conn_id >= ring->rio_max_connections) return;

    rio_connection_t* conn = &ring->rio_connections[conn_id];
    if (!conn->active) return;

    // RIO request queue is automatically cleaned up when socket closes
    // Just mark the slot as free
    conn->socket = INVALID_SOCKET;
    conn->rq = RIO_INVALID_RQ;
    conn->active = FALSE;
    ring->rio_active_connections--;
}

IORING_API int ioring_is_rio(ioring_t* ring) {
    return ring && ring->rio_mode;
}

IORING_API uint32_t ioring_rio_get_active_connections(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_active_connections : 0;
}

// Lazy commit stats
IORING_API size_t ioring_rio_get_committed_bytes(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_buffer_committed : 0;
}

IORING_API size_t ioring_rio_get_reserved_bytes(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_buffer_reserved : 0;
}

IORING_API uint32_t ioring_rio_get_committed_slabs(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_committed_slabs : 0;
}

IORING_API uint32_t ioring_rio_get_committed_connections(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_committed_connections : 0;
}

// =============================================================================
// RIO AcceptEx Support (for server-side RIO)
// =============================================================================

// Get AcceptEx and GetAcceptExSockaddrs function pointers
static BOOL load_acceptex_functions(ioring_t* ring, SOCKET listen_socket) {
    if (ring->fn_acceptex) return TRUE;  // Already loaded

    GUID acceptex_guid = WSAID_ACCEPTEX;
    GUID getacceptexsockaddrs_guid = WSAID_GETACCEPTEXSOCKADDRS;
    DWORD bytes = 0;

    if (WSAIoctl(listen_socket, SIO_GET_EXTENSION_FUNCTION_POINTER,
                 &acceptex_guid, sizeof(acceptex_guid),
                 &ring->fn_acceptex, sizeof(ring->fn_acceptex),
                 &bytes, NULL, NULL) != 0) {
        return FALSE;
    }

    if (WSAIoctl(listen_socket, SIO_GET_EXTENSION_FUNCTION_POINTER,
                 &getacceptexsockaddrs_guid, sizeof(getacceptexsockaddrs_guid),
                 &ring->fn_getacceptexsockaddrs, sizeof(ring->fn_getacceptexsockaddrs),
                 &bytes, NULL, NULL) != 0) {
        return FALSE;
    }

    return TRUE;
}

// Find a free slot in the accept pool
static int find_free_accept_slot(ioring_t* ring) {
    for (uint32_t i = 0; i < ring->accept_pool_size; i++) {
        if (!ring->accept_pool[i].pending && !ring->accept_pool[i].completed) {
            return (int)i;
        }
    }
    return -1;
}

// Post an AcceptEx operation
// NOTE: RQ creation is deferred to check_acceptex_completions (after SO_UPDATE_ACCEPT_CONTEXT)
// This matches the pattern used by RioSharp and is required for proper RIO initialization
static BOOL post_acceptex(ioring_t* ring, SOCKET listen_socket, uint64_t user_data) {
    // Load AcceptEx if not already done
    if (!load_acceptex_functions(ring, listen_socket)) {
        last_error = WSAGetLastError();
        return FALSE;
    }

    // Find free slot in accept pool
    int slot = find_free_accept_slot(ring);
    if (slot < 0) {
        last_error = WSAENOBUFS;  // No free slots
        return FALSE;
    }

    acceptex_context_t* ctx = &ring->accept_pool[slot];

    // Create accept socket with RIO flag ONLY (no OVERLAPPED - that's for the operation, not the socket)
    // The working client socket test uses only WSA_FLAG_REGISTERED_IO
    ctx->accept_socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0,
                                     WSA_FLAG_REGISTERED_IO);
    if (ctx->accept_socket == INVALID_SOCKET) {
        last_error = WSAGetLastError();
        return FALSE;
    }

    // Reserve a connection slot (RQ will be created after AcceptEx completes)
    // BUG FIX: Must check BOTH active AND reserved flags to prevent slot collision
    int conn_slot = -1;
    for (uint32_t i = 0; i < ring->rio_max_connections; i++) {
        if (!ring->rio_connections[i].active && !ring->rio_connections[i].reserved) {
            conn_slot = i;
            break;
        }
    }

    if (conn_slot < 0) {
        closesocket(ctx->accept_socket);
        ctx->accept_socket = INVALID_SOCKET;
        last_error = WSAENOBUFS;
        return FALSE;
    }

    // Ensure we have buffer capacity for this slot (lazy commit)
    if (!ensure_rio_capacity(ring, (uint32_t)conn_slot + 1)) {
        closesocket(ctx->accept_socket);
        ctx->accept_socket = INVALID_SOCKET;
        // last_error set by ensure_rio_capacity
        return FALSE;
    }

    // IMMEDIATELY mark slot as reserved to prevent collision with concurrent AcceptEx calls
    ring->rio_connections[conn_slot].reserved = TRUE;

    // Store connection slot in context (RQ created later in check_acceptex_completions)
    ctx->rq = RIO_INVALID_RQ;  // Will be created after AcceptEx completes
    ctx->conn_slot = conn_slot;

    // Use event-based notification instead of IOCP (avoids potential RIO interference)
    // Initialize overlapped structure with event
    memset(&ctx->overlapped, 0, sizeof(ctx->overlapped));
    ResetEvent(ctx->event);  // Reset before use
    ctx->overlapped.hEvent = ctx->event;  // Event-based notification
    ctx->listen_socket = listen_socket;
    ctx->user_data = user_data;
    ctx->bytes_received = 0;
    ctx->completed = FALSE;
    ctx->pending = TRUE;

    // Post AcceptEx
    DWORD bytes_received = 0;
    BOOL result = ring->fn_acceptex(
        listen_socket,
        ctx->accept_socket,
        ctx->buffer,
        0,  // Don't receive data with accept
        ACCEPTEX_ADDR_SIZE,
        ACCEPTEX_ADDR_SIZE,
        &bytes_received,
        &ctx->overlapped
    );

    if (!result) {
        DWORD err = WSAGetLastError();
        if (err != ERROR_IO_PENDING) {
            // Real error - clear reservation
            ring->rio_connections[conn_slot].reserved = FALSE;
            closesocket(ctx->accept_socket);
            ctx->accept_socket = INVALID_SOCKET;
            ctx->pending = FALSE;
            ctx->conn_slot = -1;
            last_error = err;
            return FALSE;
        }
        // ERROR_IO_PENDING is expected - operation is pending
    }

    return TRUE;
}

// Check for completed AcceptEx operations and add to completion queue
static void check_acceptex_completions(ioring_t* ring) {
    if (!ring->accept_pool) return;

    // Iterate through all pending accept contexts and check for completion via events
    for (uint32_t i = 0; i < ring->accept_pool_size; i++) {
        acceptex_context_t* found_ctx = &ring->accept_pool[i];

        if (!found_ctx->pending) continue;

        // Check if the event is signaled (non-blocking)
        DWORD wait_result = WaitForSingleObject(found_ctx->event, 0);
        if (wait_result != WAIT_OBJECT_0) continue;  // Not completed yet

        // Event is signaled - AcceptEx has completed
        found_ctx->pending = FALSE;

        // Get the result using GetOverlappedResult
        DWORD bytes_transferred = 0;
        BOOL result = GetOverlappedResult((HANDLE)found_ctx->listen_socket,
                                          &found_ctx->overlapped,
                                          &bytes_transferred,
                                          FALSE);  // Don't wait

        if (result) {
            // AcceptEx completed successfully
            found_ctx->completed = TRUE;
            found_ctx->bytes_received = bytes_transferred;

            // Update socket context so getsockname/getpeername work
            // NOTE: This does NOT interfere with RIO - the fix was creating the
            // listener socket with WSA_FLAG_REGISTERED_IO (see ioring_rio_create_listener)
            setsockopt(found_ctx->accept_socket, SOL_SOCKET, SO_UPDATE_ACCEPT_CONTEXT,
                       (char*)&found_ctx->listen_socket, sizeof(found_ctx->listen_socket));

            int conn_slot = found_ctx->conn_slot;
            if (conn_slot >= 0 && (uint32_t)conn_slot < ring->rio_max_connections) {
                rio_connection_t* conn = &ring->rio_connections[conn_slot];

                // NOW create the RQ - AFTER SO_UPDATE_ACCEPT_CONTEXT (per Gemini/RioSharp pattern)
                // This is the key timing change from the previous implementation
                WSASetLastError(0);
                RIO_RQ rq = rio.RIOCreateRequestQueue(
                    found_ctx->accept_socket,
                    ring->rio_outstanding_per_socket,
                    1,
                    ring->rio_outstanding_per_socket,
                    1,
                    ring->rio_cq,
                    ring->rio_cq,
                    (PVOID)(uintptr_t)conn_slot
                );

                if (rq == RIO_INVALID_RQ) {
                    // RQ creation failed - report error and clean up
                    int rq_error = WSAGetLastError();
                    conn->reserved = FALSE;
                    closesocket(found_ctx->accept_socket);
                    complete_op(ring, found_ctx->user_data, -rq_error, 0);

                    found_ctx->accept_socket = INVALID_SOCKET;
                    found_ctx->conn_slot = -1;
                    found_ctx->completed = FALSE;
                    continue;  // Process next completion
                }

                // Success - activate the connection
                conn->socket = found_ctx->accept_socket;
                conn->rq = rq;
                conn->active = TRUE;
                conn->reserved = FALSE;  // Clear reservation now that it's active
                ring->rio_active_connections++;

                // Add completion to user-space CQ
                // Result is the accepted socket handle (users can also use conn_slot)
                complete_op(ring, found_ctx->user_data, (int32_t)found_ctx->accept_socket, 0);
            } else {
                // Invalid conn_slot - shouldn't happen, but handle gracefully
                closesocket(found_ctx->accept_socket);
                complete_op(ring, found_ctx->user_data, -EINVAL, 0);
            }

            // Reset context for reuse (socket now owned by connection slot)
            found_ctx->accept_socket = INVALID_SOCKET;
            found_ctx->conn_slot = -1;
            found_ctx->completed = FALSE;
        } else {
            // AcceptEx failed (client disconnected during handshake, etc.)
            DWORD err = GetLastError();
            complete_op(ring, found_ctx->user_data, -(int32_t)err, 0);

            // Clean up the socket
            if (found_ctx->accept_socket != INVALID_SOCKET) {
                closesocket(found_ctx->accept_socket);
                found_ctx->accept_socket = INVALID_SOCKET;
            }

            // Clear reservation on the connection slot so it can be reused
            int conn_slot = found_ctx->conn_slot;
            if (conn_slot >= 0 && (uint32_t)conn_slot < ring->rio_max_connections) {
                ring->rio_connections[conn_slot].reserved = FALSE;
            }

            found_ctx->rq = RIO_INVALID_RQ;
            found_ctx->conn_slot = -1;
        }
    }
}

IORING_API SOCKET ioring_rio_create_accept_socket(void) {
    init_winsock();

    // Create a socket with WSA_FLAG_REGISTERED_IO so it can be used with RIO
    // after AcceptEx completes
    SOCKET sock = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0, WSA_FLAG_REGISTERED_IO);
    if (sock == INVALID_SOCKET) {
        last_error = WSAGetLastError();
        return INVALID_SOCKET;
    }

    return sock;
}

// Cached AcceptEx function pointer
static LPFN_ACCEPTEX cached_acceptex = NULL;

IORING_API void* ioring_get_acceptex(SOCKET listen_socket) {
    if (cached_acceptex) {
        return cached_acceptex;
    }

    init_winsock();

    if (listen_socket == INVALID_SOCKET) {
        // Create a temporary socket to get the function pointer
        listen_socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listen_socket == INVALID_SOCKET) {
            last_error = WSAGetLastError();
            return NULL;
        }

        GUID acceptex_guid = WSAID_ACCEPTEX;
        DWORD bytes = 0;
        if (WSAIoctl(listen_socket, SIO_GET_EXTENSION_FUNCTION_POINTER,
                     &acceptex_guid, sizeof(acceptex_guid),
                     &cached_acceptex, sizeof(cached_acceptex),
                     &bytes, NULL, NULL) != 0) {
            last_error = WSAGetLastError();
            closesocket(listen_socket);
            return NULL;
        }
        closesocket(listen_socket);
    } else {
        GUID acceptex_guid = WSAID_ACCEPTEX;
        DWORD bytes = 0;
        if (WSAIoctl(listen_socket, SIO_GET_EXTENSION_FUNCTION_POINTER,
                     &acceptex_guid, sizeof(acceptex_guid),
                     &cached_acceptex, sizeof(cached_acceptex),
                     &bytes, NULL, NULL) != 0) {
            last_error = WSAGetLastError();
            return NULL;
        }
    }

    return cached_acceptex;
}

// =============================================================================
// RIO Listener Support
// =============================================================================

IORING_API SOCKET ioring_rio_create_listener(
    ioring_t* ring,
    const char* bind_addr,
    uint16_t port,
    int backlog
) {
    if (!ring) {
        last_error = WSAEINVAL;
        return INVALID_SOCKET;
    }

    init_winsock();

    // CRITICAL: Listener MUST have WSA_FLAG_REGISTERED_IO for AcceptEx sockets to work with RIO
    // Without this flag on the listener, RIO operations on accepted sockets silently fail
    // (RIOReceive returns success but completions never appear in the CQ)
    SOCKET listener = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0, WSA_FLAG_REGISTERED_IO);
    if (listener == INVALID_SOCKET) {
        last_error = WSAGetLastError();
        return INVALID_SOCKET;
    }

    // Set SO_REUSEADDR to allow quick restarts
    int opt = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, (char*)&opt, sizeof(opt));

    // Set non-blocking mode
    u_long mode = 1;
    ioctlsocket(listener, FIONBIO, &mode);

    // Bind to address
    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    if (bind_addr && bind_addr[0]) {
        // Use InetPtonA (modern replacement for inet_addr)
        if (InetPtonA(AF_INET, bind_addr, &addr.sin_addr) != 1) {
            closesocket(listener);
            last_error = WSAGetLastError();
            return INVALID_SOCKET;
        }
    } else {
        addr.sin_addr.s_addr = INADDR_ANY;
    }

    if (bind(listener, (struct sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) {
        last_error = WSAGetLastError();
        closesocket(listener);
        return INVALID_SOCKET;
    }

    // Start listening
    if (listen(listener, backlog) == SOCKET_ERROR) {
        last_error = WSAGetLastError();
        closesocket(listener);
        return INVALID_SOCKET;
    }

    // Associate with our AcceptEx IOCP (NOT the .NET runtime's IOCP)
    if (ring->accept_iocp) {
        if (!CreateIoCompletionPort((HANDLE)listener, ring->accept_iocp, (ULONG_PTR)listener, 0)) {
            last_error = GetLastError();
            closesocket(listener);
            return INVALID_SOCKET;
        }
    }

    // Track the listener in our owned_listeners array
    if (ring->owned_listener_count >= ring->owned_listener_capacity) {
        // Grow the array
        uint32_t new_capacity = ring->owned_listener_capacity ? ring->owned_listener_capacity * 2 : 4;
        SOCKET* new_array = (SOCKET*)realloc(ring->owned_listeners, new_capacity * sizeof(SOCKET));
        if (!new_array) {
            last_error = WSAENOBUFS;
            closesocket(listener);
            return INVALID_SOCKET;
        }
        ring->owned_listeners = new_array;
        ring->owned_listener_capacity = new_capacity;
    }
    ring->owned_listeners[ring->owned_listener_count++] = listener;

    // Cache AcceptEx function pointer if not already cached
    if (!ring->fn_acceptex) {
        GUID acceptex_guid = WSAID_ACCEPTEX;
        DWORD bytes = 0;
        WSAIoctl(listener, SIO_GET_EXTENSION_FUNCTION_POINTER,
                 &acceptex_guid, sizeof(acceptex_guid),
                 &ring->fn_acceptex, sizeof(ring->fn_acceptex),
                 &bytes, NULL, NULL);
    }

    if (!ring->fn_getacceptexsockaddrs) {
        GUID sockaddrs_guid = WSAID_GETACCEPTEXSOCKADDRS;
        DWORD bytes = 0;
        WSAIoctl(listener, SIO_GET_EXTENSION_FUNCTION_POINTER,
                 &sockaddrs_guid, sizeof(sockaddrs_guid),
                 &ring->fn_getacceptexsockaddrs, sizeof(ring->fn_getacceptexsockaddrs),
                 &bytes, NULL, NULL);
    }

    return listener;
}

IORING_API void ioring_rio_close_listener(ioring_t* ring, SOCKET listener) {
    if (!ring || listener == INVALID_SOCKET) return;

    // Cancel any pending AcceptEx operations for this listener
    if (ring->accept_pool) {
        for (uint32_t i = 0; i < ring->accept_pool_size; i++) {
            acceptex_context_t* ctx = &ring->accept_pool[i];
            if (ctx->pending && ctx->listen_socket == listener) {
                // Cancel the pending operation
                CancelIoEx((HANDLE)listener, &ctx->overlapped);

                // Clean up the accept socket
                if (ctx->accept_socket != INVALID_SOCKET) {
                    closesocket(ctx->accept_socket);
                    ctx->accept_socket = INVALID_SOCKET;
                }

                // Clear reservation on the connection slot
                if (ctx->conn_slot >= 0 && (uint32_t)ctx->conn_slot < ring->rio_max_connections) {
                    ring->rio_connections[ctx->conn_slot].reserved = FALSE;
                }

                ctx->pending = FALSE;
                ctx->rq = RIO_INVALID_RQ;
                ctx->conn_slot = -1;
            }
        }
    }

    // Remove from owned_listeners array
    for (uint32_t i = 0; i < ring->owned_listener_count; i++) {
        if (ring->owned_listeners[i] == listener) {
            // Shift remaining elements
            for (uint32_t j = i; j < ring->owned_listener_count - 1; j++) {
                ring->owned_listeners[j] = ring->owned_listeners[j + 1];
            }
            ring->owned_listener_count--;
            break;
        }
    }

    // Close the socket
    closesocket(listener);
}

IORING_API void ioring_rio_configure_socket(SOCKET socket) {
    if (socket == INVALID_SOCKET) return;

    // Set non-blocking
    u_long mode = 1;
    ioctlsocket(socket, FIONBIO, &mode);

    // Disable Nagle (TCP_NODELAY)
    int opt = 1;
    setsockopt(socket, IPPROTO_TCP, TCP_NODELAY, (char*)&opt, sizeof(opt));

    // Disable linger
    struct linger lin = { .l_onoff = 0, .l_linger = 0 };
    setsockopt(socket, SOL_SOCKET, SO_LINGER, (char*)&lin, sizeof(lin));
}

IORING_API SOCKET ioring_create_listener(const char* bind_addr, uint16_t port, int backlog) {
    // Create standard TCP socket (not RIO)
    SOCKET listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) {
        return INVALID_SOCKET;
    }

    // Set non-blocking
    u_long mode = 1;
    ioctlsocket(listener, FIONBIO, &mode);

    // Disable SO_REUSEADDR (exclusive address use)
    int opt = 0;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, (char*)&opt, sizeof(opt));

    // TCP_NODELAY
    opt = 1;
    setsockopt(listener, IPPROTO_TCP, TCP_NODELAY, (char*)&opt, sizeof(opt));

    // Disable linger
    struct linger lin = { .l_onoff = 0, .l_linger = 0 };
    setsockopt(listener, SOL_SOCKET, SO_LINGER, (char*)&lin, sizeof(lin));

    // Bind
    struct sockaddr_in addr = { 0 };
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    if (bind_addr && bind_addr[0]) {
        if (InetPtonA(AF_INET, bind_addr, &addr.sin_addr) != 1) {
            closesocket(listener);
            last_error = WSAGetLastError();
            return INVALID_SOCKET;
        }
    } else {
        addr.sin_addr.s_addr = INADDR_ANY;
    }

    if (bind(listener, (struct sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) {
        closesocket(listener);
        last_error = WSAGetLastError();
        return INVALID_SOCKET;
    }

    // Listen
    if (listen(listener, backlog) == SOCKET_ERROR) {
        closesocket(listener);
        return INVALID_SOCKET;
    }

    return listener;
}

// =============================================================================
// RIO External Buffer Support (for zero-copy from user-owned memory like Pipe)
// =============================================================================

IORING_API int ioring_rio_register_external_buffer(
    ioring_t* ring,
    void* buffer,
    uint32_t length
) {
    if (!ring || !ring->rio_mode) {
        last_error = EINVAL;
        return -1;
    }

    if (!buffer || length == 0) {
        last_error = EINVAL;
        return -1;
    }

    // Find a free slot
    int slot = -1;
    for (uint32_t i = 0; i < 16; i++) {
        if (ring->external_buffer_ids[i] == RIO_INVALID_BUFFERID) {
            slot = (int)i;
            break;
        }
    }

    if (slot < 0) {
        last_error = WSAENOBUFS;  // No free slots
        return -1;
    }

    // Register buffer with RIO
    RIO_BUFFERID buf_id = rio.RIORegisterBuffer(buffer, length);
    if (buf_id == RIO_INVALID_BUFFERID) {
        last_error = WSAGetLastError();
        return -1;
    }

    // Store the registration
    ring->external_buffer_ids[slot] = buf_id;
    ring->external_buffer_ptrs[slot] = buffer;
    ring->external_buffer_lengths[slot] = length;
    ring->external_buffer_count++;

    return slot;
}

IORING_API void ioring_rio_unregister_external_buffer(ioring_t* ring, int buffer_id) {
    if (!ring || !ring->rio_mode) return;
    if (buffer_id < 0 || buffer_id >= 16) return;

    if (ring->external_buffer_ids[buffer_id] != RIO_INVALID_BUFFERID) {
        rio.RIODeregisterBuffer(ring->external_buffer_ids[buffer_id]);
        ring->external_buffer_ids[buffer_id] = RIO_INVALID_BUFFERID;
        ring->external_buffer_ptrs[buffer_id] = NULL;
        ring->external_buffer_lengths[buffer_id] = 0;
        if (ring->external_buffer_count > 0) {
            ring->external_buffer_count--;
        }
    }
}

IORING_API void ioring_prep_send_external(
    ioring_sqe_t* sqe,
    int conn_id,
    int buffer_id,
    uint32_t offset,
    uint32_t len,
    uint64_t user_data
) {
    sqe->opcode = IORING_OP_SEND;
    sqe->fd = conn_id;
    sqe->flags = 0x81;  // 0x80 = registered buffer, 0x01 = external buffer flag
    sqe->addr = (uint64_t)offset;  // Store offset in addr field
    sqe->len = len;
    sqe->buf_index = (uint16_t)buffer_id;  // Store buffer_id in buf_index
    sqe->user_data = user_data;
}

IORING_API uint32_t ioring_rio_get_external_buffer_count(ioring_t* ring) {
    if (!ring || !ring->rio_mode) return 0;
    return ring->external_buffer_count;
}
