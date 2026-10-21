/* overview_headset.c - the Quest / WiVRn read-only state for tools/overview.
 *
 * The rules this file keeps:
 *   - the adb binary is never executed; the ADB server protocol is spoken
 *     directly to an already running server on the loopback interface, so the
 *     daemon can never be started from here;
 *   - every request is a fixed, whitelisted string; the device serial is used
 *     on the wire and never leaves this module;
 *   - nothing is printed from here except counts, a generic model name and the
 *     public OpenXR runtime names;
 *   - every probe is bounded in time and in bytes.
 */
#define _GNU_SOURCE
#include "overview_headset.h"
#include "overview_core.h"

#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <netinet/in.h>
#include <time.h>
#include <unistd.h>

/* ================= the module's knobs ================= */

static int          ov_hs_timeout_ms  = 1500;   /* one probe */
static long long    ov_hs_interval_ms = 30000;  /* how long a reading stays fresh */
static int          ov_hs_port        = 5037;   /* the ADB server port */
static long         ov_hs_wire_count  = 0;

static OvAdbQuery   ov_hs_query_fn    = NULL;
static void        *ov_hs_query_ctx   = NULL;

void ov_hs_set_query(OvAdbQuery fn, void *ctx) { ov_hs_query_fn = fn; ov_hs_query_ctx = ctx; }
void ov_hs_reset_query(void) { ov_hs_query_fn = NULL; ov_hs_query_ctx = NULL; }
long ov_hs_wire_calls(void) { return ov_hs_wire_count; }

void ov_hs_set_timeout(int ms) { if (ms > 0) ov_hs_timeout_ms = ms; }
void ov_hs_set_interval(long long ms) { if (ms > 0) ov_hs_interval_ms = ms; }
void ov_hs_set_port(int port) { if (port > 0 && port < 65536) ov_hs_port = port; }

/* ================= the ADB wire ================= */

static long long ov_hs_now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (long long)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}

/* Read exactly n bytes.  Returns 1 only when all of them arrived in time.
 *
 * The socket timeout is only a polling interval, not the deadline: a read that
 * times out is retried until the real wall-clock deadline passes.  The earlier
 * code gave up at the first timeout, so a reply that arrived in more than one
 * chunk with a gap longer than the poll interval was silently dropped. */
static int ov_hs_read_exact(int fd, char *out, size_t n, long long deadline) {
    size_t got = 0;
    while (got < n) {
        if (ov_hs_now_ms() >= deadline) return 0;
        struct timeval tv = { .tv_sec = 0, .tv_usec = 50000 };
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
        ssize_t r = recv(fd, out + got, n - got, 0);
        if (r > 0) { got += (size_t)r; continue; }
        if (r == 0) return 0;                      /* the peer closed early */
        if (errno == EINTR) continue;              /* a signal: try again */
        if (errno == EAGAIN || errno == EWOULDBLOCK) continue;  /* no data yet */
        return 0;                                  /* a real error */
    }
    return 1;
}

int ov_adb_send_service(int fd, const char *service, long long deadline_ms) {
    if (fd < 0 || !service || !*service) return 0;
    size_t sl = strlen(service);
    if (sl == 0 || sl > 4096) return 0;
    char frame[4160];
    int fl = snprintf(frame, sizeof frame, "%04zx%s", sl, service);
    if (fl != (int)sl + 4) return 0;
    size_t sent = 0;
    while (sent < (size_t)fl) {
        if (ov_hs_now_ms() >= deadline_ms) return 0;
        ssize_t w = send(fd, frame + sent, (size_t)fl - sent, MSG_NOSIGNAL);
        if (w > 0) { sent += (size_t)w; continue; }
        if (w < 0 && errno == EINTR) continue;      /* a signal: try again */
        if (w < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) continue;
        return 0;
    }
    char status[4];
    if (!ov_hs_read_exact(fd, status, 4, deadline_ms)) return 0;
    return memcmp(status, "OKAY", 4) == 0;
}

int ov_adb_read_payload(int fd, char *out, size_t n, long long deadline_ms) {
    char header[5] = { 0 };
    if (!ov_hs_read_exact(fd, header, 4, deadline_ms)) return 0;
    for (int i = 0; i < 4; i++)
        if (!isxdigit((unsigned char)header[i])) return 0;
    size_t len = strtoul(header, NULL, 16);
    if (len == 0) { if (out && n) out[0] = 0; return 1; }
    if (!out || n == 0) return 0;
    if (len >= n) return 0;                       /* the answer does not fit: refuse it */
    if (!ov_hs_read_exact(fd, out, len, deadline_ms)) return 0;
    out[len] = 0;
    return 1;
}

/* The shell service answers with plain text and closes the stream.  A clean end
 * of stream is required, so a truncated answer is never mistaken for a whole one. */
