# TLD Revamp — VR architecture (issue #41)

Goal: **a VR session that can be reasoned about without a headset**, and a set of contracts that own the parts of the
game's VR code that the game itself leaves undefined. Everything here is hardware independent: the contracts are pure
state machines with no Unity, no runtime and no headset in them, so they can be tested on a build machine and they can
be reviewed without putting a headset on.

This document records the **observed** call order of the game's VR code (read out of the shipped `Assembly-CSharp.dll`),
the **owner boundaries** that follow from it, and the contracts that implement them.

Nothing in this work changes the multiplayer protocol, and no headset pose is sent over the network. The contracts hold
no pose at all: they hold states, counts and reasons, and they are asked "may I use the pose" rather than being given
one. That is what makes "no headset pose serialization" a property of the code rather than a promise.

## 1. The observed call order

Read from the shipped game assembly (`lib/Assembly-CSharp.dll`, decompiled with `ikdasm` / `ilspycmd`). The names below
are the game's own.

### 1.1 Turning VR on and off — `mainscript.SetVR`

```
mainscript.SetVR(bool _enable)                                  mainscript.cs:1612
├── if (_enable) SetXR(false)                                   // tear the loader down first
├── settingsscript.s.S.BVR = _enable                            // the flag the whole game reads
├── settingsscript.s.tabMisc.toggles[19].ChangeWithoutNotify(BVR)
├── SetXR(_enable)                                              // mainscript.cs:1641
│   ├── XRGeneralSettings.Instance.Manager.InitializeLoaderSync()
│   └── XRGeneralSettings.Instance.Manager.StartSubsystems()
│   (for _enable == false: StopSubsystems() then DeinitializeLoader())
├── if (vrHandsScript.s != null) vrHandsScript.s.Init()         // the hands on or off
├── for each u in mainscript.vrusables: u.UpdVR()               // per-usable VR trigger setup
└── mainScript2.s.UpdVRCanv()                                   // the menu canvas becomes a world-space canvas
```

`mainscript.IsVR()` (mainscript.cs:1655) returns **only** `settingsscript.s.S.BVR`. It says nothing about whether the
XR runtime is running. The flag and the runtime can disagree, and nothing in the game resolves it.

The three callers of `SetVR` are the menu toggle (`PressedDisableVR` / `PressedRefreshVR`) and
`mainscript.SettingsRefreshed()` (mainscript.cs:3844), which replays the saved setting.

### 1.2 The hands — `vrHandsScript`

```
vrHandsScript.Awake()      -> vrHandsScript.s = this            // the static handle the input reads
vrHandsScript.Init()       -> if (mainscript.IsVR()) { SetVisible(true);  gameObject.SetActive(true);  enabled = true; }
                           -> else              { SetVisible(false); gameObject.SetActive(false); enabled = false; }
vrHandsScript.LateUpdate() -> LUpd()
     -> transform.localPosition = Vector3.zero                  // the hand rig is reset every frame
     -> transform.localRotation = Quaternion.Euler(0,0,0)
     -> vrLH.LUpd(); vrRH.LUpd()
```

`SetVisible` toggles the controller graphics and the laser transform of both hands. `Init` is the only thing that
turns the hands on, and it asks `IsVR()` — the flag, not the runtime.

### 1.3 The controller input — `handControllerInputScript.Update`

```
handControllerInputScript.Update()
├── if (!device.isValid) device = InputDevices.GetDeviceAtXRNode(hand)
├── if (mainscript.IsVR()) read the device:
│     primary2DAxis, trigger, grip, triggerButton, gripButton, primary2DAxisClick,
│     primaryButton, secondaryButton, menuButton, deviceVelocity, deviceAngularVelocity,
│     primary2DAxisTouch, primaryTouch, secondaryTouch
├── else: trigger = triggerButtonHold ? 1 : 0;  grip = gripButtonHold ? 1 : 0
├── derive the edges: <x>Down = hold && !last;  <x>Up = !hold && last
└── HandPose()  -> the finger scripts and the hand pose tables
```

A device that is **not valid** keeps its last stick and button values: the edge flags are computed from the previous
frame's `Hold`, and nothing clears them when the device goes away. The game reads a controller that is gone as a
controller that is still held.

### 1.4 The interaction — `vrInteractHandScript`

