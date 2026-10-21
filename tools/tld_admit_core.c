/* tld_admit_core.c - deterministic policy helpers for the admission broker. */
#define _GNU_SOURCE
#include "tld_admit_core.h"
#include "overview_core.h"
#include <errno.h>
#include <limits.h>

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

#define GIB (1024LL * 1024LL * 1024LL)

static const AdmitProfileSpec AGENT = {
    "agent", "1536M", "2G", "1G", "256", "200%", "50", "50", 10, 1, 1536LL * 1024 * 1024, 2 * GIB, GIB, 9 * GIB, "2s"
};
static const AdmitProfileSpec TEST = {
    "test", "1536M", "2G", "1G", "256", "200%", "50", "50", 10, 1, 1536LL * 1024 * 1024, 2 * GIB, GIB, 9 * GIB, "2s"
};
static const AdmitProfileSpec GAME = {
    "game", "8G", "10G", "2G", "512", "300%", "100", "100", 0, 1,
    8 * GIB, 10 * GIB, 2 * GIB, 12 * GIB, "3s"
};

static const AdmitProfileSpec VR = {
    "vr", "10G", "12G", "4G", "768", "300%", "100", "100", 0, 1,
    10 * GIB, 12 * GIB, 4 * GIB, 26 * GIB, "3s"
};

const AdmitProfileSpec *admit_profile(AdmitProfile p) {
    if (p == ADMIT_PROFILE_AGENT) return &AGENT;
    if (p == ADMIT_PROFILE_TEST) return &TEST;
    if (p == ADMIT_PROFILE_GAME) return &GAME;
    if (p == ADMIT_PROFILE_VR) return &VR;
    return NULL;
}

AdmitProfile admit_profile_parse(const char *s) {
    if (!s) return ADMIT_PROFILE_INVALID;
    if (!strcmp(s, "agent")) return ADMIT_PROFILE_AGENT;
    if (!strcmp(s, "test")) return ADMIT_PROFILE_TEST;
    if (!strcmp(s, "game")) return ADMIT_PROFILE_GAME;
    if (!strcmp(s, "vr")) return ADMIT_PROFILE_VR;
    return ADMIT_PROFILE_INVALID;
}

const char *admit_profile_name(AdmitProfile p) {
    const AdmitProfileSpec *s = admit_profile(p);
    return s ? s->name : "invalid";
}

int admit_validate_name(const char *name, char *err, size_t err_n) {
    if (err && err_n) err[0] = '\0';
    if (!name || !*name) {
        if (err) snprintf(err, err_n, "--name is required");
        return 0;
    }
    size_t n = strlen(name);
    if (n > 64) {
        if (err) snprintf(err, err_n, "--name is longer than 64 bytes");
        return 0;
    }
    for (size_t i = 0; i < n; i++) {
        unsigned char c = (unsigned char)name[i];
        if (!(isalnum(c) || c == '.' || c == '_' || c == '-')) {
            if (err) snprintf(err, err_n, "--name may contain only letters, digits, '.', '_' and '-'");
            return 0;
        }
    }
    if (name[0] == '.' || name[n - 1] == '.') {
        if (err) snprintf(err, err_n, "--name may not start or end with '.'");
        return 0;
    }
    return 1;
}

void admit_safe_unit_suffix(const char *name, char *out, size_t out_n) {
    if (!out || !out_n) return;
    size_t j = 0;
    for (size_t i = 0; name && name[i] && j + 1 < out_n; i++) {
        unsigned char c = (unsigned char)name[i];
        if (isalnum(c) || c == '-' || c == '_') out[j++] = (char)c;
        else if (c == '.') out[j++] = '-';
        else out[j++] = '_';
    }
    while (j > 0 && (out[j - 1] == '-' || out[j - 1] == '_')) j--;
    out[j] = '\0';
    if (!j && out_n > 2) strcpy(out, "job");
}

