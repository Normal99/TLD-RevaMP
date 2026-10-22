/* One admitted systemd user cgroup, with persistent pressure/manual policy.
 * The controller and a separate waiter hold the same kernel flock. The waiter
 * survives controller death and owns launch/finalization; systemd is the second
 * barrier if either wrapper disappears. Lock files are never unlinked. */
#define _GNU_SOURCE
#include "tld_admit_core.h"
#include "overview_core.h"
#include "native_process.h"
#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* The watched cgroup is derived from the running user, not pinned to one UID: the guard runs on every
 * machine, and the second test machine is a different account. A pinned path would watch the wrong cgroup
 * there (or none), and the guard would report the host as healthy while it was not. */
#define WATCHED_FMT "/sys/fs/cgroup/user.slice/user-%lu.slice/user@%lu.service/session.slice/wayland-wm@hyprland.desktop.service"
#define STATE_MAX 32768
#define COOLDOWN 60
static char runtime_dir[PATH_MAX], admission_lock[PATH_MAX];
static volatile sig_atomic_t stopping;
static int watching;
static int ensure_watch(void);

typedef struct {
    char unit[192], name[80], cgroup[PATH_MAX], limits[2048], result[64];
    AdmitProfile profile;
    long long started, elapsed, exit_status;
    int active, oom;
} Job;
typedef struct { int paused; const char *kind; char reason[ADMIT_TEXT_MAX]; } Policy;

static long long epoch(void) { return (long long)time(NULL); }
static long long steady(void) { return native_monotonic_ms() / 1000; }
static void usage(FILE *f) {
    fputs("usage:\n  tld-admit run --profile agent|test|game|vr --name NAME -- COMMAND...\n"
          "  tld-admit status [--json]\n  tld-admit pause [reason]\n  tld-admit resume\n  tld-admit limits\n", f);
}
static int join(char *out, size_t n, const char *dir, const char *leaf) {
    int len = snprintf(out, n, "%s/%s", dir, leaf);
    return len >= 0 && (size_t)len < n;
}
static int read_text(const char *path, char *out, size_t n) {
    out[0] = 0;
    int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) return 0;
    size_t got = 0;
    while (got + 1 < n) {
        ssize_t r = read(fd, out + got, n - 1 - got);
        if (!r) break;
        if (r < 0) { if (errno == EINTR) continue; close(fd); out[0] = 0; return 0; }
        got += (size_t)r;
    }
    char extra;
    ssize_t more = read(fd, &extra, 1);
    close(fd); out[got] = 0;
    return more == 0 && !memchr(out, 0, got);
}
/* A run reads its OWN directory: policy, pause markers and published state are per-run.  The default
 * runtime directory is the machine-wide one; a private directory is fully isolated. */
static int read_state(const char *leaf, char *out, size_t n) {
    char path[PATH_MAX];
    return join(path, sizeof path, runtime_dir, leaf) && read_text(path, out, n);
}
static int exists(const char *leaf) {
    char path[PATH_MAX]; struct stat st;
    return join(path, sizeof path, runtime_dir, leaf) && lstat(path, &st) == 0;
}
static int remove_state(const char *leaf) {
    char path[PATH_MAX];
    return join(path, sizeof path, runtime_dir, leaf) && (!unlink(path) || errno == ENOENT);
}
static int secure_dir(const char *path) {
    struct stat st;
    if (mkdir(path, 0700) && errno != EEXIST) return 0;
    return !lstat(path, &st) && S_ISDIR(st.st_mode) && st.st_uid == getuid() && !(st.st_mode & 0077);
}
static int init_paths(const char *dir) {
    char common[PATH_MAX];
    const char *xdg = getenv("XDG_RUNTIME_DIR");
    const char *test_default = getenv("TLD_ADMIT_TEST_DEFAULT_DIR");
    /* The global lock is ALWAYS the real per-user admission lock. A fixture may exercise the
     * default state-directory branch with a private path, but it may never acquire a different
     * lock and thereby bypass a concurrently running controller. */
    if (xdg && *xdg) snprintf(common, sizeof common, "%s/tld-admission", xdg);
    else snprintf(common, sizeof common, "/run/user/%ld/tld-admission", (long)getuid());
    if (!secure_dir(common) || !join(admission_lock, sizeof admission_lock, common, "admission.lock")) return 0;
    if (!dir || !*dir)
        dir = test_default && *test_default && getenv("TLD_ADMIT_MEMINFO") &&
              getenv("TLD_ADMIT_WATCHED_CGROUP") ? test_default : common;
    if (dir[0] != '/' || strlen(dir) >= sizeof runtime_dir - 64 || !secure_dir(dir)) return 0;
    strcpy(runtime_dir, dir);
    return 1;
}
static int open_lock(const char *path, int nonblock) {
    int fd = open(path, O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (fd < 0) return -1;
    struct stat st;
    if (fstat(fd, &st) || !S_ISREG(st.st_mode) || st.st_uid != getuid() || st.st_nlink != 1 || (st.st_mode & 0077)) {
        close(fd); return -1;
    }
    long long deadline = native_monotonic_ms() + (nonblock ? 0 : 3000);
    do {
        if (!flock(fd, LOCK_EX | LOCK_NB)) return fd;
        if (errno != EWOULDBLOCK && errno != EINTR) break;
        if (nonblock) break;
        usleep(10000);
    } while (native_monotonic_ms() < deadline);
    close(fd); return -1;
}
static int policy_lock(void) {
    char path[PATH_MAX];
    return join(path, sizeof path, runtime_dir, "policy.lock") ? open_lock(path, 0) : -1;
}
/* Write one file atomically into a directory: a complete rename, never a partial file. */
static int write_file_atomic(const char *dir, const char *leaf, const char *text) {
    char path[PATH_MAX], tmp[PATH_MAX];
    if (!join(path, sizeof path, dir, leaf) || !join(tmp, sizeof tmp, dir, ".publish-XXXXXX")) return 0;
    int fd = mkostemp(tmp, O_CLOEXEC);
    if (fd < 0) return 0;
    size_t left = strlen(text); const char *p = text;
    int ok = 1;
    while (left) {
        ssize_t n = write(fd, p, left);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) { ok = 0; break; }
        p += n; left -= (size_t)n;
    }
    if (fsync(fd)) ok = 0;
    if (close(fd)) ok = 0;
    if (ok && rename(tmp, path)) ok = 0;
    if (!ok) unlink(tmp);
    int dfd = open(dir, O_DIRECTORY | O_RDONLY | O_CLOEXEC);
    if (dfd < 0) return 0;
    if (fsync(dfd)) ok = 0;
    close(dfd); return ok;
}
/* Every state leaf belongs to this run's runtime directory.  The default runtime directory is the
 * machine-wide one the overview reads; a private test/runtime directory stays private. */
