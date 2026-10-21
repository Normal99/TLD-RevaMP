/* overview_data.h - the panels tools/overview draws.
 *
 * Every panel fills its own struct; a panel that cannot get its data keeps its
 * `err` string and its `ok` flag clear, and the TUI shows that instead of the
 * panel.  Panels never share state, so one failing probe never blanks the rest.
 */
#ifndef OVERVIEW_DATA_H
#define OVERVIEW_DATA_H

#include <time.h>

#define OV_MAX_AGENTS 64
#define OV_MAX_GAMES 16
#define OV_MAX_REPORTS 12
#define OV_MAX_LOGS 10
#define OV_NAME 96
#define OV_PATH 320
#define OV_TEXT 200
#define OV_MAX_CONSUMERS 8
#define OV_MAX_WINDOWS 16

/* ---- agents (MiniMax / Codex workers) ---- */
typedef struct {
    int    pid;
    char   workspace[OV_NAME];   /* the worktree name, e.g. tld-revamp-free-tui-c */
    char   role[32];             /* codex / node / other */
    char   model[64];            /* the model the agent runs, if its command line says */
    double started_s;            /* seconds since the process started */
    int    has_result;           /* a result .md for this worker exists */
    long long result_size;
    double result_age_s;
} OvAgent;

/* ---- a TLD / Proton game process ---- */
typedef struct {
    int    pid;
    int    remote;               /* 0 = this machine, 1 = the second machine */
    char   prefix[OV_PATH];      /* the Proton prefix (STEAM_COMPAT_DATA_PATH) */
    char   game[OV_PATH];        /* the install path the copy came from */
    char   port[16];             /* -tldbridge <port>, if any */
    int    background;           /* 1 = no controlling terminal */
    double started_s;
    int    has_bridge;           /* the bridge answered */
    int    bridge_probe;         /* the bridge was probed on this port */
    char   bridge_version[32];
} OvGame;

/* ---- low-load graphics compliance, as the bridge reports it ---- */
typedef struct {
    int    ok;
    char   err[OV_TEXT];
    char   port[16];
    char   where[64];            /* "this machine" or "the second machine (tunnel)" */
    char   version[32];
    char   build[16];
    int    quality_level;
    char   quality_name[32];
    int    vsync;
    int    target_fps;
    int    fps_cap_limit;        /* settingsscript.s.S.FFPSLimit */
    int    fps_cap_enabled;      /* settingsscript.s.S.BEnableFpsLimit */
    int    vsync_setting;        /* settingsscript.s.S.BVsync */
    int    screen_w, screen_h;
    char   fullscreen[32];
    int    measured_fps;
    /* derived compliance, per the low-load policy */
    int    ok_quality, ok_fps, ok_vsync, ok_resolution, ok_background;
    int    compliant;            /* all of the above */
} OvGraphics;

/* ---- the Steam two-client session, read out of the logs ---- */
typedef struct {
    int    ok;
    char   err[OV_TEXT];
    char   host[OV_TEXT];        /* the machine hosting */
    char   client[OV_TEXT];      /* the machine joining */
    char   relay[32];            /* the relay setting from the log */
    char   phase[48];            /* the last phase marker seen */
    char   ports[64];            /* the bridge ports the run uses */
    int    pass_count, fail_count;
    char   last_verdict[OV_TEXT];   /* the last PASS/FAIL line, by name */
    long long log_age_s;
    char   log[OV_NAME];
} OvSteam;

/* ---- reports / findings ---- */
typedef struct {
    char   name[OV_NAME];
    long long size;
    long long age_s;
    char   head[OV_TEXT];        /* the first meaningful line */
} OvReport;

/* ---- logs ---- */
typedef struct {
    char   name[OV_NAME];
    long long size;
    long long age_s;
} OvLog;

/* ---- git ---- */
typedef struct {
    int    ok;
    char   err[OV_TEXT];
    char   branch[64];
    char   head[16];
    int    dirty_files;
    int    staged_files;
    int    untracked_files;
    char   upstream[64];
} OvGit;

/* ---- system ---- */
typedef struct {
    double load1, load5, load15;
    long long mem_total, mem_used, mem_avail;
    long long disk_total, disk_used, disk_free;
    int    cores;
    int    procs_running;
    /* the overview's own usage */
    long long self_cpu_ms;     /* the CPU it has used so far */
    long long self_rss_kb;
} OvSystem;

