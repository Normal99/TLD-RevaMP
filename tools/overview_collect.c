/* overview_collect.c - fills the panels for tools/overview.
 *
 * Read-only by construction:
 *   - /proc is read directly (no pgrep, no ps, no fork of a process listing tool)
 *   - the worker logs and results are read as files (name, mtime, size, a short head)
 *   - the bridge is asked read-only questions over TCP
 *   - SSH to the second machine runs a read-only /proc scan
 * Nothing here launches, stops, or mutates a game, a prefix, or a configuration.
 */
#define _GNU_SOURCE
#include "overview_collect.h"
#include "overview_core.h"
#include "native_process.h"

#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/statfs.h>
#include <sys/time.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <unistd.h>

/* ================= configuration ================= */

OvCache  ov_cache_global;
OvConfig ov_config;

void ov_config_defaults(OvConfig *c) {
    memset(c, 0, sizeof *c);
    st_copy(c->workers_dir, sizeof c->workers_dir, "/tmp/tld-free-workers");
    st_copy(c->logs_dir, sizeof c->logs_dir, "/tmp/tld-free-workers/logs");
    st_copy(c->results_dir, sizeof c->results_dir, "/tmp/tld-free-workers/results");
    st_copy(c->repo_dir, sizeof c->repo_dir, ".");
    st_copy(c->remote_host, sizeof c->remote_host, "");
    st_copy(c->ssh_key, sizeof c->ssh_key, "");
    c->remote_enabled = 1;
    c->bridge_port = 27050;
    c->probe_min_interval = 30;      /* the brief's 30 s minimum between expensive probes */
    c->probe_timeout_ms = 1500;      /* 1-2 s timeouts on the probes */
    c->ssh_timeout_ms = 2000;
}

/* Read one KEY=value out of an env file (the tools' own format).  Only the few
 * structural keys this tool needs are read; a key that looks like a credential is
 * never even looked at, so no secret can reach the screen. */
static int env_file_get(const char *path, const char *key, char *out, size_t n) {
    FILE *f = fopen(path, "r");
    if (!f) return 0;
    char line[1024];
    int found = 0;
    size_t klen = strlen(key);
    while (fgets(line, sizeof line, f)) {
        char *p = line;
        while (*p == ' ' || *p == '\t') p++;
        if (*p == '#' || *p == '\n' || !*p) continue;
        if (strncmp(p, "export ", 7) == 0) p += 7;
        if (strncmp(p, key, klen) != 0 || p[klen] != '=') continue;
        char *v = p + klen + 1;
        size_t l = strlen(v);
        while (l && (v[l - 1] == '\n' || v[l - 1] == '\r' || v[l - 1] == ' ')) v[--l] = '\0';
        if (l >= 2 && ((v[0] == '"' && v[l - 1] == '"') || (v[0] == '\'' && v[l - 1] == '\''))) {
            v[l - 1] = '\0';
            v++;
            l -= 2;
        }
        if (l >= n) l = n - 1;
        memcpy(out, v, l);
        out[l] = '\0';
        found = 1;
        break;
    }
    fclose(f);
    return found;
}

/* The tools keep their machine settings in tools/local.env and
 * ~/.config/tld-revamp/tools.env (see tools/README.md).  Read the same files, so the
 * overview finds the second machine without a second configuration. */
static void ov_config_read_files(OvConfig *c) {
    static const char *keys[] = { "TLD_SECOND", "TLD_SSH_KEY", "TLD_BRIDGE_PORT" };
    const char *files[4];
    int nfiles = 0;

    char home[512] = { 0 };
    const char *h = getenv("HOME");
    if (h && *h) {
        snprintf(home, sizeof home, "%s/.config/tld-revamp/tools.env", h);
        files[nfiles++] = home;
    }
    /* the repository's own env file, wherever the tool was started from */
    files[nfiles++] = getenv("TLD_TOOLS_ENV") ? getenv("TLD_TOOLS_ENV") : "tools/local.env";
    for (int fi = 0; fi < nfiles; fi++) {
        for (size_t k = 0; k < sizeof keys / sizeof keys[0]; k++) {
            if (looks_secret_key(keys[k], strlen(keys[k]))) continue;   /* never read a secret */
            char val[512] = { 0 };
            if (!env_file_get(files[fi], keys[k], val, sizeof val) || !val[0]) continue;
            if (!strcmp(keys[k], "TLD_SECOND")) {
                if (!c->remote_host[0]) st_copy(c->remote_host, sizeof c->remote_host, val);
            } else if (!strcmp(keys[k], "TLD_SSH_KEY")) {
                if (!c->ssh_key[0]) {
                    if (val[0] == '~') {
                        char expanded[512];
                        snprintf(expanded, sizeof expanded, "%s%s", h ? h : "", val + 1);
                        st_copy(c->ssh_key, sizeof c->ssh_key, expanded);
                    } else {
                        st_copy(c->ssh_key, sizeof c->ssh_key, val);
                    }
                }
            } else if (!strcmp(keys[k], "TLD_BRIDGE_PORT")) {
                if (c->bridge_port == 27050) c->bridge_port = atoi(val);
            }
        }
    }
}

void ov_config_from_env(OvConfig *c) {
    const char *e;
    if ((e = getenv("TLD_WORKERS_DIR")) && *e) st_copy(c->workers_dir, sizeof c->workers_dir, e);
    if ((e = getenv("TLD_SECOND")) && *e) st_copy(c->remote_host, sizeof c->remote_host, e);
    if ((e = getenv("TLD_SSH_KEY")) && *e) st_copy(c->ssh_key, sizeof c->ssh_key, e);
    if ((e = getenv("TLD_BRIDGE_PORT")) && *e) c->bridge_port = atoi(e);
    if ((e = getenv("OV_PROBE_INTERVAL")) && *e) c->probe_min_interval = atoi(e);
    ov_config_read_files(c);
}

/* ================= probe cache ================= */

void ov_cache_init(OvCache *ca) { memset(ca, 0, sizeof *ca); }

void ov_cache_free(OvCache *ca) {
    for (int i = 0; i < ca->count; i++) free(ca->entries[i].value);
    memset(ca, 0, sizeof *ca);
}

/* A cached value is fresh for probe_min_interval seconds; after that it is refetched. */
int ov_cache_fresh(const OvCache *ca, const char *key) {
    const OvCacheEntry *e = ov_cache_find(ca, key);
    if (!e || !e->value) return 0;
    if (e->failed) return 0;
    return (time(NULL) - e->at) < (long)ov_config.probe_min_interval;
}

const OvCacheEntry *ov_cache_find(const OvCache *ca, const char *key) {
    for (int i = 0; i < ca->count; i++)
        if (strcmp(ca->entries[i].key, key) == 0) return &ca->entries[i];
    return NULL;
}

void ov_cache_put(OvCache *ca, const char *key, const char *value, int failed) {
    for (int i = 0; i < ca->count; i++) {
        if (strcmp(ca->entries[i].key, key) == 0) {
            free(ca->entries[i].value);
            ca->entries[i].value = value ? st_dup(value) : NULL;
            ca->entries[i].at = time(NULL);
            ca->entries[i].failed = failed;
            return;
        }
    }
    if (ca->count >= OV_CACHE_MAX) return;
    st_copy(ca->entries[ca->count].key, sizeof ca->entries[ca->count].key, key);
    ca->entries[ca->count].value = value ? st_dup(value) : NULL;
    ca->entries[ca->count].at = time(NULL);
    ca->entries[ca->count].failed = failed;
    ca->count++;
}

/* ================= small utilities ================= */

static long long file_size(const char *p) {
    struct stat st;
    return stat(p, &st) == 0 ? (long long)st.st_size : -1;
}

static long long file_mtime(const char *p) {
    struct stat st;
    return stat(p, &st) == 0 ? (long long)st.st_mtime : -1;
}

static long long now_s(void) { return (long long)time(NULL); }

/* Run a read-only shell snippet locally, with a deadline.  Used for the git status
 * only; the snippet is ours and never contains user input. */
/* Run a shell command with one extra argument, passed safely: the command
 * refers to it as "$1".  The argument is never spliced into the command text. */
int ov_run_shell_argv(const char *cmd, char *out, size_t out_n, int timeout_ms, const char *arg) {
    const char *args[] = {"/bin/sh", "-c", cmd, "sh", arg, NULL};
    return native_capture(args, out, out_n, timeout_ms);
}

int ov_run_shell(const char *cmd, char *out, size_t out_n, int timeout_ms) {
    return ov_run_shell_argv(cmd, out, out_n, timeout_ms, "");
}

/* ================= /proc scan ================= */

/* Walk /proc once and hand every process to the callback.  This replaces pgrep/ps:
 * one directory walk, no fork, no subprocess.
 *
 * The command line is read lazily: a machine with a thousand processes would
 * otherwise mean a thousand reads of /proc/<pid>/cmdline per refresh, and most
 * of them are of no interest.  The callback asks for the command line only when
 * the process name already says it might matter. */
typedef void (*OvProcFn)(int pid, const char *comm, char *cmdline, size_t cmdline_n, void *user);

static void ov_scan_procs(OvProcFn fn, void *user) {
    DIR *d = opendir("/proc");
    if (!d) return;
    struct dirent *e;
    char comm[128] = { 0 };
    char cmdline[1024] = { 0 };
    while ((e = readdir(d)) != NULL) {
        if (e->d_name[0] < '0' || e->d_name[0] > '9') continue;
        int pid = atoi(e->d_name);
        comm[0] = '\0';
        if (!proc_comm(pid, comm, sizeof comm)) continue;
        cmdline[0] = '\0';
        fn(pid, comm, cmdline, sizeof cmdline, user);
    }
    closedir(d);
}

typedef struct {
    OvGame *games;
    int     count;
    int     max;
    int     remote;
    const char *match_prefix;   /* only report processes under this prefix, or NULL */
} OvGameScan;

/* A worker is a Codex (or MiniMax) agent process: its command line names the
 * worktree it works in.  We never read its environment. */
typedef struct {
    OvAgent *list;
    int      count;
    int      max;
    int      seen;
} OvAgentScan;