static int write_state(const char *leaf, const char *text) {
    return write_file_atomic(runtime_dir, leaf, text);
}
static int read_ll(const char *leaf, long long *value) {
    char b[128], *end;
    if (!read_state(leaf, b, sizeof b) || !isdigit((unsigned char)b[0])) return 0;
    errno = 0; long long v = strtoll(b, &end, 10);
    while (isspace((unsigned char)*end)) end++;
    if (errno || *end || v < 0) return 0;
    *value = v; return 1;
}
static int write_ll(const char *leaf, long long value) {
    char b[64]; snprintf(b, sizeof b, "%lld\n", value); return write_state(leaf, b);
}
static const char *watched(void) {
    const char *v = getenv("TLD_ADMIT_WATCHED_CGROUP");
    if (v && *v) return v;
    static char path[PATH_MAX];
    if (!path[0]) snprintf(path, sizeof path, WATCHED_FMT, (unsigned long)getuid(), (unsigned long)getuid());
    return path;
}
static void read_gauge(AdmitReading *r) {
    *r = (AdmitReading){.mem_total=-1, .mem_available=-1, .swap_total=-1, .swap_used=-1,
        .session_anon=-1, .session_swap=-1, .oom_kills=-1, .session_file=-1, .session_shmem=-1,
        .global_oom_kills=-1, .psi_some=-1, .psi_full=-1, .session_psi_some=-1, .session_psi_full=-1};
    char b[16384], path[PATH_MAX];
    const char *mi = getenv("TLD_ADMIT_MEMINFO");
    long long free_swap = -1;
    if (read_text(mi && *mi ? mi : "/proc/meminfo", b, sizeof b)) {
        if (admit_parse_meminfo(b, &r->mem_total, &r->mem_available, &r->swap_total, &free_swap))
            r->swap_used = r->swap_total - free_swap;
    }
    if (join(path, sizeof path, watched(), "memory.stat") && read_text(path, b, sizeof b))
        if (!admit_parse_memory_stat(b, &r->session_anon, &r->session_file, &r->session_shmem))
            r->session_anon = r->session_file = r->session_shmem = -1;
    /* memory.stat swap is never a substitute for the v2 swap gauge. */
    if (join(path, sizeof path, watched(), "memory.swap.current") && read_text(path, b, sizeof b)) {
        admit_parse_scalar(b, &r->session_swap);
    }
    if (join(path, sizeof path, watched(), "memory.events") && read_text(path, b, sizeof b))
        admit_parse_events_oom_kill(b, &r->oom_kills);
    const char *psi = getenv("TLD_ADMIT_PSI"), *vm = getenv("TLD_ADMIT_VMSTAT");
    if (read_text(psi && *psi ? psi : "/proc/pressure/memory", b, sizeof b))
        admit_parse_psi(b, &r->psi_some, &r->psi_full);
    if (join(path, sizeof path, watched(), "memory.pressure") && read_text(path, b, sizeof b))
        admit_parse_psi(b, &r->session_psi_some, &r->session_psi_full);
    if (read_text(vm && *vm ? vm : "/proc/vmstat", b, sizeof b))
        admit_parse_counter(b, "oom_kill", "", &r->global_oom_kills);

}
static void safe_label(char *out, size_t n, const char *in) {
    st_copy(out, n, in);
    for (char *p = out; *p; p++) {
        if ((unsigned char)*p < 32 || *p == 127) *p = ' ';
        if (*p == '=' || !strncasecmp(p, "bearer ", 7)) { st_copy(p, n - (size_t)(p - out), "[redacted]"); break; }
        if (*p == ':') {
            char *key = p;
            while (key > out && isspace((unsigned char)key[-1])) key--;
            char *end = key;
            while (key > out && (isalnum((unsigned char)key[-1]) || strchr("_.-", key[-1]))) key--;
            if (looks_secret_key(key, (size_t)(end - key))) { st_copy(p + 1, n - (size_t)(p + 1 - out), "[redacted]"); break; }
        }
    }
}
/* All callers hold policy.lock. Record each OOM count once, including during a
 * manual hold. Another crossing resets the healthy timer; an unchanged counter
 * cannot reset it. Uncertain readings also break the healthy interval. */
