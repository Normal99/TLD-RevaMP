/* overview_core.c - pure helpers for tools/overview.  See overview_core.h. */
#define _GNU_SOURCE
#include "overview_core.h"

#include <ctype.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/stat.h>

/* ================= buffer ================= */

void buf_init(Buf *b) { b->data = NULL; b->len = 0; b->cap = 0; }

void buf_free(Buf *b) {
    free(b->data);
    b->data = NULL; b->len = 0; b->cap = 0;
}

void buf_reset(Buf *b) { b->len = 0; if (b->data) b->data[0] = '\0'; }

static void buf_grow(Buf *b, size_t need) {
    if (b->cap >= need + 1) return;
    size_t cap = b->cap ? b->cap : 128;
    while (cap < need + 1) cap *= 2;
    char *p = realloc(b->data, cap);
    if (!p) { perror("realloc"); exit(1); }
    b->data = p;
    b->cap = cap;
}

void buf_add(Buf *b, const char *s, size_t n) {
    if (!s || !n) { buf_grow(b, b->len); b->data[b->len] = '\0'; return; }
    buf_grow(b, b->len + n);
    memcpy(b->data + b->len, s, n);
    b->len += n;
    b->data[b->len] = '\0';
}

void buf_puts(Buf *b, const char *s) { if (s) buf_add(b, s, strlen(s)); }

void buf_printf(Buf *b, const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    char tmp[1024];
    int n = vsnprintf(tmp, sizeof tmp, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if ((size_t)n < sizeof tmp) { buf_add(b, tmp, (size_t)n); return; }
    char *big = malloc((size_t)n + 1);
    if (!big) return;
    va_start(ap, fmt);
    vsnprintf(big, (size_t)n + 1, fmt, ap);
    va_end(ap);
    buf_add(b, big, (size_t)n);
    free(big);
}

char *buf_steal(Buf *b) {
    buf_grow(b, b->len);
    char *p = b->data;
    b->data = NULL; b->len = 0; b->cap = 0;
    return p;
}

/* ================= strings ================= */

int st_has(const char *hay, const char *needle) {
    if (!hay || !needle) return 0;
    return strstr(hay, needle) != NULL;
}

void st_lower(char *s) { for (; s && *s; s++) *s = (char)tolower((unsigned char)*s); }

int st_has_ci(const char *hay, const char *needle) {
    if (!hay || !needle) return 0;
    size_t nl = strlen(needle);
    if (!nl) return 1;
    for (const char *p = hay; *p; p++) {
        size_t i = 0;
        while (i < nl && p[i] &&
               tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i])) i++;
        if (i == nl) return 1;
    }
    return 0;
}

int st_starts(const char *s, const char *prefix) {
    if (!s || !prefix) return 0;
    return strncmp(s, prefix, strlen(prefix)) == 0;
}

int st_ends(const char *s, const char *suffix) {
    if (!s || !suffix) return 0;
    size_t sl = strlen(s), xl = strlen(suffix);
    if (xl > sl) return 0;
    return strcmp(s + sl - xl, suffix) == 0;
}

void st_trim(char *s) {
    if (!s) return;
    size_t n = strlen(s);
    while (n && isspace((unsigned char)s[n - 1])) s[--n] = '\0';
    size_t i = 0;
    while (s[i] && isspace((unsigned char)s[i])) i++;
    if (i) memmove(s, s + i, n - i + 1);
}

void st_copy(char *dst, size_t n, const char *src) {
    if (!dst || !n) return;
    if (!src) { dst[0] = '\0'; return; }
    size_t l = strlen(src);
    if (l >= n) l = n - 1;
    memmove(dst, src, l);
    dst[l] = '\0';
}

char *st_dup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ================= formatting ================= */

void fmt_elapsed(char *out, size_t n, double seconds) {
    if (seconds < 0) seconds = 0;
    long long t = (long long)seconds;
    if (t < 60) snprintf(out, n, "%llds", t);
    else if (t < 3600) snprintf(out, n, "%lldm%02llds", t / 60, t % 60);
    else if (t < 86400) snprintf(out, n, "%lldh%02lldm", t / 3600, (t % 3600) / 60);
    else snprintf(out, n, "%lldd%02lldh", t / 86400, (t % 86400) / 3600);
}