static void ov_agent_collect(int pid, const char *comm, char *cmdline, size_t cmdline_n, void *user) {
    OvAgentScan *s = (OvAgentScan *)user;
    if (!s) return;
    if (strncmp(comm, "codex", 4) != 0) return;   /* only the agent's own binary */
    if (!cmdline[0] && !proc_cmdline(pid, cmdline, cmdline_n)) return;
    if (!st_has(cmdline, "tld-revamp")) return;

    /* the workspace is the worktree path in the command line: take its last
     * component (tld-revamp-free-tui-c), which is what the logs and the result
     * files are named after */
    char ws[OV_NAME] = { 0 };
    const char *p = strstr(cmdline, "tld-revamp");
    if (p) {
        const char *start = p;
        while (start > cmdline && start[-1] != ' ') start--;
        const char *end = p;
        while (*end && *end != ' ' && *end != '/') end++;
        size_t l = (size_t)(end - start);
        if (l >= sizeof ws) l = sizeof ws - 1;
        memcpy(ws, start, l);
        ws[l] = '\0';
        /* the last path component: tld-revamp-free-tui-c, which is what the
         * result files are named after */
        char *slash = strrchr(ws, '/');
        if (slash && slash[1]) memmove(ws, slash + 1, strlen(slash + 1) + 1);
    }
    if (!ws[0]) st_copy(ws, sizeof ws, "unknown");

    /* one agent per workspace: keep the youngest (a restart makes a new pid) */
    int found = -1;
    for (int i = 0; i < s->count; i++)
        if (strcmp(s->list[i].workspace, ws) == 0) { found = i; break; }

    if (found < 0) {
        if (s->count >= s->max) return;
        found = s->count++;
        memset(&s->list[found], 0, sizeof s->list[found]);
        st_copy(s->list[found].workspace, sizeof s->list[found].workspace, ws);
        st_copy(s->list[found].role, sizeof s->list[found].role, comm);
    }
    s->seen++;
    s->list[found].pid = pid;
    s->list[found].started_s = proc_start_s(pid);
    st_copy(s->list[found].role, sizeof s->list[found].role, comm);

    /* the model the agent runs: "-m <model>" in its command line.  Only the model
     * name is taken; nothing else of the command line is shown. */
    const char *m = strstr(cmdline, " -m ");
    if (m) {
        m += 4;
        size_t i = 0;
        while (m[i] && m[i] != ' ' && m[i] != '\'' && m[i] != '"' && i < sizeof s->list[found].model - 1) {
            s->list[found].model[i] = m[i];
            i++;
        }
        s->list[found].model[i] = '\0';
    }
}

/* ================= the TLD / Proton game processes ================= */

static int cmdline_has_game(const char *cmdline) {
    return st_has(cmdline, "TheLongDrive.exe") || st_has(cmdline, "TheLongDrive.ex");
}

static void ov_game_collect(int pid, const char *comm, char *cmdline, size_t cmdline_n, void *user) {
    OvGameScan *s = (OvGameScan *)user;
    if (!s) return;
    /* the name says it is the game: only then is the command line worth reading */
    if (strncmp(comm, "TheLongDrive", 12) != 0) return;
    if (!cmdline[0] && !proc_cmdline(pid, cmdline, cmdline_n)) return;
    if (!cmdline_has_game(cmdline)) return;
    if (s->count >= s->max) return;

    OvGame *g = &s->games[s->count];
    memset(g, 0, sizeof *g);
    g->pid = pid;
    g->remote = s->remote;

    char env[OV_PATH] = { 0 };
    if (proc_env_value(pid, "STEAM_COMPAT_DATA_PATH", env, sizeof env)) {
        st_copy(g->prefix, sizeof g->prefix, env);
    }
    env[0] = '\0';
    if (proc_env_value(pid, "STEAM_COMPAT_INSTALL_PATH", env, sizeof env)) {
        st_copy(g->game, sizeof g->game, env);
    }
    /* the bridge port the copy was started with: -tldbridge <port> */
    const char *bp = st_has(cmdline, "-tldbridge") ? strstr(cmdline, "-tldbridge") : NULL;
    if (bp) {
        const char *q = bp + 10;
        while (*q == ' ') q++;
        size_t i = 0;
        while (q[i] && isdigit((unsigned char)q[i]) && i < sizeof g->port - 1) { g->port[i] = q[i]; i++; }
        g->port[i] = '\0';
    }
    /* a process with no controlling terminal was started in the background */
    g->background = proc_tty(pid) == 0;
    g->started_s = proc_start_s(pid);
    s->count++;
}

/* ================= bridge probing ================= */

/* Talk to the in-game debug bridge.  Read-only commands only; the caller decides
 * which questions to ask.  Returns 0 on success. */
int ov_bridge_query(int port, const char *cmd, char *out, size_t out_n, int timeout_ms) {
    const char *one[2] = { cmd, NULL };
    return ov_bridge_query_many(port, one, 1, out, out_n, timeout_ms);
}

/* Ask the bridge several questions over ONE connection.  The bridge answers one
 * line per command, so several short questions cost one handshake instead of
 * several.  `cmds` is a NULL-terminated array; the answers are written to `out`
 * one after the other, each on its own line. */
static int ov_socket_wait(int fd, short events, long long deadline) {
    while (native_monotonic_ms() < deadline) {
        struct pollfd p = {.fd=fd, .events=events};
        int left = (int)(deadline - native_monotonic_ms());
        int rc = poll(&p,1,left > 0 ? left : 0);
        if (rc > 0) return !(p.revents & POLLNVAL);
        if (rc < 0 && errno == EINTR) continue;
        return 0;
    }
    return 0;
}
int ov_bridge_query_many(int port, const char *const *cmds, int ncmds, char *out, size_t out_n, int timeout_ms) {
    if (!out || !out_n) return -1;
    out[0] = 0;
    if (port <= 0 || port > 65535 || ncmds <= 0 || ncmds > 16 || timeout_ms <= 0) return -1;
    static const char *const allowed[] = {"ping","quality","stats","openxr",
        "get settingsscript.s.S.FFPSLimit","get settingsscript.s.S.BEnableFpsLimit","get settingsscript.s.S.BVsync",
        "get UnityEngine.Screen.width","get UnityEngine.Screen.height","get UnityEngine.Screen.fullScreenMode"};
    for (int i = 0; i < ncmds; i++) {
        int ok = 0;
        for (size_t j = 0; j < sizeof allowed / sizeof allowed[0]; j++)
            if (cmds[i] && !strcmp(cmds[i],allowed[j])) ok = 1;
        if (!ok) return -1;
    }
    int fd = socket(AF_INET,SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC,0);
    if (fd < 0) return -1;
    struct sockaddr_in sa = {.sin_family=AF_INET,.sin_port=htons((unsigned short)port),.sin_addr.s_addr=htonl(INADDR_LOOPBACK)};
    long long deadline = native_monotonic_ms() + timeout_ms;
    int rc = -1;
    if (connect(fd,(struct sockaddr *)&sa,sizeof sa)) {
        if (errno != EINPROGRESS || !ov_socket_wait(fd,POLLOUT,deadline)) goto end;
        int error; socklen_t size = sizeof error;
        if (getsockopt(fd,SOL_SOCKET,SO_ERROR,&error,&size) || error) goto end;
    }
    for (int i = 0; i < ncmds; i++) {
        Buf command = {0}; buf_printf(&command,"%s\n",cmds[i]);
        size_t sent = 0;
        while (sent < command.len) {
            if (!ov_socket_wait(fd,POLLOUT,deadline)) { buf_free(&command); goto end; }
            ssize_t amount = send(fd,command.data + sent,command.len - sent,MSG_NOSIGNAL);
            if (amount > 0) sent += (size_t)amount;
            else if (amount < 0 && (errno == EINTR || errno == EAGAIN)) continue;
            else { buf_free(&command); goto end; }
        }
        buf_free(&command);
    }
    size_t got = 0; int lines = 0;
    while (got + 1 < out_n && lines < ncmds) {
        if (!ov_socket_wait(fd,POLLIN,deadline)) goto end;
        ssize_t amount = recv(fd,out + got,out_n - got - 1,0);
        if (amount < 0 && (errno == EINTR || errno == EAGAIN)) continue;
        if (amount <= 0) goto end;
        for (ssize_t i = 0; i < amount; i++) if (out[got + (size_t)i] == '\n') lines++;
        got += (size_t)amount;
    }
    if (lines < ncmds) goto end;
    out[got] = 0;
    if (ncmds == 1) { char *nl = strchr(out,'\n'); if (nl) *nl = 0; }
    rc = 0;
end:
    close(fd);
    if (rc) out[0] = 0;
    return rc;
}

/* ================= SSH to the second machine ================= */

/* Run a read-only shell snippet on the second machine.  The snippet is ours, never
 * built from user input, and it only reads /proc. */
int ov_ssh_run(const char *host, const char *key, const char *script, char *out, size_t out_n, int timeout_ms) {
    if (!host || !*host || host[0] == '-') { if (out_n) out[0] = 0; return -1; }
    const char *args[32]; int n = 0;
    args[n++] = "ssh";
    args[n++] = "-o"; args[n++] = "BatchMode=yes";
    args[n++] = "-o"; args[n++] = "ConnectTimeout=2";
    args[n++] = "-o"; args[n++] = "ControlMaster=no";
    args[n++] = "-o"; args[n++] = "ControlPath=none";
    args[n++] = "-o"; args[n++] = "PermitLocalCommand=no";
    args[n++] = "-o"; args[n++] = "ClearAllForwardings=yes";
    args[n++] = "-o"; args[n++] = "ForwardAgent=no";
    args[n++] = "-o"; args[n++] = "StrictHostKeyChecking=yes";
    args[n++] = "-o"; args[n++] = "UpdateHostKeys=no";
    if (key && *key) { args[n++] = "-i"; args[n++] = key; }
    args[n++] = "--"; args[n++] = host; args[n++] = script; args[n] = NULL;
    return native_capture(args, out, out_n, timeout_ms);
}

/* The remote scan prints one line per game process: pid|comm|prefix|install|tty.
 *
 * It reads the process name with the shell's own "read" instead of a "cat" per
 * process: a machine with a thousand processes would otherwise fork a thousand
 * times, which took a second per probe.  The name decides whether the rest is
 * read at all, so only the game's own processes are looked at. */
