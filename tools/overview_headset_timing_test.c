/* overview_headset_timing_test.c - the read-deadline cases for the headset module.
 *
 * These cases open a socket on the loopback, so they are deliberately NOT part
 * of the hermetic self-test (overview_headset_test.c), which must never open
 * one.  The mock server here speaks the same framing the module uses: it reads
 * the framed service request, answers OKAY, and then sends the reply after a
 * delay, or in several chunks with a gap between them.
 *
 * What is checked:
 *   - a reply that arrives later than the socket poll interval but before the
 *     deadline is read (the old code gave up at the first poll timeout);
 *   - a reply that is chunked with a gap longer than the poll interval is
 *     read whole;
 *   - a peer that never answers is given up on at the deadline, not before it
 *     and not after it.
 */
#define _GNU_SOURCE
#include "overview_headset.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

static int g_failures = 0;

#define CHECK(cond, what) do { \
    if (cond) { printf("  ok   %s\n", (what)); } \
    else { printf("  FAIL %s\n", (what)); g_failures++; } \
} while (0)

static long long now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (long long)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}

/* ---- the mock server ---- */

typedef struct {
    int    port;
    long   delay_ms;   /* the wait before the first byte of the reply */
    long   gap_ms;     /* the wait between two chunks */
    int    chunks;     /* how many chunks the reply is split into */
    size_t reply_len;
    int    hang;       /* answer OKAY and then never say anything */
} OvMock;

static OvMock g_mock;

/* The listener is created before the client connects and handed to the thread,
 * so there is no race between the two. */
static int g_listen_fd = -1;

static void *mock_serve(void *arg) {
    (void)arg;
    /* accept on the listener the test already created */
    int fd = accept(g_listen_fd, NULL, NULL);
    if (fd < 0) return NULL;
    /* read the framed service request */
    char head[5] = { 0 };
    if (recv(fd, head, 4, 0) != 4) { close(fd); return NULL; }
    size_t n = strtoul(head, NULL, 16);
    char *svc = malloc(n + 1);
    if (!svc) { close(fd); return NULL; }
    if (recv(fd, svc, n, 0) != (ssize_t)n) { free(svc); close(fd); return NULL; }
    svc[n] = 0;
    free(svc);
    if (send(fd, "OKAY", 4, 0) != 4) { close(fd); return NULL; }
    /* the status the module expects after OKAY */
    if (g_mock.hang) {
        /* hold the connection open and say nothing */
        struct timespec ts = { .tv_sec = 2, .tv_nsec = 0 };
        nanosleep(&ts, NULL);
        close(fd);
        return NULL;
    }
    usleep((useconds_t)(g_mock.delay_ms * 1000));
    /* the reply: a length-prefixed host payload */
    char *body = malloc(g_mock.reply_len);
    if (!body) { close(fd); return NULL; }
    for (size_t i = 0; i < g_mock.reply_len; i++) body[i] = (char)('a' + (i % 26));
    char hdr[16];
    int hl = snprintf(hdr, sizeof hdr, "%04zx", g_mock.reply_len);
    if (send(fd, hdr, hl, 0) != hl) { free(body); close(fd); return NULL; }
    size_t per = g_mock.chunks > 0 ? (g_mock.reply_len + (size_t)g_mock.chunks - 1) / (size_t)g_mock.chunks : g_mock.reply_len;
    if (!per) per = 1;
    for (size_t at = 0; at < g_mock.reply_len; at += per) {
        size_t take = g_mock.reply_len - at < per ? g_mock.reply_len - at : per;
        if (send(fd, body + at, take, 0) != (ssize_t)take) break;
        if (g_mock.gap_ms > 0) usleep((useconds_t)(g_mock.gap_ms * 1000));
    }
    free(body);
    close(fd);
    return NULL;
}

static int mock_start(int port, long delay_ms, long gap_ms, int chunks, size_t reply_len, int hang) {
    g_mock.port = port;
    g_mock.delay_ms = delay_ms;
    g_mock.gap_ms = gap_ms;
    g_mock.chunks = chunks;
    g_mock.reply_len = reply_len;
    g_mock.hang = hang;
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    struct sockaddr_in sa = { .sin_family = AF_INET, .sin_port = htons((uint16_t)port),
                              .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
    if (bind(fd, (struct sockaddr *)&sa, sizeof sa) != 0 || listen(fd, 4) != 0) { close(fd); return -1; }
    g_listen_fd = fd;
    return 0;
}

/* One case: run the mock in a thread, ask the module, and report how long it
 * took and what it got. */
static void run_case(const char *label, int port, long delay_ms, long gap_ms,
                     int chunks, size_t reply_len, int hang, int timeout_ms,
                     int expect_ok, long min_ms, long max_ms) {
    if (mock_start(port, delay_ms, gap_ms, chunks, reply_len, hang) != 0) {
        printf("  FAIL %s (the mock server could not start)\n", label);
        g_failures++;
        return;
    }
    pthread_t th;
    pthread_create(&th, NULL, mock_serve, NULL);
    ov_hs_set_port(port);
    ov_hs_set_timeout(timeout_ms);
    char out[65536];
    memset(out, 0, sizeof out);
    long long t0 = now_ms();
    int ok = ov_adb_host_query("host:devices-l", out, sizeof out);
    long long took = now_ms() - t0;
    pthread_join(th, NULL);
    if (g_listen_fd >= 0) { close(g_listen_fd); g_listen_fd = -1; }
    printf("  %-46s ok=%d took=%lldms len=%zu\n", label, ok, took, ok ? strlen(out) : 0);
    CHECK((ok != 0) == (expect_ok != 0), label);
    if (expect_ok && ok) {
        CHECK(strlen(out) == reply_len, "the whole reply arrived");
    }
    if (min_ms >= 0) CHECK(took >= min_ms, "the read waited for the reply");
    if (max_ms >= 0) CHECK(took <= max_ms, "the read gave up at the deadline");
}

int main(void) {
    /* The mock writes to a socket the client may already have closed: that is
     * the case under test, and it must not kill the test. */
    signal(SIGPIPE, SIG_IGN);
    setvbuf(stdout, NULL, _IONBF, 0);
    printf("headset read-deadline self-test\n");
    g_failures = 0;

    /* The poll interval is 50 ms; the deadline is 1500 ms.  A reply that comes
     * after 200 ms used to be dropped at the first poll timeout. */
    run_case("a reply delayed past the poll interval is read", 15531,
             200, 0, 1, 64, 0, 1500, 1, 190, 1500);

    /* A reply that arrives in chunks with a gap longer than the poll interval. */
    run_case("a chunked reply with a long gap is read whole", 15532,
             0, 120, 4, 256, 0, 1500, 1, 0, 1500);

    /* A peer that never answers: the read stops at the deadline. */
    run_case("a peer that never answers stops at the deadline", 15533,
             0, 0, 0, 0, 1, 600, 0, 500, 1400);

    /* A reply that arrives after the deadline is refused, not half-read. */
    run_case("a reply after the deadline is refused", 15534,
             1200, 0, 1, 64, 0, 400, 0, 380, 1400);

    printf("%s (%d failure(s))\n",
           g_failures ? "HEADSET TIMING SELF-TEST FAILED" : "headset timing self-test passed",
           g_failures);
    return g_failures ? 1 : 0;
}