void fmt_age(char *out, size_t n, long long age_seconds) {
    if (age_seconds < 0) { snprintf(out, n, "now"); return; }
    if (age_seconds < 60) snprintf(out, n, "%llds ago", age_seconds);
    else if (age_seconds < 3600) snprintf(out, n, "%lldm ago", age_seconds / 60);
    else if (age_seconds < 86400) snprintf(out, n, "%lldh ago", age_seconds / 3600);
    else snprintf(out, n, "%lldd ago", age_seconds / 86400);
}

void fmt_size(char *out, size_t n, long long bytes) {
    static const char *u[] = { "B", "KiB", "MiB", "GiB", "TiB" };
    double v = (double)bytes;
    int i = 0;
    while (v >= 1024.0 && i < 4) { v /= 1024.0; i++; }
    if (i == 0) snprintf(out, n, "%lld %s", bytes, u[0]);
    else snprintf(out, n, "%.1f %s", v, u[i]);
}

void fmt_dec(char *out, size_t n, double v, int decimals) {
    snprintf(out, n, "%.*f", decimals, v);
}

/* ================= tiny JSON ================= */

static const char *skip_ws(const char *p) { while (p && *p && isspace((unsigned char)*p)) p++; return p; }

/* Validate and skip a value, respecting quoted braces and bounded nesting. */
static const char *json_end(const char *p, int depth) {
    p = skip_ws(p);
    if (!p || !*p || depth > 32) return NULL;
    if (*p == '"') {
        for (p++; *p; p++) {
            if (*p == '"') return p + 1;
            if ((unsigned char)*p < 32) return NULL;
            if (*p == '\\') {
                p++;
                if (!*p) return NULL;
                if (*p == 'u') {
                    for (int i = 0; i < 4; i++)
                        if (!isxdigit((unsigned char)*++p)) return NULL;
                } else if (!strchr("\"\\/bfnrt", *p)) return NULL;
            }
        }
        return NULL;
    }
    if (*p == '{' || *p == '[') {
        int object = *p == '{';
        char close = object ? '}' : ']';
        p = skip_ws(p + 1);
        if (*p == close) return p + 1;
        for (;;) {
            if (object) {
                if (*p != '"' || !(p = json_end(p, depth + 1))) return NULL;
                p = skip_ws(p);
                if (*p++ != ':') return NULL;
            }
            if (!(p = json_end(p, depth + 1))) return NULL;
            p = skip_ws(p);
            if (*p == close) return p + 1;
            if (*p++ != ',') return NULL;
            p = skip_ws(p);
        }
    }
    const char *end = p;
    if (!strncmp(p, "true", 4) || !strncmp(p, "null", 4)) end = p + 4;
    else if (!strncmp(p, "false", 5)) end = p + 5;
    else {
        if (*end == '-') end++;
        if (*end == '0') end++;
        else {
            if (*end < '1' || *end > '9') return NULL;
            while (isdigit((unsigned char)*end)) end++;
        }
        if (*end == '.') {
            end++;
            if (!isdigit((unsigned char)*end)) return NULL;
            while (isdigit((unsigned char)*end)) end++;
        }
        if (*end == 'e' || *end == 'E') {
            end++;
            if (*end == '+' || *end == '-') end++;
            if (!isdigit((unsigned char)*end)) return NULL;
            while (isdigit((unsigned char)*end)) end++;
        }
    }
    return !*end || isspace((unsigned char)*end) || strchr(",}]", *end) ? end : NULL;
}

const char *json_value_end(const char *p) { return json_end(p, 0); }

