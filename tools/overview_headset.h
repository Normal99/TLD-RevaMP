/* overview_headset.h - the Quest / WiVRn read-only state for tools/overview.
 *
 * This module owns the only ADB client in the tool.  It speaks the ADB server
 * protocol directly to an ALREADY running server on the loopback interface, so
 * the adb binary is never executed and the daemon can never be started by the
 * overview.  No WiVRn, Steam, game or service is started either.
 *
 * Every request is a fixed, whitelisted string:
 *   host:devices-l                  the device listing
 *   reverse:list-forward            the reverse mappings
 *   shell:getprop ro.product.model  the model name
 *   shell:dumpsys battery           the battery, charging and temperature state
 *   shell:dumpsys power             the wake state
 *   shell:pm list packages org.meumeu.wivrn   the WiVRn client app
 *
 * WiVRn ships under two package ids: the Play Store build is
 * "org.meumeu.wivrn" and the GitHub build is "org.meumeu.wivrn.github".  The
 * package manager's filter is a substring match, so one fixed query for
 * "org.meumeu.wivrn" finds either of them; the answer is matched against the
 * exact known ids, and no other id is reported as the WiVRn client.
 *
 * The serial of the selected device is used on the wire and never leaves this
 * module: it is not printed, not stored in the frame, and not part of any
 * error message.  Everything the panel shows is a count, a generic model name
 * or a public runtime name.
 */
#ifndef OVERVIEW_HEADSET_H
#define OVERVIEW_HEADSET_H

#include "overview_data.h"

#include <stddef.h>

/* The reverse mapping the tool looks for: the port the streaming bridge uses. */
#define OV_REVERSE_MAPPING "tcp:9757"

/* The size of one reading slot.  A dumpsys block on a real headset is over
 * 10 KB and a long device listing is over 8 KB, so a slot holds one of each;
 * anything bigger is refused rather than truncated. */
#define OV_HS_SLOT_BYTES 65536

/* The WiVRn client app ships under two ids.  The package manager filters by
 * substring, so this one fixed query finds either build; the answer is then
 * matched against these exact ids. */
#define OV_WIVRN_PACKAGE_QUERY "pm list packages org.meumeu.wivrn"
#define OV_WIVRN_PACKAGE_PLAY  "org.meumeu.wivrn"
#define OV_WIVRN_PACKAGE_GITHUB "org.meumeu.wivrn.github"

/* ---- the ADB wire, one socket at a time ---- */

/* Send one framed service request and read the server's four byte status.
 * Returns 1 when the server answered OKAY.  The deadline bounds the wait. */
int ov_adb_send_service(int fd, const char *service, long long deadline_ms);

/* Read a length-prefixed answer (the reply of a host service). */
int ov_adb_read_payload(int fd, char *out, size_t n, long long deadline_ms);

/* Read the rest of the stream (the reply of a shell service) until the peer
 * closes it.  Returns 1 only on a clean end of stream, so a truncated answer
 * can never be read as a complete one. */
int ov_adb_read_stream(int fd, char *out, size_t n, long long deadline_ms);

/* Select the transport and run one shell command on the device. */
int ov_adb_transport_query(int fd, const char *serial, const char *command,
                            char *out, size_t n, long long deadline_ms);

/* The high level requests.  Each opens one socket and closes it again. */
int ov_adb_host_query(const char *service, char *out, size_t n);
int ov_adb_shell_query(const char *serial, const char *command, char *out, size_t n);

/* ---- the transport the collector uses ----
 *
 * The self-test replaces it with a fixture that answers from memory, so no
 * socket is ever opened while the self-test runs. */
typedef struct {
    int         kind;        /* OV_ADB_HOST, OV_ADB_SHELL or OV_ADB_DEVICE */
    const char *serial;      /* the selected device; only for a shell request */
    const char *command;     /* the fixed shell command (a shell request) */
    const char *service;     /* the fixed host or device service */
} OvAdbRequest;

/* The three kinds of request the module makes. */
#define OV_ADB_HOST   0   /* a host service on the server itself */
#define OV_ADB_SHELL  1   /* a shell command on the selected device */
#define OV_ADB_DEVICE 2   /* a device service on the selected transport */

typedef int (*OvAdbQuery)(void *ctx, const OvAdbRequest *req, char *out, size_t n);

void ov_hs_set_query(OvAdbQuery fn, void *ctx);
void ov_hs_reset_query(void);

/* How often the production transport was used.  The self-test watches this to
 * prove that the real wire was never touched. */
long ov_hs_wire_calls(void);

/* ---- the module's own knobs (the collector sets them from ov_config) ---- */

void ov_hs_set_timeout(int ms);          /* the deadline of one probe */
void ov_hs_set_interval(long long ms);    /* how long a reading stays fresh */
void ov_hs_set_port(int port);            /* the ADB server port */
void ov_hs_reset_cache(void);             /* drop the cached reading */

/* ---- the collectors ---- */

/* The cached host:devices-l reply.  Returns 1 when the listing was read. */
int ov_hs_devices_listing(char *out, size_t n);

/* Fill the headset state.  Every probe is bounded and cached.  The WiVRn server
 * state comes from the caller because the process scan already knows it. */
void ov_headset_collect(OvHeadset *h, const char *wivrn_server);

/* ---- the pure parsers: the self-test drives them with fixture text ---- */

int ov_hs_parse_battery(const char *text, OvHeadset *h);
int ov_hs_parse_model(const char *text, OvHeadset *h);
int ov_hs_parse_power(const char *text, OvHeadset *h);
int ov_hs_parse_wivrn_app(const char *text, OvHeadset *h);
int ov_hs_parse_package_list(const char *text, OvHeadset *h);
int ov_hs_parse_reverse(const char *text, OvHeadset *h);
int ov_hs_parse_devices(const char *text, OvHeadset *h, char *selected, size_t selected_n);
int ov_hs_scan_openxr(const char (*roots)[256], int nroots, const char *active, OvHeadset *h);

/* The OpenXR manifest locations, resolved from the environment.  The paths are
 * never printed; only the runtime names inside them are shown. */
void ov_hs_openxr_locations(char roots[OV_HS_MAX_ROOTS][256], int *nroots,
                             char *active, size_t active_n);

/* Render the headset block.  The result never holds a serial, a path or a
 * secret: only counts, a model name and the public runtime names. */
void ov_hs_format(const OvHeadset *h, char *out, size_t n);

#endif /* OVERVIEW_HEADSET_H */