int ov_adb_read_stream(int fd, char *out, size_t n, long long deadline_ms) {
    size_t got = 0;
    if (out && n) out[0] = 0;
    if (!out || n == 0) return 0;
    for (;;) {
        if (ov_hs_now_ms() >= deadline_ms) return 0;
        struct timeval tv = { .tv_sec = 0, .tv_usec = 50000 };
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
        if (got + 1 >= n) return 0;               /* the answer is too large */
        ssize_t r = recv(fd, out + got, n - got - 1, 0);
        if (r > 0) { got += (size_t)r; continue; }
        if (r == 0) break;                        /* the clean end of the stream */
        if (errno == EINTR) continue;              /* a signal: try again */
        if (errno == EAGAIN || errno == EWOULDBLOCK) continue;  /* no data yet */
        return 0;
    }
    out[got] = 0;
    return 1;
}

static int ov_hs_connect(long long *deadline) {
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return -1;
    struct sockaddr_in sa = { .sin_family = AF_INET, .sin_port = htons((uint16_t)ov_hs_port),
                              .sin_addr.s_addr = htonl(INADDR_LOOPBACK) };
    struct timeval tv = { .tv_sec = 0, .tv_usec = 200000 };
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof tv);
    *deadline = ov_hs_now_ms() + ov_hs_timeout_ms;
    if (connect(fd, (struct sockaddr *)&sa, sizeof sa) != 0) { close(fd); return -1; }
    return fd;
}

/* Select the transport of the given device on the open connection. */
static int ov_adb_select_transport(int fd, const char *serial, long long deadline_ms) {
    if (fd < 0 || !serial || !*serial) return 0;
    char transport[320];
    int tl = snprintf(transport, sizeof transport, "host:transport:%s", serial);
    if (tl <= 0 || tl >= (int)sizeof transport) return 0;
    return ov_adb_send_service(fd, transport, deadline_ms);
}

/* Select the transport and run one shell command on the device. */
int ov_adb_transport_query(int fd, const char *serial, const char *command,
                            char *out, size_t n, long long deadline_ms) {
    if (!command || !*command) return 0;
    if (!ov_adb_select_transport(fd, serial, deadline_ms)) return 0;
    char service[512];
    int sl = snprintf(service, sizeof service, "shell:%s", command);
    if (sl <= 0 || sl >= (int)sizeof service) return 0;
    if (!ov_adb_send_service(fd, service, deadline_ms)) return 0;
    return ov_adb_read_stream(fd, out, n, deadline_ms);
}

/* Select the transport and ask one device service.  The device services answer
 * with a plain stream that ends at the close, like a shell answer does. */
static int ov_adb_device_query(int fd, const char *serial, const char *service,
                               char *out, size_t n, long long deadline_ms) {
    if (!service || !*service) return 0;
    if (!ov_adb_select_transport(fd, serial, deadline_ms)) return 0;
    if (!ov_adb_send_service(fd, service, deadline_ms)) return 0;
    return ov_adb_read_stream(fd, out, n, deadline_ms);
}

int ov_adb_host_query(const char *service, char *out, size_t n) {
    long long deadline;
    int fd = ov_hs_connect(&deadline);
    if (fd < 0) return 0;
    int ok = ov_adb_send_service(fd, service, deadline) &&
             ov_adb_read_payload(fd, out, n, deadline);
    close(fd);
    return ok;
}

int ov_adb_shell_query(const char *serial, const char *command, char *out, size_t n) {
    long long deadline;
    int fd = ov_hs_connect(&deadline);
    if (fd < 0) return 0;
    int ok = ov_adb_transport_query(fd, serial, command, out, n, deadline);
    close(fd);
    return ok;
}

/* Ask one device service on the selected transport. */
static int ov_adb_device_service(const char *serial, const char *service, char *out, size_t n) {
    long long deadline;
    int fd = ov_hs_connect(&deadline);
    if (fd < 0) return 0;
    int ok = ov_adb_device_query(fd, serial, service, out, n, deadline);
    close(fd);
    return ok;
}

/* The production transport: the one place that touches a socket. */
static int ov_hs_wire_query(void *ctx, const OvAdbRequest *req, char *out, size_t n) {
    (void)ctx;
    ov_hs_wire_count++;
    if (req->kind == OV_ADB_SHELL) return ov_adb_shell_query(req->serial, req->command, out, n);
    if (req->kind == OV_ADB_DEVICE) return ov_adb_device_service(req->serial, req->command, out, n);
    return ov_adb_host_query(req->service, out, n);
}

/* The transport the collector uses: the fixture when one is installed. */
static int ov_hs_query(const OvAdbRequest *req, char *out, size_t n) {
    if (ov_hs_query_fn) return ov_hs_query_fn(ov_hs_query_ctx, req, out, n);
    return ov_hs_wire_query(NULL, req, out, n);
}

/* ================= the pure parsers ================= */

/* Read one "key: value" line out of a dumpsys block.  The line may be indented;
 * the value is trimmed.  The key has to be the whole first word, so "level" does
 * not match "levels" and "scale" does not match "scaleFactor". */
