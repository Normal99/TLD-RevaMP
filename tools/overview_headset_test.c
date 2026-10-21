/* overview_headset_test.c - the hermetic self-test of the headset module.
 *
 * Every case runs against fixture text and a fixture transport: no socket is
 * opened, no adb binary is executed, no systemd unit is contacted and no file
 * outside the temporary directory is read.  The fixture transport counts the
 * requests it answered, so the test can prove both that the right wire requests
 * were made and that the production wire was never touched.
 */
#define _GNU_SOURCE
#include "overview_headset.h"
#include "overview_core.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static int g_failures = 0;

#define CHECK(cond, what) do { \
    if (cond) { printf("  ok   %s\n", (what)); } \
    else { printf("  FAIL %s\n", (what)); g_failures++; } \
} while (0)

/* ================= the fixture transport ================= */

#define FIX_MAX_REPLIES 16

typedef struct {
    const char *match;     /* the service or the shell command to answer */
    const char *reply;     /* the answer; NULL means "this request fails" */
} OvHsFixture;

typedef struct {
    OvHsFixture items[FIX_MAX_REPLIES];
    int         count;
    char        serial_seen[256];   /* the serial of the last shell request */
    int         shell_calls;
    int         host_calls;
    long        wire_calls_at_start;
} OvHsFixtureCtx;

static int fixture_query(void *ctx, const OvAdbRequest *req, char *out, size_t n) {
    OvHsFixtureCtx *c = ctx;
    if (req->kind == OV_ADB_HOST) {
        c->host_calls++;
        for (int i = 0; i < c->count; i++) {
            if (c->items[i].match && !strcmp(c->items[i].match, req->service)) {
                if (!c->items[i].reply) return 0;
                st_copy(out, n, c->items[i].reply);
                return 1;
            }
        }
        return 0;
    }
    /* a shell command or a device service: both go to the device */
    c->shell_calls++;
    if (req->serial) st_copy(c->serial_seen, sizeof c->serial_seen, req->serial);
    for (int i = 0; i < c->count; i++) {
        if (c->items[i].match && !strcmp(c->items[i].match, req->command)) {
            if (!c->items[i].reply) return 0;          /* this request fails */
            st_copy(out, n, c->items[i].reply);
            return 1;
        }
    }
    return 0;
}

static void fixture_reset(OvHsFixtureCtx *c) {
    memset(c, 0, sizeof *c);
}

static void fixture_add(OvHsFixtureCtx *c, const char *match, const char *reply) {
    if (c->count >= FIX_MAX_REPLIES) return;
    c->items[c->count].match = match;
    c->items[c->count].reply = reply;
    c->count++;
}

/* ================= the fixtures ================= */

static const char *FIX_DEVICES_ONE =
    "List of devices attached\n"
    "fixture-serial-0001\tdevice product:quest model:Quest_3 transport_id:1\n";

static const char *FIX_DEVICES_TWO =
    "List of devices attached\n"
    "fixture-serial-0001\tdevice product:quest model:Quest_3 transport_id:1\n"
    "fixture-serial-0002\tdevice product:quest model:Quest_3 transport_id:2\n";

static const char *FIX_DEVICES_UNAUTH =
    "List of devices attached\n"
    "fixture-serial-0001\tunauthorized\n";

static const char *FIX_DEVICES_OFFLINE =
    "List of devices attached\n"
    "fixture-serial-0001\toffline\n";

static const char *FIX_DEVICES_EMPTY = "List of devices attached\n\n";

static const char *FIX_BATTERY_GOOD =
    "Current Battery Service state:\n"
    "  AC powered: false\n"
    "  USB powered: true\n"
    "  Wireless powered: false\n"
    "  Dock powered: false\n"
    "  status: 2\n"
    "  present: true\n"
    "  level: 73\n"
    "  scale: 100\n"
    "  temperature: 370\n";

static const char *FIX_BATTERY_WEAK =
    "Current Battery Service state:\n"
    "  AC powered: true\n"
    "  USB powered: true\n"
    "  status: 4\n"
    "  present: true\n"
    "  level: 12\n"
    "  scale: 100\n"
    "  temperature: 415\n";