AdmitDecision admit_evaluate(const AdmitReading *r) {
    AdmitDecision d = {0};
    if (!r || r->mem_total <= 0 || r->mem_available < 0 || r->mem_available > r->mem_total ||
        r->swap_total < 0 || r->swap_used < 0 || r->swap_used > r->swap_total ||
        r->session_anon < 0 || r->session_swap < 0 || r->oom_kills < 0 ||
        r->session_file < 0 || r->session_shmem < 0 || r->session_shmem > r->session_file ||
        r->global_oom_kills < 0 || r->psi_some < 0 || r->psi_some > 10000 ||
        r->psi_full < 0 || r->psi_full > r->psi_some || r->session_psi_some < 0 ||
        r->session_psi_some > 10000 || r->session_psi_full < 0 || r->session_psi_full > r->session_psi_some) {
        snprintf(d.reason, sizeof d.reason, "memory readings unavailable");
        return d;
    }
    d.all_reading_ok = 1;
    long long free_swap = r->swap_total - r->swap_used;
    long long stop_swap = r->swap_total / 20; /* 5%, minimum 2 GiB */
    long long resume_swap = r->swap_total / 10;
    if (stop_swap < 2 * GIB) stop_swap = 2 * GIB;
    if (resume_swap < 4 * GIB) resume_swap = 4 * GIB;
    long long some = r->psi_some > r->session_psi_some ? r->psi_some : r->session_psi_some;
    long long full = r->psi_full > r->session_psi_full ? r->psi_full : r->session_psi_full;
    /* file includes shmem: never treat shared anonymous pages as reclaimable cache.
     * This corroborating signal alone cannot pause on a large resident session. */
    int working_set_risk = r->session_anon >= r->session_file - r->session_shmem &&
        r->session_swap > 0 && r->mem_available < 10 * GIB && free_swap < 8 * GIB;
    if (r->mem_available < 6 * GIB)
        snprintf(d.reason, sizeof d.reason, "MemAvailable below 6 GiB reserve");
    else if (free_swap < stop_swap)
        snprintf(d.reason, sizeof d.reason, "remaining swap below 2 GiB / 5%% reserve");
    else if (working_set_risk)
        snprintf(d.reason, sizeof d.reason, "session anon dominates cache with low memory and remaining swap");
    else if (some >= 2500 || full >= 1000)
        snprintf(d.reason, sizeof d.reason, "severe memory PSI (some >=25%% or full >=10%%)");
    d.threshold_paused = d.reason[0] != 0;
    d.psi_pending = !d.threshold_paused && (some >= 1000 || full >= 200);
    if (d.psi_pending) snprintf(d.reason, sizeof d.reason, "memory PSI elevated; 5 second debounce (admission held)");
    d.recovery_ready = !d.threshold_paused && !d.psi_pending && r->mem_available >= 8 * GIB &&
        free_swap >= resume_swap && some < 500 && full < 100;
    return d;
}

int admit_launch_ready(const AdmitReading *r, AdmitProfile profile) {
    const AdmitProfileSpec *s = admit_profile(profile);
    AdmitDecision d = admit_evaluate(r);
    /* Reserve RAM cap plus swap cap up front: zram may compress poorly.
     * VR additionally leaves 10 GiB for compositor/encoder and other processes. */
    return s && d.all_reading_ok && !d.threshold_paused && !d.psi_pending &&
        r->mem_available >= s->launch_available &&
        r->swap_total - r->swap_used >= s->swap_bytes + 4 * GIB;
}

int admit_auto_resume_ready(const AdmitReading *r, long long healthy_since_epoch, long long now_epoch,
                            long long stable_seconds) {
    if (healthy_since_epoch <= 0 || now_epoch < healthy_since_epoch) return 0;
    AdmitDecision d = admit_evaluate(r);
    return stable_seconds > 0 && d.all_reading_ok && d.recovery_ready && now_epoch - healthy_since_epoch >= stable_seconds;
}

void admit_json_escape(const char *in, char *out, size_t out_n) {
    if (!out || !out_n) return;
    size_t j = 0;
    for (const unsigned char *p = (const unsigned char *)(in ? in : ""); *p && j + 1 < out_n; p++) {
        char tmp[8];
        const char *c = NULL;
        switch (*p) {
        case '"': c = "\\\""; break;
        case '\\': c = "\\\\"; break;
        case '\n': c = "\\n"; break;
        case '\r': c = "\\r"; break;
        case '\t': c = "\\t"; break;
        default:
            if (*p < 0x20) { snprintf(tmp, sizeof tmp, "\\u%04x", *p); c = tmp; }
            break;
        }
        if (c) {
            size_t n = strlen(c);
            if (j + n >= out_n) break;
            memcpy(out + j, c, n); j += n;
        } else {
            out[j++] = (char)*p;
        }
    }
    out[j] = '\0';
}

int admit_json_str(const char *json, const char *key, char *out, size_t out_n) {
    if (!out || !out_n) return 0;
    out[0] = 0;
    Buf b = {0};
    int ok = json_str(&b, json, key) && b.len < out_n;
    if (ok) memcpy(out, b.data ? b.data : "", b.len + 1);
    buf_free(&b);
    return ok;
}

int admit_json_ll(const char *json, const char *key, long long *out) {
    const char *p = json_find(json, key);
    if (!p || (*p != '-' && !isdigit((unsigned char)*p))) return 0;
    errno = 0;
    char *end;
    long long value = strtoll(p, &end, 10);
    if (errno || end == p || (*end && !isspace((unsigned char)*end) && *end != ',' && *end != '}')) return 0;
    *out = value;
    return 1;
}