static int ov_hs_field(const char *text, const char *key, char *out, size_t n) {
    size_t kl = strlen(key);
    for (const char *p = text ? text : ""; *p;) {
        const char *end = strchr(p, '\n');
        size_t len = end ? (size_t)(end - p) : strlen(p);
        const char *line = p;
        while (len && isspace((unsigned char)*line)) { line++; len--; }
        if (len > kl && !strncmp(line, key, kl) && line[kl] == ':') {
            const char *v = line + kl + 1;
            size_t take = (size_t)(line + len - v);
            if (take >= n) take = n - 1;
            memcpy(out, v, take);
            out[take] = 0;
            st_trim(out);
            return 1;
        }
        if (!end) break;
        p = end + 1;
    }
    return 0;
}

/* A boolean out of a dumpsys field: "true"/"false". */
static int ov_hs_bool_field(const char *text, const char *key, int *out) {
    char v[32] = { 0 };
    if (!ov_hs_field(text, key, v, sizeof v)) return 0;
    if (!strcasecmp(v, "true")) { *out = 1; return 1; }
    if (!strcasecmp(v, "false")) { *out = 0; return 1; }
    return 0;
}

/* An integer out of a dumpsys field.  The value has to be a whole number. */
static int ov_hs_int_field(const char *text, const char *key, int lo, int hi, int *out) {
    char v[64] = { 0 };
    if (!ov_hs_field(text, key, v, sizeof v)) return 0;
    char *end = NULL;
    errno = 0;
    long parsed = strtol(v, &end, 10);
    if (errno || !end || end == v || *end) return 0;
    if (parsed < lo || parsed > hi) return 0;
    *out = (int)parsed;
    return 1;
}

int ov_hs_parse_battery(const char *text, OvHeadset *h) {
    if (!text || !h) return 0;
    int level = -1, scale = -1, temperature = -1, status = -1;
    if (!ov_hs_int_field(text, "level", 0, 1000, &level)) return 0;
    if (!ov_hs_int_field(text, "scale", 1, 1000, &scale)) return 0;
    if (level > scale) return 0;                    /* a level above the scale is malformed */
    ov_hs_int_field(text, "temperature", -500, 2000, &temperature);
    ov_hs_int_field(text, "status", 1, 5, &status);

    h->battery = level * 100 / scale;
    h->battery_scale = scale;
    h->temperature = temperature;
    h->present_battery = 1;
    h->battery_status = status;
    ov_hs_bool_field(text, "AC powered", &h->ac_powered);
    ov_hs_bool_field(text, "USB powered", &h->usb_powered);
    ov_hs_bool_field(text, "Wireless powered", &h->wireless_powered);
    ov_hs_bool_field(text, "Dock powered", &h->dock_powered);

    int powered = (h->ac_powered == 1 || h->usb_powered == 1 ||
                   h->dock_powered == 1 || h->wireless_powered == 1);
    switch (status) {
        case 2: h->charging = 1; break;             /* BATTERY_STATUS_CHARGING */
        case 3: h->charging = 0; break;             /* BATTERY_STATUS_DISCHARGING */
        case 4:                                   /* BATTERY_STATUS_NOT_CHARGING */
            h->charging = 0;
            /* plugged in but the battery is not charged: the source is too weak */
            if (powered) h->weak_charger = 1;
            break;
        case 5: h->charging = 1; break;             /* BATTERY_STATUS_FULL */
        default: h->charging = powered ? 1 : -1; break;
    }
    /* The battery service names a weak charger in two ways: a "Weak Charger"
     * flag and a charger type.  Either one is a weak charger. */
    int weak = 0;
    if (ov_hs_bool_field(text, "Weak Charger", &weak)) h->weak_charger = weak;
    char charger[96] = { 0 };
    if (ov_hs_field(text, "charger-type", charger, sizeof charger) ||
        ov_hs_field(text, "charger type", charger, sizeof charger)) {
        if (st_has_ci(charger, "weak")) h->weak_charger = 1;
    }
    return 1;
}

/* The wake state: "mWakefulness=Awake" or "mWakefulness=Asleep". */
int ov_hs_parse_power(const char *text, OvHeadset *h) {
    if (!text || !h) return 0;
    h->wake_state = -1;
    h->wake_known = 0;
    const char *p = text;
    while ((p = strstr(p, "mWakefulness=")) != NULL) {
        p += strlen("mWakefulness=");
        if (st_starts(p, "Awake")) { h->wake_state = 0; h->wake_known = 1; return 1; }
        if (st_starts(p, "Asleep")) { h->wake_state = 1; h->wake_known = 1; return 1; }
        if (st_starts(p, "Dream")) { h->wake_state = 1; h->wake_known = 1; return 1; }
        if (st_starts(p, "Doze")) { h->wake_state = 1; h->wake_known = 1; return 1; }
    }
    return 0;
}