/* Only direct keys of a complete object match. Nested names cannot shadow them. */
const char *json_find(const char *json, const char *key) {
    if (!key) return NULL;
    const char *p = skip_ws(json), *end = json_value_end(p);
    if (!p || *p != '{' || !end || *skip_ws(end)) return NULL;
    size_t kl = strlen(key);
    p = skip_ws(p + 1);
    while (*p == '"') {
        const char *q = json_value_end(p);
        const char *value = skip_ws(skip_ws(q) + 1);
        if ((size_t)(q - p) == kl + 2 && !memcmp(p + 1, key, kl)) return value;
        p = skip_ws(json_value_end(value));
        if (*p != ',') break;
        p = skip_ws(p + 1);
    }
    return NULL;
}

int json_has(const char *json, const char *key) { return json_find(json, key) != NULL; }

/* Copy a string value, unescaping the \" \\ \n \t \r \/ forms.  Returns 0 when the key
 * is missing or is not a string. */
int json_str(Buf *out, const char *json, const char *key) {
    const char *p = json_find(json, key);
    if (!p || *p != '"') return 0;
    p++;
    buf_reset(out);
    while (*p && *p != '"') {
        if (*p == '\\' && p[1]) {
            p++;
            switch (*p) {
            case 'n': buf_puts(out, "\n"); break;
            case 't': buf_puts(out, "\t"); break;
            case 'r': buf_puts(out, "\r"); break;
            case 'b': buf_puts(out, "\b"); break;
            case 'f': buf_puts(out, "\f"); break;
            case 'u': {
                /* json_find has validated all four digits. Decode ASCII so
                 * escaped credential labels still reach the privacy filter. */
                unsigned code = 0;
                for (int i = 1; i <= 4; i++) {
                    unsigned char ch = (unsigned char)p[i];
                    code = code * 16 + (isdigit(ch) ? ch - '0' : tolower(ch) - 'a' + 10);
                }
                char ch = code > 0 && code < 128 ? (char)code : '?';
                buf_add(out, &ch, 1);
                p += 4;
                break;
            }
            default: buf_add(out, p, 1); break;
            }
            p++;
        } else {
            buf_add(out, p, 1);
            p++;
        }
    }
    if (*p != '"') return 0;
    return 1;
}

/* Copy a raw token (number, true, false, null, or an unquoted word) as text. */
int json_raw(Buf *out, const char *json, const char *key) {
    const char *p = json_find(json, key);
    if (!p) return 0;
    buf_reset(out);
    if (*p == '"') {
        Buf tmp = { 0 };
        if (json_str(&tmp, json, key)) { buf_puts(out, tmp.data ? tmp.data : ""); buf_free(&tmp); return 1; }
        buf_free(&tmp);
        return 0;
    }
    if (*p == '{' || *p == '[') {
        int depth = 0;
        const char *s = p;
        do {
            if (*s == '{' || *s == '[') depth++;
            else if (*s == '}' || *s == ']') depth--;
            s++;
        } while (*s && depth > 0);
        buf_add(out, p, (size_t)(s - p));
        return 1;
    }
    while (*p && *p != ',' && *p != '}' && *p != ']' && !isspace((unsigned char)*p)) {
        buf_add(out, p, 1);
        p++;
    }
    return out->len > 0;
}

int json_num(double *out, const char *json, const char *key) {
    Buf tmp = { 0 };
    if (!json_raw(&tmp, json, key)) { buf_free(&tmp); return 0; }
    char *end = NULL;
    double v = strtod(tmp.data ? tmp.data : "", &end);
    int ok = end && end != tmp.data;
    buf_free(&tmp);
    if (ok) *out = v;
    return ok;
}

int json_bool(int *out, const char *json, const char *key) {
    const char *p = json_find(json, key);
    if (!p) return 0;
    if (!strncmp(p, "true", 4)) { *out = 1; return 1; }
    if (!strncmp(p, "false", 5)) { *out = 0; return 1; }
    return 0;
}

/* Copy the {...} body of a nested object, braces included. */
int json_obj(Buf *out, const char *json, const char *key) {
    const char *p = json_find(json, key);
    if (!p || *p != '{') return 0;
    const char *s = json_value_end(p);
    if (!s) return 0;
    buf_reset(out);
    buf_add(out, p, (size_t)(s - p));
    return 1;
}