/* ---- admission broker and memory guard ---- */
typedef struct {
    int    available;
    char   state[24];            /* running, paused, idle, unavailable */
    char   reason[OV_TEXT];
    char   pause_kind[16];       /* manual, threshold, none */
    char   name[OV_NAME];
    char   profile[16];
    char   unit[192];
    char   cgroup[OV_PATH];
    long long updated_epoch;
    long long started_epoch;
    long long elapsed_s;
    int    has_active;
    int    exit_status;
    int    oom_killed;
    int    limits_verified;
    char   result[48];
    char   memory_high[24];
    char   memory_max[24];
    char   memory_swap_max[24];
    char   tasks_max[24];
    char   cpu_quota[24];
    char   oom_group[12];
    char   cpu_weight[24];
    char   io_weight[24];
    char   nice[16];
    long long mem_available, swap_used, session_anon, session_swap, watched_oom_kills;
} OvAdmission;

typedef struct {
    int    pid;
    char   comm[64];
    long long rss_kb;
} OvConsumer;

typedef struct {
    int    ok;
    char   err[OV_TEXT];
    long long anon;
    long long file;
    long long swap;
    long long mem_avail;
    long long global_swap;
    long long oom_kills;
    long long global_anon, global_file;
    double global_some, global_full, session_some, session_full; /* PSI avg10 % */
} OvPressure;

typedef struct {
    int    ok;
    char   err[OV_TEXT];
    char   steam[48];
    char   wivrn[48];
    char   adb[128];
    char   openxr[128];
} OvRuntimeState;

/* ---- the Quest / WiVRn headset, read over an already running ADB server ----
 *
 * Nothing here holds a serial, a device path or an environment value: the
 * serial is used on the wire and dropped, and the paths are only ever used to
 * read a manifest, never shown. */
#define OV_HS_MAX_ROOTS 4
#define OV_HS_MAX_RUNTIMES 4

typedef struct {
    int    present;          /* exactly one authorized device was found */
    int    device_count;     /* every device the server listed */
    int    ready_count;      /* the devices in the "device" state */
    int    unauthorized;     /* devices waiting for the user to allow them */
    int    offline;          /* devices the server cannot reach */
    int    denied;           /* devices without USB permission */
    char   model[64];        /* the model name, e.g. "Quest 3" */

    int    battery;          /* percent, -1 when unknown */
    int    battery_scale;    /* the scale the level was read against */
    int    battery_status;   /* the raw battery service status, -1 when unknown */
    int    temperature;      /* tenths of a degree Celsius, -1 when unknown */
    int    charging;         /* 1 = charging, 0 = not charging, -1 = unknown */
    int    usb_powered;      /* 1 = USB powered, 0 = not, -1 = unknown */
    int    ac_powered;
    int    dock_powered;
    int    wireless_powered;
    int    weak_charger;     /* 1 = the charger is too weak to charge */
    int    present_battery;  /* the battery service reports a battery */

    int    wake_state;       /* 0 = awake, 1 = asleep, -1 = unknown */
    int    wake_known;

    int    reverse;          /* 1 = the reverse mapping is there, 0 = absent */
    int    reverse_known;
    int    reverse_count;    /* every mapping the server lists */
    int    reverse_ours;     /* how many of them are the one we look for */

    int    wivrn_app;        /* 1 = the WiVRn client app is installed */
    int    wivrn_app_known;

    int    openxr_selected;  /* 1 = a selected runtime was found */
    int    openxr_installed; /* how many runtime manifests were found */
    char   openxr_names[OV_HS_MAX_RUNTIMES][64];  /* the public runtime names */
    int    openxr_name_count;
    char   openxr_active[64];  /* the selected runtime's name */

    char   status[OV_TEXT];  /* one short line: what is known and what is not */
} OvHeadset;

typedef struct {
    char   title[OV_TEXT];
    char   app_class[128];
    char   workspace[64];
    int    floating;
    int    fullscreen;
    int    focused;
    int    special_exposed;
} OvWindow;

/* ---- the whole frame ---- */
typedef struct {
    OvAgent  agents[OV_MAX_AGENTS];
    int      agent_count;
    int      agent_total_seen;
    char     agent_err[OV_TEXT];

    OvGame   games[OV_MAX_GAMES];
    int      game_count;
    int      remote_game_count;
    char     remote_err[OV_TEXT];
    char     remote_host[64];

    OvGraphics graphics;
    OvSteam    steam;
    OvReport   reports[OV_MAX_REPORTS];
    int        report_count;
    OvLog      logs[OV_MAX_LOGS];
    int        log_count;
    OvGit      git;
    OvSystem   sys;
    OvAdmission admission;
    OvConsumer consumers[OV_MAX_CONSUMERS];
    int         consumer_count;
    OvPressure pressure;
    OvRuntimeState runtime;
    OvHeadset headset;
    OvWindow   windows[OV_MAX_WINDOWS];
    int        window_count;
    int        tld_window_count;
    int        tld_tests_exposed;
    char       window_err[OV_TEXT];
    long long  new_oom_events;
} OvFrame;

#endif /* OVERVIEW_DATA_H */