/* The model name out of a getprop answer.  The value is trimmed and bounded. */
int ov_hs_parse_model(const char *text, OvHeadset *h) {
    if (!text || !h) return 0;
    char v[128] = { 0 };
    size_t len = strlen(text);
    while (len && (text[len - 1] == '\n' || text[len - 1] == '\r' || text[len - 1] == ' ')) len--;
    if (!len || len >= sizeof v) return 0;
    memcpy(v, text, len);
    v[len] = 0;
    /* only a plain model name: letters, digits, spaces and a few marks */
    for (size_t i = 0; i < len; i++) {
        unsigned char c = (unsigned char)v[i];
        if (!isalnum(c) && !strchr(" -_+()", (char)c)) return 0;
    }
    st_copy(h->model, sizeof h->model, v);
    return 1;
}

/* The WiVRn client app, read from a "pm path <id>" answer: either
 * "package:/data/app/.../base.apk" (installed) or "package:unknown" / an empty
 * answer (not installed).  The APK path is never kept. */
int ov_hs_parse_wivrn_app(const char *text, OvHeadset *h) {
    if (!text || !h) return 0;
    const char *p = strstr(text, "package:");
    if (!p) return 0;
    p += strlen("package:");
    while (*p == ' ' || *p == '\t') p++;
    if (!strncmp(p, "unknown", 7)) { h->wivrn_app = 0; h->wivrn_app_known = 1; return 1; }
    if (!*p) return 0;
    /* the path itself is never kept; only the fact that the app is installed */
    h->wivrn_app = 1;
    h->wivrn_app_known = 1;
    return 1;
}

/* The WiVRn client app, read from a "pm list packages <filter>" answer.
 *
 * The package manager filters by substring and answers one "package:<id>" line
 * per match, so the filter itself can match a longer id ("github" also matches
 * "com.github.other").  Only the two known WiVRn ids count as the client app,
 * and a line that names neither is ignored.  An empty answer is a successful
 * query with no match: the app is genuinely absent.  Text that is not a package
 * list at all (a command error) leaves the state unknown. */
int ov_hs_parse_package_list(const char *text, OvHeadset *h) {
    if (!text || !h) return 0;
    int matched = 0, saw_line = 0, error = 0;
    for (const char *p = text; *p;) {
        const char *end = strchr(p, '\n');
        if (!end) end = p + strlen(p);
        size_t len = (size_t)(end - p);
        char line[512];
        if (len && len < sizeof line) {
            memcpy(line, p, len); line[len] = 0;
            const char *q = line;
            while (*q == ' ' || *q == '\t') q++;
            if (!strncmp(q, "package:", 8)) {
                saw_line = 1;
                q += strlen("package:");
                while (*q == ' ' || *q == '\t') q++;
                /* the id ends at the first space: "org.meumeu.wivrn.github" */
                size_t idlen = 0;
                while (q[idlen] && q[idlen] != ' ' && q[idlen] != '\t') idlen++;
                if (idlen == strlen(OV_WIVRN_PACKAGE_PLAY) &&
                    !strncmp(q, OV_WIVRN_PACKAGE_PLAY, idlen)) matched = 1;
                else if (idlen == strlen(OV_WIVRN_PACKAGE_GITHUB) &&
                         !strncmp(q, OV_WIVRN_PACKAGE_GITHUB, idlen)) matched = 1;
            } else if (q[0] && (st_starts(q, "Error:") || st_starts(q, "Unknown command:") ||
                               st_starts(q, "Exception") || st_starts(q, "Usage:"))) {
                error = 1;
            }
        }
        p = *end ? end + 1 : end;
    }
    if (error) return 0;                /* the query itself failed: stay unknown */
    if (!saw_line && text[0]) return 0;  /* unrecognised text: stay unknown */
    h->wivrn_app = matched ? 1 : 0;
    h->wivrn_app_known = 1;
    return 1;
}

/* The reverse mapping list: one "<serial> tcp:<port> tcp:<port>" triple per line. */
int ov_hs_parse_reverse(const char *text, OvHeadset *h) {
    if (!text || !h) return 0;
    int count = 0, ours = 0;
    for (const char *p = text ? text : ""; *p;) {
        /* the last line may have no newline: strchr then answers NULL */
        const char *end = strchr(p, '\n');
        if (!end) end = p + strlen(p);
        size_t len = (size_t)(end - p);
        char line[512];
        if (len && len < sizeof line) {
            memcpy(line, p, len); line[len] = 0;
            /* the serial in the line is dropped; only the mapping is counted */
            char a[256], b[128], c[128];
            if (sscanf(line, "%255s %127s %127s", a, b, c) == 3) {
                count++;
                if (!strcmp(b, OV_REVERSE_MAPPING)) ours++;
            }
        }
        p = (*end == '\n') ? end + 1 : end;
    }
    h->reverse_count = count;
    h->reverse_ours = ours;
    h->reverse = ours > 0 ? 1 : 0;
    h->reverse_known = 1;
    return 1;
}