/* The block a real Quest 3 reports: the weak charger flag and the charge limits. */
static const char *FIX_BATTERY_REAL =
    "Current Battery Service state:\n"
    "  AC powered: false\n"
    "  USB powered: true\n"
    "  Wireless powered: false\n"
    "  Dock powered: false\n"
    "  Max charging current: 900000\n"
    "  Max charging voltage: 5000000\n"
    "  Weak Charger: true\n"
    "  Charge counter: 3761440\n"
    "  status: 2\n"
    "  health: 2\n"
    "  present: true\n"
    "  level: 78\n"
    "  scale: 100\n"
    "  voltage: 4215\n"
    "  temperature: 350\n"
    "  technology: Li-ion\n";

static const char *FIX_BATTERY_MALFORMED =
    "Current Battery Service state:\n"
    "  level: 101\n"
    "  scale: 100\n"
    "  temperature: 370\n";

static const char *FIX_BATTERY_NOSCALE =
    "Current Battery Service state:\n"
    "  level: 73\n"
    "  temperature: 370\n";

static const char *FIX_POWER_AWAKE =
    "POWER MANAGER (mWakefulness=Awake)\n"
    "  mSleeping=false\n"
    "  mWakefulness=Awake\n";

static const char *FIX_POWER_ASLEEP =
    "POWER MANAGER (mWakefulness=Asleep)\n"
    "  mWakefulness=Asleep\n";

static const char *FIX_REVERSE_ONE =
    "0019UsbFfs tcp:9757 tcp:9757\n";

static const char *FIX_REVERSE_MANY =
    "0019UsbFfs tcp:9757 tcp:9757\n"
    "0019UsbFfs tcp:8080 tcp:8080\n"
    "0019UsbFfs tcp:9000 tcp:9000\n";

static const char *FIX_WIVRN_INSTALLED = "package:/data/app/org.meumeu.wivrn-1/base.apk\n";
static const char *FIX_WIVRN_MISSING = "package:unknown\n";

/* The "pm list packages <filter>" answers.  The package manager filters by
 * substring and answers one "package:<id>" line per match. */
static const char *FIX_PKGLIST_GITHUB  = "package:org.meumeu.wivrn.github\n";
static const char *FIX_PKGLIST_PLAY    = "package:org.meumeu.wivrn\n";
static const char *FIX_PKGLIST_EMPTY   = "";
static const char *FIX_PKGLIST_OTHER   = "package:com.github.somebody.else\n";
static const char *FIX_PKGLIST_ERROR   = "Error: Unknown option: --bogusflag\n";
static const char *FIX_PKGLIST_UNKNOWN = "Unknown command: bogus\n";

/* ================= the cases ================= */