```
vrInteractHandScript.Update()
├── if (gripButtonHold || triggerButtonDown) Scan()   // Physics.OverlapSphere on the hand's sphere collider
├── if (gripButtonHold):
│     for each collider: use = collider.GetComponentInParent<usablescript>()
│       if (use.slideAble)  use.SlideVRStart(interactOffset.position)
│       if (use.rotateAble) use.RotVRStart(interactOffset.position)
├── else use = null
└── if (use != null): use.SlideVR(...); use.RotVR(...)
```

`usablescript.UpdVR()` (usablescript.cs:1225) is the per-usable half: it makes the collider a trigger on layer 12 and
adds a `sens` component while VR is on, and restores the original layer and trigger state when VR is off.
`usablescript.InitVR()` registers the usable in `mainscript.vrusables`.

### 1.5 The game's input — `inputscript.Update`

```
inputscript.Update()
├── ... the keyboard and mouse block ...
└── if (!mainscript.IsVR()) return;                // inputscript.cs:975
     use      = RH.button1Hold || LH.button1Hold
     useUp    = RH.button1Up   || LH.button1Up
     useDown  = RH.button1Down || LH.button1Down
     if (player != null && use && (player.Car == null || !player.BCanLook)):
          lookX = RH.stick.x + LH.stick.x            // the look: both sticks
          lookY = RH.stick.y + LH.stick.y
     else:
          forward = Clamp(LH.stick.y, 0, 1)          // the walk: the left stick
          backward = Clamp(-LH.stick.y, 0, 1)
          right   = Clamp(LH.stick.x, 0, 1)
          left    = Clamp(-LH.stick.x, 0, 1)
          lookX = RH.stick.x;  lookY = RH.stick.y
          BGas = Clamp(LH.stick.y, 0, 1)
          BBrake = Clamp(-LH.stick.y, 0, 1)
          BSteer = RH.stick.x
     if (LH.stick.magnitude > 0.9f) run = true
     handbrakeBDown = RH.stickButtonDown
     ignition = LH.stickButtonHold
     vrRecenter = RH.button2Hold
     duck: a toggle on LH.stickButtonDown
     jump = RH.stickButtonHold
```

**The left stick is both the walk and the driving gas and brake**, and it is read into the game's ordinary movement
fields. `fpscontroller.GetDir()` / `GetM()` / `Move()` then apply `input.forward` and friends to the character's
Rigidbody — the gameplay root — and the body is networked.

### 1.6 The camera rig — `fpscontroller`

Two roots, and they must never be confused:

| root | what it is | who moves it |
|---|---|---|
| **view root** | `CamParent` (the camera rig), `THeadLean` (the head lean) | the headset, and the game's own look |
| **gameplay root** | `Th` (the head/body transform), the character root, the Rigidbody | the character controller and the network |

The VR-relevant paths:

```
fpscontroller.SetCamParent(_parent)      // in VR the camera is parented to THeadLean, not to _parent
fpscontroller.MoveCamParent / RotateCamParent
fpscontroller.Update:
  if (mainscript.IsVR()):
     if (!otherView()) { Th.position = mainCam.transform.position;   // the gameplay root follows the camera
                         Th.rotation = mainCam.transform.rotation; }
     anim.HHTarget.position = Th.position;                          // the head IK target follows the body
     anim.HHTarget.rotation = Th.rotation;
  fpscontroller.Look(bool, bool):
  if (Bsitting) { if (mainscript.IsVR()) THeadLean.Rotate(...)      // a seated player looks with the head lean
                  else Th.localEulerAngles = ... }
  else          { if (!mainscript.IsVR()) Th.localEulerAngles = ... }
  fpscontroller.UpdHandPos()            // VRHP/VRHR/VLHP/VLHR: the hand poses, from the seat, the pickup or the controller
  fpscontroller.UpdVHandLegTargets()    // in VR: VLHP/VLHR = vrHandsScript.s.LH.graphics.position/rotation
                                         //           VRHP/VRHR = vrHandsScript.s.RH.graphics.position/rotation
  fpscontroller.VRRecenter() / VRRecenterTo(_pos, _eulerAnglesY)
```

`VRCamParent` and `VRCamSkyParent` are the sky camera's rig; `VRUICamera` is the camera the world-space menu canvas
is rendered with.

**The chain that matters:** in VR the camera follows the headset, `Th` (the gameplay root) is set from the camera, and
`anim.HHTarget` (the head IK target) is set from `Th`. The head IK target is what the network sends.

### 1.7 The menu canvas — `mainScript2.UpdVRCanv`