/* The device listing.  Exactly one authorized device is selected; with none or
 * with several the panel says so and no device is contacted. */
int ov_hs_parse_devices(const char *text, OvHeadset *h, char *selected, size_t selected_n) {
    if (selected && selected_n) selected[0] = 0;
    if (!text || !h) return 0;
    int ready = 0, offline = 0, unauthorized = 0, denied = 0;
    char chosen[256] = { 0 };
    for (const char *p = text; *p;) {
        /* the last line may have no newline: strchr then answers NULL */
        const char *end = strchr(p, '\n');
        if (!end) end = p + strlen(p);
        size_t len = (size_t)(end - p);
        char line[512];
        if (len && len < sizeof line) {
            memcpy(line, p, len); line[len] = 0;
            char serial[256], state[64];
            if (sscanf(line, "%255s %63s", serial, state) == 2) {
                if (!strcmp(state, "device")) {
                    if (!chosen[0]) st_copy(chosen, sizeof chosen, serial);
                    ready++;
                } else if (!strcmp(state, "offline")) offline++;
                else if (!strcmp(state, "unauthorized")) unauthorized++;
                else if (!strcmp(state, "no") && st_has(line, "no permissions")) denied++;
            }
        }
        p = (*end == '\n') ? end + 1 : end;
    }
    h->device_count = ready + offline + unauthorized + denied;
    h->unauthorized = unauthorized;
    h->offline = offline;
    h->denied = denied;
    h->ready_count = ready;
    h->present = (ready == 1 && chosen[0]) ? 1 : 0;
    if (h->present && selected && selected_n) st_copy(selected, selected_n, chosen);
    return h->device_count;
}

/* ================= the OpenXR runtimes ================= */

/* The locations the OpenXR manifests live in.  The paths are only ever read;
 * they are never printed. */
void ov_hs_openxr_locations(char roots[OV_HS_MAX_ROOTS][256], int *nroots,
                             char *active, size_t active_n) {
    if (!roots || !nroots) return;
    *nroots = 0;
    if (active && active_n) active[0] = 0;

    char home[192] = { 0 };
    const char *h = getenv("HOME");
    if (h && *h) st_copy(home, sizeof home, h);
    char xdg[192] = { 0 };
    const char *x = getenv("XDG_CONFIG_HOME");
    if (x && *x) st_copy(xdg, sizeof xdg, x);

    /* The selected runtime: the environment override first, then the user's own
     * active_runtime.json, then the system one.  The paths are only read; they
     * are never printed. */
    const char *override = getenv("XR_RUNTIME_JSON");
    if (override && *override && active && active_n) st_copy(active, active_n, override);

    if (home[0] && *nroots < OV_HS_MAX_ROOTS) {
        if (xdg[0]) snprintf(roots[*nroots], sizeof roots[0], "%s/openxr/1", xdg);
        else snprintf(roots[*nroots], sizeof roots[0], "%s/.config/openxr/1", home);
        (*nroots)++;
    }
    if (*nroots < OV_HS_MAX_ROOTS) {
        snprintf(roots[*nroots], sizeof roots[0], "/etc/xdg/openxr/1");
        (*nroots)++;
    }
    if (*nroots < OV_HS_MAX_ROOTS) {
        snprintf(roots[*nroots], sizeof roots[0], "/usr/share/openxr/1");
        (*nroots)++;
    }

    /* the selection pointer, when the environment did not name one */
    if (active && active_n && !active[0]) {
        for (int i = 0; i < *nroots && !active[0]; i++) {
            char path[512];
            int pl = snprintf(path, sizeof path, "%s/active_runtime.json", roots[i]);
            if (pl <= 0 || pl >= (int)sizeof path) continue;
            if (!path_exists(path)) continue;
            st_copy(active, active_n, path);
        }
    }
}

/* The runtime name out of a manifest.  Only the name is kept: the library path
 * inside the manifest is never shown. */
static int ov_hs_runtime_name(const char *manifest, char *out, size_t n) {
    if (!manifest || !out || !n) return 0;
    out[0] = 0;
    Buf runtime = { 0 };
    int ok = json_obj(&runtime, manifest, "runtime");
    if (ok) {
        Buf name = { 0 };
        if (json_str(&name, runtime.data, "name")) {
            st_copy(out, n, name.data ? name.data : "");
            buf_free(&name);
        } else {
            buf_free(&name);
            ok = 0;
        }
    }
    buf_free(&runtime);
    if (!ok || !out[0]) return 0;
    /* a runtime name is a short label: no path, no control characters */
    for (size_t i = 0; out[i]; i++) {
        unsigned char c = (unsigned char)out[i];
        if (c < 32 || c == 127) return 0;
        if (c == '/') return 0;
    }
    if (strlen(out) >= 64) out[63] = 0;
    return out[0] != 0;
}

/* The installed runtimes: every manifest name found in the roots, and the name
 * of the selected one when there is one. */