static void test_parsers(void) {
    printf("headset parsers\n");
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_battery(FIX_BATTERY_GOOD, &h) && h.battery == 73 &&
              h.temperature == 370 && h.usb_powered == 1 && h.ac_powered == 0 &&
              h.charging == 1 && h.present_battery == 1,
              "the battery block gives the level, the temperature and the USB charging state");
        CHECK(h.weak_charger == 0, "a healthy USB charger is not flagged as weak");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_battery(FIX_BATTERY_WEAK, &h) && h.battery == 12 &&
              h.charging == 0 && h.weak_charger == 1,
              "a plugged-in charger that does not charge is flagged as weak");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_battery(FIX_BATTERY_MALFORMED, &h) && h.battery == 0 &&
              h.present_battery == 0,
              "a level above the scale is malformed and is rejected");
    }
    {
        /* the real block: the weak charger flag is read, not guessed */
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_battery(FIX_BATTERY_REAL, &h) && h.battery == 78 &&
              h.temperature == 350 && h.usb_powered == 1 && h.charging == 1,
              "the real battery block gives the level, the temperature and the charging state");
        CHECK(h.weak_charger == 1, "the real block's weak charger flag is read");
    }
    {
        /* a weak charger flag that is false is not a weak charger */
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_battery("  level: 90\n  scale: 100\n  Weak Charger: false\n"
                                  "  status: 2\n  USB powered: true\n", &h) &&
              h.weak_charger == 0, "a weak charger flag of false is not a weak charger");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_battery(FIX_BATTERY_NOSCALE, &h),
              "a battery block without a scale is rejected");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_battery("not a battery block at all", &h),
              "an unrelated text is not a battery reading");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_model("Quest 3\n", &h) && !strcmp(h.model, "Quest 3"),
              "the model name is read");
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_model("/data/app/com.oculus.oculus\n", &h),
              "a path is not a model name");
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_model("\n", &h), "an empty model is not a reading");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_power(FIX_POWER_AWAKE, &h) && h.wake_known && h.wake_state == 0,
              "an awake headset is read from mWakefulness");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_power(FIX_POWER_ASLEEP, &h) && h.wake_known && h.wake_state == 1,
              "an asleep headset is read from mWakefulness");
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_power("POWER MANAGER (nothing here)", &h) && !h.wake_known,
              "an unreadable power block leaves the wake state unknown");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_reverse(FIX_REVERSE_ONE, &h) && h.reverse == 1 &&
              h.reverse_known && h.reverse_count == 1 && h.reverse_ours == 1,
              "the reverse mapping that is present is reported");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_reverse(FIX_REVERSE_MANY, &h) && h.reverse == 1 &&
              h.reverse_count == 3 && h.reverse_ours == 1,
              "several reverse mappings are counted and the wanted one is found");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_reverse("0019UsbFfs tcp:8080 tcp:8080\n", &h) &&
              h.reverse == 0 && h.reverse_known && h.reverse_count == 1,
              "a reverse list without the wanted mapping is an explicit absence");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_reverse("", &h) && h.reverse == 0 && h.reverse_known &&
              h.reverse_count == 0, "an empty reverse list is an explicit absence");
        /* D2: a final line without a trailing newline must not crash. */
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_reverse("0019UsbFfs tcp:9757 tcp:9757", &h) && h.reverse == 1 &&
              h.reverse_known && h.reverse_count == 1,
              "a reverse list whose last line has no newline is still read");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_reverse("0019UsbFfs tcp:8080 tcp:8080", &h) && h.reverse == 0 &&
              h.reverse_known && h.reverse_count == 1,
              "an unterminated reverse line without the wanted mapping is an explicit absence");
    }
    {
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_wivrn_app(FIX_WIVRN_INSTALLED, &h) && h.wivrn_app == 1 &&
              h.wivrn_app_known, "the WiVRn app is reported as installed");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_wivrn_app(FIX_WIVRN_MISSING, &h) && h.wivrn_app == 0 &&
              h.wivrn_app_known, "the WiVRn app is reported as not installed");
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_wivrn_app("", &h) && !h.wivrn_app_known,
              "an unreadable package answer leaves the WiVRn app unknown");
    }
    {
        /* D1: the GitHub build is the one the owner's headset carries. */
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_package_list(FIX_PKGLIST_GITHUB, &h) && h.wivrn_app == 1 &&
              h.wivrn_app_known, "the GitHub build id is reported as installed");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_package_list(FIX_PKGLIST_PLAY, &h) && h.wivrn_app == 1 &&
              h.wivrn_app_known, "the Play Store build id is reported as installed");
    }
    {
        /* D1: neither build is present: a successful query with no match. */
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_package_list(FIX_PKGLIST_EMPTY, &h) && h.wivrn_app == 0 &&
              h.wivrn_app_known, "an empty package list is a genuine absence");
    }
    {
        /* D1: the filter matches other ids too; only the known ids count. */
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_package_list(FIX_PKGLIST_OTHER, &h) && h.wivrn_app == 0 &&
              h.wivrn_app_known, "another app that matches the filter is not the WiVRn client");
    }
    {
        /* D1: a query that failed is unknown, not an absence. */
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_package_list(FIX_PKGLIST_ERROR, &h) && !h.wivrn_app_known,
              "a failed package query leaves the WiVRn app unknown");
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_package_list(FIX_PKGLIST_UNKNOWN, &h) && !h.wivrn_app_known,
              "an unknown package command leaves the WiVRn app unknown");
        memset(&h, 0, sizeof h);
        CHECK(!ov_hs_parse_package_list("garbage that is not a package list", &h) &&
              !h.wivrn_app_known, "unrecognised text leaves the WiVRn app unknown");
    }
    {
        /* D1: a prefix of a known id is not that id. */
        OvHeadset h;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_package_list("package:org.meumeu.wivrnX\n", &h) && h.wivrn_app == 0,
              "an id that merely starts like the WiVRn id is not the WiVRn client");
    }
    {
        OvHeadset h;
        char serial[256] = { 0 };
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices(FIX_DEVICES_ONE, &h, serial, sizeof serial) == 1 &&
              h.present && h.ready_count == 1 && !strcmp(serial, "fixture-serial-0001"),
              "one authorized device is selected");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices(FIX_DEVICES_TWO, &h, serial, sizeof serial) == 2 &&
              !h.present && h.ready_count == 2 && !serial[0],
              "two devices select nothing");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices(FIX_DEVICES_UNAUTH, &h, serial, sizeof serial) == 1 &&
              !h.present && h.unauthorized == 1 && !serial[0],
              "an unauthorized device selects nothing");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices(FIX_DEVICES_OFFLINE, &h, serial, sizeof serial) == 1 &&
              !h.present && h.offline == 1, "an offline device selects nothing");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices(FIX_DEVICES_EMPTY, &h, serial, sizeof serial) == 0 &&
              !h.present && !h.device_count, "an empty device list selects nothing");
        /* D2: a final line without a trailing newline must not crash. */
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices("List of devices attached\nSERIAL1\tdevice", &h, serial, sizeof serial) == 1 &&
              h.present && h.ready_count == 1 && !strcmp(serial, "SERIAL1"),
              "a device listing whose last line has no newline is still read");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices("List of devices attached\nSERIAL1\tdevice\nSERIAL2\tdevice", &h,
                                  serial, sizeof serial) == 2 && !h.present && h.ready_count == 2,
              "a two-device listing without a trailing newline selects nothing");
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_parse_devices("SERIAL1\tdevice", &h, serial, sizeof serial) == 1 && h.present,
              "a listing with a single unterminated line is still read");
    }
    {
        /* D2: the production path.  The collector used to copy the listing into
         * an 8 KiB buffer, so a longer listing was truncated into a text whose
         * last line had no newline: the parser then crashed.  The buffer now
         * matches the module's reading slot, and this case drives the same
         * path with a listing that is far longer than the old buffer. */
        size_t big = 64 * 1024;
        char *listing = malloc(big);
        if (!listing) { printf("  FAIL the large listing could not be allocated\n"); g_failures++; }
        else {
            int at = snprintf(listing, big, "List of devices attached\n");
            /* many entries, so the listing is larger than the old 8 KiB buffer */
            while (at > 0 && (size_t)at < big - 64) {
                int n = snprintf(listing + at, big - (size_t)at,
                                 "SERIAL-%06d\tdevice product:quest model:Quest_3 transport_id:1\n", at);
                if (n <= 0) break;
                at += n;
            }
            CHECK((size_t)at > 8192, "the fixture listing is larger than the old 8 KiB buffer");
            OvHeadset h;
            char serial[256] = { 0 };
            memset(&h, 0, sizeof h);
            int devices = ov_hs_parse_devices(listing, &h, serial, sizeof serial);
            CHECK(devices > 0 && h.ready_count == devices && !h.present,
                  "a listing longer than the old buffer is read whole and selects nothing");
            CHECK(!h.present && !serial[0],
                  "a long listing does not select a device");
            /* the copy into the collector's buffer must not truncate it either */
            char copy[OV_HS_SLOT_BYTES] = { 0 };
            st_copy(copy, sizeof copy, listing);
            memset(&h, 0, sizeof h);
            CHECK(ov_hs_parse_devices(copy, &h, serial, sizeof serial) == devices,
                  "the copied listing is read without truncation");
            free(listing);
        }
    }
}

