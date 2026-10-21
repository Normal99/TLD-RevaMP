/* tld_admit_core.h - pure policy and text helpers for tools/tld-admit. */
#ifndef TLD_ADMIT_CORE_H
#define TLD_ADMIT_CORE_H

#include <stddef.h>

#define ADMIT_PATH_MAX 512
#define ADMIT_TEXT_MAX 256

typedef enum {
    ADMIT_PROFILE_INVALID = 0,
    ADMIT_PROFILE_AGENT = 1,
    ADMIT_PROFILE_TEST = 2,
    ADMIT_PROFILE_GAME = 3,
    ADMIT_PROFILE_VR = 4
} AdmitProfile;

typedef struct {
    long long mem_total;
    long long mem_available;
    long long swap_total;
    long long swap_used;
    long long session_anon;
    long long session_swap;
    long long oom_kills;
    long long session_file, session_shmem;
    long long global_oom_kills;
    /* Hundredths of a percent, avg10; -1 means unavailable. */
    long long psi_some, psi_full, session_psi_some, session_psi_full;
} AdmitReading;

typedef struct {
    int threshold_paused;
    int all_reading_ok;
    int psi_pending;
    int recovery_ready;
    char reason[ADMIT_TEXT_MAX];
} AdmitDecision;

typedef struct {
    const char *name;
    const char *memory_high;
    const char *memory_max;
    const char *memory_swap_max;
    const char *tasks_max;
    const char *cpu_quota;
    const char *cpu_weight;
    const char *io_weight;
    unsigned nice;
    int oom_group;
    long long high_bytes, max_bytes, swap_bytes, launch_available;
    const char *quota_systemd;
} AdmitProfileSpec;

const AdmitProfileSpec *admit_profile(AdmitProfile p);
AdmitProfile admit_profile_parse(const char *s);
const char *admit_profile_name(AdmitProfile p);

int admit_validate_name(const char *name, char *err, size_t err_n);
void admit_safe_unit_suffix(const char *name, char *out, size_t out_n);

AdmitDecision admit_evaluate(const AdmitReading *r);
int admit_launch_ready(const AdmitReading *r, AdmitProfile profile);
int admit_auto_resume_ready(const AdmitReading *r, long long healthy_since_epoch, long long now_epoch,
                            long long stable_seconds);

void admit_json_escape(const char *in, char *out, size_t out_n);
int admit_json_str(const char *json, const char *key, char *out, size_t out_n);
int admit_json_ll(const char *json, const char *key, long long *out);
int admit_parse_memory_stat(const char *text, long long *anon, long long *file, long long *shmem);
int admit_parse_psi(const char *text, long long *some, long long *full);
int admit_parse_events_oom_kill(const char *text, long long *kills);
int admit_parse_meminfo(const char *text, long long *total, long long *available,
                        long long *swap_total, long long *swap_free);
int admit_parse_scalar(const char *text, long long *out);
int admit_parse_counter(const char *text, const char *key, const char *unit, long long *out);

#endif