static const char *OV_REMOTE_SCRIPT =
    "for p in /proc/[0-9]*; do "
    "read -r c rest < $p/comm 2>/dev/null || continue; "
    "case \"$c\" in TheLongDrive*) "
    "st=$(< $p/stat); tty=$(echo \"${st##*) }\" | cut -d' ' -f6); "
    "pre=$(tr '\\0' '\\n' < $p/environ 2>/dev/null | sed -n 's/^STEAM_COMPAT_DATA_PATH=//p' | head -1); "
    "ins=$(tr '\\0' '\\n' < $p/environ 2>/dev/null | sed -n 's/^STEAM_COMPAT_INSTALL_PATH=//p' | head -1); "
    "printf 'G|%s|%s|%s|%s|%s\\n' \"${p##*/}\" \"$c\" \"$pre\" \"$ins\" \"$tty\"; "
    ";; esac; done";

int ov_parse_remote_games(const char *text, OvGame *games, int *count, int max) {
    if (!text || !count) return 0;
    const char *p = text;
    while (*p && *count < max) {
        const char *eol = strchr(p, '\n');
        size_t len = eol ? (size_t)(eol - p) : strlen(p);
        char line[1024];
        if (len >= sizeof line) len = sizeof line - 1;
        memcpy(line, p, len);
        line[len] = '\0';
        p = eol ? eol + 1 : p + len;

        if (line[0] != 'G') continue;
        /* the line is "G|pid|comm|prefix|install|tty": the 'G' is the record tag */
        char *fields[5] = { 0 };
        int nf = 0;
        char *tok = strtok(line + 2, "|");
        while (tok && nf < 5) { fields[nf++] = tok; tok = strtok(NULL, "|"); }
        if (nf < 5) continue;

        OvGame *g = &games[*count];
        memset(g, 0, sizeof *g);
        g->pid = atoi(fields[0]);        /* pid */
        g->remote = 1;
        st_copy(g->prefix, sizeof g->prefix, fields[2]);   /* the Proton prefix */
        st_copy(g->game, sizeof g->game, fields[3]);       /* the install path */
        g->background = atoi(fields[4]) == 0;               /* no controlling tty */
        (*count)++;
    }
    return *count;
}

/* ================= panel: agents ================= */

static void ov_panel_agents(OvFrame *f) {
    OvAgentScan scan;
    memset(&scan, 0, sizeof scan);
    scan.list = f->agents;
    scan.max = OV_MAX_AGENTS;
    ov_scan_procs(ov_agent_collect, &scan);
    f->agent_count = scan.count;
    f->agent_total_seen = scan.seen;

    /* the result file of a worker is <results>/<workspace-suffix>.md, e.g. tui-c.md
     * for the workspace tld-revamp-free-tui-c */
    char results[OV_PATH];
    snprintf(results, sizeof results, "%s", ov_config.results_dir);
    for (int i = 0; i < f->agent_count; i++) {
        OvAgent *a = &f->agents[i];
        /* the workspace is tld-revamp-free-<name>; the result file is <name>.md */
        const char *dash = strrchr(a->workspace, '-');
        const char *shortname = (dash && dash[1]) ? dash + 1 : a->workspace;
        char path[OV_PATH * 2];
        snprintf(path, sizeof path, "%s/%s.md", results, shortname);
        long long sz = file_size(path);
        if (sz >= 0) {
            a->has_result = 1;
            a->result_size = sz;
            long long mt = file_mtime(path);
            a->result_age_s = mt > 0 ? now_s() - mt : -1;
        }
    }
}

/* ================= panel: game processes ================= */

static void ov_panel_games(OvFrame *f) {
    OvGameScan scan;
    memset(&scan, 0, sizeof scan);
    scan.games = f->games;
    scan.max = OV_MAX_GAMES;
    scan.remote = 0;
    ov_scan_procs(ov_game_collect, &scan);
    f->game_count = scan.count;
    f->remote_game_count = 0;

    if (!ov_config.remote_enabled || !ov_config.remote_host[0]) {
        st_copy(f->remote_err, sizeof f->remote_err,
                ov_config.remote_enabled ? "no second machine configured" : "remote probes disabled");
        return;
    }

    /* the remote probe is expensive: cache it for the minimum interval */
    const OvCacheEntry *cached = ov_cache_find(&ov_cache_global, "ssh_remote");
    if (cached && cached->value && !cached->failed &&
        (now_s() - cached->at) < (long)ov_config.probe_min_interval) {
        ov_parse_remote_games(cached->value, f->games + f->game_count, &f->remote_game_count, OV_MAX_GAMES - f->game_count);
        st_copy(f->remote_host, sizeof f->remote_host, ov_config.remote_host);
        return;
    }

    char out[8192];
    out[0] = '\0';
    int rc = ov_ssh_run(ov_config.remote_host, ov_config.ssh_key, OV_REMOTE_SCRIPT, out, sizeof out, ov_config.ssh_timeout_ms);
    if (rc != 0) {
        ov_cache_put(&ov_cache_global, "ssh_remote", NULL, 1);
        snprintf(f->remote_err, sizeof f->remote_err, "ssh probe failed (%s)", ov_config.remote_host);
        return;
    }
    ov_cache_put(&ov_cache_global, "ssh_remote", out, 0);
    ov_parse_remote_games(out, f->games + f->game_count, &f->remote_game_count, OV_MAX_GAMES - f->game_count);
    st_copy(f->remote_host, sizeof f->remote_host, ov_config.remote_host);
}

/* ================= panel: graphics compliance ================= */

/* The bridge answers are gathered into one compact text snapshot, so the whole
 * panel is one cache entry: the expensive probes run at most once every
 * probe_min_interval seconds, and a redraw costs nothing. */
static void ov_graphics_gather(OvGraphics *g, int port) {
    Buf b = { 0 };
    char json[16384] = { 0 };
    Buf tmp = { 0 };
    double d;

    snprintf(g->port, sizeof g->port, "%d", port);

    /* One connection, one batch: the bridge answers one line per command. */
    static const char *const CMDS[] = {
        "ping",
        "quality",
        "get settingsscript.s.S.FFPSLimit",
        "get settingsscript.s.S.BEnableFpsLimit",
        "get settingsscript.s.S.BVsync",
        "get UnityEngine.Screen.width",
        "get UnityEngine.Screen.height",
        "get UnityEngine.Screen.fullScreenMode",
        "stats",
        NULL
    };
    if (ov_bridge_query_many(port, CMDS, 9, json, sizeof json, ov_config.probe_timeout_ms) != 0) {
        buf_free(&b);
        return;
    }

    /* the answers come back in the order the questions were asked */
    char *lines[16];
    int nlines = 0;
    {
        char *p = json;
        while (*p && nlines < 16) {
            char *nl = strchr(p, '\n');
            if (nl) *nl = '\0';
            if (*p) lines[nlines++] = p;
            if (!nl) break;
            p = nl + 1;
        }
    }
    if (nlines < 9) { buf_free(&b); return; }

    json_str(&tmp, lines[0], "version");
    st_copy(g->version, sizeof g->version, tmp.data ? tmp.data : "?");
    buf_reset(&tmp);
    json_str(&tmp, lines[0], "build");
    st_copy(g->build, sizeof g->build, tmp.data ? tmp.data : "?");
    buf_reset(&tmp);

    if (json_num(&d, lines[1], "level")) g->quality_level = (int)d;
    if (json_num(&d, lines[1], "vSync")) g->vsync = (int)d;
    if (json_num(&d, lines[1], "targetFps")) g->target_fps = (int)d;

    if (json_num(&d, lines[2], "value")) g->fps_cap_limit = (int)d;
    if (json_num(&d, lines[3], "value")) g->fps_cap_enabled = (int)d ? 1 : 0;
    if (json_num(&d, lines[4], "value")) g->vsync_setting = (int)d ? 1 : 0;
    if (json_num(&d, lines[5], "value")) g->screen_w = (int)d;
    if (json_num(&d, lines[6], "value")) g->screen_h = (int)d;
    json_str(&tmp, lines[7], "value");
    st_copy(g->fullscreen, sizeof g->fullscreen, tmp.data ? tmp.data : "?");
    buf_reset(&tmp);
    if (json_num(&d, lines[8], "fps")) g->measured_fps = (int)(d + 0.5);

    buf_printf(&b, "version=%s\n", g->version);
    buf_printf(&b, "build=%s\n", g->build);
    buf_printf(&b, "port=%d\n", port);
    buf_printf(&b, "quality=%d\n", g->quality_level);
    buf_printf(&b, "vsync=%d\n", g->vsync);
    buf_printf(&b, "targetfps=%d\n", g->target_fps);
    buf_printf(&b, "fpslimit=%d\n", g->fps_cap_limit);
    buf_printf(&b, "fpslimiton=%d\n", g->fps_cap_enabled);
    buf_printf(&b, "vsyncsetting=%d\n", g->vsync_setting);
    buf_printf(&b, "width=%d\n", g->screen_w);
    buf_printf(&b, "height=%d\n", g->screen_h);
    buf_printf(&b, "fullscreen=%s\n", g->fullscreen);
    buf_printf(&b, "fps=%d\n", g->measured_fps);

    buf_free(&tmp);
    ov_cache_put(&ov_cache_global, "bridge_local", b.data, 0);
    buf_free(&b);
}

/* Read the cached snapshot back into the panel. */
static void ov_graphics_from_snapshot(OvGraphics *g, const char *text, int port) {
    memset(g, 0, sizeof *g);
    snprintf(g->port, sizeof g->port, "%d", port);
    const char *p = text;
    while (p && *p) {
        const char *eol = strchr(p, '\n');
        size_t len = eol ? (size_t)(eol - p) : strlen(p);
        char line[256];
        if (len >= sizeof line) len = sizeof line - 1;
        memcpy(line, p, len);
        line[len] = '\0';
        p = eol ? eol + 1 : p + len;
        char *eq = strchr(line, '=');
        if (!eq) continue;
        *eq = '\0';
        const char *k = line, *v = eq + 1;
        if (!strcmp(k, "version")) st_copy(g->version, sizeof g->version, v);
        else if (!strcmp(k, "build")) st_copy(g->build, sizeof g->build, v);
        else if (!strcmp(k, "port")) snprintf(g->port, sizeof g->port, "%s", v);
        else if (!strcmp(k, "quality")) g->quality_level = atoi(v);
        else if (!strcmp(k, "vsync")) g->vsync = atoi(v);
        else if (!strcmp(k, "targetfps")) g->target_fps = atoi(v);
        else if (!strcmp(k, "fpslimit")) g->fps_cap_limit = atoi(v);
        else if (!strcmp(k, "fpslimiton")) g->fps_cap_enabled = atoi(v);
        else if (!strcmp(k, "vsyncsetting")) g->vsync_setting = atoi(v);
        else if (!strcmp(k, "width")) g->screen_w = atoi(v);
        else if (!strcmp(k, "height")) g->screen_h = atoi(v);
        else if (!strcmp(k, "fullscreen")) st_copy(g->fullscreen, sizeof g->fullscreen, v);
        else if (!strcmp(k, "fps")) g->measured_fps = atoi(v);
    }
}