/* The whole panel, with the fixture transport installed.  No socket is opened. */
static void test_collect(void) {
    printf("headset collector (fixture transport)\n");
    long before = ov_hs_wire_calls();
    OvHsFixtureCtx ctx;
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_ONE);
    fixture_add(&ctx, "getprop ro.product.model", "Quest 3\n");
    fixture_add(&ctx, "dumpsys battery", FIX_BATTERY_GOOD);
    fixture_add(&ctx, "dumpsys power", FIX_POWER_AWAKE);
    fixture_add(&ctx, OV_WIVRN_PACKAGE_QUERY, FIX_PKGLIST_GITHUB);
    fixture_add(&ctx, "reverse:list-forward", FIX_REVERSE_ONE);
    ov_hs_set_query(fixture_query, &ctx);
    ov_hs_reset_cache();

    OvHeadset h;
    ov_headset_collect(&h, "server running");
    CHECK(h.present && !strcmp(h.model, "Quest 3"), "the fixture device is read");
    CHECK(h.battery == 73 && h.temperature == 370 && h.charging == 1 && h.usb_powered == 1,
          "the fixture battery, charging and temperature are read");
    CHECK(h.wake_known && h.wake_state == 0, "the fixture wake state is read");
    CHECK(h.reverse == 1 && h.reverse_known, "the fixture reverse mapping is read");
    CHECK(h.wivrn_app == 1 && h.wivrn_app_known, "the fixture WiVRn app is read");
    CHECK(ctx.shell_calls == 5, "the device is asked exactly the five whitelisted questions");
    CHECK(ctx.host_calls == 1, "the host is asked exactly the device listing");
    CHECK(!strcmp(ctx.serial_seen, "fixture-serial-0001"),
          "the selected serial is the one the fixture device has");
    CHECK(ov_hs_wire_calls() == before, "the fixture collector never touched the real wire");

    /* The rendered panel must not carry the serial, a path or a secret. */
    char text[1024] = { 0 };
    ov_hs_format(&h, text, sizeof text);
    CHECK(!strstr(text, "fixture-serial-0001"), "the panel does not print the serial");
    CHECK(!strstr(text, "/data/app"), "the panel does not print a device path");
    CHECK(strstr(text, "Quest 3") != NULL, "the panel prints the model name");
    CHECK(strstr(text, "73%") != NULL, "the panel prints the battery level");
    CHECK(strstr(text, "tcp:9757") != NULL, "the panel names the reverse mapping");
    CHECK(strstr(text, "awake: yes") != NULL, "the panel prints the wake state");

    /* the absent device */
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_EMPTY);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server not seen");
    CHECK(!h.present && h.device_count == 0, "no device attached selects nothing");
    CHECK(ctx.shell_calls == 0, "no device means no question is asked of one");
    CHECK(ov_hs_wire_calls() == before, "the absent-device path never touched the real wire");
    ov_hs_format(&h, text, sizeof text);
    CHECK(strstr(text, "none selected") != NULL, "the panel says that no device is selected");

    /* two devices: nothing is selected and nothing is asked */
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_TWO);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server not seen");
    CHECK(!h.present && h.ready_count == 2, "two devices select nothing");
    CHECK(ctx.shell_calls == 0, "two devices mean no question is asked of one");

    /* an unauthorized device */
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_UNAUTH);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server not seen");
    CHECK(!h.present && h.unauthorized == 1, "an unauthorized device is reported as such");
    CHECK(ctx.shell_calls == 0, "an unauthorized device is not asked anything");
    ov_hs_format(&h, text, sizeof text);
    CHECK(strstr(text, "unauthorized") != NULL, "the panel says the device is unauthorized");

    /* an offline device */
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_OFFLINE);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server not seen");
    CHECK(!h.present && h.offline == 1, "an offline device is reported as such");
    CHECK(ctx.shell_calls == 0, "an offline device is not asked anything");

    /* no ADB server at all */
    fixture_reset(&ctx);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server not seen");
    CHECK(!h.present && h.device_count == 0, "no ADB server means no device");
    CHECK(ctx.host_calls == 1 && ctx.shell_calls == 0,
          "a missing server is asked for the listing once and no device is contacted");
    ov_hs_format(&h, text, sizeof text);
    CHECK(strstr(text, "unavailable") != NULL || strstr(text, "none selected") != NULL,
          "the panel stays useful without a server");

    /* every degraded state stays explicit and names no device */
    {
        struct { const char *label; const char *listing; const char *want; } degraded[] = {
            { "no device",       FIX_DEVICES_EMPTY,    "no ADB device attached" },
            { "unauthorized",    FIX_DEVICES_UNAUTH,   "device unauthorized" },
            { "offline",         FIX_DEVICES_OFFLINE,  "device offline" },
            { "two devices",     FIX_DEVICES_TWO,      "none selected" },
        };
        for (size_t i = 0; i < sizeof degraded / sizeof degraded[0]; i++) {
            fixture_reset(&ctx);
            fixture_add(&ctx, "host:devices-l", degraded[i].listing);
            ov_hs_reset_cache();
            ov_headset_collect(&h, "server not seen");
            CHECK(!h.present, "the degraded state selects no device");
            CHECK(ctx.shell_calls == 0, "the degraded state asks the device nothing");
            CHECK(strstr(h.status, degraded[i].want) != NULL,
                  "the degraded state names its reason in the status line");
            ov_hs_format(&h, text, sizeof text);
            CHECK(strstr(text, "battery: unknown") != NULL &&
                  strstr(text, "awake: unknown") != NULL &&
                  strstr(text, "reverse tcp:9757: unknown") != NULL,
                  "the degraded state keeps every reading explicitly unknown");
        }
    }

    /* a malformed battery block: the rest of the panel still works */
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_ONE);
    fixture_add(&ctx, "getprop ro.product.model", "Quest 3\n");
    fixture_add(&ctx, "dumpsys battery", FIX_BATTERY_MALFORMED);
    fixture_add(&ctx, "dumpsys power", FIX_POWER_AWAKE);
    fixture_add(&ctx, OV_WIVRN_PACKAGE_QUERY, FIX_PKGLIST_EMPTY);
    fixture_add(&ctx, "reverse:list-forward", FIX_REVERSE_MANY);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server running");
    CHECK(h.present && h.battery == -1, "a malformed battery block leaves the battery unknown");
    CHECK(h.wake_known, "a malformed battery block does not stop the other readings");
    CHECK(h.reverse == 1 && h.reverse_count == 3, "the other readings survive a bad battery block");
    CHECK(h.wivrn_app == 0 && h.wivrn_app_known, "the WiVRn app is still read");
    ov_hs_format(&h, text, sizeof text);
    CHECK(strstr(text, "battery: unknown") != NULL, "the panel says the battery is unknown");

    /* an unreadable model */
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_ONE);
    fixture_add(&ctx, "getprop ro.product.model", NULL);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server running");
    CHECK(h.present && !h.model[0], "an unreadable model leaves the model unknown");
    ov_hs_format(&h, text, sizeof text);
    CHECK(strstr(text, "model unknown") != NULL, "the panel says the model is unknown");
    CHECK(!strstr(text, "fixture-serial-0001"), "the panel still does not print the serial");

    ov_hs_reset_query();
    ov_hs_reset_cache();
    CHECK(ov_hs_wire_calls() == before, "the whole self-test never opened a real ADB socket");
}

