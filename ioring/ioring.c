// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

#include "ioring.h"

#include <ws2tcpip.h>
#include <mswsock.h>
#include <stdlib.h>
#include <string.h>
#include <intrin.h>  // For _mm_pause()

/*
 * Windows IORing Implementation with RIO (Registered I/O)
 *
 * This library provides high-performance async socket I/O using Windows RIO:
 * - Zero-copy I/O with pre-registered buffers
 * - Batched submissions and completions
 * - True async operations that don't block
 * - AcceptEx integration for server-side RIO
 *
 * Use ioring_create_rio_ex() to create a ring instance.
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

// Forward declarations
static void dequeue_rio_completions(ioring_t* ring);

// Internal ring structure
struct ioring {
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

    // RIO data
    RIO_CQ rio_cq;                      // RIO completion queue
    uint32_t rio_max_connections;       // Max concurrent connections
    uint32_t rio_active_connections;    // Current active connections
    uint32_t rio_outstanding_per_socket;// Max outstanding ops per direction per socket
    uint32_t rio_cq_size;               // Actual CQ size
    rio_connection_t* rio_connections;  // Per-connection state

    // Owned listener sockets (library creates these)
    SOCKET* owned_listeners;            // Array of listener sockets we created
    uint32_t owned_listener_count;
    uint32_t owned_listener_capacity;

    // AcceptEx support (RIO mode only)
    acceptex_context_t* accept_pool;    // Pool of pending AcceptEx contexts
    uint32_t accept_pool_size;          // Size of accept pool
    LPFN_ACCEPTEX fn_acceptex;          // Cached AcceptEx function pointer

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
    // Dynamically allocated based on max_connections * 3 (recv + send + pipe per connection)
    uint32_t max_external_buffers;           // Max external buffers (derived from max_connections)
    RIO_BUFFERID* external_buffer_ids;       // Registered buffer IDs (dynamically allocated)
    void** external_buffer_ptrs;             // Pointers for tracking (dynamically allocated)
    uint32_t* external_buffer_lengths;       // Lengths for validation (dynamically allocated)
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
// Synchronous Operations (poll, accept, close, shutdown)
// NOTE: recv/send MUST use RIO external buffers - legacy recv/send removed
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

IORING_API void ioring_destroy(ioring_t* ring) {
    if (!ring) return;

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

    // Cleanup external buffers (user is responsible for freeing memory)
    if (rio.RIODeregisterBuffer && ring->external_buffer_ids) {
        for (uint32_t i = 0; i < ring->max_external_buffers; i++) {
            if (ring->external_buffer_ids[i] != RIO_INVALID_BUFFERID) {
                rio.RIODeregisterBuffer(ring->external_buffer_ids[i]);
            }
        }
    }

    // Free external buffer arrays
    free(ring->external_buffer_ids);
    free(ring->external_buffer_ptrs);
    free(ring->external_buffer_lengths);

    free(ring->pending_ops);
    free(ring->sq);
    free(ring->cq);
    free(ring);
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
        // RECV/SEND removed - RIO mode requires external buffers
        case IORING_OP_SEND:
        case IORING_OP_RECV:
            complete_op(ring, sqe->user_data, -EINVAL, 0);
            return 1;
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

    // Check if this is an external buffer operation (flags = 0x81)
    // 0x80 = registered buffer mode, 0x01 = external buffer
    if ((sqe->flags & 0x81) == 0x81) {
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

        // External buffer - use buffer_id from sqe
        int ext_buf_id = (int)sqe->buf_index;
        if (ext_buf_id < 0 || (uint32_t)ext_buf_id >= ring->max_external_buffers ||
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

        RIO_BUF buf;
        buf.BufferId = ring->external_buffer_ids[ext_buf_id];
        buf.Offset = offset;
        buf.Length = sqe->len;

        BOOL success = FALSE;
        int retries = 0;
        const int max_retries = 3;

        if (sqe->opcode == IORING_OP_RECV) {
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

    // RIO mode requires external buffers for RECV/SEND operations
    // Non-RIO operations (POLL, CLOSE, SHUTDOWN, etc.) still use legacy path
    if (sqe->opcode == IORING_OP_RECV || sqe->opcode == IORING_OP_SEND) {
        // RECV/SEND in RIO mode requires external buffers
        complete_op(ring, sqe->user_data, -EINVAL, 0);
        return 1;
    }

    // Other operations (POLL, CLOSE, SHUTDOWN) use synchronous path
    return legacy_execute_op(ring, sqe);
}

// Process pending operations (poll and accept only - recv/send require RIO external buffers)
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
            // RECV/SEND removed - RIO mode requires external buffers
            case IORING_OP_RECV:
            case IORING_OP_SEND:
                // These should never be pending in RIO mode
                complete_op(ring, op->user_data, -EINVAL, 0);
                completed = TRUE;
                break;
        }

        if (completed) {
            ring->pending_ops[i] = ring->pending_ops[--ring->pending_count];
        } else {
            i++;
        }
    }
}

// Dequeue RIO completions (and AcceptEx completions)
static void dequeue_rio_completions(ioring_t* ring) {
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
        rio_execute_op(ring, sqe);
        ring->sq_head++;
        submitted++;
    }

    return submitted;
}

IORING_API int ioring_submit_and_wait(ioring_t* ring, uint32_t wait_nr) {
    int submitted = ioring_submit(ring);

    while (ioring_cq_ready(ring) < wait_nr) {
        dequeue_rio_completions(ring);
        process_pending_polls(ring);

        if (ioring_cq_ready(ring) >= wait_nr) break;

        _mm_pause();
    }

    return submitted;
}

IORING_API uint32_t ioring_peek_cqe(ioring_t* ring, ioring_cqe_t* cqes, uint32_t count) {
    if (!ring || !cqes) return 0;

    // First, dequeue any RIO completions
    dequeue_rio_completions(ring);

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
        dequeue_rio_completions(ring);
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
        ring->rio_connections[i].active = FALSE;
        ring->rio_connections[i].reserved = FALSE;
    }

    // Allocate external buffer tracking arrays
    // Size = max_connections * 3 to support recv + send + pipe per connection
    ring->max_external_buffers = max_connections * 3;
    ring->external_buffer_count = 0;
    ring->external_buffer_ids = calloc(ring->max_external_buffers, sizeof(RIO_BUFFERID));
    ring->external_buffer_ptrs = calloc(ring->max_external_buffers, sizeof(void*));
    ring->external_buffer_lengths = calloc(ring->max_external_buffers, sizeof(uint32_t));
    if (!ring->external_buffer_ids || !ring->external_buffer_ptrs || !ring->external_buffer_lengths) {
        goto fail;
    }
    // Initialize all buffer IDs to invalid
    for (uint32_t i = 0; i < ring->max_external_buffers; i++) {
        ring->external_buffer_ids[i] = RIO_INVALID_BUFFERID;
    }

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

    // Initialize AcceptEx pool for server-side RIO support
    // Pool size = min(max_connections, 256) to support high-volume servers
    uint32_t accept_pool_size = max_connections < 256 ? max_connections : 256;
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
    SOCKET socket
) {
    if (!ring) {
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

    // Check if this socket is already registered (e.g., from AcceptEx)
    for (uint32_t i = 0; i < ring->rio_max_connections; i++) {
        if (ring->rio_connections[i].active && ring->rio_connections[i].socket == socket) {
            // Socket already registered - just return the slot
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

    rio_connection_t* conn = &ring->rio_connections[slot];

    // Verify RIO function is available
    if (!rio.RIOCreateRequestQueue) {
        last_error = ERROR_NOT_SUPPORTED;
        return -1;
    }

    // Create RIO request queue for this socket
    WSASetLastError(0);
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
        if (last_error == 0) {
            last_error = ERROR_INVALID_PARAMETER;
        }
        return -1;
    }

    conn->socket = socket;
    conn->active = TRUE;
    ring->rio_active_connections++;

    return slot;
}

IORING_API void ioring_rio_unregister(ioring_t* ring, int conn_id) {
    if (!ring) return;
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

IORING_API SOCKET ioring_rio_get_socket(ioring_t* ring, int conn_id) {
    if (!ring) return INVALID_SOCKET;
    if (conn_id < 0 || (uint32_t)conn_id >= ring->rio_max_connections) return INVALID_SOCKET;

    rio_connection_t* conn = &ring->rio_connections[conn_id];
    return conn->active ? conn->socket : INVALID_SOCKET;
}

// =============================================================================
// RIO AcceptEx Support (for server-side RIO)
// =============================================================================

// Get AcceptEx function pointer
static BOOL load_acceptex_functions(ioring_t* ring, SOCKET listen_socket) {
    if (ring->fn_acceptex) return TRUE;  // Already loaded

    GUID acceptex_guid = WSAID_ACCEPTEX;
    DWORD bytes = 0;

    if (WSAIoctl(listen_socket, SIO_GET_EXTENSION_FUNCTION_POINTER,
                 &acceptex_guid, sizeof(acceptex_guid),
                 &ring->fn_acceptex, sizeof(ring->fn_acceptex),
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

    // Create accept socket WITH WSA_FLAG_REGISTERED_IO for RIO support.
    // NOTE: Sockets with WSA_FLAG_REGISTERED_IO can ONLY use RIO recv/send!
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
            setsockopt(found_ctx->accept_socket, SOL_SOCKET, SO_UPDATE_ACCEPT_CONTEXT,
                       (char*)&found_ctx->listen_socket, sizeof(found_ctx->listen_socket));

            // Clear the connection slot reservation (we're not creating an RQ yet)
            // The RQ will be created lazily when RegisterSocket is called
            int conn_slot = found_ctx->conn_slot;
            if (conn_slot >= 0 && (uint32_t)conn_slot < ring->rio_max_connections) {
                ring->rio_connections[conn_slot].reserved = FALSE;
            }

            // Return the socket handle directly
            // NOTE: Socket handles on x64 can theoretically exceed INT32_MAX, but in practice
            // Windows assigns small values. We truncate here for API compatibility.
            // The socket is created with WSA_FLAG_REGISTERED_IO so it CAN be registered
            // with RIO later via RegisterSocket, but it can also be used with legacy recv/send.
            complete_op(ring, found_ctx->user_data, (int32_t)found_ctx->accept_socket, 0);

            // Reset context for reuse (socket now owned by user)
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

// =============================================================================
// RIO External Buffer Support (for zero-copy from user-owned memory like Pipe)
// =============================================================================

IORING_API int ioring_rio_register_external_buffer(
    ioring_t* ring,
    void* buffer,
    uint32_t length
) {
    if (!ring) {
        last_error = EINVAL;
        return -1;
    }

    if (!buffer || length == 0) {
        last_error = EINVAL;
        return -1;
    }

    // Find a free slot
    int slot = -1;
    for (uint32_t i = 0; i < ring->max_external_buffers; i++) {
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
    if (!ring) return;
    if (buffer_id < 0 || (uint32_t)buffer_id >= ring->max_external_buffers) return;

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

IORING_API void ioring_prep_recv_external(
    ioring_sqe_t* sqe,
    int conn_id,
    int buffer_id,
    uint32_t offset,
    uint32_t len,
    uint64_t user_data
) {
    sqe->opcode = IORING_OP_RECV;
    sqe->fd = conn_id;
    sqe->flags = 0x81;  // 0x80 = registered buffer, 0x01 = external buffer flag
    sqe->addr = (uint64_t)offset;  // Store offset in addr field
    sqe->len = len;
    sqe->buf_index = (uint16_t)buffer_id;  // Store buffer_id in buf_index
    sqe->user_data = user_data;
}

IORING_API uint32_t ioring_rio_get_external_buffer_count(ioring_t* ring) {
    if (!ring) return 0;
    return ring->external_buffer_count;
}

IORING_API uint32_t ioring_rio_get_max_external_buffers(ioring_t* ring) {
    if (!ring) return 0;
    return ring->max_external_buffers;
}
