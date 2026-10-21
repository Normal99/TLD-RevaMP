/* Deterministic tests for the admission policy and its live systemd boundary. */
#define _GNU_SOURCE
#include "tld_admit_core.h"

#include <stdio.h>
#include <string.h>

#define GIB (1024LL * 1024LL * 1024LL)

static int failures, checks;

#define CHECK(c, name) do { checks++; if (c) printf("  ok   %s\n", name); else { printf("  FAIL %s\n", name); failures++; } } while (0)

static void reading(AdmitReading *r, long long avail, long long swap, long long anon, long long session_swap) {
    memset(r, 0, sizeof *r);
    r->mem_total = 32 * GIB;
    r->mem_available = avail;
    r->swap_total = 64 * GIB;
    r->session_file = 4 * GIB;
    r->swap_used = swap;
    r->session_anon = anon;
    r->session_swap = session_swap;
}

int main(void) {
    printf("tld-admit tests\n");
    AdmitDecision d;
    AdmitReading r;

    reading(&r, 12 * GIB, 10 * GIB, 12 * GIB, 9 * GIB);
    d = admit_evaluate(&r);
    CHECK(d.all_reading_ok && !d.threshold_paused && d.recovery_ready, "current host high swap and anon still healthy");
    CHECK(admit_launch_ready(&r,ADMIT_PROFILE_GAME) && admit_launch_ready(&r,ADMIT_PROFILE_TEST), "ordinary game and test fit current headroom");
    CHECK(!admit_launch_ready(&r,ADMIT_PROFILE_VR), "VR requires additional encoder/desktop reserve");
    const AdmitProfileSpec *a = admit_profile(ADMIT_PROFILE_AGENT), *t = admit_profile(ADMIT_PROFILE_TEST),
        *g = admit_profile(ADMIT_PROFILE_GAME), *v = admit_profile(ADMIT_PROFILE_VR);
    CHECK(!strcmp(a->memory_high,"1536M") && !strcmp(a->memory_max,"2G") && !strcmp(a->memory_swap_max,"1G") &&
        !strcmp(a->tasks_max,"256") && !strcmp(a->cpu_quota,"200%") && a->oom_group, "agent controls preserved");
    CHECK(t->max_bytes == 2*GIB && t->swap_bytes == GIB && t->high_bytes == 1536LL*1024*1024 && t->oom_group, "test controls preserved");
    CHECK(g->high_bytes == 8*GIB && g->max_bytes == 10*GIB && g->swap_bytes == 2*GIB && g->launch_available == 12*GIB, "game entry reserve leaves 2 GiB even at the unit cap");
    CHECK(v->high_bytes == 10*GIB && v->max_bytes == 12*GIB && v->swap_bytes == 4*GIB && v->launch_available == 26*GIB, "VR separately bounded");
    for (int profile = ADMIT_PROFILE_AGENT; profile <= ADMIT_PROFILE_VR; profile++) {
        const AdmitProfileSpec *spec = admit_profile((AdmitProfile)profile);
        CHECK(admit_profile_parse(spec->name) == (AdmitProfile)profile, "profile name round trip");
        reading(&r,spec->launch_available,0,0,0);
        CHECK(admit_launch_ready(&r,(AdmitProfile)profile), "exact RAM launch boundary admits");
        r.mem_available--;
        CHECK(!admit_launch_ready(&r,(AdmitProfile)profile), "one byte below RAM launch boundary rejects");
        r.mem_available++;
        r.swap_used = r.swap_total - spec->swap_bytes - 4*GIB;
        CHECK(admit_launch_ready(&r,(AdmitProfile)profile), "exact swap launch boundary admits");
        r.swap_used++;
        CHECK(!admit_launch_ready(&r,(AdmitProfile)profile), "one byte below swap launch boundary rejects");
    }
    CHECK(admit_profile_parse("other") == ADMIT_PROFILE_INVALID && !admit_launch_ready(&r,ADMIT_PROFILE_INVALID), "unknown profile rejects");
    reading(&r,6*GIB,0,28*GIB,30*GIB);
    CHECK(!admit_evaluate(&r).threshold_paused, "6 GiB reserve equality, large anon/swap alone do not pause");
    r.mem_available--;
    CHECK(admit_evaluate(&r).threshold_paused, "below RAM reserve pauses immediately");
    reading(&r,18*GIB,64*GIB - (64*GIB/20),0,0);
    CHECK(!admit_evaluate(&r).threshold_paused, "5 percent swap reserve equality");
    r.swap_used++;
    CHECK(admit_evaluate(&r).threshold_paused, "one byte below percentage swap reserve pauses");
    reading(&r,18*GIB,6*GIB,0,0); r.swap_total = 8*GIB;
    CHECK(!admit_evaluate(&r).threshold_paused, "2 GiB absolute swap reserve equality");
    r.swap_used++;
    CHECK(admit_evaluate(&r).threshold_paused, "absolute swap reserve catches small tiers");
    reading(&r,18*GIB,0,0,0); r.swap_total = 0;
    CHECK(admit_evaluate(&r).all_reading_ok && admit_evaluate(&r).threshold_paused, "no swap is known pressure");
    reading(&r,9*GIB,57*GIB,12*GIB,GIB);
    CHECK(admit_evaluate(&r).threshold_paused, "anon dominance corroborates low available and swap");
    r.session_file = 20*GIB;
    CHECK(!admit_evaluate(&r).threshold_paused, "cache-heavy session not mistaken for anonymous working set");
    r.session_shmem = 16*GIB;
    CHECK(admit_evaluate(&r).threshold_paused, "shmem excluded from reclaimable file cache");
    r.session_swap = 0;
    CHECK(!admit_evaluate(&r).threshold_paused, "session swap required for compound working-set gate");
    reading(&r,18*GIB,0,0,0); r.psi_some = 999; r.psi_full = 199;
    CHECK(!admit_evaluate(&r).psi_pending, "below moderate PSI boundary");
    r.psi_some = 1000;
    CHECK(admit_evaluate(&r).psi_pending && !admit_launch_ready(&r,ADMIT_PROFILE_TEST), "some PSI boundary holds launches before debounce");
    r.psi_some = 999; r.psi_full = 200;
    CHECK(admit_evaluate(&r).psi_pending, "full PSI boundary independently holds");
    r.psi_some = 2500;
    CHECK(admit_evaluate(&r).threshold_paused, "severe some PSI bypasses debounce");
    r.psi_some = 1000; r.psi_full = 1000;
    CHECK(admit_evaluate(&r).threshold_paused, "severe full PSI bypasses debounce");
    reading(&r,18*GIB,0,0,0); r.session_psi_some = 1000;
    CHECK(admit_evaluate(&r).psi_pending, "session PSI catches local reclaim stalls");
    reading(&r,8*GIB,0,0,0);
    CHECK(admit_auto_resume_ready(&r,100,160,60), "recovery at exact RAM boundary after 60 seconds");
    CHECK(!admit_auto_resume_ready(&r,100,159,60), "59 seconds cannot resume");
    r.mem_available--;
    CHECK(!admit_auto_resume_ready(&r,100,160,60), "RAM recovery hysteresis");
    reading(&r,18*GIB,0,0,0); r.psi_some = 500;
    CHECK(!admit_evaluate(&r).recovery_ready, "some PSI recovery boundary remains held");
    r.psi_some = 499; r.psi_full = 100;
    CHECK(!admit_evaluate(&r).recovery_ready, "full PSI recovery boundary remains held");
    r.psi_full = 99;
    CHECK(admit_evaluate(&r).recovery_ready, "below both recovery PSI boundaries");
    r.swap_used = r.swap_total - r.swap_total/10;
    CHECK(admit_evaluate(&r).recovery_ready, "swap recovery percentage equality");
    r.swap_used++;
    CHECK(!admit_evaluate(&r).recovery_ready, "swap recovery hysteresis");
    reading(&r,18*GIB,0,0,0);
    long long *gauges[] = {&r.mem_total,&r.mem_available,&r.swap_total,&r.swap_used,&r.session_anon,
        &r.session_swap,&r.oom_kills,&r.session_file,&r.session_shmem,&r.global_oom_kills,
        &r.psi_some,&r.psi_full,&r.session_psi_some,&r.session_psi_full};
    for (size_t i=0;i<sizeof gauges/sizeof gauges[0];i++) {
        long long old = *gauges[i]; *gauges[i] = -1;
        CHECK(!admit_evaluate(&r).all_reading_ok && !admit_launch_ready(&r,ADMIT_PROFILE_TEST) &&
            !admit_auto_resume_ready(&r,100,160,60), "each unknown gauge rejects launch and recovery");
        *gauges[i] = old;
    }
    r.psi_some = 10001;
    CHECK(!admit_evaluate(&r).all_reading_ok, "out of range PSI rejected");
    r.psi_some = 0; r.psi_full = 1;
    CHECK(!admit_evaluate(&r).all_reading_ok, "full above some rejected");

    char err[128];
    CHECK(admit_validate_name("lane-41_test.ok", err, sizeof err), "valid job name");
    CHECK(!admit_validate_name("", err, sizeof err), "empty job name rejected");
    CHECK(!admit_validate_name("../escape", err, sizeof err), "path-like job name rejected");
    CHECK(!admit_validate_name("bad name", err, sizeof err), "shell-like job name rejected");
    char suffix[32]; admit_safe_unit_suffix("lane.41", suffix, sizeof suffix);
    CHECK(!strcmp(suffix, "lane-41"), "unit suffix is deterministic and safe");

    long long anon = -1, file = -1, shmem = -1;
    CHECK(admit_parse_memory_stat("anon 123\nfile 456\nshmem 12\nswap 789\n", &anon, &file, &shmem) &&
        anon == 123 && file == 456 && shmem == 12, "anon file and shmem parsed; stat swap ignored");
    CHECK(!admit_parse_memory_stat("anon 123\nfile 456\n", &anon, &file, &shmem), "missing shmem fails closed");
    long long some, full;
    const char *psi = "some avg10=10.01 avg60=2.00 avg300=0.01 total=123\nfull avg10=1.00 avg60=1.00 avg300=0.00 total=12\n";
    CHECK(admit_parse_psi(psi,&some,&full) && some == 1001 && full == 100, "PSI fixed point parsing");
    const char *bad_psi[] = {"", "some avg10=nan\n", "some avg10=-1.00\n", "some avg10=100.01 avg60=0.00 avg300=0.00 total=0\n", "some avg10=1e2 avg60=0.00 avg300=0.00 total=0\n"};
    for (size_t i=0;i<sizeof bad_psi/sizeof bad_psi[0];i++)
        CHECK(!admit_parse_psi(bad_psi[i],&some,&full) && some == -1 && full == -1, "malformed PSI stays unknown");
    long long kills = -1;
    CHECK(admit_parse_events_oom_kill("low 0\nhigh 1\nmax 2\noom 3\noom_kill 4\n", &kills) && kills == 4,
          "memory.events parses oom_kill");
    CHECK(!admit_parse_events_oom_kill("oom 2\n", &kills), "missing oom_kill fails closed");
    long long total = 0, avail = 0, st = 0, sf = 0;
    CHECK(admit_parse_meminfo("MemTotal: 100 kB\nMemAvailable: 60 kB\nSwapTotal: 30 kB\nSwapFree: 20 kB\n",
                              &total, &avail, &st, &sf) && total == 102400 && avail == 61440 && st == 30720 && sf == 20480,
          "meminfo parses available and swap");

    char json[2048] = "{\"state\":\"running\",\"startedEpoch\":10}";
    char out[128]; long long num = 0;
    CHECK(admit_json_str(json, "state", out, sizeof out) && !strcmp(out, "running"), "state JSON parser");
    CHECK(admit_json_ll(json, "startedEpoch", &num) && num == 10, "numeric JSON parser");
    CHECK(!admit_json_ll(json, "missing", &num), "missing numeric key rejected");
    char escaped[64]; admit_json_escape("a\"b\n", escaped, sizeof escaped);
    CHECK(!strcmp(escaped, "a\\\"b\\n"), "JSON escaping");

    CHECK(!admit_json_str("{\"limits\":{\"state\":\"running\"}}", "state", out, sizeof out),
          "nested state cannot shadow top-level state");
    CHECK(!admit_json_str("{\"state\":\"running\"", "state", out, sizeof out), "truncated JSON rejected");
    CHECK(!admit_json_ll("{\"n\":1.5}", "n", &num) && !admit_json_ll("{\"n\":1e3}", "n", &num) &&
          !admit_json_ll("{\"n\":999999999999999999999999}", "n", &num), "fractional and overflowing integers rejected");
    CHECK(!admit_parse_memory_stat("anon -1\nfile 2\n", &anon, &file, &shmem) && anon == -1, "negative anonymous reading rejected");
    CHECK(!admit_parse_memory_stat("anon 1oops\n", &anon, &file, &shmem), "malformed anonymous reading rejected");
    CHECK(!admit_parse_events_oom_kill("oom_kill 99999999999999999999999\n", &kills), "overflowing OOM counter rejected");
    CHECK(!admit_parse_events_oom_kill("oom_kill 1oops\n", &kills), "malformed OOM counter rejected");
    CHECK(!admit_parse_meminfo("MemTotal: 100 kB\nMemAvailable: 60 kB\n", &total,&avail,&st,&sf),
          "missing swap gauges remain unavailable");
    CHECK(admit_parse_meminfo("MemTotal: 100 kB\nMemAvailable: 0 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB\n", &total,&avail,&st,&sf) && avail == 0,
          "known zero available memory is pressure, not an unknown gauge");
    CHECK(!admit_parse_meminfo("MemTotal: 100 kB\nMemAvailable: 60 kB\nSwapTotal: 10 kB\nSwapFree: 11 kB\n", &total,&avail,&st,&sf),
          "inconsistent global swap rejected");
    reading(&r,7*GIB,0,0,0); r.session_swap = -1;
    CHECK(!admit_evaluate(&r).all_reading_ok && !admit_auto_resume_ready(&r,100,1000,60), "unknown gauge cannot admit or resume");
    reading(&r,7*GIB,0,0,0); r.oom_kills = -1;
    CHECK(!admit_evaluate(&r).all_reading_ok, "unknown watched OOM reading fails closed");
    CHECK(!admit_auto_resume_ready(&r,200,100,60), "backwards clock cannot resume");
    printf("tld-admit tests: %s (%d checks, %d failure(s))\n", failures ? "FAILED" : "passed", checks, failures);
    return failures ? 1 : 0;
}