static Policy evaluate(const AdmitReading *r) {
    Policy p = {0, "none", "admission healthy"};
    AdmitDecision d = admit_evaluate(r);
    int ok = 1, crossing = 0;
    if (exists("oom-pending")) {
        ok &= write_state("auto-pause", "watched cgroup OOM pause recovered");
        ok &= remove_state("oom-pending");
        crossing = 1;
    }
    const char *last_keys[] = {"oom-last-seen", "global-oom-last-seen"};
    const char *base_keys[] = {"oom-baseline", "global-oom-baseline"};
    long long counters[] = {r->oom_kills, r->global_oom_kills};
    for (int i = 0; i < 2; i++) {
        long long baseline = -1;
        if (counters[i] < 0) continue;
        const char *key = exists(last_keys[i]) ? last_keys[i] : base_keys[i];
        if (read_ll(key, &baseline)) {
            if (baseline != counters[i]) {
                crossing = 1;
                ok &= write_state("auto-pause", baseline < counters[i] ?
                    (i ? "global OOM-kill counter increased" : "watched cgroup OOM-kill counter increased") : "OOM counter reset");
            }
        } else if (exists(key)) ok = 0;
        else ok &= write_ll(base_keys[i], counters[i]);
        ok &= write_ll(last_keys[i], counters[i]);
    }
    long long now = steady();
    if (d.psi_pending) {
        long long since, last;
        if ((exists("psi-since") && !read_ll("psi-since", &since)) ||
            (exists("psi-last") && !read_ll("psi-last", &last))) ok = 0;
        if (!read_ll("psi-since", &since) || !read_ll("psi-last", &last) ||
            since > now || last > now || now - last > 3) {
            since = now;
            ok &= write_ll("psi-since", now);
        }
        ok &= write_ll("psi-last", now);
        if (now - since >= 5) {
            crossing = 1;
            ok &= write_state("auto-pause", "memory PSI sustained for 5 seconds");
        }
    } else {
        ok &= remove_state("psi-since"); ok &= remove_state("psi-last");
    }
    if (d.threshold_paused) { crossing = 1; ok &= write_state("auto-pause", d.reason); }
    if (!d.all_reading_ok) ok &= write_state("auto-pause", "memory readings were unavailable");
    if (crossing || !d.all_reading_ok || !d.recovery_ready) {
        ok &= remove_state("healthy-since");
        ok &= remove_state("healthy-last");
    }
    if (!d.all_reading_ok || !ok) {
        p.paused = 1; p.kind = "unavailable";
        st_copy(p.reason, sizeof p.reason, ok ? d.reason : "policy state unavailable");
    } else if (crossing) {
        p.paused = 1; p.kind = "threshold";
        if (!read_state("auto-pause", p.reason, sizeof p.reason)) st_copy(p.reason, sizeof p.reason, "pressure detected");
    } else if (d.psi_pending || (exists("auto-pause") && !d.recovery_ready)) {
        p.paused = 1; p.kind = "threshold";
        st_copy(p.reason, sizeof p.reason, d.psi_pending ? d.reason : "waiting for recovery hysteresis (8 GiB available, swap and PSI reserves)");
    } else if (exists("auto-pause")) {
        long long since, last;
        p.paused = 1; p.kind = "threshold";
        if (!read_ll("healthy-since", &since) || since > steady() ||
            !read_ll("healthy-last", &last) || last > steady() || steady() - last > 3) {
            if (!write_ll("healthy-since", steady())) p.kind = "unavailable";
            st_copy(p.reason, sizeof p.reason, "pressure cleared; waiting for 60 seconds of healthy readings");
        } else if (steady() - since < COOLDOWN) {
            snprintf(p.reason, sizeof p.reason, "pressure cleared; %lld seconds of healthy readings remain", COOLDOWN - (steady() - since));
        } else if (!write_ll("oom-baseline", r->oom_kills) || !write_ll("global-oom-baseline", r->global_oom_kills) || !remove_state("auto-pause") || !remove_state("healthy-since") || !remove_state("healthy-last")) {
            p.kind = "unavailable"; st_copy(p.reason, sizeof p.reason, "policy state unavailable");
        } else p = (Policy){0, "none", "admission healthy"};
    }
    if (exists("auto-pause") && d.recovery_ready && !crossing && !write_ll("healthy-last", steady())) {
        p.paused = 1; p.kind = "unavailable"; st_copy(p.reason, sizeof p.reason, "policy state unavailable");
    }
    if (exists("manual-pause")) {
        char reason[ADMIT_TEXT_MAX];
        p.paused = 1; p.kind = "manual";
        if (!read_state("manual-pause", reason, sizeof reason) || !reason[0]) strcpy(reason, "paused by operator");
        safe_label(p.reason, sizeof p.reason, reason);
    }
    return p;
}
static int valid_unit(const char *unit) {
    if (!st_starts(unit, "tld-job-") || !st_ends(unit, ".service") || strlen(unit) >= 192) return 0;
    for (const unsigned char *p = (const unsigned char *)unit; *p; p++)
        if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || isdigit(*p) || strchr("_.-", *p))) return 0;
    return 1;
}
/* Include teardown: a deactivating unit still owns a process tree. */
static int find_active(char *unit, size_t n) {
    const char *args[] = {"/usr/bin/systemctl", "--user", "list-units", "--all", "--plain", "--no-legend", "--no-pager", "tld-job-*", NULL};
    char out[32768]; unit[0] = 0;
    if (native_capture(args, out, sizeof out, 3000)) return -1;
    int found = 0;
    for (char *line = out; line && *line;) {
        char u[192], load[64], active[64], sub[64];
        char *end = strchr(line, '\n'); if (end) *end = 0;
        if (sscanf(line, "%191s %63s %63s %63s", u, load, active, sub) != 4 || !valid_unit(u)) return -1;
        if (strcmp(active, "inactive") && strcmp(active, "failed")) { found++; st_copy(unit, n, u); }
        line = end ? end + 1 : NULL;
    }
    return found;
}
static int field(const char *text, const char *key, char *out, size_t n) {
    size_t kl = strlen(key); out[0] = 0;
    for (const char *p = text; p && *p;) {
        const char *end = strchr(p, '\n'); size_t len = end ? (size_t)(end - p) : strlen(p);
        if (len > kl && !memcmp(p, key, kl) && p[kl] == '=') {
            size_t take = len - kl - 1; if (take >= n) return 0;
            memcpy(out, p + kl + 1, take); out[take] = 0; return 1;
        }
        p = end ? end + 1 : NULL;
    }
    return 0;
}
static void job_init(Job *j) {
    memset(j, 0, sizeof *j); j->exit_status = j->elapsed = -1; j->active = j->oom = -1;
    strcpy(j->limits, "{}");
}
static void load_job(Job *j) {
    job_init(j); char b[STATE_MAX], profile[32], state[32];
    if (!read_state("state.json", b, sizeof b) || !admit_json_str(b, "state", state, sizeof state)) return;
    admit_json_str(b, "unit", j->unit, sizeof j->unit);
    admit_json_str(b, "name", j->name, sizeof j->name);
    admit_json_str(b, "cgroup", j->cgroup, sizeof j->cgroup);
    admit_json_str(b, "profile", profile, sizeof profile); j->profile = admit_profile_parse(profile);
    admit_json_str(b, "result", j->result, sizeof j->result);
    admit_json_ll(b, "startedEpoch", &j->started); admit_json_ll(b, "elapsedSeconds", &j->elapsed);
    admit_json_ll(b, "exitStatus", &j->exit_status); json_bool(&j->oom, b, "oomKilled");
    safe_label(j->name,sizeof j->name,j->name);
    safe_label(j->result,sizeof j->result,j->result);
    safe_label(j->cgroup,sizeof j->cgroup,j->cgroup);
    if (!valid_unit(j->unit)) j->unit[0] = 0;
    Buf lim = {0}, clean = {0};
    if (json_obj(&lim,b,"limits")) {
        static const char *keys[] = {"memoryHigh","memoryMax","memorySwapMax","tasksMax","cpuQuota","cpuQuotaSystemd","cpuWeight","ioWeight"};
        buf_puts(&clean,"{");
        int fields = 0;
        for (size_t i = 0; i < sizeof keys / sizeof keys[0]; i++) {
            char value[64], escaped[384];
            if (!admit_json_str(lim.data,keys[i],value,sizeof value)) continue;
            safe_label(value,sizeof value,value); admit_json_escape(value,escaped,sizeof escaped);
            buf_printf(&clean,"%s\"%s\":\"%s\"",fields++ ? "," : "",keys[i],escaped);
        }
        long long nice;
        if (admit_json_ll(lim.data,"nice",&nice) && nice >= 0 && nice <= 19)
            buf_printf(&clean,"%s\"nice\":%lld",fields++ ? "," : "",nice);
        int v;
        if (json_bool(&v,lim.data,"memoryOOMGroup")) buf_printf(&clean,"%s\"memoryOOMGroup\":%s",fields++ ? "," : "",v ? "true" : "false");
        if (json_bool(&v,lim.data,"verified")) buf_printf(&clean,"%s\"verified\":%s",fields++ ? "," : "",v ? "true" : "false");
        buf_puts(&clean,"}");
        if (clean.len < sizeof j->limits) strcpy(j->limits,clean.data);
    }
    buf_free(&clean); buf_free(&lim);
}
static int publish(const Job *j, const AdmitReading *r, const Policy *p, const char *state, Buf *json, Buf *text) {
    char reason[ADMIT_TEXT_MAX * 6], unit[384], name[160], cg[PATH_MAX * 2], result[128];
    admit_json_escape(p->reason, reason, sizeof reason); admit_json_escape(j->unit, unit, sizeof unit);
    admit_json_escape(j->name, name, sizeof name); admit_json_escape(j->cgroup, cg, sizeof cg);
    admit_json_escape(j->result, result, sizeof result);
    long long elapsed = j->active == 1 && j->started > 0 ? epoch() - j->started : j->elapsed;
    buf_reset(json); buf_reset(text);
    buf_printf(json, "{\"version\":2,\"updatedEpoch\":%lld,\"state\":\"%s\",\"pauseKind\":\"%s\",\"reason\":\"%s\","
        "\"active\":%s,\"unit\":\"%s\",\"profile\":\"%s\",\"name\":\"%s\",\"cgroup\":\"%s\","
        "\"startedEpoch\":%lld,\"elapsedSeconds\":%lld,\"exitStatus\":%lld,\"oomKilled\":%s,\"result\":\"%s\","
        "\"memAvailable\":%lld,\"swapUsed\":%lld,\"sessionAnon\":%lld,\"sessionSwap\":%lld,\"watchedOOMKills\":%lld,\"sessionFile\":%lld,\"sessionShmem\":%lld,\"swapFree\":%lld,\"globalOOMKills\":%lld,\"psiSomeAvg10Bps\":%lld,\"psiFullAvg10Bps\":%lld,\"sessionPsiSomeAvg10Bps\":%lld,\"sessionPsiFullAvg10Bps\":%lld,\"limits\":%s}\n",
        epoch(), state, p->kind, reason, j->active < 0 ? "null" : j->active ? "true" : "false", unit,
        j->profile ? admit_profile_name(j->profile) : "", name, cg, j->started, elapsed, j->exit_status,
        j->oom < 0 ? "null" : j->oom ? "true" : "false", result, r->mem_available, r->swap_used,
        r->session_anon, r->session_swap, r->oom_kills, r->session_file, r->session_shmem,
        r->swap_used < 0 ? -1 : r->swap_total - r->swap_used, r->global_oom_kills,
        r->psi_some, r->psi_full, r->session_psi_some, r->session_psi_full, j->limits);
    buf_printf(text, "state: %s\npause: %s\nreason: %s\nactive: %s\nname: %s\nprofile: %s\nunit: %s\ncgroup: %s\n"
        "started: %lld\nelapsed: %llds\nexit: %lld\nOOM: %s\nresult: %s\nMemAvailable: %lld bytes\nglobal swap: %lld bytes\n"
        "session anon: %lld bytes\nsession swap: %lld bytes\nwatched OOM kills: %lld\n",
        state, p->kind, p->reason, j->active < 0 ? "unknown" : j->active ? "yes" : "no", j->name,
        j->profile ? admit_profile_name(j->profile) : "", j->unit, j->cgroup, j->started, elapsed, j->exit_status,
        j->oom < 0 ? "unknown" : j->oom ? "yes" : "no", j->result, r->mem_available, r->swap_used, r->session_anon, r->session_swap, r->oom_kills);
    buf_printf(text,"updated: %lld\nlimits: %s\n",epoch(),j->limits);
    /* Each file is a complete atomic rename. JSON is authoritative when present. */
    int ok = write_state("status.txt", text->data) && write_state("state.json", json->data);
    if (!ok) fputs("tld-admit: state publication unavailable\n", stderr);
    return ok;
}
static const char *policy_state(const Policy *p, int active, const char *idle) {
    if (active < 0) return "unavailable";
    if (active) return p->paused ? "pressure-paused" : "running";
    return p->paused ? (!strcmp(p->kind, "unavailable") ? "unavailable" : "paused") : idle;
}
static int sample(Job *j, const char *idle) {
    int fd = policy_lock(); if (fd < 0) return 0;
    AdmitReading r; read_gauge(&r); Policy p = evaluate(&r);
    Buf json = {0}, text = {0};
    int starting = !strcmp(j->result,"starting");
    int launch_ok = !starting || (!p.paused && admit_launch_ready(&r,j->profile));
    if (!launch_ok && !p.paused) {
        p.paused = 1; p.kind = "capacity";
        st_copy(p.reason,sizeof p.reason,"profile headroom changed before child execution");
    }
    int ok = publish(j, &r, &p, policy_state(&p, j->active, idle), &json, &text) && launch_ok;
    buf_free(&json); buf_free(&text); close(fd);
    if (!watching && (exists("auto-pause") || exists("psi-since"))) ensure_watch();
    return ok;
}
static int status_cmd(int as_json, int resume) {
    int fd = policy_lock(); if (fd < 0) return 75;
    if (resume && !remove_state("manual-pause")) { close(fd); return 75; }
    Job j; load_job(&j);
    AdmitReading r; read_gauge(&r); Policy p = evaluate(&r);
    char unit[192]; int count = find_active(unit, sizeof unit);
    if (count > 0 && strcmp(unit, j.unit)) { job_init(&j); st_copy(j.unit, sizeof j.unit, unit); }
    j.active = count < 0 ? -1 : count > 0;
    if (count < 0 || count > 1) {
        p.paused = 1; p.kind = "unavailable";
        st_copy(p.reason, sizeof p.reason, count < 0 ? "systemd user manager unavailable; active job unknown" : "multiple active tld-job units; admission unavailable");
    }
    Buf json = {0}, text = {0};
    int ok = publish(&j, &r, &p, policy_state(&p, j.active, "healthy"), &json, &text);
    fputs(as_json ? json.data : text.data, stdout);
    buf_free(&json); buf_free(&text); close(fd);
    if (!watching && (exists("auto-pause") || exists("psi-since"))) ensure_watch();
    return !ok || count < 0 || count > 1 || (resume && p.paused) ? 75 : 0;
}
static int pause_cmd(const char *reason) {
    int fd = policy_lock(); if (fd < 0) return 75;
    char safe[ADMIT_TEXT_MAX]; safe_label(safe, sizeof safe, reason && *reason ? reason : "paused by operator");
    int ok = write_state("manual-pause", safe); close(fd);
    return ok ? status_cmd(0, 0) : 75;
}
static long long cg_counter(const char *cg, const char *leaf) {
    char path[PATH_MAX], b[4096]; long long value = -1;
    if (join(path, sizeof path, cg, leaf) && read_text(path, b, sizeof b)) {
        if (!strcmp(leaf, "memory.events")) admit_parse_events_oom_kill(b, &value);
        else admit_parse_scalar(b, &value);
    }
    return value;
}
static int own_cgroup(char *cg, size_t n, const char *unit) {
    char text[PATH_MAX];
    if (!read_text("/proc/self/cgroup", text, sizeof text)) return 0;
    /* The unified (v2) line, wherever it is: a legacy v1 hierarchy mounted beside it (Mullvad's net_cls for split
     * tunneling) lists its own line first, "1:net_cls:/". */
    char *raw = !strncmp(text, "0::/", 4) ? text : strstr(text, "\n0::/");
    if (!raw) return 0;
    if (raw != text) raw++;
    raw[strcspn(raw, "\r\n")] = 0;
    char *leaf = strrchr(raw, '/');
    if (!leaf || strcmp(leaf + 1, unit)) return 0;
    int len = snprintf(cg, n, "/sys/fs/cgroup%s", raw + 3);
    return len > 0 && (size_t)len < n;
}
static int read_limits(Job *j) {
    const char *args[] = {"/usr/bin/systemctl", "--user", "show", j->unit, "-p", "MemoryHigh", "-p", "MemoryMax", "-p", "MemorySwapMax", "-p", "TasksMax", "-p", "CPUQuotaPerSecUSec", "-p", "CPUWeight", "-p", "IOWeight", "-p", "Nice", "-p", "OOMPolicy", NULL};
    char out[4096], high[64], max[64], swap[64], tasks[64], quota[64], cpu[64], io[64], nice[64], oom[64];
    if (native_capture(args, out, sizeof out, 3000)) return 0;
    if (!field(out,"MemoryHigh",high,sizeof high) || !field(out,"MemoryMax",max,sizeof max) ||
        !field(out,"MemorySwapMax",swap,sizeof swap) || !field(out,"TasksMax",tasks,sizeof tasks) ||
        !field(out,"CPUQuotaPerSecUSec",quota,sizeof quota) || !field(out,"CPUWeight",cpu,sizeof cpu) ||
        !field(out,"IOWeight",io,sizeof io) || !field(out,"Nice",nice,sizeof nice) || !field(out,"OOMPolicy",oom,sizeof oom)) return 0;
    const AdmitProfileSpec *s = admit_profile(j->profile);
    char expected_high[64], expected_max[64], expected_swap[64], expected_nice[32];
    snprintf(expected_high,sizeof expected_high,"%lld",s->high_bytes);
    snprintf(expected_max,sizeof expected_max,"%lld",s->max_bytes);
    snprintf(expected_swap,sizeof expected_swap,"%lld",s->swap_bytes);
    snprintf(expected_nice,sizeof expected_nice,"%u",s->nice);
    long long group = cg_counter(j->cgroup, "memory.oom.group");
    int verified = !strcmp(high,expected_high) && !strcmp(max,expected_max) &&
        !strcmp(swap,expected_swap) && !strcmp(tasks,s->tasks_max) &&
        !strcmp(quota,s->quota_systemd) && !strcmp(cpu,s->cpu_weight) && !strcmp(io,s->io_weight) &&
        !strcmp(nice,expected_nice) && !strcmp(oom,"kill") && group == 1 &&
        cg_counter(j->cgroup,"memory.max") == s->max_bytes &&
        cg_counter(j->cgroup,"memory.swap.max") == s->swap_bytes &&
        cg_counter(j->cgroup,"pids.max") == strtoll(s->tasks_max,NULL,10) &&
        cg_counter(j->cgroup,"memory.high") == s->high_bytes;

    snprintf(j->limits, sizeof j->limits,
        "{\"memoryHigh\":\"%s\",\"memoryMax\":\"%s\",\"memorySwapMax\":\"%s\",\"tasksMax\":\"%s\",\"cpuQuota\":\"%s\","
        "\"cpuQuotaSystemd\":\"%s\",\"cpuWeight\":\"%s\",\"ioWeight\":\"%s\",\"nice\":%u,\"memoryOOMGroup\":%s,\"verified\":%s}",
        high,max,swap,tasks,s->cpu_quota,quota,cpu,io,s->nice,group == 1 ? "true" : "false", verified ? "true" : "false");
    return verified;
}
static void stop_signal(int sig) { stopping = sig; }
static int supervisor(int argc, char **argv) {
    /* Fixed positional arguments from run_cmd, validated before any side effect. */
    if (argc < 10 || strcmp(argv[8], "--") || !valid_unit(argv[3])) return 64;
    Job j; job_init(&j); j.profile = admit_profile_parse(argv[4]);
    char err[ADMIT_TEXT_MAX];
    if (!j.profile || !admit_validate_name(argv[5],err,sizeof err)) return 64;
    char *end; errno = 0; j.started = strtoll(argv[6],&end,10);
    if (errno || *end || j.started <= 0 || strcmp(argv[7], "v2") || !init_paths(argv[2])) return 64;
    st_copy(j.unit,sizeof j.unit,argv[3]); st_copy(j.name,sizeof j.name,argv[5]);
    if (!own_cgroup(j.cgroup,sizeof j.cgroup,j.unit)) return 75;
    j.active = 1; j.oom = 0; strcpy(j.result,"starting");
    if (!read_limits(&j)) {
        j.active = 0; j.exit_status = 75; strcpy(j.result,"profile-verification-failed"); sample(&j,"unavailable"); return 75;
    }
    struct sigaction sa = {0}; sa.sa_handler = stop_signal; sigemptyset(&sa.sa_mask);
    sigaction(SIGINT,&sa,NULL); sigaction(SIGTERM,&sa,NULL); sigaction(SIGHUP,&sa,NULL);
    long long initial_oom = cg_counter(j.cgroup,"memory.events");
    if (initial_oom < 0 || !sample(&j,"running")) return 75;
    pid_t child = fork();
    if (child < 0) return 75;
    if (!child) {
        setpgid(0,0);
        signal(SIGINT,SIG_DFL); signal(SIGTERM,SIG_DFL); signal(SIGHUP,SIG_DFL); signal(SIGPIPE,SIG_DFL);
        execvp(argv[9],&argv[9]);
        fputs("tld-admit: command exec failed\n",stderr); _exit(127);
    }
    setpgid(child,child);
    int status = 0, reaped = 0; long long next_sample = 0;
    strcpy(j.result,"running");
    while (!stopping) {
        pid_t got = waitpid(child,&status,WNOHANG);
        if (got == child) { reaped = 1; break; }
        if (got < 0 && errno != EINTR) break;
        if (cg_counter(j.cgroup,"memory.events") > initial_oom) j.oom = 1;
        if (native_monotonic_ms() >= next_sample) { sample(&j,"running"); next_sample = native_monotonic_ms() + 1000; }
        usleep(50000);
    }
    if (!reaped) {
        native_signal_owned_group(child,stopping ? stopping : SIGTERM);
        long long deadline = native_monotonic_ms() + 2000;
        while (native_monotonic_ms() < deadline) {
            if (waitpid(child,&status,WNOHANG) == child) { reaped = 1; break; }
            usleep(20000);
        }
        if (!reaped) { native_signal_owned_group(child,SIGKILL); while (waitpid(child,&status,0) < 0 && errno == EINTR) {} }
    }
    if (cg_counter(j.cgroup,"memory.events") > initial_oom) j.oom = 1;
    j.exit_status = WIFEXITED(status) ? WEXITSTATUS(status) : WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 75;
    j.active = 0; j.elapsed = epoch() - j.started;
    strcpy(j.result,j.oom ? "oom-kill" : stopping ? "stopped" : j.exit_status ? "exit-code" : "success");
    sample(&j,j.oom ? "oom-killed" : j.exit_status ? "failed" : "idle");
    return (int)j.exit_status;
}
static void profile_limits(AdmitProfile profile, char *out, size_t n) {
    const AdmitProfileSpec *s = admit_profile(profile);
    snprintf(out,n,"{\"memoryHigh\":\"%s\",\"memoryMax\":\"%s\",\"memorySwapMax\":\"%s\",\"tasksMax\":\"%s\","
        "\"cpuQuota\":\"%s\",\"cpuWeight\":\"%s\",\"ioWeight\":\"%s\",\"nice\":%u,\"memoryOOMGroup\":true,\"verified\":false}",
        s->memory_high ? s->memory_high : "infinity",s->memory_max,s->memory_swap_max,s->tasks_max,s->cpu_quota,s->cpu_weight,s->io_weight,s->nice);
}
static int finish_runner(Job *j, int runner_status) {
    const char *args[] = {"/usr/bin/systemctl","--user","show",j->unit,"-p","Result","-p","ExecMainCode","-p","ExecMainStatus",NULL};
    char out[2048], result[64], code[32], status[32];
    int queried = !native_capture(args,out,sizeof out,3000) && field(out,"Result",result,sizeof result) &&
        field(out,"ExecMainCode",code,sizeof code) && field(out,"ExecMainStatus",status,sizeof status);
    Job saved; load_job(&saved);
    int same = !strcmp(saved.unit,j->unit), rc = 75;
    if (same && saved.exit_status >= 0 && saved.exit_status <= 255) rc = (int)saved.exit_status;
    else if (queried && (!strcmp(code,"1") || !strcmp(code,"2") || !strcmp(code,"3"))) {
        char *end; errno = 0; long value = strtol(status,&end,10);
        if (!errno && !*end && value >= 0 && value <= 255) rc = (int)value + (strcmp(code,"1") ? 128 : 0);
    }
    /* systemd-run failure before ExecStart is unavailable, not a child exit. */
    if (rc > 255) rc = 75;
    if (same && saved.exit_status >= 0) return rc;
    if (same) *j = saved;
    j->active = 0; j->exit_status = rc; j->elapsed = epoch() - j->started;
    j->oom = queried ? !strcmp(result,"oom-kill") : -1;
    strcpy(j->result,j->oom == 1 ? "oom-kill" : rc == 75 && runner_status ? "runner-unavailable" : rc ? "exit-code" : "success");
    sample(j,j->oom == 1 ? "oom-killed" : rc ? "failed" : "idle");
    return rc;
}
static int run_cmd(int argc, char **argv) {
    const char *profile = NULL, *name = NULL; int cmd = -1;
    for (int i = 2; i < argc; i++) {
        if (!strcmp(argv[i],"--profile") && i + 1 < argc && !profile) profile = argv[++i];
        else if (!strncmp(argv[i],"--profile=",10) && !profile) profile = argv[i] + 10;
        else if (!strcmp(argv[i],"--name") && i + 1 < argc && !name) name = argv[++i];
        else if (!strncmp(argv[i],"--name=",7) && !name) name = argv[i] + 7;
        else if (!strcmp(argv[i],"--")) { cmd = i + 1; break; }
        else return 64;
    }
    char err[ADMIT_TEXT_MAX]; AdmitProfile p = admit_profile_parse(profile);
    if (!p || !admit_validate_name(name,err,sizeof err) || cmd < 0 || cmd >= argc || !argv[cmd][0]) return 64;
    if (!init_paths(getenv("TLD_ADMIT_RUNTIME_DIR"))) return 75;
    int lockfd = open_lock(admission_lock,1); if (lockfd < 0) { fputs("tld-admit: another controller holds admission\n",stderr); return 75; }
    int policyfd = policy_lock(); if (policyfd < 0) { close(lockfd); return 75; }
    char active[192]; int count = find_active(active,sizeof active);
    if (count != 0) { close(policyfd); close(lockfd); fputs("tld-admit: active job or systemd unavailable\n",stderr); return 75; }
    AdmitReading r; read_gauge(&r); Policy policy = evaluate(&r);
    if (!policy.paused && !admit_launch_ready(&r,p)) {
        policy.paused = 1; policy.kind = "capacity";
        snprintf(policy.reason,sizeof policy.reason,"%s needs %lld GiB MemAvailable and %lld GiB swap free",profile,
            admit_profile(p)->launch_available / (1024LL*1024*1024), admit_profile(p)->swap_bytes / (1024LL*1024*1024) + 4);
    }
    Job j; job_init(&j); j.profile = p; st_copy(j.name,sizeof j.name,name); j.active = 0;
    Buf json = {0}, text = {0};
    if (policy.paused) {
        j.exit_status = 75; strcpy(j.result,"admission-paused");
        publish(&j,&r,&policy,policy_state(&policy,0,"paused"),&json,&text);
        buf_free(&json); buf_free(&text); close(policyfd); close(lockfd);
        if ((exists("auto-pause") || exists("psi-since"))) ensure_watch();
        return 75;
    }
    char suffix[80]; admit_safe_unit_suffix(name,suffix,sizeof suffix);
    snprintf(j.unit,sizeof j.unit,"tld-job-%s-%ld-%lld.service",suffix,(long)getpid(),epoch());
    j.started = epoch(); j.active = 1; strcpy(j.result,"starting"); profile_limits(p,j.limits,sizeof j.limits);
    int ok = publish(&j,&r,&policy,"starting",&json,&text);
    buf_free(&json); buf_free(&text); close(policyfd);
    if (!ok) { close(lockfd); return 75; }
    const AdmitProfileSpec *s = admit_profile(p);
    /* No shell, environment expansion or systemd specifiers in child arguments. */
    char *args[argc + 64]; int n = 0;
    char unitopt[224], high[96], max[96], swap[96], tasks[96], quota[96], cpu[96], io[96], nice[64], start[32];
    snprintf(unitopt,sizeof unitopt,"--unit=%s",j.unit);
    snprintf(high,sizeof high,"--property=MemoryHigh=%s",s->memory_high ? s->memory_high : "infinity");
    snprintf(max,sizeof max,"--property=MemoryMax=%s",s->memory_max); snprintf(swap,sizeof swap,"--property=MemorySwapMax=%s",s->memory_swap_max);
    snprintf(tasks,sizeof tasks,"--property=TasksMax=%s",s->tasks_max); snprintf(quota,sizeof quota,"--property=CPUQuota=%s",s->cpu_quota);
    snprintf(cpu,sizeof cpu,"--property=CPUWeight=%s",s->cpu_weight); snprintf(io,sizeof io,"--property=IOWeight=%s",s->io_weight);
    snprintf(nice,sizeof nice,"--property=Nice=%u",s->nice); snprintf(start,sizeof start,"%lld",j.started);
    args[n++] = "/usr/bin/systemd-run"; args[n++] = "--user"; args[n++] = "--quiet"; args[n++] = "--wait"; args[n++] = "--pipe";
    args[n++] = "--expand-environment=no"; args[n++] = "--no-ask-password"; args[n++] = unitopt;
    args[n++] = "--property=Type=exec"; args[n++] = "--property=OOMPolicy=kill"; args[n++] = "--property=KillMode=control-group";
    args[n++] = "--property=TimeoutStopSec=3s"; args[n++] = high; args[n++] = max; args[n++] = swap; args[n++] = tasks;
    args[n++] = quota; args[n++] = cpu; args[n++] = io; args[n++] = nice; args[n++] = "--same-dir";
    char *envargs[5] = {0}; const char *envkeys[] = {"TLD_ADMIT_MEMINFO","TLD_ADMIT_WATCHED_CGROUP","TLD_ADMIT_TEST_NO_WATCH","TLD_ADMIT_PSI","TLD_ADMIT_VMSTAT"};
    for (int i = 0; i < 5; i++) {
        const char *value = getenv(envkeys[i]);
        if (value && *value) {
            if (asprintf(&envargs[i],"--setenv=%s=%s",envkeys[i],value) < 0) {
                close(lockfd); for (int k = 0; k < i; k++) free(envargs[k]); return 75;
            }
            args[n++] = envargs[i];
        }
    }
    char self[PATH_MAX]; ssize_t len = readlink("/proc/self/exe",self,sizeof self - 1);
    if (len <= 0) { close(lockfd); for (int i = 0; i < 5; i++) free(envargs[i]); return 75; } self[len] = 0;
    args[n++] = "--"; args[n++] = self; args[n++] = "--internal-supervise"; args[n++] = runtime_dir; args[n++] = j.unit;
    args[n++] = (char *)profile; args[n++] = (char *)name; args[n++] = start; args[n++] = "v2"; args[n++] = "--";
    for (int i = cmd; i < argc; i++) args[n++] = argv[i];
    args[n] = NULL;
    pid_t waiter = fork();
    if (!waiter) {
        /* Keep lockfd here, never rely on systemd-run preserving an extra fd. */
        signal(SIGHUP,SIG_IGN); signal(SIGINT,SIG_IGN); signal(SIGTERM,SIG_IGN);
        pid_t runner = fork();
        if (!runner) {
            signal(SIGHUP,SIG_DFL); signal(SIGINT,SIG_DFL); signal(SIGTERM,SIG_DFL);
            int flags = fcntl(lockfd,F_GETFD);
            if (flags >= 0) fcntl(lockfd,F_SETFD,flags & ~FD_CLOEXEC);
            execv(args[0],args); _exit(75);
        }
        int status = 0;
        if (runner < 0) _exit(75);
        pid_t got; do { got = waitpid(runner,&status,0); } while (got < 0 && errno == EINTR);
        int rc = finish_runner(&j,got == runner && WIFEXITED(status) ? WEXITSTATUS(status) : 75);
        const char *cleanup[] = {"/usr/bin/systemctl","--user","reset-failed",j.unit,NULL};
        char ignored[512]; native_capture(cleanup,ignored,sizeof ignored,3000);
        close(lockfd); _exit(rc);
    }
    for (int i = 0; i < 5; i++) free(envargs[i]);
    if (waiter < 0) { close(lockfd); return 75; }
    int status = 0; pid_t got;
    do { got = waitpid(waiter,&status,0); } while (got < 0 && errno == EINTR);
    close(lockfd);
    return got == waiter && WIFEXITED(status) ? WEXITSTATUS(status) : 75;
}
static int ensure_watch(void) {
    /* Only the private synthetic fixture harness may opt out of sampling. */
    if (getenv("TLD_ADMIT_TEST_NO_WATCH") && getenv("TLD_ADMIT_MEMINFO") && getenv("TLD_ADMIT_WATCHED_CGROUP")) return 1;
    unsigned long long hash = 14695981039346656037ULL;
    for (const unsigned char *p = (const unsigned char *)runtime_dir; *p; p++) hash = (hash ^ *p) * 1099511628211ULL;
    char unit[96], unitopt[128], self[PATH_MAX], out[2048];
    snprintf(unit,sizeof unit,"tld-admission-watch-%016llx.service",hash);
    const char *check[] = {"/usr/bin/systemctl","--user","is-active",unit,NULL};
    if (!native_capture(check,out,sizeof out,1000)) return 1;
    ssize_t len = readlink("/proc/self/exe",self,sizeof self - 1);
    if (len <= 0) return 0;
    self[len] = 0;
    snprintf(unitopt,sizeof unitopt,"--unit=%s",unit);
    const char *args[32]; int n = 0;
    args[n++] = "/usr/bin/systemd-run"; args[n++] = "--user"; args[n++] = "--quiet"; args[n++] = "--collect";
    args[n++] = "--no-block"; args[n++] = "--expand-environment=no"; args[n++] = "--no-ask-password"; args[n++] = unitopt;
    args[n++] = "--property=Type=exec"; args[n++] = "--property=MemoryMax=128M"; args[n++] = "--property=MemorySwapMax=0";
    args[n++] = "--property=TasksMax=32"; args[n++] = "--property=CPUQuota=10%"; args[n++] = "--property=Nice=10";
    args[n++] = "--property=CPUWeight=10"; args[n++] = "--property=IOWeight=10"; args[n++] = "--property=OOMPolicy=kill";
    char *envargs[4] = {0}; const char *keys[] = {"TLD_ADMIT_MEMINFO","TLD_ADMIT_WATCHED_CGROUP","TLD_ADMIT_PSI","TLD_ADMIT_VMSTAT"};
    for (int i = 0; i < 4; i++) {
        const char *v = getenv(keys[i]);
        if (v && *v) {
            if (asprintf(&envargs[i],"--setenv=%s=%s",keys[i],v) < 0) {
                for (int k = 0; k < i; k++) free(envargs[k]);
                return 0;
            }
            args[n++] = envargs[i];
        }
    }
    args[n++] = "--"; args[n++] = self; args[n++] = "--internal-watch"; args[n++] = runtime_dir; args[n] = NULL;
    int ok = write_state("watch-unit",unit) && !native_capture(args,out,sizeof out,3000);
    for (int i = 0; i < 4; i++) free(envargs[i]);
    if (!ok) fputs("tld-admit: automatic pressure monitor unavailable; cooldown requires continuous samples\n",stderr);
    return ok;
}
static int watch_main(const char *dir) {
    if (!init_paths(dir)) return 75;
    watching = 1;
    int nullfd = open("/dev/null",O_RDWR);
    if (nullfd < 0) return 75;
    dup2(nullfd,STDOUT_FILENO); dup2(nullfd,STDERR_FILENO); close(nullfd);
    while (exists("auto-pause") || exists("psi-since")) {
        status_cmd(0,0);
        if (exists("auto-pause") || exists("psi-since")) usleep(1000000);
    }
    return 0;
}
int main(int argc, char **argv) {
    signal(SIGPIPE,SIG_IGN);
    if (argc == 3 && !strcmp(argv[1],"--internal-watch")) return watch_main(argv[2]);
    if (argc >= 2 && !strcmp(argv[1],"--internal-supervise")) return supervisor(argc,argv);
    if (argc >= 2 && !strcmp(argv[1],"run")) return run_cmd(argc,argv);
    if (argc == 2 && (!strcmp(argv[1],"--help") || !strcmp(argv[1],"-h"))) { usage(stdout); return 0; }
    if (argc == 2 && !strcmp(argv[1],"limits")) {
        for (int p = ADMIT_PROFILE_AGENT; p <= ADMIT_PROFILE_VR; p++) {
            const AdmitProfileSpec *s = admit_profile((AdmitProfile)p);
            printf("%s: MemoryHigh=%s MemoryMax=%s MemorySwapMax=%s TasksMax=%s CPUQuota=%s MemoryOOMGroup=yes CPUWeight=%s IOWeight=%s Nice=%u\n",
                s->name,s->memory_high ? s->memory_high : "infinity",s->memory_max,s->memory_swap_max,s->tasks_max,s->cpu_quota,s->cpu_weight,s->io_weight,s->nice);
        }
        return 0;
    }
    int status = argc >= 2 && !strcmp(argv[1],"status") && (argc == 2 || (argc == 3 && !strcmp(argv[2],"--json")));
    int pause = argc >= 2 && !strcmp(argv[1],"pause") && argc <= 3;
    int resume = argc == 2 && !strcmp(argv[1],"resume");
    if (!status && !pause && !resume) { usage(stderr); return 64; }
    if (!init_paths(getenv("TLD_ADMIT_RUNTIME_DIR"))) { fputs("tld-admit: runtime unavailable\n",stderr); return 75; }
    return pause ? pause_cmd(argc == 3 ? argv[2] : NULL) : status_cmd(argc == 3,resume);
}