/* Which of the running copies' bridge ports actually answers?  A probe of one port
 * is one short TCP exchange; the ports come from the copies' own command lines. */
/* The ports the overview may try: the configured one, the port the running copies
 * declare, and the usual test ports.  The first one that answers wins. */
static int ov_candidate_ports(const OvFrame *f, int *out, int max) {
    int n = 0;
    if (ov_config.bridge_port > 0) out[n++] = ov_config.bridge_port;
    for (int i = 0; i < f->game_count && n < max; i++)
        if (f->games[i].port[0]) {
            int p = atoi(f->games[i].port);
            int seen = 0;
            for (int j = 0; j < n; j++) if (out[j] == p) seen = 1;
            if (!seen && p > 0) out[n++] = p;
        }
    /* the ports the tools use: the local bridge and the tunnels to the second one */
    static const int usual[] = { 27050, 27051, 27060, 27070 };
    for (size_t i = 0; i < sizeof usual / sizeof usual[0] && n < max; i++) {
        int seen = 0;
        for (int j = 0; j < n; j++) if (out[j] == usual[i]) seen = 1;
        if (!seen) out[n++] = usual[i];
    }
    return n;
}

static int ov_find_bridge_port(OvFrame *f) {
    int fallback = ov_config.bridge_port;

    /* the port discovery is itself a probe: cache it like the others */
    const OvCacheEntry *cached = ov_cache_find(&ov_cache_global, "bridge_port");
    if (cached && cached->value && !cached->failed &&
        (now_s() - cached->at) < (long)ov_config.probe_min_interval) {
        int p = atoi(cached->value);
        if (p > 0) {
            for (int i = 0; i < f->game_count; i++)
                if (f->games[i].port[0] && atoi(f->games[i].port) == p) {
                    f->games[i].bridge_probe = 1;
                    f->games[i].has_bridge = 1;
                    st_copy(f->games[i].bridge_version, sizeof f->games[i].bridge_version, "up");
                }
            return p;
        }
    }

    int ports[8];
    int nports = ov_candidate_ports(f, ports, 8);
    for (int k = 0; k < nports; k++) {
        int p = ports[k];
        if (p <= 0) continue;
        char ping[256] = { 0 };
        if (ov_bridge_query(p, "ping", ping, sizeof ping, ov_config.probe_timeout_ms) != 0) continue;
        for (int i = 0; i < f->game_count; i++) {
            if (!f->games[i].port[0] || atoi(f->games[i].port) != p) continue;
            f->games[i].bridge_probe = 1;
            f->games[i].has_bridge = 1;
            st_copy(f->games[i].bridge_version, sizeof f->games[i].bridge_version, "up");
        }
        char portbuf[16];
        snprintf(portbuf, sizeof portbuf, "%d", p);
        ov_cache_put(&ov_cache_global, "bridge_port", portbuf, 0);
        /* the tools tunnel the second machine's bridge to 27051 */
        if (p == 27051) ov_cache_put(&ov_cache_global, "bridge_where", "the second machine (through a tunnel)", 0);
        else if (p == 27070) ov_cache_put(&ov_cache_global, "bridge_where", "the second machine (tunnel 27070)", 0);
        else ov_cache_put(&ov_cache_global, "bridge_where", "this machine", 0);
        return p;
    }
    /* nothing answered: remember that too, so the next polls do not re-probe */
    char portbuf[16];
    snprintf(portbuf, sizeof portbuf, "%d", fallback);
    ov_cache_put(&ov_cache_global, "bridge_port", portbuf, 0);
    return fallback;
}

static void ov_panel_graphics(OvFrame *f) {
    OvGraphics *g = &f->graphics;
    memset(g, 0, sizeof *g);

    /* the port: the running copy's own -tldbridge port, else the configured one */
    int port = ov_find_bridge_port(f);

    /* the bridge probe is expensive: cache the whole snapshot */
    const OvCacheEntry *cached = ov_cache_find(&ov_cache_global, "bridge_local");
    if (cached && cached->value && !cached->failed &&
        (now_s() - cached->at) < (long)ov_config.probe_min_interval) {
        ov_graphics_from_snapshot(g, cached->value, port);
        g->ok = 1;
    } else {
        ov_graphics_gather(g, port);
        const OvCacheEntry *now = ov_cache_find(&ov_cache_global, "bridge_local");
        if (!now || !now->value) {
            ov_cache_put(&ov_cache_global, "bridge_local", NULL, 1);
            snprintf(g->err, sizeof g->err, "no bridge on 127.0.0.1:%d", port);
            return;
        }
        ov_graphics_from_snapshot(g, now->value, port);
        g->ok = 1;
    }

    /* which machine the bridge is on */
    {
        const OvCacheEntry *w = ov_cache_find(&ov_cache_global, "bridge_where");
        if (w && w->value) st_copy(g->where, sizeof g->where, w->value);
        else st_copy(g->where, sizeof g->where, "this machine");
    }

    /* compliance, per the low-load graphics policy */
    g->ok_quality = (g->quality_level == 0);
    /* the low-load policy: a 30 fps cap.  It can be set either by the bridge's
     * target frame rate or by the game's own limiter; -1 means "no cap". */
    g->ok_fps = (g->target_fps == 30) ||
                (g->fps_cap_enabled && g->fps_cap_limit == 30);
    g->ok_vsync = (g->vsync == 0) && (g->vsync_setting == 0);
    g->ok_resolution = (g->screen_w > 0 && g->screen_h > 0 &&
                        g->screen_w <= 1280 && g->screen_h <= 720);
    g->ok_background = 1;   /* the caller fills this from the process table */
    g->compliant = g->ok_quality && g->ok_fps && g->ok_vsync && g->ok_resolution && g->ok_background;
}

/* ================= panel: the Steam session, read from the logs ================= */

/* The steam workers' logs are the agents' transcripts, so the run's own output sits
 * inside them.  Only the last run block is read, and only lines that match the run
 * header's exact shape are taken: the source of the tool (which contains the same
 * words inside string literals) must not be mistaken for the run's output. */

/* A line of the run's own output starts with exactly two spaces and the key, then
 * a colon.  The key may itself contain spaces (the run header aligns them:
 * "  local  prefix   : ..."), so the match is on the key as written. */
static const char *ov_after_key(const char *line, const char *key) {
    size_t kl = strlen(key);
    if (strncmp(line, "  ", 2) != 0) return NULL;      /* exactly two spaces */
    if (strncmp(line + 2, key, kl) != 0) return NULL;
    const char *p = line + 2 + kl;
    while (*p == ' ') p++;
    if (*p != ':') return NULL;
    p++;
    while (*p == ' ') p++;
    return p;
}

static void ov_copy_to_eol(char *out, size_t n, const char *p) {
    out[0] = '\0';
    size_t i = 0;
    while (p[i] && p[i] != '\n' && p[i] != '\r' && i < n - 1) { out[i] = p[i]; i++; }
    out[i] = '\0';
    st_trim(out);
}