```
mainScript2.UpdVRCanv()  ->  UpdVRCanv(lastOpenedMenu)
  if (mainscript.IsVR()):
     CheckCam()                                   // cam = mainscript.s.player.Cam
     canv.renderMode = RenderMode.WorldSpace
     canv.worldCamera = cam
     if (openedMenu && canvPosMenu != null): the canvas goes to the menu position, scale 0.00055
     else if (canvPosGame != null):               the canvas is parented to the game position, scale 0.0004
  else: canv.renderMode = RenderMode.ScreenSpaceOverlay
  mainscript.s.UpdEmergencyCanvas()               // the emergency canvas: pauseMenuOpen && IsVR()
```

### 1.8 The network — what is actually sent

`PlayerLook.WriteLocalPose` (src/TLDRevamp/Net/PlayerLook.cs:34) is the only place the player's limbs go on the wire:

```
body:  frame.InverseTransformPoint(T.position),  frame.InverseTransformPoint(T.rotation)
limbs: WP(T.InverseTransformPoint(a.HHTarget))   // the head IK target
       WP(T.InverseTransformPoint(a.LHTarget))   // the left hand
       WP(T.InverseTransformPoint(a.RHTarget))   // the right hand
       WP(T.InverseTransformPoint(a.LLTarget))   // the left foot
       WP(T.InverseTransformPoint(a.RLTarget))   // the right foot
       WP(T.InverseTransformPoint(a.Pelvis))
       WR(...)                                    // the same targets' rotations
```

So the head IK target is the path a headset pose would take to the network — not because the network reads a headset,
but because the game copies the camera's pose into `Th` and then into `anim.HHTarget`, and the network sends
`anim.HHTarget`. Everything else the network carries is the **body and the seat**, which the game already synchronizes.

There is no VR in the net layer: `Entities/Net/*.cs` do not name a VR type, and the protocol version is untouched by this
work (`tools/unit/VrBoundaryTest.cs` asserts both).

## 2. The owner boundaries

The observed code has six places that decide something about VR, and none of them owns the decision. Each contract
below takes one of them.

| owner | the decision it owns | the game code it replaces as the source of truth |
|---|---|---|
| `VrSessionMachine` | is there an XR session, and is it running | `settingsscript.s.S.BVR` (the flag alone) |
| `VrTrackingSet` | is the head / a hand tracked right now | `device.isValid` (the last value is kept) |
| `VrHandMachine` | is a hand pressing, holding or releasing | `handControllerInputScript`'s edge flags |
| `VrRigPolicy` | which transform the headset may move | `if (mainscript.IsVR())` scattered over the rig |
| `VrLocomotionPolicy` | may a seated player walk | the left stick going into `input.forward` |
| `VrUiRayMachine` | a VR press, its one activation and its cancel | nothing: the game has no VR UI ray |
| `VrReconnectPolicy` | what a reconnect clears | nothing: the per-player fields survive |

### The rules the boundaries enforce

1. **The view root moves, the gameplay root does not.** The rig policy never allows the gameplay root to follow the
   headset, in any state, even when a caller explicitly asks. A seated player's head comes from the seat, not the
   headset. This is the rule that keeps a headset pose out of the body's motion, and out of the network with it.
2. **A seated player does not walk.** The left stick is gas and brake in a car and a walk on foot; while seated, the
   movement axes and the run are taken away. The look and the seated controls are untouched.
3. **One press, one activation.** A VR press opens exactly one activation; a held press does not repeat it, and a
   cancel releases the control without activating it.
4. **A lost device releases what it held.** A lost hand cancels its grab and its UI press. The game keeps the last
   button values, so this is a real difference.
5. **A failure falls back.** A runtime that refuses leaves the game's legacy VR path in charge, with the hands off and
   the menu canvas flat, and the failure is remembered with its reason.
6. **A reconnect clears.** The session, the holds, the UI presses, the tracking, the seat and the latched axes are all
   cleared, in that order.

## 3. The contracts

All of them live in `src/TLDRevamp/VR/` and are pure: no Unity, no `HarmonyLib`, no net layer, no game type. They hold
states, counts and reasons; none of them holds a position, a rotation or a transform.

| file | what it is |
|---|---|
| `VrSessionState.cs` | the session states: off, initializing, active, suspended, failed; the fallback decision |
| `VrTrackingMachine.cs` | the tracking states: untracked, lost, tracked; the loss and the recovery |
| `VrHandMachine.cs` | the hand's action states: idle, pressed, holding, released, cancelled; the neutral pose |
| `VrRigPolicy.cs` | which targets may come from the view root; the local-head-view-only rule |
| `VrLocomotion.cs` | the seated rule: the movement axes and the run are taken away while seated |
| `VrUiRay.cs` | the UI ray: hit, focus, one activation, cancel, release |
| `VrReconnectPolicy.cs` | what a reconnect clears, and in which order |
| `VrContracts.cs` | the seven above, wired together, with a `Tick` that takes the runtime's answers |
| `VrDiagnostics.cs` | the state as JSON for the debug bridge — states, counts and reasons, never a pose |

