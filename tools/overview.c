/* overview.c - a short native terminal overview of the TLD free-worker fleet.
 *
 * Build:  make -C tools
 * Run:    tools/overview            (alternate screen, 2 s poll, q or Ctrl-C to quit)
 *         tools/overview --once     (one text snapshot, no screen, no TTY needed)
 *         tools/overview --no-remote
 *         tools/overview --self-test
 *
 * What it does NOT do: it never launches, stops, or configures a game, never writes
 * to a prefix, a config, or a result file, and never prints a credential or an
 * environment variable.  It only reads /proc, the worker logs, the result files and
 * the read-only debug bridge.
 */
#define _GNU_SOURCE
#include "overview_core.h"
#include "native_process.h"
#include "overview_collect.h"
#include "overview_headset.h"
#include "overview_data.h"

#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

/* ================= terminal handling ================= */

static struct termios g_saved_termios;
static int  g_termios_saved = 0;
static volatile sig_atomic_t g_interrupted = 0;

static void on_signal(int sig) { (void)sig; g_interrupted = 1; }

static int g_stdout_is_tty = 0;

static void raw_mode_on(void) {
    if (!g_stdout_is_tty) return;
    if (tcgetattr(STDIN_FILENO, &g_saved_termios) != 0) return;
    g_termios_saved = 1;
    struct termios raw = g_saved_termios;
    raw.c_lflag &= (tcflag_t)~(ECHO | ICANON | ISIG);
    raw.c_iflag &= (tcflag_t)~(IXON);
    raw.c_cc[VMIN] = 0;
    raw.c_cc[VTIME] = 1;          /* a tenth of a second: the poll drives the pace */
    tcsetattr(STDIN_FILENO, TCSANOW, &raw);
}

static void raw_mode_off(void) {
    if (g_termios_saved) {
        tcsetattr(STDIN_FILENO, TCSANOW, &g_saved_termios);
        g_termios_saved = 0;
    }
}

static void term_enter(void) {
    /* alternate screen, hide the cursor, clear */
    fputs("\033[?1049h\033[?25l\033[2J\033[H", stdout);
    fflush(stdout);
}

static void term_leave(void) {
    fputs("\033[?25h\033[?1049l", stdout);
    fflush(stdout);
}

static int term_size(int *rows, int *cols) {
    struct winsize ws;
    if (ioctl(STDOUT_FILENO, TIOCGWINSZ, &ws) == 0 && ws.ws_row > 0 && ws.ws_col > 0) {
        *rows = ws.ws_row;
        *cols = ws.ws_col;
        return 1;
    }
    *rows = 24;
    *cols = 80;
    return 0;
}

/* ================= the frame ================= */

#define MAX_LINES 512

typedef struct {
    char *lines[MAX_LINES];
    int   count;
} OvText;

static void text_free(OvText *t) {
    for (int i = 0; i < t->count; i++) free(t->lines[i]);
    t->count = 0;
}

static void text_add(OvText *t, const char *fmt, ...) {
    if (t->count >= MAX_LINES) return;
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    t->lines[t->count++] = st_dup(buf);
}

static const char *mark(int ok) { return ok ? "OK  " : "WARN"; }

/* A string field, or "?" when it is empty.  (A ternary between a char[] and a
 * literal would decay the array to a pointer and test the pointer, not the text.) */
static const char *or_q(const char *s) { return (s && s[0]) ? s : "?"; }

static const char *yes_no(int value) { return value < 0 ? "?" : value ? "yes" : "no"; }
static void guard_size(char *out, size_t n, long long value) {
    if (value < 0) st_copy(out, n, "?");
    else fmt_size(out, n, value);
}
static void guard_psi(char *out, size_t n, double some, double full) {
    char a[24] = "?", b[24] = "?";
    if (some >= 0) snprintf(a, sizeof a, "%.2f%%", some);
    if (full >= 0) snprintf(b, sizeof b, "%.2f%%", full);
    snprintf(out, n, "some %s / full %s", a, b);
}
static void build_guard(const OvFrame *f, OvText *t) {
    const OvAdmission *a = &f->admission;
    const OvPressure *p = &f->pressure;
    char anon[32], file[32], swap[32], avail[32], elapsed[32] = "?", exit[24] = "?";
    text_add(t, "ADMISSION / MEMORY GUARD (read-only)");
    text_add(t, "  state: %s  pause: %s  active: %s", or_q(a->state), or_q(a->pause_kind), yes_no(a->has_active));
    text_add(t, "  reason: %s", or_q(a->reason));
    char age[32] = "?";
    if (a->updated_epoch > 0 && (long long)time(NULL) >= a->updated_epoch)
        fmt_age(age,sizeof age,(long long)time(NULL) - a->updated_epoch);
    text_add(t, "  published: %s; active/job fields are broker snapshots", age);
    long long seconds = a->elapsed_s;
    long long now = (long long)time(NULL);
    if (a->has_active == 1 && a->started_epoch > 0 && now >= a->started_epoch) seconds = now - a->started_epoch;
    if (seconds >= 0) fmt_elapsed(elapsed, sizeof elapsed, (double)seconds);
    if (a->exit_status >= 0) snprintf(exit, sizeof exit, "%d", a->exit_status);
    text_add(t, "  job: %s  profile: %s  unit: %s", or_q(a->name), or_q(a->profile), or_q(a->unit));
    if (a->cgroup[0]) text_add(t, "  cgroup: %s", a->cgroup);
    text_add(t, "  elapsed: %s  published exit: %s  OOM killed: %s  result: %s", elapsed, exit,
             yes_no(a->oom_killed), or_q(a->result));
    text_add(t, "  limits: memory high %s / max %s / swap %s / OOM group %s",
             or_q(a->memory_high), or_q(a->memory_max), or_q(a->memory_swap_max), or_q(a->oom_group));
    text_add(t, "          tasks %s / CPU quota %s / CPU weight %s / IO weight %s / nice %s",
             or_q(a->tasks_max), or_q(a->cpu_quota), or_q(a->cpu_weight), or_q(a->io_weight), or_q(a->nice));
    text_add(t, "  limit verification: %s", yes_no(a->limits_verified));
    guard_size(avail, sizeof avail, a->mem_available); guard_size(swap, sizeof swap, a->swap_used);
    guard_size(anon, sizeof anon, a->session_anon); guard_size(file, sizeof file, a->session_swap);
    text_add(t, "  broker snapshot: available %s / global swap %s / session anon %s / swap %s", avail, swap, anon, file);
    text_add(t, "");
    text_add(t, "GLOBAL / SESSION MEMORY PRESSURE");
    guard_size(avail, sizeof avail, p->mem_avail); guard_size(swap, sizeof swap, p->global_swap);
    guard_size(anon, sizeof anon, p->global_anon); guard_size(file, sizeof file, p->global_file);
    text_add(t, "  global: available %s / swap used %s / anon %s / file cache %s", avail, swap, anon, file);
    guard_size(anon, sizeof anon, p->anon); guard_size(file, sizeof file, p->file); guard_size(swap, sizeof swap, p->swap);
    text_add(t, "  watched session: anon %s / file %s / swap %s", anon, file, swap);
    if (p->err[0]) text_add(t, "  %s", p->err);
    char global[80], session[80];
    guard_psi(global, sizeof global, p->global_some, p->global_full);
    guard_psi(session, sizeof session, p->session_some, p->session_full);
    text_add(t, "  memory PSI avg10: global %s / session %s", global, session);
    char delta[32] = "?", current[32] = "?", published[32] = "?";
    if (f->new_oom_events >= 0) snprintf(delta, sizeof delta, "%lld", f->new_oom_events);
    if (p->oom_kills >= 0) snprintf(current, sizeof current, "%lld", p->oom_kills);
    if (a->watched_oom_kills >= 0) snprintf(published, sizeof published, "%lld", a->watched_oom_kills);
    text_add(t, "  external OOM events since broker baseline: %s (live watched %s / broker snapshot %s)", delta, current, published);
    text_add(t, "");
    text_add(t, "TOP MEMORY CONSUMERS (RSS; shared pages may be counted more than once)");
    if (!f->consumer_count) text_add(t, "  (no readable process RSS)");
    for (int i = 0; i < f->consumer_count; i++) {
        guard_size(anon, sizeof anon, f->consumers[i].rss_kb * 1024);
        text_add(t, "  pid %-7d %-24s %s", f->consumers[i].pid, f->consumers[i].comm, anon);
    }
    text_add(t, "");
    text_add(t, "STEAM / WIVRN / ADB / OPENXR");
    text_add(t, "  Steam: %s  WiVRn: %s", or_q(f->runtime.steam), or_q(f->runtime.wivrn));
    text_add(t, "  ADB: %s", or_q(f->runtime.adb));
    text_add(t, "  OpenXR: %s", or_q(f->runtime.openxr));
    text_add(t, "");
    text_add(t, "HEADSET (Quest / WiVRn, read-only over an already running ADB server)");
    {
        char hs[1024] = { 0 };
        ov_hs_format(&f->headset, hs, sizeof hs);
        text_add(t, "  %s", hs[0] ? hs : "unavailable");
        if (f->headset.status[0]) text_add(t, "  %s", f->headset.status);
    }
    text_add(t, "");
    text_add(t, "TLD WINDOWS / SPECIAL WORKSPACE");
    text_add(t, "  special:tld-tests exposed: %s  TLD windows: %d", yes_no(f->tld_tests_exposed), f->tld_window_count);
    if (f->window_err[0]) text_add(t, "  %s", f->window_err);
    for (int i = 0; i < f->window_count; i++) {
        const OvWindow *w = &f->windows[i];
        text_add(t, "  %s / %s  floating %s / fullscreen %s / focused %s", or_q(w->app_class),
                 or_q(w->workspace), yes_no(w->floating), yes_no(w->fullscreen), yes_no(w->focused));
        text_add(t, "    %s", or_q(w->title));
    }
    if (f->tld_window_count > f->window_count) text_add(t, "  ... %d more TLD windows", f->tld_window_count - f->window_count);
    text_add(t, "");
}