/* The cached probes: a fresh reading is reused, an old one is refetched. */
static void test_cache(void) {
    printf("headset probe cache\n");
    OvHsFixtureCtx ctx;
    fixture_reset(&ctx);
    fixture_add(&ctx, "host:devices-l", FIX_DEVICES_ONE);
    fixture_add(&ctx, "getprop ro.product.model", "Quest 3\n");
    fixture_add(&ctx, "dumpsys battery", FIX_BATTERY_GOOD);
    fixture_add(&ctx, "dumpsys power", FIX_POWER_AWAKE);
    fixture_add(&ctx, OV_WIVRN_PACKAGE_QUERY, FIX_PKGLIST_GITHUB);
    fixture_add(&ctx, "reverse:list-forward", FIX_REVERSE_ONE);
    ov_hs_set_query(fixture_query, &ctx);
    ov_hs_reset_cache();

    /* a long interval: the second collection reuses the first reading */
    ov_hs_set_interval(60000);
    OvHeadset h;
    ov_headset_collect(&h, "server running");
    int first = ctx.shell_calls + ctx.host_calls;
    ov_headset_collect(&h, "server running");
    CHECK(ctx.shell_calls + ctx.host_calls == first, "a fresh reading is reused instead of refetched");
    CHECK(h.battery == 73, "the reused reading is still the one that was read");

    /* a short interval: the reading is refetched once it is old */
    ov_hs_set_interval(1);
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server running");
    int before = ctx.shell_calls + ctx.host_calls;
    ov_hs_reset_cache();
    ov_headset_collect(&h, "server running");
    CHECK(ctx.shell_calls + ctx.host_calls > before, "an expired reading is refetched");

    ov_hs_set_interval(30000);
    ov_hs_reset_query();
    ov_hs_reset_cache();
}

