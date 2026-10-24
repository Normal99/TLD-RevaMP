using System;
using System.Collections.Generic;
using TLDRevamp.Cinematic;

// Test-only: RUNTIME proof of the cinematic camera's "no camera mutation" contract.
//
// The worker's own test asserts the state machine's properties, not that the game's cameras are
// left untouched. This test drives the PRODUCTION CinematicRig with Unity stubs and records every
// write the rig makes to any camera; it then asserts that none of the five gameplay cameras is
// touched, that the rig is its own root, and that the rig's own camera is the only one changed.
//
// Not part of the tree.
static class CamRt {
  static int checks, fails;
  static void Check(string n, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n); if (!ok) fails++; }
  static void Check(string n, bool ok, string why) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (ok ? "" : "  -- " + why)); if (!ok) fails++; }

  static int Main() {
    // The rig is built against the Unity stub; every write to a Camera is recorded.
    UnityEngine.Camera.ResetWrites();
    var rig = new CinematicRig();

    // a gameplay camera with a known state
    var gameplay = new UnityEngine.Camera();
    gameplay.transform.position = new UnityEngine.Vector3(10f, 3f, 20f);
    gameplay.fieldOfView = 70f;
    gameplay.enabled = true;
    gameplay.cullingMask = 0x1234;
    gameplay.depth = -1f;

    // 1. the rig copies the gameplay camera's setup, it does not change the gameplay camera.
    //    The recording is reset right before the call, so a write made DURING the copy is caught: a copy that
    //    also wrote to the gameplay camera would otherwise be hidden by the setup writes above.
    UnityEngine.Camera.ResetWrites();
    rig.ApplyGameplayMasks(gameplay);
    Check("the gameplay camera was not written to while the rig copied its setup",
          !UnityEngine.Camera.WasWritten(gameplay),
          "the rig wrote to the gameplay camera it was copying from");

    Check("the gameplay camera's position is untouched", gameplay.transform.position.x == 10f && gameplay.transform.position.z == 20f);
    Check("the gameplay camera's rotation is untouched", gameplay.transform.rotation.x == 0f && gameplay.transform.rotation.y == 0f);
    Check("the gameplay camera's FOV is untouched", gameplay.fieldOfView == 70f);
    Check("the gameplay camera stays enabled", gameplay.enabled);
    Check("the gameplay camera's culling mask is untouched", gameplay.cullingMask == 0x1234);
    Check("the gameplay camera's depth is untouched", gameplay.depth == -1f);
    Check("the gameplay camera's clip planes are untouched", gameplay.nearClipPlane == 0.3f && gameplay.farClipPlane == 1000f);
    Check("the gameplay camera's parent is untouched", gameplay.transform.parent == null,
          "the gameplay camera was reparented");
    Check("the gameplay camera's clear flags are untouched", gameplay.clearFlags == UnityEngine.CameraClearFlags.Skybox);

    // 2. the rig has its own camera, on its own root
    var cam = rig.Camera();
    Check("the rig has its own camera", cam != null && cam != gameplay);
    Check("the rig's camera is named for the feature", UnityEngine.Camera.LastCreatedName == "TLDRevampCinematicCamera");
    Check("the rig's own GameObject is a root", rigCamGameObjectIsRoot(cam));
    Check("the rig's camera is not a child of anything", rigCamParentIsNull(cam));
    Check("the rig's camera is a root of its own", cam.transform.parent == null,
          "the rig's camera was parented to " + (cam.transform.parent == null ? "nothing" : "something"));

    // 3. the rig writes only to its own camera.
    //    The rig's own construction (SetParent(null) on its own transform) is a write to the RIG's
    //    camera, not to a gameplay camera, so the assertion below is about the gameplay cameras.
    UnityEngine.Camera.ResetWrites();
    rig.Position = new UnityEngine.Vector3(1f, 2f, 3f);
    rig.Rotation = new UnityEngine.Quaternion(0f, 90f, 0f, 1f);
    rig.Fov = 42f;
    rig.Apply();
    Check("the rig's camera got the position", cam.transform.position.x == 1f && cam.transform.position.y == 2f && cam.transform.position.z == 3f);
    Check("the rig's camera got the rotation", cam.transform.rotation.y == 90f);
    Check("the rig's camera got the FOV", cam.fieldOfView == 42f);
    var written = UnityEngine.Camera.Writes();
    var strangers = written.FindAll(c => c != cam);
    Check("no camera other than the rig's own was written to", strangers.Count == 0,
          "also written to: " + strangers.Count + " camera(s)");
    Check("the rig's own camera was written to", UnityEngine.Camera.WasWritten(cam));
    Check("no gameplay camera was written to", !UnityEngine.Camera.WasWritten(gameplay));

    // 4. the gameplay camera is still exactly as it was after the rig moved
    Check("the gameplay camera is still where it was", gameplay.transform.position.x == 10f && gameplay.transform.position.z == 20f);
    Check("the gameplay camera still has its FOV", gameplay.fieldOfView == 70f);
    Check("the gameplay camera is still enabled", gameplay.enabled);
    Check("the gameplay camera was never written to at all", !UnityEngine.Camera.WasWritten(gameplay));

    // 5. the rig's camera starts disabled (the game owns when it is on)
    var rig2 = new CinematicRig();
    var cam2 = rig2.Camera();
    Check("the rig's camera starts disabled", !cam2.enabled);
    Check("the rig's camera is in front of the world's", cam2.depth == 50f);

    // 6. the game's far layer (distant terrain on): the rig mirrors it from its own viewpoint, reads only.
    //    With the far camera on, the gameplay camera clears depth only; a rig of one camera then let the player's
    //    own view show through everywhere the rig drew nothing (seen live: the sky showed the room the player was in).
    var rig3 = new CinematicRig();
    var play3 = new UnityEngine.Camera(); play3.clearFlags = UnityEngine.CameraClearFlags.Depth; play3.cullingMask = 0x10; play3.farClipPlane = 500f;
    var far3 = new UnityEngine.Camera(); far3.clearFlags = UnityEngine.CameraClearFlags.Skybox; far3.cullingMask = 557057;
    far3.nearClipPlane = 200f; far3.farClipPlane = 12000f; far3.depth = -10f; far3.enabled = true;
    far3.transform.position = new UnityEngine.Vector3(5f, 6f, 7f);
    UnityEngine.Camera.ResetWrites();
    rig3.MirrorStack(play3, far3);
    rig3.Position = new UnityEngine.Vector3(100f, 25f, 200f); rig3.Fov = 33f;
    rig3.Apply(); rig3.SetEnabled(true);
    Check("the far layer is mirrored while the game's far camera is on", rig3.FarMirrored);
    var main3 = rig3.Camera();
    var mirrors = UnityEngine.Camera.Writes().FindAll(c => c != main3 && c != play3 && c != far3);
    var mirror = mirrors.Count > 0 ? mirrors[0] : null;
    Check("the rig made one camera of its own for the far layer", mirror != null && mirrors.TrueForAll(c => c == mirror));
    Check("the far mirror copies the far camera's mask and clip planes", mirror != null && mirror.cullingMask == 557057 &&
          mirror.nearClipPlane == 200f && mirror.farClipPlane == 12000f && mirror.clearFlags == UnityEngine.CameraClearFlags.Skybox);
    Check("the far mirror draws under the rig's camera and over every game camera", mirror != null && mirror.depth < main3.depth && mirror.depth > 1f);
    Check("the far mirror looks from the cinematic viewpoint, not the player's", mirror != null &&
          mirror.transform.position.x == 100f && mirror.transform.position.z == 200f && mirror.fieldOfView == 33f);
    Check("the far mirror is on with the rig", mirror != null && mirror.enabled);
    Check("the rig's own camera keeps the gameplay camera's depth-only clear over the far layer", main3.clearFlags == UnityEngine.CameraClearFlags.Depth);
    Check("the game's far camera was never written to", !UnityEngine.Camera.WasWritten(far3));
    Check("the gameplay camera was never written to while mirroring", !UnityEngine.Camera.WasWritten(play3));
    Check("the game's far camera is where it was", far3.transform.position.x == 5f && far3.enabled && far3.depth == -10f);
    // the far camera goes off (draw distance back to 500 m): the mirror follows and the rig clears to the sky again
    var play4 = new UnityEngine.Camera(); play4.clearFlags = UnityEngine.CameraClearFlags.Skybox;
    UnityEngine.Camera.ResetWrites();
    far3.enabled = false; UnityEngine.Camera.ResetWrites();
    rig3.MirrorStack(play4, far3);
    Check("the mirror is off when the game's far camera is off", !rig3.FarMirrored && mirror != null && !mirror.enabled);
    Check("with no far layer the rig clears to the sky like the gameplay camera", main3.clearFlags == UnityEngine.CameraClearFlags.Skybox);
    Check("the game's far camera was not written to when it went off", !UnityEngine.Camera.WasWritten(far3));
    rig3.SetEnabled(false);
    Check("the mirror turns off with the rig", mirror != null && !mirror.enabled);

    // 7. the game's sky layer (sky objects on): the gameplay and far cameras both clear depth only and the game's sky
    //    camera, standing at the world's origin, draws the sky under them. Without a mirror of it the sky showed the
    //    player's own view (in a replay, a frozen picture of where the player stood).
    var rig5 = new CinematicRig();
    var play5 = new UnityEngine.Camera(); play5.clearFlags = UnityEngine.CameraClearFlags.Depth; play5.cullingMask = 0x10;
    var far5 = new UnityEngine.Camera(); far5.clearFlags = UnityEngine.CameraClearFlags.Depth; far5.enabled = true; far5.cullingMask = 557057;
    var sky5 = new UnityEngine.Camera(); sky5.clearFlags = UnityEngine.CameraClearFlags.Color; sky5.cullingMask = 0x2000;
    sky5.backgroundColor = new UnityEngine.Color(0.5f, 0.6f, 0.9f, 1f); sky5.nearClipPlane = 1f; sky5.farClipPlane = 30000f;
    sky5.depth = -20f; sky5.enabled = true; sky5.transform.position = new UnityEngine.Vector3(0f, 0f, 0f);
    UnityEngine.Camera.ResetWrites();
    rig5.MirrorStack(play5, far5, sky5);
    rig5.Position = new UnityEngine.Vector3(300f, 40f, -80f); rig5.Rotation = new UnityEngine.Quaternion(0f, 45f, 0f, 1f); rig5.Fov = 50f;
    rig5.Apply(); rig5.SetEnabled(true);
    Check("the sky layer is mirrored while the game's sky camera is on", rig5.SkyMirrored && rig5.FarMirrored);
    var main5 = rig5.Camera();
    var made5 = UnityEngine.Camera.Writes().FindAll(c => c != main5 && c != play5 && c != far5 && c != sky5);
    var skyMirror = made5.Find(c => c.cullingMask == 0x2000);
    var farMirror = made5.Find(c => c.cullingMask == 557057);
    Check("the rig made a camera of its own for the sky layer", skyMirror != null && skyMirror != farMirror && farMirror != null);
    Check("the sky mirror copies the sky camera's clear, colour and clip planes", skyMirror != null &&
          skyMirror.clearFlags == UnityEngine.CameraClearFlags.Color && skyMirror.backgroundColor.b == 0.9f &&
          skyMirror.nearClipPlane == 1f && skyMirror.farClipPlane == 30000f);
    Check("the sky draws first: under the far mirror, over every game camera", skyMirror != null && farMirror != null &&
          skyMirror.depth < farMirror.depth && skyMirror.depth > 1f);
    Check("the sky mirror stands where the sky camera stands and turns with the cinematic view", skyMirror != null &&
          skyMirror.transform.position.x == 0f && skyMirror.transform.position.z == 0f && skyMirror.transform.rotation.y == 45f && skyMirror.fieldOfView == 50f);
    Check("the sky mirror is on with the rig", skyMirror != null && skyMirror.enabled);
    Check("the game's sky camera was never written to", !UnityEngine.Camera.WasWritten(sky5));
    sky5.enabled = false; UnityEngine.Camera.ResetWrites();
    rig5.MirrorStack(play5, far5, sky5);
    Check("the sky mirror is off when the game's sky camera is off", !rig5.SkyMirrored && skyMirror != null && !skyMirror.enabled);
    Check("the game's sky camera was not written to when it went off", !UnityEngine.Camera.WasWritten(sky5));
    rig5.SetEnabled(false);
    Check("the sky mirror turns off with the rig", skyMirror != null && !skyMirror.enabled);

    Console.WriteLine(fails == 0 ? $"ALL PASS ({checks} checks)" : $"{fails} FAILED of {checks}");
    return fails == 0 ? 0 : 1;
  }

  static bool rigCamParentIsNull(UnityEngine.Camera cam) =>
      cam.transform.parent == null;

  static bool rigCamGameObjectIsRoot(UnityEngine.Camera cam) =>
      cam.gameObject != null && cam.gameObject.transform.parent == null;
}