int ov_parse_steam_log(const char *text, OvSteam *s) {
    /* Find the newest run: the run headers look like
     *   "=== <name> run 20261021-001352 (relay iad) ==="
     * and a run prints its header more than once, so the run's output starts at the
     * FIRST header carrying that run's timestamp.  A header without a timestamp is
     * not a run header at all (the transcript quotes the tools' own source, which
     * contains similar words). */
    const char *last = NULL;
    char stamp[64] = { 0 };
    for (const char *q = text; (q = strstr(q, "=== ")) != NULL; q += 4) {
        const char *eol = strchr(q, '\n');
        size_t len = eol ? (size_t)(eol - q) : strlen(q);
        if (len < 10 || len > 200) continue;
        char head[256];
        if (len >= sizeof head) len = sizeof head - 1;
        memcpy(head, q, len);
        head[len] = '\0';
        if (!strstr(head, " run ") || !st_ends(head, " ===")) continue;
        const char *st = strstr(head, " run ") + 5;
        int digits = 0;
        while (st[digits] >= '0' && st[digits] <= '9') digits++;
        if (digits < 8) continue;
        last = q;
        size_t i = 0;
        while (st[i] && st[i] != ' ' && i < sizeof stamp - 1) { stamp[i] = st[i]; i++; }
        stamp[i] = '\0';
    }
    if (!last) return 0;

    /* walk back to the first header of that same run */
    const char *run = last;
    for (const char *q = text; q < last; ) {
        const char *nxt = strstr(q, "=== ");
        if (!nxt || nxt >= last) break;
        const char *eol = strchr(nxt, '\n');
        size_t len = eol ? (size_t)(eol - nxt) : strlen(nxt);
        if (len >= 10 && len <= 200) {
            char head[256];
            if (len >= sizeof head) len = sizeof head - 1;
            memcpy(head, nxt, len);
            head[len] = '\0';
            if (st_has(head, stamp) && st_has(head, " run ")) run = nxt;
        }
        q = nxt + 4;
    }

    /* the run header's own fields come from the newest run; the verdicts are
     * counted over the whole tail */
    const char *p = run;
    const char *vscan = text;

    char line[1024];
    while (*vscan) {
        const char *eol = strchr(vscan, '\n');
        size_t len = eol ? (size_t)(eol - vscan) : strlen(vscan);
        if (len >= sizeof line) len = sizeof line - 1;
        memcpy(line, vscan, len);
        line[len] = '\0';
        vscan = eol ? eol + 1 : vscan + len;

        /* The verdicts live in whichever run reached that stage; a newer run may
         * have failed before it.  They are counted over the whole tail, and the
         * last one is kept so the panel can name it. */
        if (st_starts(line, "  PASS  ") || st_starts(line, "  FAIL  ") ||
            st_starts(line, "  FAILED:")) {
            if (st_starts(line, "  PASS  ")) s->pass_count++;
            else s->fail_count++;
            /* only the verdict's name is kept: the detail after it can be a JSON
             * dump, and this tool must not put such a thing on the screen */
            char name[OV_TEXT];
            ov_copy_to_eol(name, sizeof name, line + 8);
            char *brace = strchr(name, '{');
            if (brace) *brace = '\0';
            st_trim(name);
            st_copy(s->last_verdict, sizeof s->last_verdict, name);
        }

        /* the run's own header fields, only inside the newest run */
        if (vscan < run) continue;

        const char *eol2 = strchr(p, '\n');
        size_t len2 = eol2 ? (size_t)(eol2 - p) : strlen(p);
        if (len2 >= sizeof line) len2 = sizeof line - 1;
        memcpy(line, p, len2);
        line[len2] = '\0';
        p = eol2 ? eol2 + 1 : p + len2;

        char val[OV_TEXT];
        const char *v;
        if ((v = ov_after_key(line, "relay setting")) != NULL) {
            ov_copy_to_eol(val, sizeof val, v);
            if (val[0] && !s->relay[0]) st_copy(s->relay, sizeof s->relay, val);
        } else if ((v = ov_after_key(line, "local  prefix")) != NULL) {
            ov_copy_to_eol(val, sizeof val, v);
            if (val[0]) st_copy(s->host, sizeof s->host, val);
        } else if ((v = ov_after_key(line, "remote prefix")) != NULL) {
            ov_copy_to_eol(val, sizeof val, v);
            if (val[0]) st_copy(s->client, sizeof s->client, val);
        } else if ((v = ov_after_key(line, "bridge ports")) != NULL) {
            ov_copy_to_eol(val, sizeof val, v);
            if (val[0]) st_copy(s->ports, sizeof s->ports, val);
        }
    }

    /* The phase is the run's last "--- <name> ---" section header, and its bridge
     * ports.  The scan starts at the run header, so the tool's own source code
     * (quoted in the transcript) can never be mistaken for the run's output. */
    /* The run's own phases are the first section headers after its header; the
     * notes the agent writes later are not phases. */
    const char *sec = NULL;
    for (const char *q = run; (q = strstr(q, "\n--- ")) != NULL; q += 5) {
        /* a section header is a whole line: "--- <name> ---" and nothing else */
        const char *e = strstr(q + 5, " ---");
        if (!e) continue;
        const char *after = e + 4;
        if (*after != '\n' && *after != '\r' && *after != '\0') continue;
        /* the name is a short slug: lower-case words, spaces and dashes only.
         * Prose (and the tools' own source, quoted in the transcript) does not
         * look like that. */
        size_t nlen = (size_t)(e - (q + 5));
        if (nlen == 0 || nlen > 40) continue;
        int slug = 1;
        int words = 1;
        for (size_t i = 0; i < nlen; i++) {
            char ch = q[5 + i];
            if (ch == ' ') { words++; continue; }
            if (ch >= 'a' && ch <= 'z') continue;
            if (ch >= '0' && ch <= '9') continue;
            if (ch == '-' || ch == '_' || ch == '/') continue;
            slug = 0;
            break;
        }
        /* a section the run names is a short slug, not a sentence */
        if (!slug || words > 3) continue;
        if (!sec) sec = q + 5;    /* the first one is the phase the run is in */
    }
    if (sec) {
        char name[OV_TEXT];
        ov_copy_to_eol(name, sizeof name, sec);
        /* "--- preflight ---" -> "preflight" */
        char *dash = strstr(name, " ---");
        if (dash) *dash = '\0';
        st_trim(name);
        if (name[0]) st_copy(s->phase, sizeof s->phase, name);
    }
    return 1;
}

static void ov_panel_steam(OvFrame *f) {
    OvSteam *s = &f->steam;
    memset(s, 0, sizeof *s);

    /* The steam workers keep their rolling logs next to the others.  A log only
     * carries session data once its run has started, so a log with a run block is
     * preferred over a merely newer one; among those the newest wins. */
    static const char *candidates[] = { "steamcoord.log", "steamdeath.log", "steamsettings.log" };
    char path[OV_PATH * 2];
    OvSteam best;
    int have_best = 0;

    for (size_t i = 0; i < sizeof candidates / sizeof candidates[0]; i++) {
        snprintf(path, sizeof path, "%s/%s", ov_config.logs_dir, candidates[i]);
        if (!path_exists(path)) continue;
        long long mt = file_mtime(path);
        if (mt <= 0) continue;

        /* read the tail only: the log is large and grows while we watch */
        Buf tail = { 0 };
        file_tail(path, 1024 * 1024, &tail);
        OvSteam cur;
        memset(&cur, 0, sizeof cur);
        int has_run = ov_parse_steam_log(tail.data ? tail.data : "", &cur);
        buf_free(&tail);
        if (!has_run) continue;

        cur.log_age_s = now_s() - mt;
        st_copy(cur.log, sizeof cur.log, candidates[i]);
        if (!have_best || cur.log_age_s < best.log_age_s) {
            best = cur;
            have_best = 1;
        }
    }

    if (!have_best) {
        snprintf(s->err, sizeof s->err, "no steam session running (no run block in the steam logs)");
        return;
    }
    *s = best;
    if (!s->host[0]) st_copy(s->host, sizeof s->host, "this machine");
    if (!s->client[0]) st_copy(s->client, sizeof s->client, "the second machine");
    s->ok = 1;
}

/* ================= panel: reports and findings ================= */

static int ov_cmp_report(const void *a, const void *b) {
    const OvReport *x = a, *y = b;
    if (x->age_s < y->age_s) return -1;
    if (x->age_s > y->age_s) return 1;
    return 0;
}

static void ov_panel_reports(OvFrame *f) {
    f->report_count = 0;
    DIR *d = opendir(ov_config.results_dir);
    if (!d) return;
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        size_t nl = strlen(e->d_name);
        if (nl < 3 || !st_ends(e->d_name, ".md")) continue;
        if (f->report_count >= OV_MAX_REPORTS) break;
        char path[OV_PATH * 2];
        snprintf(path, sizeof path, "%s/%s", ov_config.results_dir, e->d_name);
        long long sz = file_size(path);
        long long mt = file_mtime(path);
        if (sz < 0 || mt <= 0) continue;
        OvReport *r = &f->reports[f->report_count];
        memset(r, 0, sizeof *r);
        st_copy(r->name, sizeof r->name, e->d_name);
        r->size = sz;
        r->age_s = now_s() - mt;

        /* the first heading of the report, as its summary */
        Buf head = { 0 };
        if (file_read(path, &head, 4096)) {
            const char *nl2 = strchr(head.data ? head.data : "", '\n');
            size_t n = nl2 ? (size_t)(nl2 - head.data) : strlen(head.data ? head.data : "");
            char line[OV_TEXT];
            if (n >= sizeof line) n = sizeof line - 1;
            memcpy(line, head.data ? head.data : "", n);
            line[n] = '\0';
            st_trim(line);
            if (!line[0]) st_copy(line, sizeof line, "(no heading)");
            st_copy(r->head, sizeof r->head, line);
        }
        buf_free(&head);
        f->report_count++;
    }
    closedir(d);
    qsort(f->reports, (size_t)f->report_count, sizeof f->reports[0], ov_cmp_report);
}

/* ================= panel: the worker logs ================= */

static int ov_cmp_log(const void *a, const void *b) {
    const OvLog *x = a, *y = b;
    if (x->age_s < y->age_s) return -1;
    if (x->age_s > y->age_s) return 1;
    return 0;
}

static void ov_panel_logs(OvFrame *f) {
    f->log_count = 0;
    DIR *d = opendir(ov_config.logs_dir);
    if (!d) return;
    struct dirent *e;
    while ((e = readdir(d)) != NULL) {
        if (!st_ends(e->d_name, ".log")) continue;
        char path[OV_PATH * 2];
        snprintf(path, sizeof path, "%s/%s", ov_config.logs_dir, e->d_name);
        long long mt = file_mtime(path);
        if (mt <= 0) continue;
        if (f->log_count >= OV_MAX_LOGS) break;
        OvLog *l = &f->logs[f->log_count];
        memset(l, 0, sizeof *l);
        st_copy(l->name, sizeof l->name, e->d_name);
        l->size = file_size(path);
        l->age_s = now_s() - mt;
        f->log_count++;
    }
    closedir(d);
    qsort(f->logs, (size_t)f->log_count, sizeof f->logs[0], ov_cmp_log);
}

/* ================= panel: git ================= */

/* Read the porcelain output: the first two lines are the branch and the head, the
 * rest is one line per changed file. */
static void ov_git_from_porcelain(OvGit *g, const char *res) {
    char copy[8192]; st_copy(copy, sizeof copy, res);
    char *p = copy;
    char *l1 = strchr(p, '\n');
    if (!l1) { snprintf(g->err, sizeof g->err, "git status gave no branch"); return; }
    *l1 = '\0';
    st_copy(g->branch, sizeof g->branch, p);
    char *l2 = l1 + 1;
    char *l2e = strchr(l2, '\n');
    if (l2e) *l2e = '\0';
    st_copy(g->head, sizeof g->head, l2);
    if (l2e) {
        /* one porcelain line per changed file: " M path", "?? path", "A  path" */
        char *q = l2e + 1;
        while (*q) {
            char *nl = strchr(q, '\n');
            if (!nl) break;
            *nl = '\0';
            if (q[0] == '?') g->untracked_files++;
            else if (q[0] != ' ') g->staged_files++;
            g->dirty_files++;
            q = nl + 1;
        }
    }
}