int ov_hs_scan_openxr(const char (*roots)[256], int nroots, const char *active, OvHeadset *h) {
    if (!h) return 0;
    h->openxr_installed = 0;
    h->openxr_name_count = 0;
    h->openxr_selected = 0;
    h->openxr_active[0] = 0;
    int found = 0;

    for (int i = 0; i < nroots && roots; i++) {
        if (!roots[i][0]) continue;
        DIR *d = opendir(roots[i]);
        if (!d) continue;
        struct dirent *e;
        while ((e = readdir(d)) != NULL) {
            size_t len = strlen(e->d_name);
            if (len < 6 || strcmp(e->d_name + len - 5, ".json")) continue;
            /* the selection pointer is not an installed runtime */
            if (st_ends(e->d_name, "/active_runtime.json") ||
                !strcmp(e->d_name, "active_runtime.json")) continue;
            char path[1024];
            int pl = snprintf(path, sizeof path, "%s/%s", roots[i], e->d_name);
            if (pl <= 0 || pl >= (int)sizeof path) continue;
            Buf manifest = { 0 };
            if (!file_read(path, &manifest, 65536) || !manifest.len) { buf_free(&manifest); continue; }
            char name[64] = { 0 };
            int ok = ov_hs_runtime_name(manifest.data, name, sizeof name);
            buf_free(&manifest);
            if (!ok) continue;
            found++;
            h->openxr_installed++;
            int known = 0;
            for (int k = 0; k < h->openxr_name_count; k++)
                if (!strcmp(h->openxr_names[k], name)) { known = 1; break; }
            if (!known && h->openxr_name_count < OV_HS_MAX_RUNTIMES) {
                st_copy(h->openxr_names[h->openxr_name_count], sizeof h->openxr_names[0], name);
                h->openxr_name_count++;
            }
        }
        closedir(d);
    }

    /* the selected runtime: the manifest the active pointer names */
    if (active && *active) {
        Buf manifest = { 0 };
        if (file_read(active, &manifest, 65536) && manifest.len) {
            char name[64] = { 0 };
            if (ov_hs_runtime_name(manifest.data, name, sizeof name)) {
                h->openxr_selected = 1;
                st_copy(h->openxr_active, sizeof h->openxr_active, name);
            }
        }
        buf_free(&manifest);
    }
    return found;
}

/* ================= the collector ================= */

/* The cached readings.  One entry per probe, each with its own deadline. */
typedef struct {
    long long at_ms;
    int       valid;
    char      value[OV_HS_SLOT_BYTES];
} OvHsSlot;

static OvHsSlot ov_hs_slot_devices;
static OvHsSlot ov_hs_slot_battery;
static OvHsSlot ov_hs_slot_power;
static OvHsSlot ov_hs_slot_model;
static OvHsSlot ov_hs_slot_reverse;
static OvHsSlot ov_hs_slot_wivrn;

void ov_hs_reset_cache(void) {
    memset(&ov_hs_slot_devices, 0, sizeof ov_hs_slot_devices);
    memset(&ov_hs_slot_battery, 0, sizeof ov_hs_slot_battery);
    memset(&ov_hs_slot_power, 0, sizeof ov_hs_slot_power);
    memset(&ov_hs_slot_model, 0, sizeof ov_hs_slot_model);
    memset(&ov_hs_slot_reverse, 0, sizeof ov_hs_slot_reverse);
    memset(&ov_hs_slot_wivrn, 0, sizeof ov_hs_slot_wivrn);
}

/* One cached probe.  The command is fixed by the caller; the serial is only
 * used to select the transport and never leaves this function. */
static int ov_hs_probe(OvHsSlot *slot, const char *serial, const char *command, size_t n) {
    long long now = ov_hs_now_ms();
    if (slot->valid && now - slot->at_ms < ov_hs_interval_ms) return 1;
    slot->valid = 0;
    slot->value[0] = 0;
    OvAdbRequest req = { .kind = OV_ADB_SHELL, .serial = serial, .command = command, .service = NULL };
    if (!ov_hs_query(&req, slot->value, n)) return 0;
    slot->at_ms = now;
    slot->valid = 1;
    return 1;
}

/* The device listing, through the same cache. */
int ov_hs_devices_listing(char *out, size_t n) {
    long long now = ov_hs_now_ms();
    if (ov_hs_slot_devices.valid && now - ov_hs_slot_devices.at_ms < ov_hs_interval_ms) {
        st_copy(out, n, ov_hs_slot_devices.value);
        return 1;
    }
    ov_hs_slot_devices.valid = 0;
    ov_hs_slot_devices.value[0] = 0;
    OvAdbRequest req = { .kind = OV_ADB_HOST, .serial = NULL, .command = NULL, .service = "host:devices-l" };
    if (!ov_hs_query(&req, ov_hs_slot_devices.value, sizeof ov_hs_slot_devices.value)) return 0;
    ov_hs_slot_devices.at_ms = now;
    ov_hs_slot_devices.valid = 1;
    st_copy(out, n, ov_hs_slot_devices.value);
    return 1;
}

