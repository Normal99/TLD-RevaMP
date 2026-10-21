# The live XR test on the Quest 3 (issue #41)

This is the exact script the coordinator runs on the Quest 3. It is written step by step, with the command, what
the command must print, and the assertion it has to hold. Nothing here launches the game: the coordinator owns the
live run, through the guarded path.

Every command below is READ-ONLY except the ones marked **[MUTATION]**, which change the XR session. A **[MUTATION]**
step needs the session's developer access (`developer access is disabled by the host's session policy` means the
step cannot run: the policy has to allow it first).

The hardware this was written for:

    Quest 3, serial 2G0YC5ZG5K015L, adb over usb
    the host's WiVRn server on port 9757, with `adb reverse tcp:9757 tcp:9757`
    the OpenXR runtime: WiVRn (/usr/share/openxr/1/openxr_wivrn.json -> /usr/lib/wivrn/libopenxr_wivrn.so)

---

## Step 0 — the hardware, before anything else (no game needed)

    adb devices -l
    adb -s 2G0YC5ZG5K015L reverse --list
    ss -ltn | grep 9757
    adb -s 2G0YC5ZG5K015L shell "echo -n '' | toybox nc -w 3 127.0.0.1 9757 && echo CONNECTED"

ASSERTIONS

| what | assertion |
|---|---|
| `adb devices -l` | lists `2G0YC5ZG5K015L  device  ... model:Quest_3` |
| `adb reverse --list` | contains `UsbFfs tcp:9757 tcp:9757` |
| `ss -ltn` | `wivrn-server` is LISTENING on 9757 |
| the `nc` probe | prints `CONNECTED` (the headset reaches the host's WiVRn server) |

Then the preflight, which checks all of that plus the runtime, in one place:

    python3 tools/quest3vr_live.py preflight

ASSERTIONS: 7 of 7 (the sixth is the active runtime, see step 0b).

### Step 0b — the active OpenXR runtime

The Unity OpenXR loader finds its runtime the way every OpenXR client does: `$XR_RUNTIME_JSON` first, then the
ACTIVE runtime file (`$XDG_DATA_HOME/openxr/1/active_runtime`, which is what the WiVRn server writes when a
headset connects), then nothing.

MEASURED on this machine, with the headset ASLEEP:

    XR_RUNTIME_JSON                     = (unset)
    ~/.local/share/openxr/1/active_runtime = (ABSENT)
    installed manifests                 = /usr/share/openxr/1/openxr_wivrn.json, openxr_wivrn.i686.json

So the runtime is INSTALLED but not ACTIVE. The WiVRn server sets the active runtime **when a headset connects**
(its own help: `--no-manage-active-runtime  don't set the active runtime on connection`). With the headset asleep
there is no connection, so there is no active runtime, so the loader has nothing to load.

The fix is a device action, and it is the coordinator's:

    1. wake the headset: wear it, or `adb -s 2G0YC5ZG5K015L shell input keyevent KEYCODE_POWER`
    2. start the WiVRn client on the headset (the Quest store build is `org.meumeu.wivrn.github`)
    3. re-run `python3 tools/quest3vr_live.py preflight` — the active-runtime assertion must now hold

THE FALLBACK, if the headset is not going to connect: the runtime is installed and its manifest resolves, so it
can be selected explicitly for the launch. Verified to resolve to a library that exports the exact symbol the
OpenXR loader binds:

    $ python3 -c "import json,os,ctypes; d=json.load(open('/usr/share/openxr/1/openxr_wivrn.json')); \
        lib=os.path.normpath(os.path.join('/usr/share/openxr/1', d['runtime']['library_path'])); \
        c=ctypes.CDLL(lib); print(lib, hasattr(c,'xrNegotiateLoaderRuntimeInterface'))"
    /usr/lib/wivrn/libopenxr_wivrn.so True

Pass it into the game process as `XR_RUNTIME_JSON=/usr/share/openxr/1/openxr_wivrn.json`. The launch composition
is the coordinator's lane; this lane did not change it.

---

## Step 1 — the coordinator launches the game through the guarded path

