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

// Connection state for RIO mode
typedef struct rio_connection {
    SOCKET socket;
    RIO_RQ rq;              // Request queue for this socket
    uint32_t recv_offset;   // Offset in buffer for recv
    uint32_t send_offset;   // Offset in buffer for send
    BOOL active;
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
    uint64_t user_data;         // User data to return with completion
    BOOL pending;               // TRUE if AcceptEx is pending
    BOOL completed;             // TRUE if AcceptEx has completed (check result)
    DWORD bytes_received;       // Bytes received by AcceptEx
} acceptex_context_t;

// RIO configuration defaults
#define RIO_DEFAULT_OUTSTANDING_PER_SOCKET 2  // 1 recv + 1 send typical for echo
#define RIO_MAX_OUTSTANDING_PER_SOCKET 16     // Upper limit per direction

// Forward declarations
static void dequeue_rio_completions(ioring_t* ring);

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
    BOOL rio_mode;                  // True if created with ioring_create_rio
    RIO_CQ rio_cq;                  // RIO completion queue
    RIO_BUFFERID rio_buf_id;        // Registered buffer handle
    char* rio_buffer;               // The actual buffer memory
    uint32_t rio_buffer_size;       // Total buffer size
    uint32_t rio_recv_buf_size;     // Per-connection recv buffer size
    uint32_t rio_send_buf_size;     // Per-connection send buffer size
    uint32_t rio_max_connections;   // Max concurrent connections
    uint32_t rio_active_connections;// Current active connections
    uint32_t rio_outstanding_per_socket; // Max outstanding ops per direction per socket
    uint32_t rio_cq_size;           // Actual CQ size
    rio_connection_t* rio_connections; // Per-connection state
    HANDLE rio_event;               // Event for completion notification

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
        // Cleanup AcceptEx pool
        if (ring->accept_pool) {
            for (uint32_t i = 0; i < ring->accept_pool_size; i++) {
                if (ring->accept_pool[i].accept_socket != INVALID_SOCKET) {
                    closesocket(ring->accept_pool[i].accept_socket);
                }
            }
            free(ring->accept_pool);
        }
        if (ring->accept_iocp) {
            CloseHandle(ring->accept_iocp);
        }

        // Cleanup RIO resources
        if (ring->rio_connections) {
            for (uint32_t i = 0; i < ring->rio_max_connections; i++) {
                if (ring->rio_connections[i].active && ring->rio_connections[i].rq != RIO_INVALID_RQ) {
                    // RQ is automatically cleaned up when socket closes
                }
            }
            free(ring->rio_connections);
        }
        if (ring->rio_cq != RIO_INVALID_CQ) {
            rio.RIOCloseCompletionQueue(ring->rio_cq);
        }
        if (ring->rio_buf_id != RIO_INVALID_BUFFERID) {
            rio.RIODeregisterBuffer(ring->rio_buf_id);
        }
        if (ring->rio_buffer) {
            VirtualFree(ring->rio_buffer, 0, MEM_RELEASE);
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

// Forward declaration
static void check_acceptex_completions(ioring_t* ring);

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
        buf.BufferId = ring->rio_buf_id;

        BOOL success = FALSE;
        int retries = 0;
        const int max_retries = 3;

        if (sqe->opcode == IORING_OP_RECV) {
            buf.Offset = conn->recv_offset;
            buf.Length = sqe->len;

            while (!success && retries < max_retries) {
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
            buf.Offset = conn->send_offset;
            buf.Length = sqe->len;

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

// Dequeue RIO completions (and AcceptEx completions)
static void dequeue_rio_completions(ioring_t* ring) {
    if (!ring->rio_mode) return;

    // First check for AcceptEx completions
    if (ring->accept_iocp) {
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

IORING_API const char* ioring_strerror(int error) {
    switch (error) {
        case 0: return "Success";
        case ERROR_OUTOFMEMORY: return "Out of memory";
        case ERROR_NOT_SUPPORTED: return "Not supported";
        case WSAEWOULDBLOCK: return "Would block";
        case WSAENOTCONN: return "Not connected";
        case WSAECONNRESET: return "Connection reset";
        default: return "Unknown error";
    }
}

// =============================================================================
// High-Performance RIO API Implementation
// =============================================================================

IORING_API ioring_t* ioring_create_rio(
    uint32_t entries,
    uint32_t max_connections,
    uint32_t recv_buffer_size,
    uint32_t send_buffer_size
) {
    return ioring_create_rio_ex(entries, max_connections, recv_buffer_size,
                                 send_buffer_size, RIO_DEFAULT_OUTSTANDING_PER_SOCKET);
}

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
    ring->rio_recv_buf_size = recv_buffer_size;
    ring->rio_send_buf_size = send_buffer_size;
    ring->rio_max_connections = max_connections;
    ring->rio_outstanding_per_socket = outstanding_per_socket;
    ring->rio_cq = RIO_INVALID_CQ;
    ring->rio_buf_id = RIO_INVALID_BUFFERID;

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
    for (uint32_t i = 0; i < max_connections; i++) {
        ring->rio_connections[i].rq = RIO_INVALID_RQ;
    }

    // Calculate total buffer size (recv + send per connection)
    uint32_t per_connection = recv_buffer_size + send_buffer_size;
    ring->rio_buffer_size = per_connection * max_connections;

    // Allocate RIO buffer (must be page-aligned)
    ring->rio_buffer = (char*)VirtualAlloc(NULL, ring->rio_buffer_size,
                                            MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!ring->rio_buffer) {
        goto fail;
    }

    // Register buffer with RIO
    ring->rio_buf_id = rio.RIORegisterBuffer(ring->rio_buffer, ring->rio_buffer_size);
    if (ring->rio_buf_id == RIO_INVALID_BUFFERID) {
        goto fail;
    }

    // Create completion event
    ring->rio_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!ring->rio_event) {
        goto fail;
    }

    // Create RIO completion queue
    // CQ must be large enough for all outstanding operations across all RQs
    // CQ size = max_connections * outstanding_per_socket * 2 (recv + send)
    uint32_t rio_cq_size = max_connections * outstanding_per_socket * 2;
    if (rio_cq_size < ring->cq_entries) {
        rio_cq_size = ring->cq_entries;
    }
    // RIO CQ max is typically around 8 million, but let's cap at something reasonable
    if (rio_cq_size > 2000000) {
        rio_cq_size = 2000000;
    }
    ring->rio_cq_size = rio_cq_size;

    // Use polling mode (no notification) - we'll poll with RIODequeueCompletion
    // Event-based notification can cause issues on some systems
    ring->rio_cq = rio.RIOCreateCompletionQueue(rio_cq_size, NULL);
    if (ring->rio_cq == RIO_INVALID_CQ) {
        goto fail;
    }

    // Initialize AcceptEx pool for server-side RIO support
    // Size the pool to handle a reasonable number of concurrent accepts
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
    }

    // Create IOCP for AcceptEx completion notifications
    ring->accept_iocp = CreateIoCompletionPort(INVALID_HANDLE_VALUE, NULL, 0, 1);
    if (!ring->accept_iocp) {
        goto fail;
    }

    // Initialize connection buffer offsets
    for (uint32_t i = 0; i < max_connections; i++) {
        ring->rio_connections[i].recv_offset = i * per_connection;
        ring->rio_connections[i].send_offset = i * per_connection + recv_buffer_size;
    }

    return ring;

fail:
    last_error = GetLastError();
    if (last_error == 0) last_error = ERROR_OUTOFMEMORY;
    ioring_destroy(ring);
    return NULL;
}

// Diagnostic: Check if RIO is properly initialized
IORING_API int ioring_rio_check_init(void) {
    if (!rio_initialized) return -1;
    if (!rio.RIOCreateRequestQueue) return -2;
    if (!rio.RIOCreateCompletionQueue) return -3;
    if (!rio.RIORegisterBuffer) return -4;
    if (!rio.RIOReceive) return -5;
    if (!rio.RIOSend) return -6;
    if (!rio.RIODequeueCompletion) return -7;
    return 0;  // All good
}

// Diagnostic: Test RIO setup with a fresh socket we create ourselves
// Returns 0 on success, negative error code on failure
// This helps diagnose if the issue is with RIO setup or with external sockets
//
// Return codes:
// -1: RIO init failed
// -2: Socket creation failed
// -3: Bind failed
// -4: Buffer alloc failed
// -5: Buffer registration failed
// -6: CQ creation failed
// -7: RQ creation failed on accepted socket (expected - accept() doesn't inherit WSA_FLAG_REGISTERED_IO)
// -8: Listen failed
// -9: Client socket creation failed
// -10: Connect failed
// -11: Accept failed
// -12: RQ creation failed on CLIENT socket (this would indicate a real problem)
// 1: RQ works on client but not accepted socket (expected behavior)
IORING_API int ioring_rio_test_setup(void) {
    if (!init_rio()) {
        last_error = 1;  // RIO init failed
        return -1;
    }

    // Allocate a small buffer first
    char* buffer = (char*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!buffer) {
        last_error = GetLastError();
        return -4;  // Buffer alloc failed
    }

    // Register buffer
    RIO_BUFFERID buf_id = rio.RIORegisterBuffer(buffer, 4096);
    if (buf_id == RIO_INVALID_BUFFERID) {
        last_error = WSAGetLastError();
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -5;  // Buffer registration failed
    }

    // Create completion queue (polling mode)
    RIO_CQ cq = rio.RIOCreateCompletionQueue(256, NULL);
    if (cq == RIO_INVALID_CQ) {
        last_error = WSAGetLastError();
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -6;  // CQ creation failed
    }

    // Create listener socket
    SOCKET listener = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0, WSA_FLAG_REGISTERED_IO);
    if (listener == INVALID_SOCKET) {
        last_error = WSAGetLastError();
        rio.RIOCloseCompletionQueue(cq);
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -2;  // Socket creation failed
    }

    // Bind listener
    struct sockaddr_in addr = { 0 };
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    if (bind(listener, (struct sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) {
        last_error = WSAGetLastError();
        closesocket(listener);
        rio.RIOCloseCompletionQueue(cq);
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -3;  // Bind failed
    }

    // Listen
    if (listen(listener, 1) == SOCKET_ERROR) {
        last_error = WSAGetLastError();
        closesocket(listener);
        rio.RIOCloseCompletionQueue(cq);
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -8;  // Listen failed
    }

    // Get the port we bound to
    int addrlen = sizeof(addr);
    getsockname(listener, (struct sockaddr*)&addr, &addrlen);

    // Create client socket and connect
    SOCKET client = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0, WSA_FLAG_REGISTERED_IO);
    if (client == INVALID_SOCKET) {
        last_error = WSAGetLastError();
        closesocket(listener);
        rio.RIOCloseCompletionQueue(cq);
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -9;  // Client socket creation failed
    }

    // Connect (non-blocking would be better but for test this is fine)
    if (connect(client, (struct sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) {
        last_error = WSAGetLastError();
        closesocket(client);
        closesocket(listener);
        rio.RIOCloseCompletionQueue(cq);
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -10;  // Connect failed
    }

    // Accept
    SOCKET server = accept(listener, NULL, NULL);
    if (server == INVALID_SOCKET) {
        last_error = WSAGetLastError();
        closesocket(client);
        closesocket(listener);
        rio.RIOCloseCompletionQueue(cq);
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -11;  // Accept failed
    }

    // First, try to create RQ on the CLIENT socket (created with WSA_FLAG_REGISTERED_IO)
    // This SHOULD work
    WSASetLastError(0);
    RIO_RQ client_rq = rio.RIOCreateRequestQueue(client, 2, 1, 2, 1, cq, cq, NULL);
    if (client_rq == RIO_INVALID_RQ) {
        // If even the client socket fails, there's a fundamental RIO problem
        last_error = WSAGetLastError();
        closesocket(server);
        closesocket(client);
        closesocket(listener);
        rio.RIOCloseCompletionQueue(cq);
        rio.RIODeregisterBuffer(buf_id);
        VirtualFree(buffer, 0, MEM_RELEASE);
        return -12;  // RQ creation failed on CLIENT socket - real problem!
    }

    // Now try to create RQ on the accepted server socket
    // This is expected to FAIL with 10045 because accept() doesn't inherit WSA_FLAG_REGISTERED_IO
    WSASetLastError(0);
    RIO_RQ server_rq = rio.RIOCreateRequestQueue(server, 2, 1, 2, 1, cq, cq, NULL);
    BOOL server_rq_works = (server_rq != RIO_INVALID_RQ);
    if (!server_rq_works) {
        last_error = WSAGetLastError();  // Save error for diagnostics
    }

    // Clean up
    closesocket(server);
    closesocket(client);
    closesocket(listener);
    rio.RIOCloseCompletionQueue(cq);
    rio.RIODeregisterBuffer(buf_id);
    VirtualFree(buffer, 0, MEM_RELEASE);

    if (server_rq_works) {
        return 0;  // Both worked - full RIO support!
    } else {
        return 1;  // Client works, server doesn't - expected (need AcceptEx with pre-created sockets)
    }
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

    // Return buffer pointers
    if (recv_buf) {
        *recv_buf = ring->rio_buffer + conn->recv_offset;
    }
    if (send_buf) {
        *send_buf = ring->rio_buffer + conn->send_offset;
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

IORING_API uint32_t ioring_rio_get_recv_buffer_size(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_recv_buf_size : 0;
}

IORING_API uint32_t ioring_rio_get_send_buffer_size(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_send_buf_size : 0;
}

IORING_API int ioring_is_rio(ioring_t* ring) {
    return ring && ring->rio_mode;
}

IORING_API uint32_t ioring_rio_get_max_connections(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_max_connections : 0;
}

IORING_API uint32_t ioring_rio_get_active_connections(ioring_t* ring) {
    return ring && ring->rio_mode ? ring->rio_active_connections : 0;
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

    // Create accept socket with RIO flag
    ctx->accept_socket = WSASocketW(AF_INET, SOCK_STREAM, IPPROTO_TCP, NULL, 0,
                                     WSA_FLAG_REGISTERED_IO | WSA_FLAG_OVERLAPPED);
    if (ctx->accept_socket == INVALID_SOCKET) {
        last_error = WSAGetLastError();
        return FALSE;
    }

    // Associate accept socket with IOCP for completion notification
    if (CreateIoCompletionPort((HANDLE)listen_socket, ring->accept_iocp, (ULONG_PTR)slot, 0) == NULL) {
        // Might already be associated, that's OK
        DWORD err = GetLastError();
        if (err != ERROR_INVALID_PARAMETER) {
            closesocket(ctx->accept_socket);
            ctx->accept_socket = INVALID_SOCKET;
            last_error = err;
            return FALSE;
        }
    }

    // Initialize overlapped structure
    memset(&ctx->overlapped, 0, sizeof(ctx->overlapped));
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
            // Real error
            closesocket(ctx->accept_socket);
            ctx->accept_socket = INVALID_SOCKET;
            ctx->pending = FALSE;
            last_error = err;
            return FALSE;
        }
        // ERROR_IO_PENDING is expected - operation is pending
    }

    return TRUE;
}

// Check for completed AcceptEx operations and add to completion queue
static void check_acceptex_completions(ioring_t* ring) {
    if (!ring->accept_iocp) return;

    DWORD bytes_transferred;
    ULONG_PTR completion_key;
    LPOVERLAPPED overlapped;

    // Non-blocking check for completions
    while (GetQueuedCompletionStatus(ring->accept_iocp, &bytes_transferred,
                                      &completion_key, &overlapped, 0)) {
        if (!overlapped) continue;

        // Find the accept context from the overlapped pointer
        for (uint32_t i = 0; i < ring->accept_pool_size; i++) {
            acceptex_context_t* ctx = &ring->accept_pool[i];
            if (&ctx->overlapped == overlapped && ctx->pending) {
                // AcceptEx completed
                ctx->pending = FALSE;
                ctx->completed = TRUE;
                ctx->bytes_received = bytes_transferred;

                // Update socket context so getsockname/getpeername work
                setsockopt(ctx->accept_socket, SOL_SOCKET, SO_UPDATE_ACCEPT_CONTEXT,
                           (char*)&ctx->listen_socket, sizeof(ctx->listen_socket));

                // Set non-blocking and linger options
                u_long mode = 1;
                ioctlsocket(ctx->accept_socket, FIONBIO, &mode);
                struct linger lin = { 1, 0 };
                setsockopt(ctx->accept_socket, SOL_SOCKET, SO_LINGER, (char*)&lin, sizeof(lin));

                // Add completion to user-space CQ
                // Result is the accepted socket handle
                complete_op(ring, ctx->user_data, (int32_t)ctx->accept_socket, 0);

                // Reset context for reuse (socket ownership transferred to user)
                ctx->accept_socket = INVALID_SOCKET;
                ctx->completed = FALSE;
                break;
            }
        }
    }

    // Check for failed completions
    DWORD err = GetLastError();
    if (err != WAIT_TIMEOUT && overlapped) {
        // Find and handle the failed accept
        for (uint32_t i = 0; i < ring->accept_pool_size; i++) {
            acceptex_context_t* ctx = &ring->accept_pool[i];
            if (&ctx->overlapped == overlapped && ctx->pending) {
                ctx->pending = FALSE;
                complete_op(ring, ctx->user_data, -(int32_t)err, 0);

                // Clean up the socket
                if (ctx->accept_socket != INVALID_SOCKET) {
                    closesocket(ctx->accept_socket);
                    ctx->accept_socket = INVALID_SOCKET;
                }
                break;
            }
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