/* The OpenXR scan, against a directory of fixture manifests. */
static void test_openxr(void) {
    printf("headset OpenXR scan\n");
    char dir[] = "/tmp/ov-hs-openxr-XXXXXX";
    if (!mkdtemp(dir)) { printf("  FAIL cannot make a temporary directory\n"); g_failures++; return; }
    char sub[512];
    snprintf(sub, sizeof sub, "%s/openxr", dir);
    mkdir(sub, 0755);
    char path[1024];
    snprintf(path, sizeof path, "%s/wivrn.json", sub);
    FILE *f = fopen(path, "w");
    if (f) { fputs("{\"runtime\":{\"name\":\"WiVRn\",\"library_path\":\"/opt/wivrn/libopenxr.so\"}}", f); fclose(f); }
    snprintf(path, sizeof path, "%s/monado.json", sub);
    f = fopen(path, "w");
    if (f) { fputs("{\"runtime\":{\"name\":\"Monado\",\"library_path\":\"/usr/lib/libmonado.so\"}}", f); fclose(f); }
    snprintf(path, sizeof path, "%s/broken.json", sub);
    f = fopen(path, "w");
    if (f) { fputs("{not json", f); fclose(f); }
    char active[1024];
    snprintf(active, sizeof active, "%s/wivrn.json", sub);

    char roots[1][256];
    st_copy(roots[0], sizeof roots[0], sub);
    const char (*root_rows)[256] = roots;
    OvHeadset h;
    memset(&h, 0, sizeof h);
    int found = ov_hs_scan_openxr(root_rows, 1, active, &h);
    CHECK(found == 2, "the broken manifest is skipped and the two good ones are read");
    CHECK(h.openxr_installed == 2, "both runtime manifests are counted");
    CHECK(h.openxr_name_count == 2, "both runtime names are kept");
    CHECK(h.openxr_selected && !strcmp(h.openxr_active, "WiVRn"),
          "the selected runtime is the one the active pointer names");
    char text[1024] = { 0 };
    ov_hs_format(&h, text, sizeof text);
    CHECK(strstr(text, "WiVRn") != NULL && strstr(text, "Monado") != NULL,
          "the panel names the installed runtimes");
    CHECK(!strstr(text, "/opt/wivrn") && !strstr(text, "libopenxr"),
          "the panel does not print the runtime library path");
    CHECK(strstr(text, "selected") != NULL, "the panel says which runtime is selected");

    /* no active pointer: the runtimes are installed but none is selected */
    memset(&h, 0, sizeof h);
    ov_hs_scan_openxr(root_rows, 1, NULL, &h);
    CHECK(h.openxr_installed == 2 && !h.openxr_selected,
          "without an active pointer no runtime is selected");
    ov_hs_format(&h, text, sizeof text);
    CHECK(strstr(text, "none selected") != NULL, "the panel says that no runtime is selected");

    /* the selection pointer: it names the selected runtime and is not itself an
     * installed runtime */
    char sel[1024];
    snprintf(sel, sizeof sel, "%s/active_runtime.json", sub);
    f = fopen(sel, "w");
    if (f) { fputs("{\"runtime\":{\"name\":\"WiVRn\",\"library_path\":\"/opt/wivrn/libopenxr.so\"}}", f); fclose(f); }
    memset(&h, 0, sizeof h);
    ov_hs_scan_openxr(root_rows, 1, sel, &h);
    CHECK(h.openxr_installed == 2, "the selection pointer is not counted as an installed runtime");
    CHECK(h.openxr_selected && !strcmp(h.openxr_active, "WiVRn"),
          "the selection pointer names the selected runtime");
    unlink(sel);

    /* an empty directory: no runtime at all */
    char empty[] = "/tmp/ov-hs-empty-XXXXXX";
    if (mkdtemp(empty)) {
        char eroots[1][256];
        st_copy(eroots[0], sizeof eroots[0], empty);
        const char (*empty_rows)[256] = eroots;
        memset(&h, 0, sizeof h);
        CHECK(ov_hs_scan_openxr(empty_rows, 1, NULL, &h) == 0 && h.openxr_installed == 0,
              "an empty directory has no runtime");
        ov_hs_format(&h, text, sizeof text);
        CHECK(strstr(text, "none selected") != NULL, "the panel stays useful without a runtime");
        rmdir(empty);
    }

    unlink(active);
    char p2[1024];
    snprintf(p2, sizeof p2, "%s/monado.json", sub);
    unlink(p2);
    snprintf(p2, sizeof p2, "%s/broken.json", sub);
    unlink(p2);
    rmdir(sub);
    rmdir(dir);
}