NOT this lane's job. The requirements are the mandatory ones: `tools/testlaunch.py` + `tools/testdisplay.py`, a
UNIQUE game directory, a UNIQUE Proton prefix, an EXPLICIT unique bridge port (the examples below use 27050), the
launch token set, and `UnityEngine.Application.dataPath` verified. Never a raw Proton script, never the owner
install, never `~/tld-installs/test/game`, never a shared prefix, never `nohup`/`setsid`/`&`.

The game must have the bridge ON: `Bridge = true` in `BepInEx/config/tldrevamp.core.cfg`. Without it the bridge
is off and every command below fails with "unknown command".

---

## Step 2 — the read-only view (safe at any time, no developer access needed)

    python3 tools/quest3vr.py status --port 27050
    python3 tools/quest3vr_live.py status --port 27050

ASSERTIONS

| what | assertion |
|---|---|
| the command | answers with one JSON line, no exception |
| `bridge.state` | one of `idle`, `starting`, `running`, `stopping`, `failed` |
| `bridge.running` | matches `state == running` |
| `bridge.managerInitialized` | `true` exactly when the XR manager holds an initialized loader |
| `bridge.display` / `bridge.input` | `true` when the session is really up (the runtime's own proof) |
| `bridge.loader` / `loaderName` / `loaderUsable` | the plain `OpenXRLoader` was chosen, and it is usable |
| `bridge.featuresConfigured` | `true` — the seven controller profiles were switched on |
| `bridge.runtime` / `bridge.api` | the runtime's name and API version (the WiVRn runtime reports itself here) |
| `bridge.originMode` / `originSupported` | the runtime's tracking-origin mode, and the modes it supports |
| `bridge.controllers.count` | the number of controllers the runtime reports |
| `bridge.recenter` / `starts` / `stops` | the counters |

`status` sends ONLY the pure read `openxr`. It cannot start or stop a session and cannot need developer access.

---

## Step 3 — the gate

    python3 tools/quest3vr_live.py gate --port 27050

The gate is 29 assertions in 6 steps (31 with --check-origin). It exits non-zero if any of them fails.

### Step 1 of the gate — the state before anything is started [READ]

    openxr

ASSERTIONS: the bridge answers the pure read; the state reports the loader, the manager, the controllers, the
recenter count and the tracking-origin mode.

### Step 2 of the gate — start the session [MUTATION]

    openxr start

ASSERTIONS

| what | assertion |
|---|---|
| the answer | `running` is `true` |
| `state` | `running` |
| `managerInitialized` | `true` |
| `display` | `true` — the runtime is really presenting |
| `input` | `true` — the runtime is really taking input |
| `featuresConfigured` | `true` — the profiles were configured BEFORE the loader initialized |
| `loader` / `loaderUsable` | the plain OpenXR loader, usable |
| `originMode` | the runtime reports a tracking-origin mode |
| `originSupported` | the runtime reports the modes it supports |

If the start FAILS, the gate retries once with `openxr retry` (a retry is always explicit: nothing retries on its
own). A start that fails even after the retry means the runtime was not found — check step 0b.

### Step 3 of the gate — the controllers [READ]

    openxr

ASSERTIONS: at least one controller is reported; at least one is `valid`; a LEFT hand controller is reported; a
RIGHT hand controller is reported.

A controller that is reported but NOT valid is the shape of "the headset is on but the controllers did not bind".

### Step 4 of the gate — the recenter moves the rig ONCE [MUTATION]

    openxr recenter

ASSERTIONS

| what | assertion |
|---|---|
| the recenter count | it went up by EXACTLY one |
| the origin mode | UNCHANGED — a plain recenter must not move the runtime's tracking origin |
| the recenter count, read again | unchanged — the recenter happened once, not twice |

The rig the player sees is the GAME's own rig recenter. The runtime's tracking origin is a different thing, in a
different place, and moving both on one key press is what makes the world and the rig drift apart.

### Step 4b of the gate (OPTIONAL) — the explicit origin request [MUTATION]

    python3 tools/quest3vr_live.py gate --port 27050 --check-origin

Adds one step that sends `openxr recenter origin`: the ONLY path that may move the runtime's tracking origin.

ASSERTIONS: the explicit request was counted as a recenter; the runtime reports an origin mode afterwards; the
script prints the mode before and after, so the movement is visible in the transcript.

This step is OFF by default because it MOVES THE PLAYER'S WORLD. It only has to be proven once. Run it after the
plain gate has passed, and note that the player's world origin shifts when it runs.

### Step 5 of the gate — stop the session [MUTATION]

    openxr stop

ASSERTIONS

| what | assertion |
|---|---|
| `running` | `false` |
| `state` | `idle` — **not** `stopping` and **not** `failed` |
| `managerInitialized` | `false` — the manager really let the loader go |
| `stops` | at least 1 — the stop was counted |

THIS IS THE STEP THAT CATCHES THE CONFIRMED DEFECT. Before the fix, `Disable()` threw away the action the
lifecycle returned, so the machine was left in `stopping` with the stop uncounted. If the state comes back as
`stopping`, the defect is live.

### Step 6 of the gate — start again [MUTATION]

    openxr start

ASSERTION: the session started again, so the run is left in a known state.

---

## Step 4 — the long run (optional, after the gate)

    python3 tools/quest3vr.py run --seconds 600 --port 27050 --out bench/quest3-openxr.json

It samples the session's state, the frame timings and the error counters every few seconds, recenters on a timer,
takes a headset screenshot at the end, and ALWAYS stops the session afterwards (whatever happened).

A Quest 3 whose screen is asleep answers `screencap` with an empty file: a missing screenshot is not a failed
session, and the tool reports it as such.

---

## Step 5 — the human check (only a headset can answer this)

The gate cannot judge these; they are a two-minute human check with the headset on:

1. the game is in VR: the view is stereo, the menu canvas is on, and the hands appear.
2. the RECENTER moves the rig ONCE and the world does NOT jump: press the game's recenter key, look at the
   horizon. If the world jumps under you, the rig and the origin both moved — that is the old double-recenter
   defect, and the gate's step 4 is what proves it offline.
3. the CONTROLLERS are tracked: both hands appear and follow the real controllers.
4. SEATED COMFORT and the in-headset visual quality: a human judgement, not an assertion.
5. the WiVRn STREAMING LATENCY: a human judgement.

---

## What this script does NOT check, and why

- **that the runtime really loaded**: the gate's step 2 asserts `display` and `input` are running, which IS the
  runtime's own proof that it loaded and bound.
- **that the seven controller profiles bind to real controllers**: the profiles being configured before the
  initialize is proven offline (the behaviour test's stub loader records the profiles that were enabled at the
  moment it initialized); the real binding is the gate's step 3 and the human check.
- **that the game's own `SetVR(true)` path reaches the initialize with the profiles on**: the Harmony prefix does
  it, but the ORDER between the two `SetVR` patches (`OpenXrBridge.SetGameVR` and `VrContractBridge.SetVrSeam`,
  both at the default priority 400) is only observable at runtime. Both prefixes call the same idempotent session
  contract, so on the available evidence the duplication is benign.
- **that a failed start leaves the game playable without VR**: a failed start is reported with a reason and the
  game stays flat; the human check can confirm the flat menu canvas.
- **the in-headset visual quality, comfort, tracking accuracy and streaming latency**: a human check (step 5).

---

## The offline proof that backs this script

The script is not a wish list: it is verified offline against a faithful stub bridge, and every assertion in it
has a matching offline guard.

    $ cd tools && python3 -m unittest test_vrtool
    Ran 18 tests ... OK

    $ dotnet run --project tools/unit/OpenXrBridgeBehaviourTest.csproj -c Release
    OpenXR bridge behaviour: all 114 checks passed

    $ python3 -m unittest tools.test_vr_mutations
    Ran 16 tests ... OK       (every mutation is caught)

The gate is known to PASS against a faithful bridge and to FAIL against a bridge with the defects:
`test_the_live_gate_fails_when_the_bridge_lies` (a bridge that reports `failed` after a stop fails the gate) and
`test_the_live_gate_catches_a_moved_tracking_origin` (a bridge that moves the origin on a plain recenter fails
the gate).