int admit_parse_counter(const char *text, const char *key, const char *unit, long long *out) {
    *out = -1;
    int found = 0;
    long long parsed = -1;
    for (const char *p = text ? text : ""; *p;) {
        const char *end = strchr(p, '\n');
        if (!end) end = p + strlen(p);
        size_t kl = strlen(key);
        if ((size_t)(end - p) > kl && !memcmp(p, key, kl) && isspace((unsigned char)p[kl])) {
            const char *v = p + kl;
            while (v < end && isspace((unsigned char)*v)) v++;
            if (!isdigit((unsigned char)*v)) return 0;
            char *tail;
            errno = 0;
            long long value = strtoll(v, &tail, 10);
            if (errno || tail == v || value < 0) return 0;
            while (tail < end && isspace((unsigned char)*tail)) tail++;
            size_t ul = strlen(unit);
            if ((size_t)(end - tail) < ul || memcmp(tail, unit, ul)) return 0;
            tail += ul;
            while (tail < end && isspace((unsigned char)*tail)) tail++;
            if (tail != end) return 0;
            if (found++) return 0;
            parsed = value;
        }
        p = *end ? end + 1 : end;
    }
    if (found) *out = parsed;
    return found == 1;
}

int admit_parse_scalar(const char *text, long long *out) {
    *out = -1;
    if (!text || !isdigit((unsigned char)*text)) return 0;
    char *end; errno = 0;
    long long v = strtoll(text, &end, 10);
    if (errno) return 0;
    while (isspace((unsigned char)*end)) end++;
    if (*end) return 0;
    *out = v; return 1;
}

int admit_parse_memory_stat(const char *text, long long *anon, long long *file, long long *shmem) {
    int a = admit_parse_counter(text, "anon", "", anon);
    int f = admit_parse_counter(text, "file", "", file);
    int h = admit_parse_counter(text, "shmem", "", shmem);
    return a && f && h && *shmem <= *file;
}

/* Strict kernel PSI format: fixed point avoids locale, NaN and infinity.
 * Validate all fields, even averages not used by the policy. */
static int psi_percent(const char **cursor, long long *out) {
    const char *p = *cursor;
    long long v = 0;
    int digits = 0;
    while (isdigit((unsigned char)*p)) {
        if (++digits > 3) return 0;
        v = v * 10 + *p++ - '0';
    }
    if (!digits || *p++ != '.' || !isdigit((unsigned char)p[0]) || !isdigit((unsigned char)p[1])) return 0;
    v = v * 100 + (p[0] - '0') * 10 + p[1] - '0';
    p += 2;
    if (v > 10000 || *p != ' ') return 0;
    *out = v; *cursor = p + 1; return 1;
}
int admit_parse_psi(const char *text, long long *some, long long *full) {
    *some = *full = -1;
    const char *p = text ? text : "";
    long long values[2][3];
    for (int row = 0; row < 2; row++) {
        const char *prefix = row ? "full " : "some ";
        if (strncmp(p, prefix, 5)) return 0;
        p += 5;
        const char *keys[] = {"avg10=", "avg60=", "avg300="};
        for (int i = 0; i < 3; i++) {
            size_t n = strlen(keys[i]);
            if (strncmp(p, keys[i], n)) return 0;
            p += n;
            if (!psi_percent(&p, &values[row][i])) return 0;
        }
        if (strncmp(p, "total=", 6)) return 0;
        p += 6;
        if (!isdigit((unsigned char)*p)) return 0;
        char *end; errno = 0; strtoll(p, &end, 10);
        if (errno || (*end != '\n' && *end)) return 0;
        p = *end ? end + 1 : end;
    }
    if (*p) return 0;
    for (int i = 0; i < 3; i++) if (values[1][i] > values[0][i]) return 0;
    *some = values[0][0]; *full = values[1][0]; return 1;
}

int admit_parse_events_oom_kill(const char *text, long long *kills) {
    return admit_parse_counter(text, "oom_kill", "", kills);
}

int admit_parse_meminfo(const char *text, long long *total, long long *available,
                       long long *swap_total, long long *swap_free) {
    long long *out[] = {total, available, swap_total, swap_free};
    const char *keys[] = {"MemTotal:", "MemAvailable:", "SwapTotal:", "SwapFree:"};
    int ok = 1;
    for (int i = 0; i < 4; i++) {
        long long kb;
        *out[i] = -1;
        if (!admit_parse_counter(text, keys[i], "kB", &kb) || kb > LLONG_MAX / 1024) ok = 0;
        else *out[i] = kb * 1024;
    }
    return ok && *total > 0 && *available <= *total && *swap_free <= *swap_total;
}