### The session states

```
        RequestStart()                ReportStarted()
  Off ──────────────────> Initializing ─────────────> Active
   ^                            │                       │  │
   │                            │ ReportFailure()       │  │ ReportSuspended()
   │ RequestStop()              v                       v  v
   └──────────────────────── Failed <────────────── Suspended
                                     ReportLost()      │
                                     ReportFailure()   │ ReportResumed()
                                                        └────> Active
```

| state | the decision |
|---|---|
| `Off` | no hands, a flat canvas, no head look |
| `Initializing` | the hands and the canvas wait: there is no VR world yet |
| `Active` | hands, a world-space canvas, head look |
| `Suspended` | the hands and the head look are held; the canvas stays in world space |
| `Failed` | the legacy VR path takes over: no hands, a flat canvas |

`RequestStart` and `RequestStop` are **idempotent**: asking for VR twice is one start, asking for flat play twice is one
stop. `ReportStarted` on a running session changes nothing.

## 4. The seams

`src/TLDRevamp/Fixes/VrContractBridge.cs` is the only file that touches the game. It:

- reads what the runtime reports (the display subsystem, the controller devices) and feeds it into the contracts;
- applies the **seated locomotion rule** to the values `inputscript` just read;
- reports the state to the debug bridge (`vr` — a read-only command);
- **does not** write a packet, **does not** move the gameplay root, **does not** enable a VR feature the game does not
  have.

`src/TLDRevamp/Fixes/OpenXrBridge.cs` owns the **XR loader's life**, and it was rewritten (see §7): the XR manager
drives the loader now, the loader is chosen by a policy instead of by the list's order, the controller profiles are
switched on before the loader is initialized, nothing starts a session on its own, and the recenter has one owner.

### The bridges' commands

| command | what it does | read or mutation |
|---|---|---|
| `vr` | the whole VR state (the contracts) | read |
| `openxr` | the XR loader's state | read |
| `openxr state` | the XR loader's state (the same read, named) | read |
| `openxr start` | starts a session (through the XR manager) | mutation |
| `openxr retry` | retries a failed start | mutation |
| `openxr stop` | stops the session (through the XR manager) | mutation |
| `openxr recenter` | the recenter | mutation |
| `openxr recenter origin` | the recenter, and the runtime's tracking origin as well | mutation |

`openxr` on its own and `openxr state` are reads: a session that denies developer access still lets a
joiner ask what the policy is. The other forms change the session and go through the developer-access
policy like every other mutating command.

The split is not written twice. It lives in the pure core (`VrBridgeCommandPolicy` in
`src/TLDRevamp/VR/VrLoaderPolicy.cs`), and both the debug bridge and the XR bridge ask it, so the two
cannot drift apart. The policy also owns the command's SYNTAX, and the bridge's `help` prints that
constant, so the help lists the command exactly once, with one syntax (the first wave listed it twice,
with a stale syntax that did not even mention `retry`).

A form the bridge does not know is refused with the usage line. A refusal is neither a read nor a
mutation: it changes nothing, so it does not need developer access either.

### The hooks

| hook | what it does |
|---|---|
| `mainscript.SetVR` prefix | turns the request into a session decision before the game's own XR setup |
| `mainscript.SetVR` postfix | reports what the runtime really did, so the contract knows whether the session runs |
| `inputscript.Update` postfix | applies the seated rule to the stick values the game just read |
| `fpscontroller.GetIn` / `GetOut` postfix | the seat and the driving state, which decide whether the stick may move the body |

## 5. The diagnostics

The debug bridge's `vr` command (read-only) returns the whole state as JSON: the session state and reason, the tracking
state and the loss and recovery counts, the hand actions and the neutral poses, the UI ray states and the activation
counts, the rig's reason and its body-motion refusal, the locomotion counts and the reconnect state's cleanliness.

It carries no pose. `tools/unit/VrContractsTest.cs` asserts this twice: by reflection over every contract type (no
field may be a `Vector3`, a `Quaternion`, a `Transform` or a `Pose`), and by checking the rendered JSON holds no
decimal number — with the guard itself checked against a real coordinate, so it is not a check that passes because it
never fires.

