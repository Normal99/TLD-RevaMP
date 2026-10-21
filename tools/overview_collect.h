/* overview_collect.h - the panels tools/overview draws.  See overview_data.h. */
#ifndef OVERVIEW_COLLECT_H
#define OVERVIEW_COLLECT_H

#include "overview_data.h"
#include "overview_headset.h"

#include <stddef.h>

#define OV_CACHE_MAX 16

typedef struct {
    char   key[32];
    char  *value;
    time_t at;
    int    failed;
} OvCacheEntry;

typedef struct {
    OvCacheEntry entries[OV_CACHE_MAX];
    int          count;
} OvCache;

typedef struct {
    char workers_dir[320];
    char logs_dir[320];
    char results_dir[320];
    char repo_dir[320];
    char remote_host[64];
    char ssh_key[256];
    int  remote_enabled;
    int  bridge_port;
    int  probe_min_interval;   /* seconds between expensive probes */
    int  probe_timeout_ms;     /* 1-2 s timeouts */
    int  ssh_timeout_ms;
} OvConfig;

extern OvCache  ov_cache_global;
extern OvConfig ov_config;

void ov_config_defaults(OvConfig *c);
void ov_config_from_env(OvConfig *c);

void   ov_cache_init(OvCache *ca);
void   ov_cache_free(OvCache *ca);
int    ov_cache_fresh(const OvCache *ca, const char *key);
const OvCacheEntry *ov_cache_find(const OvCache *ca, const char *key);
void   ov_cache_put(OvCache *ca, const char *key, const char *value, int failed);

int    ov_bridge_query(int port, const char *cmd, char *out, size_t out_n, int timeout_ms);
int    ov_bridge_query_many(int port, const char *const *cmds, int ncmds, char *out, size_t out_n, int timeout_ms);
int    ov_ssh_run(const char *host, const char *key, const char *script, char *out, size_t out_n, int timeout_ms);
int    ov_run_shell(const char *cmd, char *out, size_t out_n, int timeout_ms);
int    ov_run_shell_argv(const char *cmd, char *out, size_t out_n, int timeout_ms, const char *arg);

void   ov_collect(OvFrame *f, OvCache *cache);

/* Parse the remote scan's output (one "G|pid|comm|prefix|install|tty" line per
 * game process).  Exposed so the self-test can check it without a second machine. */
int    ov_parse_remote_games(const char *text, OvGame *games, int *count, int max);

/* Parse a steam worker's log into the session panel.  Returns 1 when the log held
 * a run block, 0 when it did not. */
int    ov_parse_steam_log(const char *text, OvSteam *s);

/* Admission and environment collection.  These are used by overview and its
 * self-test.  They only parse text or read state; they never call the broker. */
int ov_parse_admission_json(const char *text, const char *fallback_text, OvAdmission *a);
int ov_parse_memory_stat(const char *text, long long *anon, long long *file_mem, long long *swap);
int ov_parse_memory_events_oom_kills(const char *text, long long *kills);
int ov_parse_meminfo_available(const char *text, long long *available);
int ov_parse_meminfo_swap(const char *text, long long *swap_total, long long *swap_free);
int ov_parse_hypr_clients(const char *text, OvWindow *windows, int *count, int max, int *tld_tests_exposed);
int ov_parse_memory_psi(const char *text, double *some, double *full);
long long ov_oom_delta(long long current, long long baseline);
int ov_parse_hypr_monitors(const char *text, int *tld_tests_exposed);
int ov_parse_adb_devices(const char *text, char *out, size_t out_n);

#endif /* OVERVIEW_COLLECT_H */