/* How many rows the frame wants; the caller trims it to the terminal. */
static int build_frame(const OvFrame *f, OvText *t, int rows) {
    char tmp[256];
    time_t now = time(NULL);
    struct tm tmv;
    localtime_r(&now, &tmv);
    strftime(tmp, sizeof tmp, "%H:%M:%S", &tmv);

    text_add(t, "TLD free-worker overview   %s   poll 2s   q or Ctrl-C to quit", tmp);
    text_add(t, "================================================================================");

    build_guard(f, t);

    /* ---- agents ---- */
    text_add(t, "AGENTS / WORKSPACES");
    if (f->agent_count == 0) {
        text_add(t, "  (no agent processes found under /proc)");
    } else {
        /* the workers are sorted by name; when the terminal is short the tail is
         * dropped and the count says how many are hidden */
        int shown = f->agent_count;
        int max_agents = rows > 30 ? f->agent_count : 12;
        int hidden = 0;
        if (shown > max_agents) { hidden = shown - max_agents; shown = max_agents; }
        for (int i = 0; i < shown; i++) {
            const OvAgent *a = &f->agents[i];
            fmt_elapsed(tmp, sizeof tmp, a->started_s);
            char res[64];
            if (a->has_result) {
                char sz[24], age[24];
                fmt_size(sz, sizeof sz, a->result_size);
                fmt_age(age, sizeof age, a->result_age_s);
                snprintf(res, sizeof res, "result %s (%s)", sz, age);
            } else {
                snprintf(res, sizeof res, "no result yet");
            }
            /* the model, when the agent's command line names one */
            char model[72] = { 0 };
            if (a->model[0]) {
                const char *slash = strrchr(a->model, '/');
                st_copy(model, sizeof model, slash ? slash + 1 : a->model);
            }
            text_add(t, "  %-38s pid %-7d up %-8s %-22s %s",
                     a->workspace, a->pid, tmp, model, res);
        }
        if (hidden > 0) text_add(t, "  ... and %d more worker(s) (scroll for the rest)", hidden);
    }
    text_add(t, "");

    /* ---- game processes ---- */
    text_add(t, "TLD / PROTON PROCESSES");
    if (f->game_count == 0 && f->remote_game_count == 0) {
        text_add(t, "  local: none running");
    } else {
        for (int i = 0; i < f->game_count; i++) {
            const OvGame *g = &f->games[i];
            text_add(t, "  local  pid %-7d %s  bg=%s  bridge=%s",
                     g->pid, g->prefix[0] ? g->prefix : "(prefix unknown)",
                     g->background ? "yes" : "no",
                     g->port[0] ? g->port : "-");
            if (g->game[0]) text_add(t, "          install: %s", g->game);
        }
    }
    if (f->remote_game_count > 0) {
        for (int i = f->game_count; i < f->game_count + f->remote_game_count; i++) {
            const OvGame *g = &f->games[i];
            text_add(t, "  remote pid %-7d %s  bg=%s",
                     g->pid, g->prefix[0] ? g->prefix : "(prefix unknown)",
                     g->background ? "yes" : "no");
            if (g->game[0]) text_add(t, "          install: %s", g->game);
        }
    } else if (f->remote_err[0]) {
        text_add(t, "  remote: %s", f->remote_err);
    }
    text_add(t, "");

    /* ---- graphics compliance ---- */
    text_add(t, "EFFECTIVE BRIDGE STATE / LOW-LOAD COMPLIANCE");
    if (!f->graphics.ok) {
        text_add(t, "  %s  %s", mark(0), f->graphics.err[0] ? f->graphics.err : "bridge unavailable");
    } else {
        const OvGraphics *g = &f->graphics;
        text_add(t, "  bridge %s  build %s  port %s  on %s",
                 or_q(g->version), or_q(g->build), or_q(g->port), or_q(g->where));
        text_add(t, "  %s quality   level %d%s   (lowest = 0)",
                 mark(g->ok_quality), g->quality_level,
                 g->quality_name[0] ? g->quality_name : "");
        text_add(t, "  %s fps       target %d  measured %d  game limiter %s %d",
                 mark(g->ok_fps), g->target_fps, g->measured_fps,
                 g->fps_cap_enabled ? "on" : "off", g->fps_cap_limit);
        text_add(t, "  %s vsync     bridge %d  game setting %s",
                 mark(g->ok_vsync), g->vsync, g->vsync_setting ? "on" : "off");
        text_add(t, "  %s resolution %dx%d  %s",
                 mark(g->ok_resolution), g->screen_w, g->screen_h, or_q(g->fullscreen));
        text_add(t, "  %s background  game runs detached from a terminal",
                 mark(g->ok_background));
        text_add(t, "  overall: %s", g->compliant ? "COMPLIANT" : "NOT compliant");
    }
    text_add(t, "");

    /* ---- the Steam session ---- */
    text_add(t, "STEAM HOST / CLIENT / RELAY / SESSION PHASE");
    if (!f->steam.ok) {
        text_add(t, "  %s  %s", mark(0), f->steam.err[0] ? f->steam.err : "no steam log");
    } else {
        const OvSteam *s = &f->steam;
        fmt_age(tmp, sizeof tmp, s->log_age_s);
        text_add(t, "  log %s (%s)   relay %s", s->log, tmp, or_q(s->relay));
        text_add(t, "  host: %s   client: %s", or_q(s->host), or_q(s->client));
        text_add(t, "  phase: %s   verdicts: %d pass, %d fail",
                 s->phase[0] ? s->phase : "(none seen)", s->pass_count, s->fail_count);
        if (s->ports[0]) text_add(t, "  bridge ports: %s", s->ports);
        if (s->last_verdict[0]) text_add(t, "  last verdict: %s", s->last_verdict);
    }
    text_add(t, "");

    /* ---- reports and findings ---- */
    text_add(t, "REPORTS / FINDINGS");
    if (f->report_count == 0) {
        text_add(t, "  (no result files)");
    } else {
        int max_reports = rows > 30 ? f->report_count : 6;
        int shown = f->report_count < max_reports ? f->report_count : max_reports;
        for (int i = 0; i < shown; i++) {
            const OvReport *r = &f->reports[i];
            char sz[32], age[32];
            fmt_size(sz, sizeof sz, r->size);
            fmt_age(age, sizeof age, r->age_s);
            text_add(t, "  %-26s %9s  %-10s  %s", r->name, sz, age, r->head);
        }
    }
    text_add(t, "");

    /* ---- git ---- */
    text_add(t, "GIT");
    if (!f->git.ok) {
        text_add(t, "  %s  %s", mark(0), f->git.err[0] ? f->git.err : "unknown");
    } else {
        text_add(t, "  branch %s  head %s  dirty %d file(s)  untracked %d",
                 or_q(f->git.branch), or_q(f->git.head),
                 f->git.dirty_files, f->git.untracked_files);
    }
    text_add(t, "");

    /* ---- the machine ---- */
    text_add(t, "SYSTEM");
    {
        const OvSystem *s = &f->sys;
        char load[32], mem[32], used[32], disk[32];
        fmt_dec(load, sizeof load, s->load1, 2);
        fmt_size(mem, sizeof mem, s->mem_total);
        fmt_size(used, sizeof used, s->mem_used);
        fmt_size(disk, sizeof disk, s->disk_used);
        text_add(t, "  load %s / %.2f / %.2f  (%d cores, %d running)",
                 load, s->load5, s->load15, s->cores, s->procs_running);
        text_add(t, "  memory %s used of %s", used, mem);
        text_add(t, "  disk   %s used", disk);
        {
            char self[64];
            fmt_size(self, sizeof self, s->self_rss_kb * 1024);
            text_add(t, "  overview itself: %s resident, %lld ms cpu so far", self, s->self_cpu_ms);
        }
    }
    text_add(t, "================================================================================");
    return t->count;
}