static void ov_panel_git(OvFrame *f) {
    OvGit *g = &f->git;
    memset(g, 0, sizeof *g);

    char head[OV_PATH * 2];
    snprintf(head, sizeof head, "%s/.git", ov_config.repo_dir);
    /* a worktree's .git is a file pointing at the real git dir */
    if (!path_exists(head)) {
        Buf dotgit = { 0 };
        if (file_read(head, &dotgit, 4096) && dotgit.len) {
            buf_free(&dotgit);
        } else {
            buf_free(&dotgit);
            snprintf(g->err, sizeof g->err, "no git repository here");
            return;
        }
    }

    /* the git status forks git: cache it, but on a shorter leash than the bridge */
    const OvCacheEntry *cached = ov_cache_find(&ov_cache_global, "git");
    if (cached && cached->value && !cached->failed &&
        (now_s() - cached->at) < 10) {
        ov_git_from_porcelain(g, cached->value);
        g->ok = 1;
        return;
    }

    /* Git is used here only for a read-only status: the branch, the head and the
     * counts.  The directory is passed as an argument, never spliced into a shell
     * string, so a path with a quote in it cannot change the command. */
    char out[8192];
    {
        int n = snprintf(out, sizeof out,
                         "git --no-optional-locks -C \"$1\" rev-parse --abbrev-ref HEAD 2>/dev/null; "
                         "git --no-optional-locks -C \"$1\" rev-parse --short HEAD 2>/dev/null; "
                         "git --no-optional-locks -C \"$1\" status --porcelain 2>/dev/null");
        if (n < 0 || (size_t)n >= sizeof out) {
            snprintf(g->err, sizeof g->err, "the git command does not fit");
            return;
        }
    }
    char res[8192];
    res[0] = '\0';
    if (ov_run_shell_argv(out, res, sizeof res, 3000, ov_config.repo_dir) != 0 || !res[0]) {
        snprintf(g->err, sizeof g->err, "git status failed");
        return;
    }
    ov_cache_put(&ov_cache_global, "git", res, 0);
    ov_git_from_porcelain(g, res);
    g->ok = 1;
}

/* ================= panel: the machine ================= */

static void ov_panel_system(OvFrame *f) {
    OvSystem *s = &f->sys;
    memset(s, 0, sizeof *s);
    s->cores = (int)sysconf(_SC_NPROCESSORS_ONLN);
    if (s->cores < 1) s->cores = 1;

    FILE *lf = fopen("/proc/loadavg", "r");
    if (lf) {
        unsigned long running = 0;
        if (fscanf(lf, "%lf %lf %lf %lu", &s->load1, &s->load5, &s->load15, &running) >= 4)
            s->procs_running = (int)running;
        fclose(lf);
    }
    FILE *mf = fopen("/proc/meminfo", "r");
    if (mf) {
        char line[256];
        while (fgets(line, sizeof line, mf)) {
            long long v;
            if (sscanf(line, "MemTotal: %lld kB", &v) == 1) s->mem_total = v * 1024;
            else if (sscanf(line, "MemAvailable: %lld kB", &v) == 1) s->mem_avail = v * 1024;
        }
        fclose(mf);
    }
    if (s->mem_total > 0) s->mem_used = s->mem_total - s->mem_avail;

    /* the overview's own usage */
    {
        long long ut = 0, stt = 0;
        long long ticks = proc_cpu_ticks((int)getpid(), &ut, &stt);
        long hz = sysconf(_SC_CLK_TCK);
        if (ticks >= 0 && hz > 0) s->self_cpu_ms = ticks * 1000 / hz;
        s->self_rss_kb = proc_rss_kb((int)getpid());
    }

    struct statfs st;
    if (statfs("/", &st) == 0) {
        s->disk_total = (long long)st.f_blocks * st.f_bsize;
        s->disk_free = (long long)st.f_bavail * st.f_bsize;
        s->disk_used = s->disk_total - (long long)st.f_bfree * st.f_bsize;
    }
}

/* ================= admission / memory guard (observation only) ================= */

/* The paths are derived from the running user, not pinned to one UID: the tool summarises the fleet, and
 * the second test machine runs a different account. A pinned path would watch the wrong cgroup (or none)
 * there and would report the host as healthy while it was not. $XDG_RUNTIME_DIR is the user's own runtime
 * directory; the cgroup path is built from the same UID. */
static const char *ov_admission_dir(void) {
    static char dir[PATH_MAX];
    if (!dir[0]) {
        const char *xdg = getenv("XDG_RUNTIME_DIR");
        const char *uid = xdg && *xdg ? strrchr(xdg, '/') : NULL;
        if (uid && uid[1]) snprintf(dir, sizeof dir, "%s/tld-admission", xdg);
        else snprintf(dir, sizeof dir, "/run/user/%lu/tld-admission", (unsigned long)getuid());
    }
    return dir;
}

static const char *ov_watched_cgroup(void) {
    static char path[PATH_MAX];
    if (!path[0]) {
        const char *env = getenv("TLD_ADMIT_WATCHED_CGROUP");
        if (env && *env) { snprintf(path, sizeof path, "%s", env); return path; }
        snprintf(path, sizeof path,
                 "/sys/fs/cgroup/user.slice/user-%lu.slice/user@%lu.service/session.slice/wayland-wm@hyprland.desktop.service",
                 (unsigned long)getuid(), (unsigned long)getuid());
    }
    return path;
}

/* All external labels pass this boundary: no terminal controls, assignments or
 * credential values from a job name/reason, window title or runtime manifest. */
static void ov_safe_text(char *out, size_t n, const char *text) {
    st_copy(out, n, text);
    for (char *p = out; *p; p++)
        if ((unsigned char)*p < 32 || (unsigned char)*p == 127) *p = ' ';
    for (char *p = out; *p; p++) {
        if (*p == '=' || !strncasecmp(p, "bearer ", 7)) {
            st_copy(p, n - (size_t)(p - out), "[redacted]");
            break;
        }
        if (*p != ':') continue;
        char *key = p;
        while (key > out && (isspace((unsigned char)key[-1]) || key[-1] == '"')) key--;
        char *end = key;
        while (key > out && (isalnum((unsigned char)key[-1]) || strchr("_.-", key[-1]))) key--;
        if (looks_secret_key(key, (size_t)(end - key))) {
            st_copy(p + 1, n - (size_t)(p + 1 - out), "[redacted]");
            break;
        }
    }
}

static int ov_json_text(const char *text, const char *key, char *out, size_t n) {
    Buf b = { 0 };
    int ok = json_str(&b, text, key);
    if (ok) ov_safe_text(out, n, b.data ? b.data : "");
    buf_free(&b);
    return ok;
}

static int ov_json_ll(const char *text, const char *key, long long *out) {
    const char *p = json_find(text, key);
    if (!p) return 0;
    errno = 0;
    char *end;
    long long v = strtoll(p, &end, 10);
    if (errno || end == p || (*end && !isspace((unsigned char)*end) && *end != ',' && *end != '}')) return 0;
    *out = v;
    return 1;
}

static int ov_status_value(const char *text, const char *key, char *out, size_t n) {
    size_t kl = strlen(key);
    for (const char *p = text ? text : ""; *p;) {
        const char *end = strchr(p, '\n');
        size_t len = end ? (size_t)(end - p) : strlen(p);
        if (len > kl && !memcmp(p, key, kl) && p[kl] == ':') {
            const char *v = p + kl + 1;
            while (v < p + len && isspace((unsigned char)*v)) v++;
            size_t take = (size_t)(p + len - v);
            if (take >= n) take = n - 1;
            memcpy(out, v, take); out[take] = 0; st_trim(out);
            return 1;
        }
        if (!end) break;
        p = end + 1;
    }
    return 0;
}

static void ov_admission_limits(const char *text, OvAdmission *a) {
    if (text) {
#define LIMIT(key, field) ov_json_text(text, key, a->field, sizeof a->field)
            LIMIT("memoryHigh", memory_high); LIMIT("memoryMax", memory_max);
            LIMIT("memorySwapMax", memory_swap_max); LIMIT("tasksMax", tasks_max);
            LIMIT("cpuQuota", cpu_quota); LIMIT("cpuWeight", cpu_weight); LIMIT("ioWeight", io_weight);
#undef LIMIT
            json_bool(&a->limits_verified, text, "verified");
            int group;
            if (json_bool(&group, text, "memoryOOMGroup"))
                st_copy(a->oom_group, sizeof a->oom_group, group ? "yes" : "no");
            long long nice;
            if (ov_json_ll(text, "nice", &nice)) snprintf(a->nice, sizeof a->nice, "%lld", nice);
        }

}

int ov_parse_admission_json(const char *text, const char *fallback, OvAdmission *a) {
    memset(a, 0, sizeof *a);
    a->has_active = a->exit_status = a->oom_killed = a->limits_verified = -1;
    a->updated_epoch = a->started_epoch = a->elapsed_s = -1;
    a->mem_available = a->swap_used = a->session_anon = a->session_swap = a->watched_oom_kills = -1;
    st_copy(a->state, sizeof a->state, "unavailable");
    if (ov_json_text(text, "state", a->state, sizeof a->state) && a->state[0]) {
        a->available = 1;
#define JTEXT(key, field) ov_json_text(text, key, a->field, sizeof a->field)
        JTEXT("pauseKind", pause_kind); JTEXT("reason", reason);
        JTEXT("cgroup", cgroup); JTEXT("name", name); JTEXT("unit", unit); JTEXT("profile", profile); JTEXT("result", result);
#undef JTEXT
        json_bool(&a->has_active, text, "active");
        json_bool(&a->oom_killed, text, "oomKilled");
        long long exit_status;
        if (ov_json_ll(text, "exitStatus", &exit_status) && exit_status >= INT_MIN && exit_status <= INT_MAX)
            a->exit_status = (int)exit_status;
        ov_json_ll(text, "updatedEpoch", &a->updated_epoch);
        ov_json_ll(text, "startedEpoch", &a->started_epoch);
        ov_json_ll(text, "elapsedSeconds", &a->elapsed_s);
        ov_json_ll(text, "memAvailable", &a->mem_available);
        ov_json_ll(text, "swapUsed", &a->swap_used);
        ov_json_ll(text, "sessionAnon", &a->session_anon);
        ov_json_ll(text, "sessionSwap", &a->session_swap);
        ov_json_ll(text, "watchedOOMKills", &a->watched_oom_kills);
        Buf limits = {0};
        if (json_obj(&limits,text,"limits")) ov_admission_limits(limits.data,a);
        buf_free(&limits);
    } else {
        char value[512];
        if (!ov_status_value(fallback, "state", value, sizeof value) || !value[0]) {
            st_copy(a->reason, sizeof a->reason, "broker state unavailable");
            return 0;
        }
        ov_safe_text(a->state, sizeof a->state, value);
        a->available = 1;
#define STATUS(key, field) do { if (ov_status_value(fallback, key, value, sizeof value)) ov_safe_text(a->field, sizeof a->field, value); } while (0)
        STATUS("pause", pause_kind); STATUS("reason", reason); STATUS("name", name);
        STATUS("cgroup", cgroup); STATUS("profile", profile); STATUS("unit", unit); STATUS("result", result);
#undef STATUS
        if (ov_status_value(fallback, "active", value, sizeof value))
            a->has_active = !strcmp(value, "yes") ? 1 : !strcmp(value, "no") ? 0 : -1;
        if (ov_status_value(fallback, "OOM", value, sizeof value))
            a->oom_killed = !strcmp(value, "yes") ? 1 : !strcmp(value, "no") ? 0 : -1;
        struct { const char *key; long long *target; } nums[] = {
            {"updated", &a->updated_epoch}, {"started", &a->started_epoch}, {"elapsed", &a->elapsed_s},
            {"MemAvailable", &a->mem_available}, {"global swap", &a->swap_used},
            {"session anon", &a->session_anon}, {"session swap", &a->session_swap},
            {"watched OOM kills", &a->watched_oom_kills}
        };
        for (size_t i = 0; i < sizeof nums / sizeof nums[0]; i++) {
            if (!ov_status_value(fallback, nums[i].key, value, sizeof value)) continue;
            char *end; errno = 0;
            long long v = strtoll(value, &end, 10);
            if (!errno && end != value && (!*end || !strcmp(end, "s") || !strcmp(end, " bytes"))) *nums[i].target = v;
        }
        if (ov_status_value(fallback, "limits", value, sizeof value)) ov_admission_limits(value,a);
        if (ov_status_value(fallback, "exit", value, sizeof value)) {
            char *end; errno = 0;
            long v = strtol(value, &end, 10);
            if (!errno && end != value && !*end && v >= INT_MIN && v <= INT_MAX) a->exit_status = (int)v;
        }
    }
    return 1;
}