/* The reverse mapping list.  It is a device service: the transport is selected
 * and the service is asked on that transport. */
static int ov_hs_probe_reverse(OvHsSlot *slot, const char *serial) {
    long long now = ov_hs_now_ms();
    if (slot->valid && now - slot->at_ms < ov_hs_interval_ms) return 1;
    slot->valid = 0;
    slot->value[0] = 0;
    OvAdbRequest req = { .kind = OV_ADB_DEVICE, .serial = serial, .command = "reverse:list-forward",
                          .service = NULL };
    if (!ov_hs_query(&req, slot->value, sizeof slot->value)) return 0;
    slot->at_ms = now;
    slot->valid = 1;
    return 1;
}

void ov_headset_collect(OvHeadset *h, const char *wivrn_server) {
    if (!h) return;
    memset(h, 0, sizeof *h);
    h->battery = -1;
    h->battery_scale = -1;
    h->battery_status = -1;
    h->temperature = -1;
    h->charging = -1;
    h->usb_powered = -1;
    h->ac_powered = -1;
    h->dock_powered = -1;
    h->wireless_powered = -1;
    h->weak_charger = 0;
    h->present_battery = 0;
    h->wake_state = -1;
    h->wake_known = 0;
    h->reverse = -1;
    h->reverse_known = 0;
    h->wivrn_app = -1;
    h->wivrn_app_known = 0;

    /* ---- the OpenXR runtimes: the local manifests, no device needed ---- */
    {
        char roots[OV_HS_MAX_ROOTS][256];
        int nroots = 0;
        char active[512] = { 0 };
        ov_hs_openxr_locations(roots, &nroots, active, sizeof active);
        ov_hs_scan_openxr(roots, nroots, active, h);
    }

    /* ---- the ADB server and the device list ---- */
    /* The buffer matches the module's reading slot, so a long listing is never
     * truncated into a listing that lies about how many devices there are. */
    char listing[OV_HS_SLOT_BYTES] = { 0 };
    if (!ov_hs_devices_listing(listing, sizeof listing)) {
        snprintf(h->status, sizeof h->status, "no ADB server on the loopback (not started by the tool)");
        return;
    }

    char serial[256] = { 0 };
    int devices = ov_hs_parse_devices(listing, h, serial, sizeof serial);
    if (!devices) {
        snprintf(h->status, sizeof h->status, "no ADB device attached");
        return;
    }
    if (!h->present) {
        if (h->ready_count > 1)
            snprintf(h->status, sizeof h->status,
                     "%d devices attached; none selected (exactly one is needed)", h->ready_count);
        else if (h->unauthorized)
            snprintf(h->status, sizeof h->status, "device unauthorized (allow it in the headset's prompt)");
        else if (h->offline)
            snprintf(h->status, sizeof h->status, "device offline");
        else if (h->denied)
            snprintf(h->status, sizeof h->status, "device has no USB permission");
        else
            snprintf(h->status, sizeof h->status, "no usable device attached");
        return;
    }

    /* ---- the device's own readings ---- */
    if (ov_hs_probe(&ov_hs_slot_model, serial, "getprop ro.product.model", sizeof ov_hs_slot_model.value))
        ov_hs_parse_model(ov_hs_slot_model.value, h);
    if (ov_hs_probe(&ov_hs_slot_battery, serial, "dumpsys battery", sizeof ov_hs_slot_battery.value))
        ov_hs_parse_battery(ov_hs_slot_battery.value, h);
    if (ov_hs_probe(&ov_hs_slot_power, serial, "dumpsys power", sizeof ov_hs_slot_power.value))
        ov_hs_parse_power(ov_hs_slot_power.value, h);
    if (ov_hs_probe(&ov_hs_slot_wivrn, serial, OV_WIVRN_PACKAGE_QUERY, sizeof ov_hs_slot_wivrn.value))
        ov_hs_parse_package_list(ov_hs_slot_wivrn.value, h);
    if (ov_hs_probe_reverse(&ov_hs_slot_reverse, serial))
        ov_hs_parse_reverse(ov_hs_slot_reverse.value, h);

    /* ---- the one line the panel shows ---- */
    if (wivrn_server && *wivrn_server)
        snprintf(h->status, sizeof h->status, "device present; WiVRn server %s", wivrn_server);
    else
        snprintf(h->status, sizeof h->status, "device present; WiVRn server not seen");
}

/* ================= the rendering ================= */

/* A short word for a state that may be unknown. */
static const char *ov_hs_word(int value, const char *yes, const char *no) {
    if (value < 0) return "unknown";
    return value ? yes : no;
}

/* Every external label passes this boundary before it is printed: no control
 * characters, no assignment and no credential value can reach the screen. */