/* ================= the one-shot text output ================= */

static void print_once(const OvFrame *f) {
    OvText t = { { 0 }, 0 };
    build_frame(f, &t, 1000);
    for (int i = 0; i < t.count; i++) printf("%s\n", t.lines[i]);
    text_free(&t);
}

/* ================= the self-test ================= */

static int g_failures = 0;

#define CHECK(cond, what) do { \
    if (cond) { printf("  ok   %s\n", (what)); } \
    else { printf("  FAIL %s\n", (what)); g_failures++; } \
} while (0)

static int run_self_test(void) {
    printf("overview self-test\n");
    g_failures = 0;

    {
        char output[128];
        const char *failed[] = {"/bin/sh", "-c", "printf 'PASSWORD=fixture-private-helper'; exit 1", NULL};
        CHECK(native_capture(failed,output,sizeof output,1000) != 0 && !output[0], "failed helper output stays private");
        const char *slow[] = {"/bin/sh", "-c", "sleep 5 & wait", NULL};
        long long start = native_monotonic_ms();
        CHECK(native_capture(slow,output,sizeof output,100) != 0 && native_monotonic_ms() - start < 1000 && !output[0],
              "helper timeout bounds the entire owned process tree");
        const char *overflow[] = {"/bin/sh", "-c", "printf 'too much output'", NULL};
        CHECK(native_capture(overflow,output,4,1000) != 0 && !output[0], "overflowed helper output is unavailable");
    }
    /* string helpers */
    CHECK(st_has("hello world", "lo wo"), "st_has finds a substring");
    CHECK(!st_has("hello", "xyz"), "st_has rejects a missing substring");
    CHECK(st_has_ci("HelloWorld", "hellow"), "st_has_ci ignores case");
    CHECK(st_starts("tld-revamp-free-x", "tld-revamp"), "st_starts");
    CHECK(st_ends("report.md", ".md"), "st_ends");
    {
        char b[32];
        st_copy(b, sizeof b, "  padded  ");
        st_trim(b);
        CHECK(strcmp(b, "padded") == 0, "st_trim removes both ends");
    }

    /* formatting */
    {
        char b[32];
        fmt_elapsed(b, sizeof b, 45);       CHECK(strcmp(b, "45s") == 0, "fmt_elapsed seconds");
        fmt_elapsed(b, sizeof b, 125);      CHECK(strcmp(b, "2m05s") == 0, "fmt_elapsed minutes");
        fmt_elapsed(b, sizeof b, 3725);     CHECK(strcmp(b, "1h02m") == 0, "fmt_elapsed hours");
        fmt_size(b, sizeof b, 2048);        CHECK(strcmp(b, "2.0 KiB") == 0, "fmt_size");
        fmt_age(b, sizeof b, 90);           CHECK(strcmp(b, "1m ago") == 0, "fmt_age");
    }

    /* the JSON reader against a real bridge reply */
    {
        const char *j = "{\"ok\":true,\"version\":\"0.69.1\",\"level\":0,\"vSync\":0,\"targetFps\":30}";
        Buf s = { 0 };
        CHECK(json_str(&s, j, "version") && s.data && strcmp(s.data, "0.69.1") == 0, "json_str reads a string");
        buf_free(&s);
        double d = -1;
        CHECK(json_num(&d, j, "targetFps") && d == 30, "json_num reads a number");
        CHECK(json_num(&d, j, "level") && d == 0, "json_num reads a zero");
        int b = -1;
        CHECK(json_bool(&b, j, "ok") && b == 1, "json_bool reads true");
        CHECK(!json_has(j, "nope"), "json_has rejects a missing key");
    }
    {
        /* a nested object: the graphics block of renderinv */
        const char *j = "{\"quality\":{\"level\":\"Ultra\",\"vSyncCount\":0,\"targetFrameRate\":30,\"screen\":\"1920x1080\"},\"cameras\":[]}";
        Buf o = { 0 };
        CHECK(json_obj(&o, j, "quality") && st_has(o.data, "Ultra"), "json_obj takes a nested object");
        buf_free(&o);
        Buf s = { 0 };
        CHECK(json_str(&s, j, "screen") == 0, "json_str on a nested key is scoped");
        buf_free(&s);
    }
    {
        /* an escaped string */
        const char *j = "{\"msg\":\"a\\\"b\"}";
        Buf s = { 0 };
        CHECK(json_str(&s, j, "msg") && s.data && strcmp(s.data, "a\"b") == 0, "json_str unescapes");
        buf_free(&s);
    }

    /* the privacy filter */
    CHECK(looks_secret_key("password", 8), "a password key looks secret");
    CHECK(looks_secret_key("STEAM_API_KEY", 13), "an api key looks secret");
    CHECK(!looks_secret_key("quality", 7), "a plain key does not");
    {
        char b[64];
        st_copy(b, sizeof b, "password=hunter2");
        redact(b);
        CHECK(!st_has(b, "hunter2"), "redact hides the value");
        CHECK(st_has(b, "password"), "redact keeps the key");
    }

    /* the line diff: only the changed lines are marked */
    {
        const char *a[] = { "one", "two", "three" };
        const char *b[] = { "one", "TWO", "three", "four" };
        LineDiff d;
        linediff(a, 3, b, 4, &d);
        CHECK(d.changed_count == 2, "linediff marks the changed and the new line");
        CHECK(d.changed[0] == 1 && d.changed[1] == 3, "linediff reports the right indexes");
        linediff_free(&d);
    }
    {
        const char *a[] = { "same" };
        const char *b[] = { "same" };
        LineDiff d;
        linediff(a, 1, b, 1, &d);
        CHECK(d.changed_count == 0, "linediff marks nothing when nothing changed");
        linediff_free(&d);
    }

    /* /proc: the tool's own process must be visible without pgrep */
    {
        char comm[64];
        CHECK(proc_comm((int)getpid(), comm, sizeof comm), "proc_comm reads our own process");
        CHECK(proc_uptime_s() > 0, "proc_uptime_s reads the uptime");
        CHECK(proc_start_s((int)getpid()) >= 0, "proc_start_s reads our own start time");
        long long rss = proc_rss_kb((int)getpid());
        CHECK(rss > 0, "proc_rss_kb reads our own resident size");
    }

    /* the buffer */
    {
        Buf b = { 0 };
        buf_puts(&b, "abc");
        buf_printf(&b, "%d", 42);
        CHECK(strcmp(b.data, "abc42") == 0, "the buffer appends");
        buf_free(&b);
    }

    /* the file helpers */
    {
        Buf b = { 0 };
        CHECK(file_read("/proc/uptime", &b, 64) && b.len > 0, "file_read reads a file");
        buf_free(&b);
        CHECK(!file_read("/proc/definitely-not-here", &b, 64), "file_read reports a missing file");
        buf_free(&b);
    }

    /* the remote game's record parser, without a second machine */
    {
        OvGame games[4];
        int n = 0;
        const char *remote =
            "G|206758|TheLongDrive.ex|/opt/tld/steamcoord-prefix|/srv/tld/SteamCoord/game|0\n"
            "G|208621|TheLongDrive.ex|/opt/tld/revamp-testprefix|/srv/tld/test/game|3\n";
        int got = ov_parse_remote_games(remote, games, &n, 4);
        CHECK(got == 2, "the remote scan yields both game processes");
        CHECK(n == 2, "the remote game count is right");
        CHECK(games[0].pid == 206758, "the remote pid is parsed");
        CHECK(strcmp(games[0].prefix, "/opt/tld/steamcoord-prefix") == 0, "the remote prefix is parsed");
        CHECK(strcmp(games[0].game, "/srv/tld/SteamCoord/game") == 0, "the remote install path is parsed");
        CHECK(games[0].background == 1, "tty 0 means a background process");
        CHECK(games[1].background == 0, "a tty means a foreground process");
        CHECK(games[0].remote == 1, "the remote games are marked as remote");
    }
    {
        /* a malformed line is skipped, not misread */
        OvGame games[4];
        int n = 0;
        const char *remote = "garbage\nG|1|x|y\nG|2|TheLongDrive.ex|/p|/i|0\n";
        int got = ov_parse_remote_games(remote, games, &n, 4);
        CHECK(got == 1, "a short record is skipped");
        CHECK(games[0].pid == 2, "the good record is still read");
    }

    /* the steam log parser, on the shape the workers print */
    {
        OvSteam s;
        memset(&s, 0, sizeof s);
        const char *log =
            "some tool source: log(f\"  local  prefix   : {tldenv.TEST_COMPAT}\")\n"
            "=== steamdeath run 20261021-001352 (relay iad) ===\n"
            "  local  game dir : /srv/tld/SteamDeath/game\n"
            "  local  prefix   : /opt/tld/steamdeath-prefix\n"
            "  remote prefix   : /opt/tld/steamdeath-prefix\n"
            "  bridge ports    : local 27060, remote via tunnel 27070\n"
            "  relay setting   : iad\n"
            "--- preflight ---\n"
            "  PASS  PREFIX ISOLATED (desktop)\n"
            "  FAIL  BUILD MATCH (both machines)\n"
            "--- a later note by the agent ---\n";
        int ok = ov_parse_steam_log(log, &s);
        CHECK(ok == 1, "the steam log parser finds the run block");
        CHECK(strcmp(s.relay, "iad") == 0, "the relay setting is read");
        CHECK(strcmp(s.host, "/opt/tld/steamdeath-prefix") == 0, "the local prefix is read");
        CHECK(strcmp(s.client, "/opt/tld/steamdeath-prefix") == 0, "the remote prefix is read");
        CHECK(strcmp(s.ports, "local 27060, remote via tunnel 27070") == 0, "the bridge ports are read");
        CHECK(s.pass_count == 1 && s.fail_count == 1, "the verdicts are counted");
        CHECK(strcmp(s.phase, "preflight") == 0, "the phase is the run's first section header");
        CHECK(strncmp(s.last_verdict, "BUILD MATCH", 11) == 0, "the last verdict is named");
    }
    {
        /* a log without a run block holds no session data */
        OvSteam s;
        memset(&s, 0, sizeof s);
        const char *log = "=== their latest run ===\nsome code\n  relay setting   : off\n";
        CHECK(ov_parse_steam_log(log, &s) == 0, "a log without a timestamped run header is refused");
    }
    {
        /* the tool's own source (quoted in the transcript) is not run output */
        OvSteam s;
        memset(&s, 0, sizeof s);
        const char *log =
            "=== steamdeath run 20261021-000559 (relay iad) ===\n"
            "  relay setting   : iad\n"
            "code: log(f\"  relay setting   : {RELAY}\")\n";
        ov_parse_steam_log(log, &s);
        CHECK(strcmp(s.relay, "iad") == 0, "the real relay setting wins over the quoted source");
    }

    /* Admission contract: policy is owned by the broker, including threshold
     * reasons. These fixtures never contact systemd or start a workload. */
    {
        OvAdmission a;
        const char *j =
            "{\"state\":\"pressure-paused\",\"pauseKind\":\"threshold\","
            "\"reason\":\"MemAvailable below 6 GiB (4096 MiB)\",\"active\":true,"
            "\"unit\":\"tld-job-fixture.service\",\"profile\":\"test\",\"name\":\"fixture\","
            "\"startedEpoch\":1700000000,\"elapsedSeconds\":42,\"exitStatus\":0,"
            "\"oomKilled\":false,\"result\":\"running\",\"memAvailable\":4294967296,"
            "\"swapUsed\":123,\"sessionAnon\":456,\"sessionSwap\":789,\"watchedOOMKills\":11,"
            "\"limits\":{\"memoryHigh\":\"8G\",\"memoryMax\":\"12G\",\"memorySwapMax\":\"1G\","
            "\"tasksMax\":\"1024\",\"cpuQuota\":\"200%\",\"memoryOOMGroup\":true,"
            "\"cpuWeight\":\"50\",\"ioWeight\":\"25\",\"nice\":10}}";
        CHECK(ov_parse_admission_json(j, "state: idle\n", &a) && a.available,
              "admission JSON takes precedence over text status");
        CHECK(!strcmp(a.state, "pressure-paused") && !strcmp(a.pause_kind, "threshold") &&
              !strcmp(a.reason, "MemAvailable below 6 GiB (4096 MiB)"), "threshold state and broker reason survive parsing");
        CHECK(a.has_active == 1 && !strcmp(a.name, "fixture") && !strcmp(a.profile, "test") &&
              !strcmp(a.unit, "tld-job-fixture.service"), "active job identity is parsed");
        CHECK(a.started_epoch == 1700000000 && a.elapsed_s == 42 && a.exit_status == 0 &&
              a.oom_killed == 0 && !strcmp(a.result, "running"), "job timing and exit/OOM state are parsed");
        CHECK(a.mem_available == 4294967296LL && a.swap_used == 123 && a.session_anon == 456 &&
              a.session_swap == 789 && a.watched_oom_kills == 11, "broker memory snapshot keeps byte units");
        CHECK(!strcmp(a.memory_high, "8G") && !strcmp(a.memory_max, "12G") && !strcmp(a.memory_swap_max, "1G") &&
              !strcmp(a.tasks_max, "1024") && !strcmp(a.cpu_quota, "200%") && !strcmp(a.oom_group, "yes") &&
              !strcmp(a.cpu_weight, "50") && !strcmp(a.io_weight, "25") && !strcmp(a.nice, "10"),
              "all broker profile limits are scoped and parsed");
        ov_parse_admission_json("{\"limits\":{\"state\":\"running\"}}", NULL, &a);
        CHECK(!a.available && a.has_active == -1 && a.exit_status == -1 && a.mem_available == -1,
              "nested state cannot masquerade as admission status; absent readings are unknown");
        CHECK(ov_parse_admission_json("{\"state\":\"running\"", "state: paused\npause: manual\nreason: operator hold\n"
              "active: no\nname: fixture\nprofile: agent\nunit: tld-job-fixture.service\nstarted: 1700000000\n"
              "elapsed: 65s\nexit: 137\nOOM: yes\nresult: oom-killed\nMemAvailable: 1024 bytes\n"
              "global swap: 2048 bytes\nsession anon: 4096 bytes\nsession swap: 512 bytes\nwatched OOM kills: 12\n", &a),
              "truncated JSON falls back to broker text status");
        CHECK(!strcmp(a.state, "paused") && !strcmp(a.pause_kind, "manual") && !strcmp(a.reason, "operator hold") &&
              a.has_active == 0 && a.elapsed_s == 65 && a.exit_status == 137 && a.oom_killed == 1 &&
              a.mem_available == 1024 && a.swap_used == 2048 && a.session_anon == 4096 && a.session_swap == 512 &&
              a.watched_oom_kills == 12, "text fallback reads manual pause, timing, exit and memory fields");
        ov_parse_admission_json(NULL, "state: idle\nupdated: 100\ncgroup: /sys/fs/cgroup/fixture\nlimits: {\"memoryHigh\":\"1610612736\",\"memoryMax\":\"2147483648\",\"cpuQuota\":\"200%\",\"cpuWeight\":\"50\",\"ioWeight\":\"50\",\"nice\":10,\"memoryOOMGroup\":true,\"verified\":true}\n", &a);
        CHECK(a.updated_epoch == 100 && !strcmp(a.cgroup,"/sys/fs/cgroup/fixture") &&
              !strcmp(a.memory_max,"2147483648") && !strcmp(a.cpu_quota,"200%") && a.limits_verified == 1,
              "text fallback preserves publication age, cgroup and verified limits");
        CHECK(!ov_parse_admission_json(NULL, "unrelated state: running\n", &a), "status keys must match whole line keys");
        ov_parse_admission_json("{\"state\":\"idle\",\"active\":false,\"exitStatus\":1.5,\"elapsedSeconds\":1e3}", NULL, &a);
        CHECK(a.has_active == 0 && a.exit_status == -1 && a.elapsed_s == -1, "fractional and exponent job integers are rejected");
        ov_parse_admission_json("{\"state\":\"paused\",\"reason\":\"hold \\u001b password: hidden-value\","
                                "\"name\":\"API_KEY=hidden-key\",\"result\":\"Bearer hidden-token\"}", NULL, &a);
        CHECK(!st_has(a.reason, "hidden-value") && !st_has(a.name, "hidden-key") && !st_has(a.result, "hidden-token") &&
              !strchr(a.reason, '\033'), "external admission labels cannot print secrets or terminal escapes");
    }
    {
        OvAdmission a;
        ov_parse_admission_json("{\"state\":\"paused\",\"reason\":\"\\u0070assword: hidden-escaped-value\",\"active\":\"true\"}", NULL, &a);
        CHECK(!st_has(a.reason, "hidden-escaped-value") && a.has_active == -1,
              "escaped credential labels are filtered and string booleans remain unknown");
    }
    {
        long long anon, file, swap, kills, available, total, free_swap;
        CHECK(ov_parse_memory_stat("anon_thp 9000\nfile_mapped 8000\nanon 4096\nfile 8192\nswap 1024\n", &anon, &file, &swap) &&
              anon == 4096 && file == 8192 && swap == 1024, "memory.stat distinguishes anon/file/swap from sub-counters");
        CHECK(ov_parse_memory_stat("anon 0\nfile 16384\n", &anon, &file, &swap) && anon == 0 && file == 16384 && swap == -1,
              "cgroup v2 stat without swap preserves unknown until memory.swap.current is read");
        CHECK(!ov_parse_memory_stat("anon -1\nfile 2oops\nswap 999999999999999999999\n", &anon, &file, &swap) &&
              anon == -1 && file == -1 && swap == -1, "malformed negative and overflowing memory counters are rejected");
        CHECK(!ov_parse_memory_stat("inactive_anon 1\nactive_file 2\n", &anon, &file, &swap), "missing anon/file is not reported as healthy zero");
        CHECK(ov_parse_memory_events_oom_kills("oom 7\noom_group_kill 9\noom_kill 3\n", &kills) && kills == 3,
              "memory.events selects exactly oom_kill");
        CHECK(ov_parse_memory_events_oom_kills("oom_kill 0\n", &kills) && kills == 0, "zero OOM kills is a known reading");
        CHECK(!ov_parse_memory_events_oom_kills("oom 3\noom_group_kill 2\n", &kills) && kills == -1,
              "missing oom_kill remains unknown");
        CHECK(!ov_parse_memory_events_oom_kills("oom_kill 3bad\n", &kills), "invalid OOM counter suffix is rejected");
        CHECK(ov_oom_delta(12, 9) == 3 && ov_oom_delta(9, 9) == 0, "external OOM events use the broker baseline");
        CHECK(ov_oom_delta(1, 9) == -1 && ov_oom_delta(1, -1) == -1 && ov_oom_delta(-1, 0) == -1,
              "missing baseline or reset counter cannot claim zero new OOM events");
        const char *mem = "MemTotal: 99999 kB\nMemAvailable: 4096 kB\nSwapTotal: 2048 kB\nSwapFree: 512 kB\n";
        CHECK(ov_parse_meminfo_available(mem, &available) && available == 4194304 &&
              ov_parse_meminfo_swap(mem, &total, &free_swap) && total - free_swap == 1572864,
              "global available memory and used swap convert kB to bytes");
        CHECK(!ov_parse_meminfo_available("MemAvailable: 1 bytes\n", &available) &&
              !ov_parse_meminfo_swap("SwapTotal: 1 kB\nSwapFree: 2 kB\n", &total, &free_swap), "bad meminfo units and impossible swap are rejected");
        double some, full;
        CHECK(ov_parse_memory_psi("some avg10=3.25 avg60=1.00 total=90\nfull avg10=0.50 avg60=0.01 total=2\n", &some, &full) &&
              some == 3.25 && full == 0.5, "memory PSI reads avg10 stall percentages");
        CHECK(!ov_parse_memory_psi("some avg10=nan\nfull avg10=101\n", &some, &full) && some == -1 && full == -1,
              "unavailable or invalid PSI is unknown");
    }
    {
        OvWindow windows[2]; int count, exposed;
        const char *clients =
            "[{\"class\":\"terminal\",\"title\":\"unrelated\",\"workspace\":{\"name\":\"special:tld-tests\"}},"
            "{\"class\":\"steam_app_1017180\",\"initialClass\":\"TheLongDrive.exe\","
            "\"title\":\"The Long Drive {test} \\\"quoted\\\"\",\"workspace\":{\"id\":-99,\"name\":\"special:tld-tests\"},"
            "\"floating\":true,\"fullscreen\":0,\"focusHistoryID\":0},"
            "{\"class\":\"TheLongDrive.exe\",\"title\":\"second\",\"workspace\":{\"name\":\"2\"},\"fullscreen\":2,\"focusHistoryID\":1}]";
        CHECK(ov_parse_hypr_clients(clients, windows, &count, 2, &exposed) == 2 && count == 2,
              "Hyprland clients select TLD by class, initialClass or title");
        CHECK(!strcmp(windows[0].workspace, "special:tld-tests") && windows[0].floating && !windows[0].fullscreen &&
              windows[0].focused && !strcmp(windows[0].title, "The Long Drive {test} \"quoted\"") && windows[1].fullscreen,
              "nested workspaces and escaped title braces do not corrupt window parsing");
        CHECK(exposed == -1 && windows[0].special_exposed == -1, "a client on a special workspace does not prove exposure");
        CHECK(ov_parse_hypr_monitors("[{\"specialWorkspace\":{\"id\":0,\"name\":\"\"}},{\"specialWorkspace\":{\"name\":\"special:tld-tests\"}}]", &exposed) &&
              exposed == 1, "special workspace exposed on any monitor is detected");
        CHECK(ov_parse_hypr_monitors("[{\"specialWorkspace\":{\"name\":\"special:other\"}}]", &exposed) && exposed == 0,
              "another exposed special workspace does not expose tld-tests");
        CHECK(!ov_parse_hypr_monitors("not JSON", &exposed) && exposed == -1, "failed monitor query means unknown exposure");
        CHECK(ov_parse_hypr_clients(clients, windows, &count, 1, &exposed) == 2 && count == 1,
              "Hyprland keeps the total count while bounding stored windows");
        CHECK(!ov_parse_hypr_clients("[{\"class\":\"TheLongDrive.exe\"}", windows, &count, 2, &exposed) && count == 0,
              "truncated Hyprland JSON is rejected without partial windows");
        CHECK(!ov_parse_hypr_clients("[] trailing", windows, &count, 2, &exposed) && count == 0,
              "Hyprland trailing diagnostic text cannot masquerade as an empty list");
        CHECK(!ov_parse_hypr_clients("[]", windows, &count, 2, &exposed) && count == 0,
              "empty Hyprland clients are handled");
        CHECK(ov_parse_hypr_clients("[{\"class\":\"steam_app_1017180\",\"title\":\"custom title\"}]", windows, &count, 2, &exposed) == 1,
              "TLD Steam application class works with a renamed window");
        CHECK(ov_parse_hypr_clients("[{\"class\":\"wine\",\"title\":\"The Long Drive\"}]", windows, &count, 2, &exposed) == 1,
              "TLD window title works when Wine supplies a generic class");
        const char *adb = "List of devices attached\nfixture-a\tdevice product:quest model:Quest_3 transport_id:1\n"
                          "fixture-b\tunauthorized\nfixture-c\toffline\nfixture-d\tno permissions (udev rules)\n"
                          "* daemon diagnostic\nmalformed\n";
        char summary[128];
        CHECK(ov_parse_adb_devices(adb, summary, sizeof summary) == 4 &&
              !strcmp(summary, "1 ready, 1 unauthorized, 1 offline, 1 no permissions"), "ADB output distinguishes readiness, authorization and offline state");
        CHECK(!st_has(summary, "fixture-") && !st_has(summary, "Quest_3"), "ADB summary excludes device identifiers and properties");
        CHECK(!ov_parse_adb_devices("List of devices attached\n\n", summary, sizeof summary) && st_starts(summary, "0 ready"),
              "empty ADB device list is valid");
    }
    {
        /* Exercise the actual renderer with known readings and unavailable probes. */
        OvFrame fixture = { 0 };
        ov_parse_admission_json("{\"state\":\"failed\",\"pauseKind\":\"threshold\",\"reason\":\"fixture pressure\","
                               "\"active\":false,\"name\":\"fixture-job\",\"profile\":\"test\",\"elapsedSeconds\":65,"
                               "\"exitStatus\":137,\"oomKilled\":true,\"result\":\"oom-killed\",\"limits\":{\"memoryMax\":\"12G\"}}", NULL, &fixture.admission);
        fixture.pressure.anon = 4096; fixture.pressure.file = 8192; fixture.pressure.swap = 1024;
        fixture.pressure.oom_kills = 12; fixture.new_oom_events = 3; fixture.tld_tests_exposed = 1;
        fixture.tld_window_count = 2;
        OvText rendered = { { 0 }, 0 }; Buf snapshot = { 0 };
        build_frame(&fixture, &rendered, 1000);
        for (int i = 0; i < rendered.count; i++) { buf_puts(&snapshot, rendered.lines[i]); buf_puts(&snapshot, "\n"); }
        CHECK(st_has(snapshot.data, "state: failed  pause: threshold") && st_has(snapshot.data, "fixture pressure") &&
              st_has(snapshot.data, "fixture-job  profile: test") && st_has(snapshot.data, "elapsed: 1m05s  published exit: 137  OOM killed: yes") &&
              st_has(snapshot.data, "max 12G"), "guard renderer presents job state, reason, limits and exit/OOM result");
        CHECK(st_has(snapshot.data, "watched session: anon 4.0 KiB / file 8.0 KiB / swap 1.0 KiB") &&
              st_has(snapshot.data, "external OOM events since broker baseline: 3") &&
              st_has(snapshot.data, "special:tld-tests exposed: yes  TLD windows: 2"), "guard renderer presents memory split, OOM delta and workspace exposure");
        text_free(&rendered); buf_free(&snapshot);
        ov_parse_admission_json(NULL, NULL, &fixture.admission);
        fixture.pressure.anon = fixture.pressure.file = fixture.pressure.swap = -1;
        fixture.new_oom_events = fixture.tld_tests_exposed = -1;
        build_frame(&fixture, &rendered, 1000);
        for (int i = 0; i < rendered.count; i++) buf_puts(&snapshot, rendered.lines[i]);
        CHECK(st_has(snapshot.data, "state: unavailable") && st_has(snapshot.data, "watched session: anon ? / file ? / swap ?") &&
              st_has(snapshot.data, "external OOM events since broker baseline: ?") && st_has(snapshot.data, "special:tld-tests exposed: ?") &&
              st_has(snapshot.data, "TLD / PROTON PROCESSES"), "unknown guard readings remain explicit while other panels render");
        text_free(&rendered); buf_free(&snapshot);
    }

    printf("%s (%d failure(s))\n", g_failures ? "SELF-TEST FAILED" : "self-test passed", g_failures);
    return g_failures ? 1 : 0;
}