/* Exact line keys and units: file/anon sub-counters and oom_group_kill must not
 * match. Missing readings remain unknown rather than suggesting zero pressure. */
static int ov_counter(const char *text, const char *key, const char *unit, long long *out) {
    for (const char *p = text ? text : ""; *p;) {
        const char *end = strchr(p, '\n');
        if (!end) end = p + strlen(p);
        size_t kl = strlen(key);
        if ((size_t)(end - p) > kl && !memcmp(p, key, kl) && isspace((unsigned char)p[kl])) {
            const char *v = p + kl;
            while (v < end && isspace((unsigned char)*v)) v++;
            char *tail; errno = 0;
            long long num = strtoll(v, &tail, 10);
            if (errno || tail == v || num < 0) return 0;
            while (tail < end && isspace((unsigned char)*tail)) tail++;
            if (unit && *unit) {
                size_t ul = strlen(unit);
                if ((size_t)(end - tail) < ul || memcmp(tail, unit, ul)) return 0;
                tail += ul;
                while (tail < end && isspace((unsigned char)*tail)) tail++;
            }
            if (tail != end) return 0;
            *out = num; return 1;
        }
        p = *end ? end + 1 : end;
    }
    return 0;
}

int ov_parse_memory_stat(const char *text, long long *anon, long long *file_mem, long long *swap) {
    *anon = *file_mem = *swap = -1;
    int a = ov_counter(text, "anon", "", anon);
    int f = ov_counter(text, "file", "", file_mem);
    ov_counter(text, "swap", "", swap); /* on v2, memory.swap.current is read separately */
    return a && f;
}
int ov_parse_memory_events_oom_kills(const char *text, long long *kills) {
    *kills = -1; return ov_counter(text, "oom_kill", "", kills);
}
int ov_parse_meminfo_available(const char *text, long long *available) {
    *available = -1;
    long long kb;
    if (!ov_counter(text, "MemAvailable:", "kB", &kb) || kb > LLONG_MAX / 1024) return 0;
    *available = kb * 1024; return 1;
}
int ov_parse_meminfo_swap(const char *text, long long *total, long long *free_swap) {
    *total = *free_swap = -1;
    long long t, f;
    if (!ov_counter(text, "SwapTotal:", "kB", &t) || !ov_counter(text, "SwapFree:", "kB", &f) ||
        t > LLONG_MAX / 1024 || f > t) return 0;
    *total = t * 1024; *free_swap = f * 1024; return 1;
}
int ov_parse_memory_psi(const char *text, double *some, double *full) {
    *some = *full = -1;
    for (const char *p = text ? text : ""; *p;) {
        double v;
        if (sscanf(p, "some avg10=%lf", &v) == 1 && v >= 0 && v <= 100) *some = v;
        if (sscanf(p, "full avg10=%lf", &v) == 1 && v >= 0 && v <= 100) *full = v;
        const char *end = strchr(p, '\n');
        if (!end) break;
        p = end + 1;
    }
    return *some >= 0 && *full >= 0;
}
long long ov_oom_delta(long long current, long long baseline) {
    return current < 0 || baseline < 0 || current < baseline ? -1 : current - baseline;
}

/* Hyprland JSON arrays contain nested objects and quoted braces in titles. */
static int ov_json_array_start(const char *text, const char **first) {
    const char *p = text ? text : "";
    while (isspace((unsigned char)*p)) p++;
    const char *end = json_value_end(p);
    if (*p != '[' || !end) return 0;
    while (isspace((unsigned char)*end)) end++;
    if (*end) return 0;
    *first = p + 1; return 1;
}
static const char *ov_json_array_object(const char *p, Buf *object) {
    while (isspace((unsigned char)*p) || *p == ',') p++;
    if (*p != '{') return NULL;
    const char *end = json_value_end(p);
    if (!end) return NULL;
    buf_reset(object); buf_add(object, p, (size_t)(end - p));
    return end;
}
int ov_parse_hypr_clients(const char *text, OvWindow *windows, int *count, int max, int *exposed) {
    *count = 0; *exposed = -1; /* clients alone cannot establish exposure */
    const char *p;
    if (!ov_json_array_start(text, &p)) return 0;
    Buf obj = { 0 }, ws = { 0 };
    int total = 0;
    while ((p = ov_json_array_object(p, &obj))) {
        OvWindow w = {.floating=-1, .fullscreen=-1, .focused=-1};
        ov_json_text(obj.data, "class", w.app_class, sizeof w.app_class);
        ov_json_text(obj.data, "title", w.title, sizeof w.title);
        char initial[128] = { 0 };
        ov_json_text(obj.data, "initialClass", initial, sizeof initial);
        if (!st_has_ci(w.app_class, "thelongdrive") && !st_has_ci(initial, "thelongdrive") &&
            strcmp(w.app_class, "steam_app_1017180") && !st_has_ci(w.title, "the long drive")) continue;
        total++;
        if (*count >= max) continue;
        if (json_obj(&ws, obj.data, "workspace")) ov_json_text(ws.data, "name", w.workspace, sizeof w.workspace);
        json_bool(&w.floating, obj.data, "floating");
        long long fullscreen;
        if (ov_json_ll(obj.data, "fullscreen", &fullscreen)) w.fullscreen = fullscreen != 0;
        long long focus;
        if (ov_json_ll(obj.data, "focusHistoryID", &focus)) w.focused = focus == 0;
        w.special_exposed = -1;
        windows[(*count)++] = w;
    }
    buf_free(&obj); buf_free(&ws);
    return total;
}
int ov_parse_hypr_monitors(const char *text, int *exposed) {
    *exposed = -1;
    const char *p;
    if (!ov_json_array_start(text, &p)) return 0;
    *exposed = 0;
    Buf obj = { 0 }, ws = { 0 };
    while ((p = ov_json_array_object(p, &obj))) {
        if (!json_obj(&ws, obj.data, "specialWorkspace")) continue;
        char name[64] = { 0 };
        ov_json_text(ws.data, "name", name, sizeof name);
        if (!strcmp(name, "special:tld-tests")) *exposed = 1;
    }
    buf_free(&obj); buf_free(&ws); return 1;
}
int ov_parse_adb_devices(const char *text, char *out, size_t n) {
    int ready = 0, offline = 0, unauthorized = 0, denied = 0;
    for (const char *p = text ? text : ""; *p;) {
        const char *end = strchr(p, '\n');
        if (!end) end = p + strlen(p);
        char line[512], serial[256], state[64];
        size_t len = (size_t)(end - p);
        if (len < sizeof line) {
            memcpy(line, p, len); line[len] = 0;
            if (sscanf(line, "%255s %63s", serial, state) == 2) {
                if (!strcmp(state, "device")) ready++;
                else if (!strcmp(state, "offline")) offline++;
                else if (!strcmp(state, "unauthorized")) unauthorized++;
                else if (!strcmp(state, "no") && st_has(line, "no permissions")) denied++;
            }
        }
        p = *end ? end + 1 : end;
    }
    snprintf(out, n, "%d ready, %d unauthorized, %d offline, %d no permissions", ready, unauthorized, offline, denied);
    return ready + unauthorized + offline + denied;
}

static int ov_read(const char *path, Buf *b) { return file_read(path, b, 32768) && b->len > 0; }
static long long ov_read_counter(const char *path) {
    Buf b = { 0 }, line = { 0 };
    long long value = -1;
    if (ov_read(path, &b)) {
        buf_printf(&line, "value %s", b.data);
        ov_counter(line.data, "value", "", &value);
    }
    buf_free(&b); buf_free(&line); return value;
}