/* ================= privacy ================= */

int looks_secret_key(const char *key, size_t len) {
    static const char *bad[] = {
        "pass", "passwd", "password", "secret", "token", "api_key", "apikey",
        "auth", "authorization", "credential", "cookie", "session_key", "private",
        "steamid", "steam_id", "account", "login", "user", "email", "ssn", "pin"
    };
    char low[128];
    size_t n = len < sizeof low - 1 ? len : sizeof low - 1;
    for (size_t i = 0; i < n; i++) low[i] = (char)tolower((unsigned char)key[i]);
    low[n] = '\0';
    for (size_t i = 0; i < sizeof bad / sizeof bad[0]; i++)
        if (strstr(low, bad[i])) return 1;
    return 0;
}

/* Replace a secret-looking value in place: everything after the '=' or ':' becomes "***". */
void redact(char *s) {
    if (!s) return;
    char *sep = strpbrk(s, "=:");
    if (!sep) return;
    size_t keep = (size_t)(sep - s) + 1;
    for (size_t i = keep; s[i]; i++) s[i] = '*';
    if (s[keep] == '\0' && keep > 1) s[keep - 1] = '*';
}

/* ================= files ================= */

int path_exists(const char *path) {
    struct stat st;
    return path && stat(path, &st) == 0;
}

int file_read(const char *path, Buf *out, size_t max) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return 0;
    buf_reset(out);
    char chunk[8192];
    size_t total = 0;
    while (total < max) {
        ssize_t n = read(fd, chunk, sizeof chunk);
        if (n <= 0) break;
        size_t take = (size_t)n;
        if (total + take > max) take = max - total;
        buf_add(out, chunk, take);
        total += take;
    }
    close(fd);
    return 1;
}

int file_tail(const char *path, size_t bytes, Buf *out) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return 0;
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return 0; }
    off_t start = 0;
    if ((size_t)st.st_size > bytes) start = st.st_size - (off_t)bytes;
    if (lseek(fd, start, SEEK_SET) < 0) { close(fd); return 0; }
    buf_reset(out);
    char chunk[8192];
    ssize_t n;
    while ((n = read(fd, chunk, sizeof chunk)) > 0) buf_add(out, chunk, (size_t)n);
    close(fd);
    return 1;
}

/* ================= /proc ================= */

double proc_uptime_s(void) {
    FILE *f = fopen("/proc/uptime", "r");
    if (!f) return -1;
    double up = -1;
    if (fscanf(f, "%lf", &up) != 1) up = -1;
    fclose(f);
    return up;
}

static long long proc_stat_field(int pid, int index) {
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/stat", pid);
    FILE *f = fopen(path, "r");
    if (!f) return -1;
    char buf[4096];
    size_t n = fread(buf, 1, sizeof buf - 1, f);
    fclose(f);
    buf[n] = '\0';
    /* the comm field is in parentheses and may contain spaces */
    char *p = strrchr(buf, ')');
    if (!p) return -1;
    p++;
    /* field 3 (state) is the first after ')' */
    int field = 2;
    while (*p) {
        while (*p == ' ') p++;
        if (!*p) break;
        char *end = p;
        while (*end && *end != ' ') end++;
        field++;
        if (field == index) return strtoll(p, NULL, 10);
        p = end;
    }
    return -1;
}

double proc_start_s(int pid) {
    long long starttime = proc_stat_field(pid, 22);
    double up = proc_uptime_s();
    if (starttime < 0 || up < 0) return -1;
    long hz = sysconf(_SC_CLK_TCK);
    if (hz <= 0) hz = 100;
    return up - (double)starttime / (double)hz;
}

int proc_ppid(int pid) { return (int)proc_stat_field(pid, 4); }
int proc_tty(int pid)  { return (int)proc_stat_field(pid, 7); }

int proc_cmdline(int pid, char *out, size_t n) {
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/cmdline", pid);
    int fd = open(path, O_RDONLY);
    if (fd < 0) return 0;
    ssize_t got = read(fd, out, n - 1);
    close(fd);
    if (got < 0) return 0;
    out[got] = '\0';
    for (ssize_t i = 0; i < got; i++) if (out[i] == '\0') out[i] = ' ';
    st_trim(out);
    return 1;
}