static void ov_hs_safe(char *out, size_t n, const char *text) {
    st_copy(out, n, text);
    for (char *p = out; *p; p++)
        if ((unsigned char)*p < 32 || (unsigned char)*p == 127) *p = ' ';
    for (char *p = out; *p; p++) {
        if (*p == '=' || !strncasecmp(p, "bearer ", 7)) {
            st_copy(p, n - (size_t)(p - out), "[redacted]");
            return;
        }
        if (*p != ':') continue;
        /* a "key: value" pair whose key looks like a credential loses its value */
        char *key = p;
        while (key > out && (isspace((unsigned char)key[-1]) || key[-1] == '"')) key--;
        char *end = key;
        while (key > out && (isalnum((unsigned char)key[-1]) || strchr("_.-", key[-1]))) key--;
        if (looks_secret_key(key, (size_t)(end - key))) {
            st_copy(p + 1, n - (size_t)(p + 1 - out), "[redacted]");
            return;
        }
    }
}

void ov_hs_format(const OvHeadset *h, char *out, size_t n) {
    if (!out || !n) return;
    out[0] = 0;
    if (!h) { snprintf(out, n, "unavailable"); return; }

    char line[1024];
    size_t at = 0;

    /* the device */
    if (h->present) {
        char model[64] = { 0 };
        ov_hs_safe(model, sizeof model, h->model);
        snprintf(line, sizeof line, "device: %s", model[0] ? model : "model unknown");
    } else {
        snprintf(line, sizeof line, "device: none selected");
    }
    at += (size_t)snprintf(out + at, n - at, "%s", line);
    if (h->device_count)
        at += (size_t)snprintf(out + at, n - at, "  (%d attached", h->device_count);
    if (h->unauthorized) at += (size_t)snprintf(out + at, n - at, ", %d unauthorized", h->unauthorized);
    if (h->offline) at += (size_t)snprintf(out + at, n - at, ", %d offline", h->offline);
    if (h->denied) at += (size_t)snprintf(out + at, n - at, ", %d without USB permission", h->denied);
    if (h->device_count) at += (size_t)snprintf(out + at, n - at, ")");

    /* the battery, the charging state and the temperature */
    at += (size_t)snprintf(out + at, n - at, "  battery: %s",
                            h->battery >= 0 ? "known" : "unknown");
    if (h->battery >= 0) at += (size_t)snprintf(out + at, n - at, " %d%%", h->battery);
    at += (size_t)snprintf(out + at, n - at, "  charging: %s", ov_hs_word(h->charging, "yes", "no"));
    at += (size_t)snprintf(out + at, n - at, "  USB: %s", ov_hs_word(h->usb_powered, "yes", "no"));
    if (h->ac_powered == 1) at += (size_t)snprintf(out + at, n - at, "  AC: yes");
    if (h->dock_powered == 1) at += (size_t)snprintf(out + at, n - at, "  dock: yes");
    if (h->weak_charger) at += (size_t)snprintf(out + at, n - at, "  WEAK CHARGER");
    if (h->temperature >= 0)
        at += (size_t)snprintf(out + at, n - at, "  %d.%d C", h->temperature / 10,
                               abs(h->temperature % 10));

    /* the wake state, the reverse mapping and the WiVRn app */
    at += (size_t)snprintf(out + at, n - at, "  awake: %s",
                            h->wake_known ? (h->wake_state == 0 ? "yes" : "no") : "unknown");
    if (h->reverse_known) {
        at += (size_t)snprintf(out + at, n - at, "  reverse %s: %s", OV_REVERSE_MAPPING,
                                h->reverse ? "ready" : "absent");
        if (h->reverse_count > 1)
            at += (size_t)snprintf(out + at, n - at, " (%d mappings)", h->reverse_count);
    } else {
        at += (size_t)snprintf(out + at, n - at, "  reverse %s: unknown", OV_REVERSE_MAPPING);
    }
    if (h->wivrn_app_known)
        at += (size_t)snprintf(out + at, n - at, "  WiVRn app: %s",
                                h->wivrn_app ? "installed" : "not installed");

    /* the runtimes */
    if (h->openxr_selected) {
        char active[64] = { 0 };
        ov_hs_safe(active, sizeof active, h->openxr_active);
        at += (size_t)snprintf(out + at, n - at, "  OpenXR: %s (selected)", active);
    } else {
        at += (size_t)snprintf(out + at, n - at, "  OpenXR: none selected");
    }
    if (h->openxr_installed) {
        at += (size_t)snprintf(out + at, n - at, "  installed: %d", h->openxr_installed);
        for (int i = 0; i < h->openxr_name_count; i++) {
            char name[64] = { 0 };
            ov_hs_safe(name, sizeof name, h->openxr_names[i]);
            at += (size_t)snprintf(out + at, n - at, "%s%s", i ? ", " : " (", name);
        }
        if (h->openxr_name_count) at += (size_t)snprintf(out + at, n - at, ")");
    }
}