## 6. The XR loader's life (issue #41)

### 6.1 What the game ships

Read out of the game's own files (`lib/Assembly-CSharp.dll` decompiled, `lib/Unity.XR.Management.dll` and
`lib/Unity.XR.OpenXR.dll` disassembled, and the strings in `globalgamemanagers.assets`):

- the XR General Settings asset lists **two** loaders: `OpenXRLoader` and `OpenXRLoaderNoPreInit`, and **all seven**
  controller interaction profiles;
- the managed folder has `Unity.XR.OpenXR.dll` and **no other XR runtime's assembly at all**: there is no legacy VR
  runtime to fall back to. The fallback when OpenXR cannot start is non-VR gameplay, and the code says so instead of
  promising a fallback that does not exist;
- `mainscript.SetVR(bool)` is the game's only VR entry. It sets `settingsscript.s.S.BVR`, calls `SetXR(bool)` — which
  is `XRGeneralSettings.Instance.Manager.InitializeLoaderSync()` + `StartSubsystems()`, and `StopSubsystems()` +
  `DeinitializeLoader()` for the way back — and then turns the hands, the usables and the menu canvas on;
- `mainscript.IsVR()` reads **only** `settingsscript.s.S.BVR`: the flag and the runtime can disagree, and the game
  never resolves it.

### 6.2 The XR manager owns the lifecycle

`XRManagerSettings` keeps the loader's state in three places that a caller has to keep in step: `activeLoader` (a
private backing field, set only by `InitializeLoaderSync` / `DeinitializeLoader`), `m_InitializationComplete` (set to
true only after a successful `loader.Initialize()` inside `InitializeLoaderSync`), and its own registered-loader set.

The first wave of this work drove the loader itself (`loader.Initialize()`, `loader.Start()`), so the manager never
learned about it. The consequences were concrete: the manager's `activeLoader` stayed null, the game's own
`SetXR(false)` hit the "StopSubsystems without an initialized manager" path, and the XR session was left running while
the game believed VR was off.

Every step goes through the manager now:

| step | the call |
|---|---|
| choose | `VrLoaderPolicy.Choose` (see 7.3) |
| configure | the controller profiles are switched on (`feature.enabled = true`) |
| initialize | `manager.InitializeLoaderSync()` |
| start | `manager.StartSubsystems()` |
| verify | the display and the input subsystems are really running |
| stop | `manager.StopSubsystems()` |
| deinitialize | `manager.DeinitializeLoader()` (it stops the subsystems itself first) |

`tools/unit/OpenXrBridgeTest.cs` asserts that the bridge never calls `loader.Initialize()`, `loader.Start()`,
`loader.Stop()` or `loader.Deinitialize()` itself, and `tools/unit/VrLoaderPolicyTest.cs` exercises the lifecycle's
transitions.

### 6.3 The loader is chosen by a policy

