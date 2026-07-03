# Linux echo integration harness

Runs `TestServer` and `TestClient` against each other in containers so the Linux
backends (epoll and io_uring) can be exercised end-to-end from any host with
Docker. macOS/kqueue is not covered here — test that on a real machine.

The client's **ping-pong** mode (one message in flight, wait for each full echo)
is the correctness regression test: it keeps the server sitting with a recv armed
and no inbound data, which is the condition that exposes readiness-bridge bugs.
A stalled or dropped echo trips a per-connection timeout, the connection is
counted as failed, and the client exits non-zero (`--abort-on-container-exit
--exit-code-from client` propagates that as the compose exit code).

## Run it

epoll correctness (default):

```bash
docker compose -f docker/docker-compose.yml up --build \
  --abort-on-container-exit --exit-code-from client
```

epoll pipelined throughput:

```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.pipelined.yml \
  up --build --abort-on-container-exit --exit-code-from client
```

io_uring backend (the default Docker seccomp profile blocks the io_uring
syscalls, so the server runs `seccomp=unconfined` via the overlay — without it
the server would silently fall back to epoll):

```bash
BACKEND_FLAG=--ioring docker compose -f docker/docker-compose.yml \
  -f docker/docker-compose.iouring.yml up --build \
  --abort-on-container-exit --exit-code-from client
```

Tear down between runs:

```bash
docker compose -f docker/docker-compose.yml down --remove-orphans
```

## Knobs (environment variables)

| Var            | Default     | Meaning                                        |
|----------------|-------------|------------------------------------------------|
| `BACKEND_FLAG` | `--epoll`   | Server backend: `--epoll` or `--ioring`.       |
| `MESSAGES`     | `1000`      | Messages per connection (`5000` for pipelined).|
| `CONNECTIONS`  | `50`        | Concurrent connections.                        |

## Pass/fail signal

The client checks **bytes**, not message count: every message is echoed, so a
correct run round-trips `connections * messages * size * 2` bytes exactly. The
message count is only approximate in pipelined mode because TCP coalesces reads.
