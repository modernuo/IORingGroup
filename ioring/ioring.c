// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO

#include "ioring.h"

#include <ws2tcpip.h>
#include <mswsock.h>
#include <stdlib.h>
#include <string.h>
#include <intrin.h>  // For _mm_pause()

/*
 * Windows IORing Implementation
 *
 * Note: Windows 11 IoRing API (ioringapi.h) only supports FILE I/O operations,
 * NOT socket operations. For socket I/O, we use Registered I/O (RIO) which
 * provides high-performance async socket operations with completion queues.
 *
 * This library uses RIO for socket operations, with a user-space submission
 * queue to provide io_uring-like batched submission semantics.
 */

// RIO function table - primary mechanism for high-performance socket I/O
static RIO_EXTENSION_FUNCTION_TABLE rio_funcs = { 0 };
static BOOL rio_initialized = FALSE;

// Thread-local error
static __declspec(thread) int last_error = 0;

// Note: We always use RIO backend for socket operations since Windows IoRing
// doesn't support sockets. The ioring_backend enum is retained for API
// compatibility and future expansion.

// Poll mask translation between Linux io_uring style and Windows WSAPoll
// Linux poll values (used by our API):
#define LINUX_POLLIN    0x0001
#define LINUX_POLLPRI   0x0002
#define LINUX_POLLOUT   0x0004
#define LINUX_POLLERR   0x0008
#define LINUX_POLLHUP   0x0010
#define LINUX_POLLNVAL  0x0020

// Windows poll values (used by WSAPoll):
// POLLRDNORM = 0x0100, POLLIN = POLLRDNORM
// POLLWRNORM = 0x0010, POLLOUT = POLLWRNORM
// POLLERR = 0x0001, POLLHUP = 0x0002, POLLNVAL = 0x0004

static SHORT linux_to_windows_poll(uint32_t linux_mask) {
    SHORT win_mask = 0;
    if (linux_mask & LINUX_POLLIN)  win_mask |= POLLRDNORM;  // 0x0001 -> 0x0100
    if (linux_mask & LINUX_POLLPRI) win_mask |= POLLRDBAND;  // 0x0002 -> 0x0200
    if (linux_mask & LINUX_POLLOUT) win_mask |= POLLWRNORM;  // 0x0004 -> 0x0010
    if (linux_mask & LINUX_POLLERR) win_mask |= POLLERR;     // 0x0008 -> 0x0001
    if (linux_mask & LINUX_POLLHUP) win_mask |= POLLHUP;     // 0x0010 -> 0x0002
    if (linux_mask & LINUX_POLLNVAL) win_mask |= POLLNVAL;   // 0x0020 -> 0x0004
    return win_mask;
}

static uint32_t windows_to_linux_poll(SHORT win_mask) {
    uint32_t linux_mask = 0;
    if (win_mask & POLLRDNORM) linux_mask |= LINUX_POLLIN;   // 0x0100 -> 0x0001
    if (win_mask & POLLRDBAND) linux_mask |= LINUX_POLLPRI;  // 0x0200 -> 0x0002
    if (win_mask & POLLWRNORM) linux_mask |= LINUX_POLLOUT;  // 0x0010 -> 0x0004
    if (win_mask & POLLERR)    linux_mask |= LINUX_POLLERR;  // 0x0001 -> 0x0008
    if (win_mask & POLLHUP)    linux_mask |= LINUX_POLLHUP;  // 0x0002 -> 0x0010
    if (win_mask & POLLNVAL)   linux_mask |= LINUX_POLLNVAL; // 0x0004 -> 0x0020
    return linux_mask;
}

// Internal ring structure
struct ioring {
    ioring_backend_t backend;
    uint32_t sq_entries;
    uint32_t cq_entries;
    uint32_t sq_mask;
    uint32_t cq_mask;

    // Submission queue
    ioring_sqe_t* sq;
    uint32_t sq_head;
    uint32_t sq_tail;

    // Completion queue
    ioring_cqe_t* cq;
    uint32_t cq_head;
    uint32_t cq_tail;

    // RIO backend data (Windows IoRing doesn't support sockets, so RIO only)
    SOCKET rio_socket;          // Socket for RIO (one per ring for simplicity)
    RIO_CQ rio_cq;              // RIO completion queue
    RIO_RQ rio_rq;              // RIO request queue
    RIO_BUFFERID rio_buf_id;    // Registered buffer ID
    char* rio_buffer;           // Registered buffer
    uint32_t rio_buf_size;
    HANDLE rio_event;           // Completion event

