/* overview_core.h - pure helpers for tools/overview (the TLD free-worker terminal overview).
 *
 * Everything here is free of side effects on the target system: no process is
 * started, no game is touched, and nothing but files under /proc is read.
 * Kept separate from overview.c so the self-test can link it without the TUI.
 */
#ifndef OVERVIEW_CORE_H
#define OVERVIEW_CORE_H

#include <stddef.h>
#include <stdarg.h>

/* ---- growable byte buffer ---- */
typedef struct {
    char  *data;
    size_t len;
    size_t cap;
} Buf;

void   buf_init(Buf *b);
void   buf_free(Buf *b);
void   buf_reset(Buf *b);
void   buf_add(Buf *b, const char *s, size_t n);
void   buf_puts(Buf *b, const char *s);
void   buf_printf(Buf *b, const char *fmt, ...);
char  *buf_steal(Buf *b);            /* NUL-terminated; caller frees */

/* ---- small string helpers ---- */
int    st_has(const char *hay, const char *needle);
int    st_has_ci(const char *hay, const char *needle);
int    st_starts(const char *s, const char *prefix);
int    st_ends(const char *s, const char *suffix);
void   st_trim(char *s);
void   st_copy(char *dst, size_t n, const char *src);
void   st_lower(char *s);
char  *st_dup(const char *s);

/* ---- formatting ---- */
void   fmt_elapsed(char *out, size_t n, double seconds);
void   fmt_age(char *out, size_t n, long long age_seconds);
void   fmt_size(char *out, size_t n, long long bytes);
void   fmt_dec(char *out, size_t n, double v, int decimals);

/* ---- tiny JSON reader (flat keys, one nesting level) ---- */
const char *json_value_end(const char *json); /* NULL for malformed/truncated input */
const char *json_find(const char *json, const char *key);
int    json_str(Buf *out, const char *json, const char *key);   /* unquoted string / raw token */
int    json_num(double *out, const char *json, const char *key);
int    json_bool(int *out, const char *json, const char *key);
int    json_obj(Buf *out, const char *json, const char *key);   /* the {...} body, braces included */
int    json_has(const char *json, const char *key);

/* ---- privacy: strip anything that looks like a secret ---- */
int    looks_secret_key(const char *key, size_t len);
void   redact(char *s);

/* ---- files ---- */
int    file_read(const char *path, Buf *out, size_t max);
int    file_tail(const char *path, size_t bytes, Buf *out);
int    path_exists(const char *path);

/* ---- /proc readers ---- */
double proc_uptime_s(void);
double proc_start_s(int pid);        /* seconds since boot, <0 when unknown */
int    proc_tty(int pid);            /* controlling tty, 0 when detached */
int    proc_ppid(int pid);
int    proc_cmdline(int pid, char *out, size_t n);
int    proc_comm(int pid, char *out, size_t n);
int    proc_env_value(int pid, const char *key, char *out, size_t n);
long long proc_cpu_ticks(int pid, long long *utime, long long *stime);
long long proc_rss_kb(int pid);

/* ---- line diff (the "redraw only changed lines" core) ---- */
typedef struct {
    int *changed;
    int  changed_count;
    int  removed;        /* lines the old frame had and the new one does not */
} LineDiff;

void   linediff(const char *const *prev, int prev_n, const char *const *next, int next_n, LineDiff *out);
void   linediff_free(LineDiff *d);

#endif /* OVERVIEW_CORE_H */