int proc_comm(int pid, char *out, size_t n) {
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/comm", pid);
    int fd = open(path, O_RDONLY);
    if (fd < 0) return 0;
    ssize_t got = read(fd, out, n - 1);
    close(fd);
    if (got < 0) return 0;
    out[got] = '\0';
    st_trim(out);
    return 1;
}

/* Read ONE named variable out of a process's environment.  The caller must ask only for
 * the few structural variables this tool needs (prefix, install path); never for
 * anything credential-shaped. */
int proc_env_value(int pid, const char *key, char *out, size_t n) {
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/environ", pid);
    int fd = open(path, O_RDONLY);
    if (fd < 0) return 0;
    size_t klen = strlen(key);
    int found = 0;
    char chunk[8192];
    ssize_t got;
    while ((got = read(fd, chunk, sizeof chunk)) > 0) {
        ssize_t i = 0;
        while (i < got) {
            const char *entry = chunk + i;
            size_t elen = 0;
            while (i + (ssize_t)elen < got && entry[elen] != '\0') elen++;
            if (elen > klen && strncmp(entry, key, klen) == 0 && entry[klen] == '=') {
                size_t vlen = elen - klen - 1;
                if (vlen >= n) vlen = n - 1;
                memcpy(out, entry + klen + 1, vlen);
                out[vlen] = '\0';
                found = 1;
            }
            i += (ssize_t)elen + 1;
        }
    }
    close(fd);
    return found;
}

long long proc_cpu_ticks(int pid, long long *utime, long long *stime) {
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/stat", pid);
    FILE *f = fopen(path, "r");
    if (!f) return -1;
    char buf[4096];
    size_t n = fread(buf, 1, sizeof buf - 1, f);
    fclose(f);
    buf[n] = '\0';
    char *p = strrchr(buf, ')');
    if (!p) return -1;
    p++;
    /* after the comm field: state(3) ppid(4) ... utime(14) stime(15) */
    int field = 2;
    long long u = 0, s = 0;
    while (*p) {
        while (*p == ' ') p++;
        if (!*p) break;
        char *end = p;
        while (*end && *end != ' ') end++;
        field++;
        if (field == 14) u = strtoll(p, NULL, 10);
        else if (field == 15) s = strtoll(p, NULL, 10);
        p = end;
    }
    if (utime) *utime = u;
    if (stime) *stime = s;
    return u + s;
}

long long proc_rss_kb(int pid) {
    char path[64];
    snprintf(path, sizeof path, "/proc/%d/statm", pid);
    FILE *f = fopen(path, "r");
    if (!f) return -1;
    long long size = 0, res = 0;
    if (fscanf(f, "%lld %lld", &size, &res) != 2) res = -1;
    fclose(f);
    long page = sysconf(_SC_PAGESIZE);
    if (page <= 0) page = 4096;
    return res * page / 1024;
}

/* ================= line diff ================= */

void linediff(const char *const *prev, int prev_n, const char *const *next, int next_n, LineDiff *out) {
    out->changed = calloc((size_t)(next_n > 0 ? next_n : 1), sizeof(int));
    out->changed_count = 0;
    out->removed = 0;
    if (!out->changed) return;
    /* A line counts as changed when it is not present at the same index in the previous
     * frame.  That is enough to keep the screen fresh without redrawing identical lines:
     * unchanged lines keep their position, changed ones are rewritten. */
    for (int i = 0; i < next_n; i++) {
        int same = 0;
        if (i < prev_n && prev[i] && strcmp(prev[i], next[i]) == 0) same = 1;
        if (!same) out->changed[out->changed_count++] = i;
    }
    out->removed = prev_n > next_n ? prev_n - next_n : 0;
}

void linediff_free(LineDiff *d) {
    free(d->changed);
    d->changed = NULL;
    d->changed_count = 0;
}