/* ================= the idle measurement ================= */

/* The monotonic clock, in milliseconds. */
static double ov_now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec * 1000.0 + (double)t.tv_nsec / 1e6;
}

/* Run the overview for a while, doing nothing but polling, and report the CPU and
 * the resident size it used.  The measurement is of the real interactive loop: the
 * frames are built and written to a pipe instead of a terminal. */
static int run_measure(int seconds) {
    ov_cache_init(&ov_cache_global);

    long long ut = 0, st = 0;
    proc_cpu_ticks((int)getpid(), &ut, &st);
    long long c0 = ut + st;
    long long r0 = proc_rss_kb((int)getpid());
    long hz = sysconf(_SC_CLK_TCK);
    if (hz <= 0) hz = 100;

    struct timespec t0, t1;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    int polls = 0;
    double deadline = ov_now_ms() + seconds * 1000.0;
    while (ov_now_ms() < deadline && !g_interrupted) {
        /* the same 2 s poll the TUI does */
        double frame_start = ov_now_ms();
        OvFrame f;
        ov_collect(&f, &ov_cache_global);
        OvText t = { { 0 }, 0 };
        build_frame(&f, &t, 1000);
        text_free(&t);
        polls++;
        /* the poll itself takes time (the remote probe is the slow part), so the
         * wait is what is left of the 2 s */
        long long spent = (long long)(ov_now_ms() - frame_start);
        long long wait_ms = 2000 - spent;
        long long remaining_ms = (long long)(deadline - ov_now_ms());
        if (wait_ms > remaining_ms) wait_ms = remaining_ms;
        if (wait_ms < 0) wait_ms = 0;
        for (long long w = 0; w < wait_ms && !g_interrupted; w += 100) {
            long long step = wait_ms - w < 100 ? wait_ms - w : 100;
            usleep((useconds_t)(step * 1000));
        }
    }
    clock_gettime(CLOCK_MONOTONIC, &t1);

    proc_cpu_ticks((int)getpid(), &ut, &st);
    long long c1 = ut + st;
    long long r1 = proc_rss_kb((int)getpid());
    double wall = (double)(t1.tv_sec - t0.tv_sec) + (double)(t1.tv_nsec - t0.tv_nsec) / 1e9;
    double cpu = (double)(c1 - c0) / (double)hz;

    printf("overview idle measurement\n");
    printf("  duration      %.1f s (%d polls of the 2 s frame)\n", wall, polls);
    printf("  cpu           %.3f s  (%.2f%% of one core)\n", cpu, wall > 0 ? 100.0 * cpu / wall : 0.0);
    printf("  resident      %.1f MiB -> %.1f MiB (%+.2f MiB)\n",
           r0 / 1024.0, r1 / 1024.0, (r1 - r0) / 1024.0);
    ov_cache_free(&ov_cache_global);
    return 0;
}