The manager keeps its loaders in a list whose order nobody promised, and the game lists both OpenXR loaders. Taking
"the first OpenXR loader in the list" would make the choice depend on that order, and the two loaders are not
interchangeable (`OpenXRLoaderNoPreInit` skips the runtime's pre-init handshake).

`VrLoaderPolicy.Choose` ranks them explicitly: the **plain** `OpenXRLoader` always wins, whatever the list's order;
`OpenXRLoaderNoPreInit` is used only when it is the only OpenXR loader there is, and only when the install allows it
(`OpenXrAllowNoPreInit`, off by default). An unknown loader is refused rather than guessed at.

### 6.4 The controller profiles are configured before the loader is initialized

The OpenXR loader reads the enabled features **when it initializes**, so a profile switched on after the initialize is
never used for that session. The first wave configured them after the start, so it could not enable the profiles it
claimed to enable.

There are two paths into the loader, and both configure the profiles first:

- the bridge's own `openxr start` (the `ConfigureFeatures` step, before `InitializeLoaderSync`);
- the game's own `mainscript.SetVR(true)`, which calls `SetXR(true)` → `InitializeLoaderSync()` on the next line: the
  `SetVR` prefix configures the profiles before that happens.

### 6.5 Nothing starts a session on its own

The first wave had a per-frame tick that started the loader whenever the `BVR` preference was on, and its failure was
sticky: after one failed attempt the bridge never tried again, even with the headset plugged in.

There is no per-frame start now. The lifecycle is explicit: `openxr start` starts it, `openxr retry` retries a failed
start, and a failure is never retried on its own. The state machine (`VrLoaderLifecycle`) is pure and holds the
states (idle, starting, running, stopping, failed) and the failures (no manager, no loader, refused loader,
initialize, start, verify).

When the game started the session itself, the bridge **adopts** it instead of initializing it a second time: the
manager warns ("XR Management has already initialized an active loader in this scene") and a second `Initialize()` on
the same OpenXR session is exactly the state corruption the first wave produced.

### 6.6 A stop really stops

`VrLoaderLifecycle.RequestStop()` is not a query: it MOVES the machine (`running` → `stopping`) and RETURNS the
action to perform. Two defects dropped that answer, and both are fixed:

1. `Disable()` took an early branch when the manager was already down (the game tore the session down itself)
   and called `RequestStop()` for its side effect only. The machine moved to `stopping` and the stop was never
   accounted for: the reported state said `stopping` for a session that was off, and the stop counter never moved.
   The branch now accounts for the stop (`ReportExternallyStopped()`) and reads the state from the machine
   AFTER the transition.
2. `Drive()` always started at the `ChooseLoader` step, so even the branch that DID honour the action restarted
   the machine at the wrong step. A stop therefore never reached `manager.StopSubsystems()` or
   `manager.DeinitializeLoader()` at all: **the XR session kept running while the bridge reported that it had
   stopped.** `Drive()` now starts at the action it was given.

A stop from a FAILED start leaves the failure alone (nothing is running, and the failure has to survive so the
explicit retry still works), and a stop that is already in progress is finished by the same request.

`tools/unit/OpenXrBridgeBehaviourTest.cs` drives the production bridge against faithful Unity/OpenXR stubs and
checks that a stop really calls the manager, that the lifecycle and the REPORTED state always agree, and that a
`Disable()` from idle / starting / running / stopping / failed leaves the machine in a state it can leave.

The exact live script for the Quest 3 (every command, every assertion, and what each step is allowed to change) is
in [docs/VR-LIVE-TEST.md](VR-LIVE-TEST.md).

### 6.7 The recenter has one owner

The game already recenters: `mainScript2.LateUpdate` reads `inputscript.vrRecenterDown` and calls
`mainScript2.VRRecenter()`, which moves the camera rig (`CamParent`) onto the head-lean transform. That is the rig's
recenter, and it is the one the player sees.

The first wave also called `XRInputSubsystem.TryRecenter()`, which moves the **runtime's tracking origin**: a
different thing, in a different place. Doing both on one key press moves the rig once and the world origin once, so
the two disagree — and the disagreement compounds, because the game's next recenter re-derives the rig from a
head-lean transform the runtime has already moved. Its origin test was inverted as well (it set the mode only when
the mode already was that one).

`VrTrackingPolicy` decides now: the game owns the recenter, and the runtime's origin is only touched when it is asked
for explicitly (`openxr recenter origin`) and the game does not recenter itself. The first wave's `inputscript` patch
is gone: the game reads the same flag and acts on it in the same frame.

### 6.8 The state says what it knows

`openxr` reports the loader's state, the failure and its reason, which loader was chosen and whether the manager
accepted it, whether the manager's own initialization flag is set, the runtime's name and API version, the display and
the input, the controllers, and the counts (starts, stops, failures, retries). It carries no pose, and the
`OpenXrBridgeTest` checks that the reported fields never name a transform or a device pose.

## 7. What is not here

- **No headset claims.** Nothing in this document says how the VR looks, feels or performs; the contracts are about
  state, and the only observations are of the game's call order.
- **No protocol change.** The multiplayer protocol is not touched: no new message id, no version bump, no field.
- **No new VR feature.** The game has no VR UI ray; the contract for one is a contract for the *rules* a ray would
  follow, and the bridge does not yet draw a ray.
- **No seat, camera or menu rewrite.** The contracts observe the seat and the camera; they do not own them.
- **No headset claim.** Nothing here says that a runtime starts, that the controllers bind, that the menu is
  comfortable or that the frame rate holds. Those need a real headset and a human observer. What the code does say is
  that the paths are wired so that they *can* work: the manager owns the loader, the loader is chosen by a policy, the
  profiles are on before the loader is initialized, and a failure is reported with its reason instead of being papered
  over.
- **No `libs.sh` change is needed for the profiles.** The build references the seven controller profile types
  directly, and `Unity.InputSystem.dll` (which `Unity.XR.OpenXR.dll` references) is already in `lib/` through the
  game's own managed folder. A build machine that copies the reference assemblies with `tools/libs.sh` gets them too.