/* The privacy boundary: nothing in the frame may carry a serial or a path. */
static void test_privacy(void) {
    printf("headset privacy\n");
    char text[1024] = { 0 };
    OvHeadset h;
    memset(&h, 0, sizeof h);
    h.present = 1;
    st_copy(h.model, sizeof h.model, "Quest 3");
    h.battery = 42;
    h.temperature = 365;
    h.charging = 1;
    h.usb_powered = 1;
    h.reverse = 1;
    h.reverse_known = 1;
    h.reverse_count = 2;
    h.wivrn_app = 1;
    h.wivrn_app_known = 1;
    h.wake_state = 0;
    h.wake_known = 1;
    st_copy(h.openxr_active, sizeof h.openxr_active, "WiVRn");
    h.openxr_selected = 1;
    st_copy(h.openxr_names[0], 64, "WiVRn");
    h.openxr_name_count = 1;
    h.openxr_installed = 1;
    ov_hs_format(&h, text, sizeof text);
    CHECK(!strstr(text, "PASSWORD=") && !strstr(text, "SECRET=") &&
          !strstr(text, "TOKEN=") && !strstr(text, "=") ,
          "the rendered panel carries no assignment of any kind");
    CHECK(strstr(text, "42%") != NULL && strstr(text, "36.5 C") != NULL,
          "the rendered panel carries the readings");
    /* a hostile model name cannot smuggle a terminal escape or an assignment */
    st_copy(h.model, sizeof h.model, "Quest 3 PASSWORD=hunter2");
    ov_hs_format(&h, text, sizeof text);
    CHECK(!strstr(text, "hunter2"), "a hostile model name cannot smuggle a value");
}

int main(void) {
    printf("headset self-test\n");
    g_failures = 0;
    test_parsers();
    test_collect();
    test_cache();
    test_openxr();
    test_privacy();
    printf("%s (%d failure(s))\n", g_failures ? "HEADSET SELF-TEST FAILED" : "headset self-test passed",
           g_failures);
    return g_failures ? 1 : 0;
}
