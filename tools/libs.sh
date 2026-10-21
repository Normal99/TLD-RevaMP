#!/usr/bin/env bash
# Reference assemblies for building: copied from your own game install into lib/ (never committed or shipped).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${TLD_DIR:-$HOME/.local/share/Steam/steamapps/common/The Long Drive}"
M="$GAME/TheLongDrive_Data/Managed"
mkdir -p "$ROOT/lib"
for f in Assembly-CSharp com.rlabrecque.steamworks.net Unity.TextMeshPro UnityEngine UnityEngine.CoreModule UnityEngine.AudioModule UnityEngine.AnimationModule \
         UnityEngine.ImageConversionModule UnityEngine.IMGUIModule UnityEngine.InputLegacyModule UnityEngine.JSONSerializeModule \
         UnityEngine.ParticleSystemModule UnityEngine.PhysicsModule UnityEngine.ScreenCaptureModule UnityEngine.TerrainModule \
         UnityEngine.TextRenderingModule UnityEngine.UI UnityEngine.UIModule UnityEngine.VehiclesModule \
         UnityEngine.VideoModule UnityEngine.UnityWebRequestModule; do
  cp "$M/$f.dll" "$ROOT/lib/"
done
# OpenXR runtime support shipped with the game (issue #41). Kept optional here because older supported game copies may
# not include every one; the build references them only when present in lib/.
for f in Unity.XR.OpenXR Unity.XR.Management Unity.XR.CoreUtils UnityEngine.XRModule UnityEngine.VRModule UnityEngine.SubsystemsModule; do
  [ -f "$M/$f.dll" ] && cp "$M/$f.dll" "$ROOT/lib/"
done
cp "$GAME/BepInEx/core/0Harmony.dll" "$GAME/BepInEx/core/BepInEx.dll" "$GAME/BepInEx/core/Mono.Cecil.dll" "$ROOT/lib/"
echo "lib/ ready ($(ls "$ROOT/lib" | wc -l) assemblies). Set TLD_DIR if the game is somewhere else."