static void ov_panel_admission(OvFrame *f) {
    Buf json = { 0 }, status = { 0 };
    char apath[PATH_MAX + 64];
    snprintf(apath, sizeof apath, "%s/state.json", ov_admission_dir());
    ov_read(apath, &json);
    snprintf(apath, sizeof apath, "%s/status.txt", ov_admission_dir());
    if (!ov_read(apath, &status)) {
        snprintf(apath, sizeof apath, "%s/state.txt", ov_admission_dir());
        if (!ov_read(apath, &status)) { snprintf(apath, sizeof apath, "%s/status", ov_admission_dir()); ov_read(apath, &status); }
    }
    ov_parse_admission_json(json.data, status.data, &f->admission);
    buf_free(&json); buf_free(&status);
}
static void ov_panel_pressure(OvFrame *f) {
    OvPressure *p = &f->pressure;
    p->anon = p->file = p->swap = p->mem_avail = p->global_swap = p->oom_kills = -1;
    p->global_anon = p->global_file = -1;
    p->global_some = p->global_full = p->session_some = p->session_full = -1;
    Buf b = { 0 };
    if (ov_read("/proc/meminfo", &b)) {
        ov_parse_meminfo_available(b.data, &p->mem_avail);
        long long total, free_swap, kb;
        if (ov_parse_meminfo_swap(b.data, &total, &free_swap)) p->global_swap = total - free_swap;
        if (ov_counter(b.data, "AnonPages:", "kB", &kb) && kb <= LLONG_MAX / 1024) p->global_anon = kb * 1024;
        if (ov_counter(b.data, "Cached:", "kB", &kb) && kb <= LLONG_MAX / 1024) p->global_file = kb * 1024;
    }
    char wpath[PATH_MAX + 64];
    snprintf(wpath, sizeof wpath, "%s/memory.stat", ov_watched_cgroup());
    if (ov_read(wpath, &b))
        p->ok = ov_parse_memory_stat(b.data, &p->anon, &p->file, &p->swap);
    if (!p->ok) st_copy(p->err, sizeof p->err, "watched session memory.stat unavailable/incomplete");
    snprintf(wpath, sizeof wpath, "%s/memory.swap.current", ov_watched_cgroup());
    long long swap = ov_read_counter(wpath);
    p->swap = swap; /* only memory.swap.current is authoritative */
    snprintf(wpath, sizeof wpath, "%s/memory.events", ov_watched_cgroup());
    if (ov_read(wpath, &b)) ov_parse_memory_events_oom_kills(b.data, &p->oom_kills);
    if (ov_read("/proc/pressure/memory", &b)) ov_parse_memory_psi(b.data, &p->global_some, &p->global_full);
    snprintf(wpath, sizeof wpath, "%s/memory.pressure", ov_watched_cgroup());
    if (ov_read(wpath, &b)) ov_parse_memory_psi(b.data, &p->session_some, &p->session_full);
    snprintf(wpath, sizeof wpath, "%s/oom-baseline", ov_admission_dir());
    f->new_oom_events = ov_oom_delta(p->oom_kills, ov_read_counter(wpath));
    buf_free(&b);
}
static void ov_guard_proc(int pid, const char *comm, char *cmd, size_t cmd_n, void *user) {
    (void)cmd; (void)cmd_n;
    OvFrame *f = user;
    if (!strcmp(comm, "steam")) st_copy(f->runtime.steam, sizeof f->runtime.steam, "running");
    if (st_starts(comm, "wivrn-server")) st_copy(f->runtime.wivrn, sizeof f->runtime.wivrn, "server running");
    long long rss = proc_rss_kb(pid);
    if (rss <= 0) return;
    int at = 0;
    while (at < f->consumer_count && f->consumers[at].rss_kb >= rss) at++;
    if (at >= OV_MAX_CONSUMERS) return;
    if (f->consumer_count < OV_MAX_CONSUMERS) f->consumer_count++;
    for (int i = f->consumer_count - 1; i > at; i--) f->consumers[i] = f->consumers[i - 1];
    f->consumers[at].pid = pid;
    f->consumers[at].rss_kb = rss;
    ov_safe_text(f->consumers[at].comm, sizeof f->consumers[at].comm, comm);
}

/* Fixed argv, bounded bytes and wall time, and no stderr content on the screen.
 * Kill the helper's process group on timeout so descendants cannot linger. */
static int ov_guard_command(const char *const argv[], char *out, size_t n) {
    return native_capture(argv, out, n, ov_config.probe_timeout_ms) == 0;
}
static int ov_guard_cached_command(const char *key, const char *const argv[], char *out, size_t n) {
    const OvCacheEntry *entry = ov_cache_find(&ov_cache_global, key);
    if (!entry || now_s() - entry->at >= ov_config.probe_min_interval) {
        int ok = ov_guard_command(argv, out, n);
        ov_cache_put(&ov_cache_global, key, ok ? out : "", !ok);
        return ok;
    }
    st_copy(out, n, entry->value ? entry->value : ""); return !entry->failed;
}

/* The headset module is the tool's only ADB client: it speaks the ADB server
 * protocol to an already running server and never invokes adb, so the daemon
 * can never be started from here. */
static void ov_panel_runtime(OvFrame *f) {
    st_copy(f->runtime.steam, sizeof f->runtime.steam, "not seen");
    st_copy(f->runtime.wivrn, sizeof f->runtime.wivrn, "server not seen");
    ov_scan_procs(ov_guard_proc, f);

    /* The Quest / WiVRn state.  The headset module owns the only ADB client: it
     * speaks to an already running server and never starts the daemon. */
    ov_hs_set_timeout(ov_config.probe_timeout_ms);
    ov_hs_set_interval((long long)ov_config.probe_min_interval * 1000);
    ov_headset_collect(&f->headset, f->runtime.wivrn);

    /* The device summary the runtime panel shows comes from the same listing,
     * so the tool reads the ADB server once and not twice. */
    const OvCacheEntry *cached = ov_cache_find(&ov_cache_global, "guard_adb");
    char out[32768];
    if (!cached || now_s() - cached->at >= ov_config.probe_min_interval) {
        int ok = ov_hs_devices_listing(out, sizeof out);
        ov_cache_put(&ov_cache_global, "guard_adb", ok ? out : "", !ok);
        cached = ov_cache_find(&ov_cache_global, "guard_adb");
    }
    if (cached && !cached->failed) ov_parse_adb_devices(cached->value, f->runtime.adb, sizeof f->runtime.adb);
    else st_copy(f->runtime.adb, sizeof f->runtime.adb, "server unavailable (not started)");

    /* The OpenXR line comes from the headset module, which owns the manifest
     * scan: the selected runtime and the installed ones are read in one place. */
    if (f->headset.openxr_selected)
        snprintf(f->runtime.openxr, sizeof f->runtime.openxr, "selected: %s (%d installed)",
                 f->headset.openxr_active[0] ? f->headset.openxr_active : "name unavailable",
                 f->headset.openxr_installed);
    else if (f->headset.openxr_installed)
        snprintf(f->runtime.openxr, sizeof f->runtime.openxr, "none selected, %d installed",
                 f->headset.openxr_installed);
    else
        st_copy(f->runtime.openxr, sizeof f->runtime.openxr, "no runtime installed");

    char name[64] = { 0 };
    if (f->graphics.ok && !strcmp(f->graphics.where, "this machine")) {
        char key[32]; snprintf(key, sizeof key, "guard_xr_%s", f->graphics.port);
        cached = ov_cache_find(&ov_cache_global, key);
        if (!cached || now_s() - cached->at >= ov_config.probe_min_interval) {
            int ok = ov_bridge_query(atoi(f->graphics.port), "openxr", out, sizeof out, ov_config.probe_timeout_ms) == 0;
            ov_cache_put(&ov_cache_global, key, ok ? out : "", !ok);
            cached = ov_cache_find(&ov_cache_global, key);
        }
        int started, display, input;
        if (cached && !cached->failed && json_bool(&started, cached->value, "started") &&
            json_bool(&display, cached->value, "display") && json_bool(&input, cached->value, "input")) {
            ov_json_text(cached->value, "runtime", name, sizeof name);
            snprintf(f->runtime.openxr, sizeof f->runtime.openxr, "game: %.60s started=%s display=%s input=%s", name,
                     started ? "yes" : "no", display ? "yes" : "no", input ? "yes" : "no");
        }
    }
    f->runtime.ok = 1;
}
static void ov_panel_windows(OvFrame *f) {
    f->tld_tests_exposed = -1;
    if (!getenv("HYPRLAND_INSTANCE_SIGNATURE")) {
        st_copy(f->window_err, sizeof f->window_err, "Hyprland unavailable"); return;
    }
    static const char *const clients[] = { "hyprctl", "-j", "clients", NULL };
    static const char *const monitors[] = { "hyprctl", "-j", "monitors", NULL };
    char out[131072];
    const char *first;
    if (ov_guard_cached_command("guard_clients", clients, out, sizeof out) && ov_json_array_start(out, &first))
        f->tld_window_count = ov_parse_hypr_clients(out, f->windows, &f->window_count, OV_MAX_WINDOWS, &f->tld_tests_exposed);
    else st_copy(f->window_err, sizeof f->window_err, "Hyprland clients unavailable");
    if (ov_guard_cached_command("guard_monitors", monitors, out, sizeof out))
        ov_parse_hypr_monitors(out, &f->tld_tests_exposed);
    for (int i = 0; i < f->window_count; i++)
        if (!strcmp(f->windows[i].workspace, "special:tld-tests")) f->windows[i].special_exposed = f->tld_tests_exposed;
}

static void ov_frame_labels(OvFrame *f) {
#define SAFE(v) ov_safe_text((v),sizeof(v),(v))
    for (int i = 0; i < f->agent_count; i++) {
        SAFE(f->agents[i].workspace); SAFE(f->agents[i].role); SAFE(f->agents[i].model);
    }
    for (int i = 0; i < f->game_count; i++) { SAFE(f->games[i].prefix); SAFE(f->games[i].game); }
    SAFE(f->graphics.version); SAFE(f->graphics.build); SAFE(f->graphics.quality_name); SAFE(f->graphics.fullscreen);
    SAFE(f->steam.host); SAFE(f->steam.client); SAFE(f->steam.relay); SAFE(f->steam.phase);
    SAFE(f->steam.ports); SAFE(f->steam.last_verdict); SAFE(f->steam.log);
    for (int i = 0; i < f->report_count; i++) { SAFE(f->reports[i].name); SAFE(f->reports[i].head); }
    for (int i = 0; i < f->log_count; i++) SAFE(f->logs[i].name);
    SAFE(f->git.branch); SAFE(f->git.head); SAFE(f->git.upstream);
#undef SAFE
}

/* ================= the whole frame ================= */

void ov_collect(OvFrame *f, OvCache *cache) {
    (void)cache;
    memset(f, 0, sizeof *f);
    ov_panel_admission(f);
    ov_panel_pressure(f);
    ov_panel_agents(f);
    ov_panel_games(f);
    ov_panel_graphics(f);
    ov_panel_steam(f);
    ov_panel_reports(f);
    ov_panel_logs(f);
    ov_panel_git(f);
    ov_panel_system(f);
    ov_panel_runtime(f);
    ov_panel_windows(f);
    ov_frame_labels(f);

    /* the background flag of the local game decides the background part of the policy */
    if (f->game_count > 0) {
        int bg = 1;
        for (int i = 0; i < f->game_count; i++)
            if (!f->games[i].background) { bg = 0; break; }
        f->graphics.ok_background = bg;
        f->graphics.compliant = f->graphics.ok_quality && f->graphics.ok_fps &&
                                 f->graphics.ok_vsync && f->graphics.ok_resolution && bg;
    }
}