/* ================= main ================= */

static void usage(FILE *out) {
    fprintf(out,
        "usage: overview [options]\n"
        "\n"
        "  --once            print one text snapshot and exit (no alternate screen)\n"
        "  --no-remote       do not probe the second machine over SSH\n"
        "  --interval N      poll interval in seconds (default 2)\n"
        "  --workers DIR     the worker root (default /tmp/tld-free-workers)\n"
        "  --repo DIR        the repository to report the git status of (default .)\n"
        "  --bridge-port N   the local debug bridge port (default 27050)\n"
        "  --self-test       run the built-in self-test and exit\n"
        "  --measure         measure the idle cpu and rss over 30 s and print it\n"
        "  --measure-seconds N   how long --measure runs (default 30)\n"
        "  --help            this text\n"
        "\n"
        "The overview only reads: /proc, the worker logs, the result files and the\n"
        "read-only debug bridge.  It never starts, stops or configures a game.\n");
}

int main(int argc, char **argv) {
    int once = 0, no_remote = 0, self_test = 0, measure = 0;
    int interval = 2, measure_seconds = 30;

    ov_config_defaults(&ov_config);
    ov_config_from_env(&ov_config);

    for (int i = 1; i < argc; i++) {
        const char *a = argv[i];
        if (!strcmp(a, "--once")) once = 1;
        else if (!strcmp(a, "--no-remote")) no_remote = 1;
        else if (!strcmp(a, "--self-test")) self_test = 1;
        else if (!strcmp(a, "--measure")) measure = 1;
        else if (!strcmp(a, "--measure-seconds") && i + 1 < argc) { measure = 1; measure_seconds = atoi(argv[++i]); }
        else if (!strcmp(a, "--help") || !strcmp(a, "-h")) { usage(stdout); return 0; }
        else if (!strcmp(a, "--interval") && i + 1 < argc) interval = atoi(argv[++i]);
        else if (!strcmp(a, "--workers") && i + 1 < argc) {
            st_copy(ov_config.workers_dir, sizeof ov_config.workers_dir, argv[++i]);
            snprintf(ov_config.logs_dir, sizeof ov_config.logs_dir, "%.*s/logs", (int)(sizeof ov_config.logs_dir) - 8, ov_config.workers_dir);
            snprintf(ov_config.results_dir, sizeof ov_config.results_dir, "%.*s/results", (int)(sizeof ov_config.results_dir) - 10, ov_config.workers_dir);
        }
        else if (!strcmp(a, "--repo") && i + 1 < argc) st_copy(ov_config.repo_dir, sizeof ov_config.repo_dir, argv[++i]);
        else if (!strcmp(a, "--bridge-port") && i + 1 < argc) ov_config.bridge_port = atoi(argv[++i]);
        else {
            fprintf(stderr, "overview: unknown option '%s'\n\n", a);
            usage(stderr);
            return 2;
        }
    }
    if (no_remote) ov_config.remote_enabled = 0;
    if (interval < 1) interval = 1;

    if (self_test) return run_self_test();
    if (measure) return run_measure(measure_seconds);

    signal(SIGINT, on_signal);
    signal(SIGTERM, on_signal);
    signal(SIGPIPE, SIG_IGN);

    ov_cache_init(&ov_cache_global);

    if (once) {
        OvFrame f;
        ov_collect(&f, &ov_cache_global);
        print_once(&f);
        ov_cache_free(&ov_cache_global);
        return 0;
    }

    g_stdout_is_tty = isatty(STDOUT_FILENO) ? 1 : 0;
    if (!g_stdout_is_tty) {
        fprintf(stderr, "overview: not a terminal; use --once for a text snapshot\n");
        return 2;
    }

    raw_mode_on();
    term_enter();

    OvText prev = { { 0 }, 0 };
    OvText cur = { { 0 }, 0 };
    int first = 1;
    int scroll = 0;              /* the first visible line of the frame */
    int last_scroll = 0;         /* the scroll the previous frame was drawn with */

    while (!g_interrupted) {
        OvFrame f;
        ov_collect(&f, &ov_cache_global);
        text_free(&cur);
        build_frame(&f, &cur, 1000);

        int rows = 24, cols = 80;
        term_size(&rows, &cols);
        if (rows < 8) rows = 8;
        int body = rows - 1;      /* the last row carries the key help */
        if (body < 4) body = 4;

        /* keep the scroll inside the frame */
        int max_scroll = cur.count - body;
        if (max_scroll < 0) max_scroll = 0;
        if (scroll > max_scroll) scroll = max_scroll;
        if (scroll < 0) scroll = 0;

        /* Only the lines that changed are written again; the rest of the screen
         * is left exactly as it was. */
        LineDiff d;
        linediff((const char *const *)prev.lines, prev.count,
                 (const char *const *)cur.lines, cur.count, &d);

        /* The lines that differ from the previous frame, as a per-line flag.  When
         * the view scrolled, every visible line moved: all of them are rewritten. */
        int changed_line[MAX_LINES];
        int scrolled = (scroll != last_scroll);
        for (int i = 0; i < cur.count; i++) changed_line[i] = first || scrolled;
        for (int j = 0; j < d.changed_count; j++) {
            int i = d.changed[j];
            if (i >= 0 && i < cur.count) changed_line[i] = 1;
        }

        int at = 1;
        for (int i = scroll; i < cur.count && at <= body; i++, at++) {
            if (!changed_line[i]) continue;    /* the screen keeps this line as it is */
            printf("\033[%d;1H\033[K%s", at, cur.lines[i]);
        }
        /* the leftover lines of a shorter frame */
        for (int i = at; i <= body; i++) printf("\033[%d;1H\033[K", i);
        /* the key help on the last row */
        printf("\033[%d;1H\033[Kq quit  up/down scroll  (line %d-%d of %d)",
               rows, scroll + 1, (scroll + body < cur.count) ? scroll + body : cur.count, cur.count);
        fflush(stdout);
        linediff_free(&d);
        last_scroll = scroll;

        text_free(&prev);
        prev = cur;
        memset(&cur, 0, sizeof cur);
        first = 0;

        /* the poll interval, and a look at the keyboard in between */
        for (int slept = 0; slept < interval * 10 && !g_interrupted; slept++) {
            usleep(100000);
            char c;
            ssize_t n = read(STDIN_FILENO, &c, 1);
            if (n != 1) continue;
            if (c == 'q' || c == 'Q' || c == 3) { g_interrupted = 1; break; }
            if (c == 'j' || c == '\n') scroll++;
            else if (c == 'k') scroll--;
            else if (c == ' ') scroll += body;
            else if (c == 'g') scroll = 0;
            else if (c == 'G') scroll = max_scroll;
            if (scroll < 0) scroll = 0;
            if (scroll > max_scroll) scroll = max_scroll;
            /* the view changed: redraw now, not at the next poll */
            first = 1;
            break;
        }
    }

    term_leave();
    raw_mode_off();
    text_free(&prev);
    text_free(&cur);
    ov_cache_free(&ov_cache_global);
    return 0;
}
