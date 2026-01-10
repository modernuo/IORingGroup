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
    int32_t fd;             // File descriptor (socket)
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
    IORING_BACKEND_IORING = 1,  // Windows 11+ IoRing
    IORING_BACKEND_RIO = 2,     // Windows 8+ Registered I/O
} ioring_backend_t;

// Create/destroy ring
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

#ifdef __cplusplus
}
#endif

#endif // IORING_H