    // Pending operations tracking (for RIO)
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
static BOOL ioring_available = FALSE;

static void init_backends(void) {
    if (initialized) return;
    initialized = TRUE;

    // Note: Windows IoRing API does NOT support socket operations.
    // We use RIO (Registered I/O) exclusively for socket I/O.

    // Initialize RIO - the primary backend for socket operations
    if (!rio_initialized) {
        WSADATA wsaData;
        if (WSAStartup(MAKEWORD(2, 2), &wsaData) == 0) {
            SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
            if (sock != INVALID_SOCKET) {
                GUID rio_guid = WSAID_MULTIPLE_RIO;
                DWORD bytes = 0;
                if (WSAIoctl(sock, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER,
                             &rio_guid, sizeof(rio_guid),
                             &rio_funcs, sizeof(rio_funcs),
                             &bytes, NULL, NULL) == 0) {
                    rio_initialized = TRUE;
                }
                closesocket(sock);
            }
        }
    }
}

static uint32_t next_power_of_two(uint32_t n) {
    n--;
    n |= n >> 1;
    n |= n >> 2;
    n |= n >> 4;
    n |= n >> 8;
    n |= n >> 16;
    return n + 1;
}

IORING_API ioring_t* ioring_create(uint32_t entries) {
    init_backends();

    if (entries == 0) entries = 4096;
    entries = next_power_of_two(entries);

    ioring_t* ring = calloc(1, sizeof(ioring_t));
    if (!ring) {
        last_error = ERROR_OUTOFMEMORY;
        return NULL;
    }

    ring->sq_entries = entries;
    ring->cq_entries = entries * 2;  // CQ is typically 2x SQ
    ring->sq_mask = entries - 1;
    ring->cq_mask = ring->cq_entries - 1;

    // Allocate queues
    ring->sq = (ioring_sqe_t*)calloc(entries, sizeof(ioring_sqe_t));
    ring->cq = (ioring_cqe_t*)calloc(ring->cq_entries, sizeof(ioring_cqe_t));
    if (!ring->sq || !ring->cq) {
        free(ring->sq);
        free(ring->cq);
        free(ring);
        last_error = ERROR_OUTOFMEMORY;
        return NULL;
    }

    // Allocate pending ops array for RIO
    ring->pending_capacity = entries;
    ring->pending_ops = (struct pending_op*)calloc(entries, sizeof(struct pending_op));
    if (!ring->pending_ops) {
        free(ring->sq);
        free(ring->cq);
        free(ring);
        last_error = ERROR_OUTOFMEMORY;
        return NULL;
    }

    // Use RIO backend for socket operations (Windows IoRing doesn't support sockets)
    if (rio_initialized) {
        ring->rio_event = CreateEventW(NULL, FALSE, FALSE, NULL);
        if (ring->rio_event == NULL) {
            goto fail;
        }

        // Create RIO completion queue
        RIO_NOTIFICATION_COMPLETION notify = { 0 };
        notify.Type = RIO_EVENT_COMPLETION;
        notify.Event.EventHandle = ring->rio_event;
        notify.Event.NotifyReset = TRUE;

        ring->rio_cq = rio_funcs.RIOCreateCompletionQueue(ring->cq_entries, &notify);
        if (ring->rio_cq == RIO_INVALID_CQ) {
            CloseHandle(ring->rio_event);
            goto fail;
        }

        ring->backend = IORING_BACKEND_RIO;
        ring->rio_socket = INVALID_SOCKET;
        return ring;
    }

fail:
    free(ring->pending_ops);
    free(ring->sq);
    free(ring->cq);
    free(ring);
    last_error = ERROR_NOT_SUPPORTED;
    return NULL;
}

IORING_API void ioring_destroy(ioring_t* ring) {
    if (!ring) return;

    if (ring->backend == IORING_BACKEND_RIO) {
        if (ring->rio_cq != RIO_INVALID_CQ) {
            rio_funcs.RIOCloseCompletionQueue(ring->rio_cq);
        }
        if (ring->rio_rq != RIO_INVALID_RQ) {
            // Note: RQ is closed when socket is closed
        }
        if (ring->rio_buf_id != RIO_INVALID_BUFFERID) {
            rio_funcs.RIODeregisterBuffer(ring->rio_buf_id);
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

// Helper to add pending operation
static BOOL add_pending_op(ioring_t* ring, uint64_t user_data, uint8_t opcode,
                           SOCKET fd, void* buf, uint32_t len, uint32_t flags,
                           void* addr, int addrlen) {
    if (ring->pending_count >= ring->pending_capacity) {
        return FALSE;
    }
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

// Helper to complete an operation
static void complete_op(ioring_t* ring, uint64_t user_data, int32_t res, uint32_t flags) {
    ring->cq[ring->cq_tail & ring->cq_mask] = (ioring_cqe_t){
        .user_data = user_data,
        .res = res,
        .flags = flags
    };
    ring->cq_tail++;
}

// Execute a single operation (non-blocking)
static int rio_execute_op(ioring_t* ring, ioring_sqe_t* sqe) {
    SOCKET fd = (SOCKET)sqe->fd;
    int err;

    switch (sqe->opcode) {
        case IORING_OP_POLL_ADD: {
            // Use WSAPoll for poll operations - always defer to pending
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
                // Set new socket to non-blocking
                u_long mode = 1;
                ioctlsocket(client, FIONBIO, &mode);
                // Set SO_LINGER to 0 to avoid TIME_WAIT accumulation
                struct linger lin = { 1, 0 };
                setsockopt(client, SOL_SOCKET, SO_LINGER, (char*)&lin, sizeof(lin));
                complete_op(ring, sqe->user_data, (int32_t)client, 0);
                return 1;
            }
            err = WSAGetLastError();
            if (err == WSAEWOULDBLOCK) {
                // Defer accept - add to pending
                if (!add_pending_op(ring, sqe->user_data, sqe->opcode, fd, NULL, 0, 0, addr, addrlen ? *addrlen : 0)) {
                    // Pending queue full - return error instead of silent drop
                    complete_op(ring, sqe->user_data, -WSAENOBUFS, 0);
                    return 1;
                }
                return 0;
            }
            complete_op(ring, sqe->user_data, -err, 0);
            return 1;
        }
        case IORING_OP_CONNECT: {
            const struct sockaddr* addr = (const struct sockaddr*)sqe->addr;
            int addrlen = (int)sqe->off;
            int res = connect(fd, addr, addrlen);
            if (res == 0) {
                complete_op(ring, sqe->user_data, 0, 0);
                return 1;
            }
            err = WSAGetLastError();
            if (err == WSAEWOULDBLOCK || err == WSAEINPROGRESS) {
                // Defer connect
                add_pending_op(ring, sqe->user_data, sqe->opcode, fd, NULL, 0, 0, (void*)addr, addrlen);
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
            // Don't defer - return error immediately, let caller use poll to retry
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
            // Don't defer - return error immediately, let caller use poll to retry
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
        case IORING_OP_CANCEL:
        case IORING_OP_POLL_REMOVE: {
            uint64_t target = sqe->addr;
            BOOL found = FALSE;
            for (uint32_t i = 0; i < ring->pending_count; i++) {
                if (ring->pending_ops[i].user_data == target) {
                    ring->pending_ops[i] = ring->pending_ops[--ring->pending_count];
                    found = TRUE;
                    break;
                }
            }
            complete_op(ring, sqe->user_data, found ? 0 : -ENOENT, 0);
            return 1;
        }
        default:
            complete_op(ring, sqe->user_data, -ENOSYS, 0);
            return 1;
    }
}

// Process pending operations (called during wait)
static void process_pending_ops(ioring_t* ring) {
    for (uint32_t i = 0; i < ring->pending_count; ) {
        struct pending_op* op = &ring->pending_ops[i];
        int err;
        BOOL completed = FALSE;

        switch (op->opcode) {
            case IORING_OP_POLL_ADD: {
                // Translate Linux-style poll mask to Windows poll mask
                WSAPOLLFD pfd = { op->fd, linux_to_windows_poll(op->flags), 0 };
                int result = WSAPoll(&pfd, 1, 0);
                if (result > 0) {
                    // Translate Windows revents back to Linux-style for the caller
                    complete_op(ring, op->user_data, windows_to_linux_poll(pfd.revents), 0);
                    completed = TRUE;
                } else if (result < 0) {
                    complete_op(ring, op->user_data, -WSAGetLastError(), 0);
                    completed = TRUE;
                }
                break;
            }
            case IORING_OP_ACCEPT: {
                SOCKET client = accept(op->fd, op->addr,
                                       op->addr ? &op->addrlen : NULL);
                if (client != INVALID_SOCKET) {
                    u_long mode = 1;
                    ioctlsocket(client, FIONBIO, &mode);
                    // Set SO_LINGER to 0 to avoid TIME_WAIT accumulation
                    struct linger lin = { 1, 0 };
                    setsockopt(client, SOL_SOCKET, SO_LINGER, (char*)&lin, sizeof(lin));
                    complete_op(ring, op->user_data, (int32_t)client, 0);
                    completed = TRUE;
                } else {
                    err = WSAGetLastError();
                    if (err != WSAEWOULDBLOCK) {
                        complete_op(ring, op->user_data, -err, 0);
                        completed = TRUE;
                    }
                }
                break;
            }
            case IORING_OP_CONNECT: {
                // Check if connect completed using select
                fd_set writefds, exceptfds;
                FD_ZERO(&writefds);
                FD_ZERO(&exceptfds);
                FD_SET(op->fd, &writefds);
                FD_SET(op->fd, &exceptfds);
                struct timeval tv = { 0, 0 };
                int result = select(0, NULL, &writefds, &exceptfds, &tv);
                if (result > 0) {
                    if (FD_ISSET(op->fd, &exceptfds)) {
                        int error = 0;
                        int len = sizeof(error);
                        getsockopt(op->fd, SOL_SOCKET, SO_ERROR, (char*)&error, &len);
                        complete_op(ring, op->user_data, -error, 0);
                    } else {
                        complete_op(ring, op->user_data, 0, 0);
                    }
                    completed = TRUE;
                }
                break;
            }
            case IORING_OP_SEND: {
                int sent = send(op->fd, op->buf, op->len, op->flags);
                if (sent >= 0) {
                    complete_op(ring, op->user_data, sent, 0);
                    completed = TRUE;
                } else {
                    err = WSAGetLastError();
                    if (err != WSAEWOULDBLOCK) {
                        complete_op(ring, op->user_data, -err, 0);
                        completed = TRUE;
                    }
                }
                break;
            }
            case IORING_OP_RECV: {
                int recvd = recv(op->fd, op->buf, op->len, op->flags);
                if (recvd >= 0) {
                    complete_op(ring, op->user_data, recvd, 0);
                    completed = TRUE;
                } else {
                    err = WSAGetLastError();
                    if (err != WSAEWOULDBLOCK) {
                        complete_op(ring, op->user_data, -err, 0);
                        completed = TRUE;
                    }
                }
                break;
            }
        }

        if (completed) {
            // Remove by swapping with last
            ring->pending_ops[i] = ring->pending_ops[--ring->pending_count];
        } else {
            i++;
        }
    }
}

IORING_API int ioring_submit(ioring_t* ring) {
    if (!ring) return -1;

    uint32_t submitted = 0;
    uint32_t to_submit = ring->sq_tail - ring->sq_head;

    // Execute operations using RIO backend
    // Note: We execute synchronously for simplicity. A more advanced
    // implementation could use RIO's async capabilities with RIOSend/RIOReceive.
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

    // If we need more completions, poll pending operations
    while (ioring_cq_ready(ring) < wait_nr && ring->pending_count > 0) {
        process_pending_ops(ring);
        // No sleep - let caller control timing
    }

    return submitted;
}

IORING_API uint32_t ioring_peek_cqe(ioring_t* ring, ioring_cqe_t* cqes, uint32_t count) {
    if (!ring || !cqes) return 0;

    uint32_t available = ioring_cq_ready(ring);
    uint32_t to_copy = count < available ? count : available;

    for (uint32_t i = 0; i < to_copy; i++) {
        cqes[i] = ring->cq[ring->cq_head + i & ring->cq_mask];
    }

    return to_copy;
}

IORING_API uint32_t ioring_wait_cqe(ioring_t* ring, ioring_cqe_t* cqes, uint32_t count, uint32_t min_complete, int timeout_ms) {
    if (!ring || !cqes) return 0;

    DWORD start = GetTickCount();
    DWORD elapsed = 0;
    int spin_count = 0;

    while (ioring_cq_ready(ring) < min_complete) {
        // Process all pending operations
        process_pending_ops(ring);

        if (ioring_cq_ready(ring) >= min_complete) break;

        elapsed = GetTickCount() - start;
        if (timeout_ms >= 0 && elapsed >= (DWORD)timeout_ms) break;

        // Spin briefly before yielding (reduces latency for quick operations)
        if (++spin_count < 100) {
            _mm_pause();  // CPU hint for spin-wait
        } else {
            spin_count = 0;
            SwitchToThread();  // Yield without adding latency like Sleep(1)
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
    // Simple error messages
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
