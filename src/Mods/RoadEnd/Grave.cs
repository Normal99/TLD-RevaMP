using System;
using HarmonyLib;
using UnityEngine;

namespace RoadEnd
{
    /// At the end of the main road: the game's own roadside grave (the "kereszt" POI, itemdatabase.b.kereszt) and a
    /// note on the ground in front of it.
    ///
    /// The grave is added as a POI of the game's building generator, so the game places, removes, saves and shapes the
    /// ground for it as for any roadside grave. The note is this mod's own object, kept at its global position every
    /// frame (the game moves its world origin as you drive).
    [HarmonyPatch]
    public static class Grave
    {
        public static GameObject Instance => _poi != null && _poi.pobj != null ? _poi.pobj.gameObject : null;
        public static GameObject NoteObject;
        static poiGenScript.poiClass _poi;
        static poiGenScript _gen;
        static Vector3d _notePos, _graveAt;
        static bool _placed;
        static Texture2D _noteTex;

        public static bool IsGrave(poiGenScript.poiClass p) => p != null && p == _poi;

        public static void Reset()
        {
            _poi = null; _gen = null; _placed = false;
            if (NoteObject != null) UnityEngine.Object.Destroy(NoteObject);
            NoteObject = null;
        }

        /// After the building generator's frame: make sure the grave POI exists once the road's end is known.
        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.Upd))]
        [HarmonyPostfix]
        private static void AfterPoiUpd(poiGenScript __instance)
        {
            if (!World.Active || !EndPlan.Current.HasRoadEnd || __instance.roadgen == null) return;
            try { EnsurePoi(__instance); }
            catch (Exception e) { Plugin.Once("grave", e); }
        }

        static void EnsurePoi(poiGenScript gen)
        {
            if (_gen != null && _gen != gen) return;   // one generator holds it
            var end = EndPlan.Current;
            var dir = end.RoadDir;
            var at = end.RoadEnd + dir * Plugin.GraveDistance;
            if (_poi != null && (_graveAt - at).magnitude < 0.5 && gen.chunks.TryGetValue(mainscript.GetChunkPos(_poi.pos, gen.chunkSize), out var ch) && ch.pois.ContainsValue(_poi)) return;
            at.y = gen.terrainGen.Height(at, mapSettingScript.RoadsNear(at));
            // the grave faces back along the road, at whoever arrives
            float yaw = Mathf.Atan2((float)-dir.x, (float)-dir.z) * Mathf.Rad2Deg + Plugin.GraveYaw;
            _poi = gen.AddPoi(at, new Vector3(0, yaw, 0), (int)itemdatabase.b.kereszt, false);
            _gen = gen;
            _graveAt = end.RoadEnd + dir * Plugin.GraveDistance;
            _placed = false;
            Plugin.Log.LogInfo($"the grave stands at {at.x:0.0} {at.y:0.0} {at.z:0.0}");
        }

        /// The note (Runner.LateUpdate).
        public static void Tick()
        {
            if (!World.Active || !EndPlan.Current.HasRoadEnd || mainscript.s == null) return;
            var end = EndPlan.Current;
            if (!_placed)
            {
                _placed = true;
                _notePos = end.RoadEnd + end.RoadDir * Plugin.NoteDistance;
                if (NoteObject == null) NoteObject = MakeNote();
            }
            if (NoteObject != null)
            {
                var p = _notePos;
                var gen = _gen != null ? _gen.terrainGen : null;
                // stand on whatever is there: the road's end or the ground (a ray from above, the game's colliders)
                var u = mainscript.UnityPosFromGlobal(p);
                if (Physics.Raycast(u + Vector3.up * 30f, Vector3.down, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore) && hit.collider.gameObject != NoteObject)
                    NoteObject.transform.position = hit.point + Vector3.up * 0.015f;
                else if (gen != null) { p.y = gen.Height(p) + 0.015; NoteObject.transform.position = mainscript.UnityPosFromGlobal(p); }
                else NoteObject.transform.position = u;
                // the text reads from the road: its top points the way the road went
                var fwd = new Vector3((float)end.RoadDir.x, 0, (float)end.RoadDir.z);
                NoteObject.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0, Plugin.NoteYaw, 0);
            }
        }

        /// A sheet of paper lying flat, the text drawn on it once.
        static GameObject MakeNote()
        {
            var go = new GameObject("RoadEndNote");   // a scene object: gone with the world
            var sheet = GameObject.CreatePrimitive(PrimitiveType.Quad);
            sheet.name = "Sheet";
            UnityEngine.Object.Destroy(sheet.GetComponent<Collider>());
            sheet.transform.SetParent(go.transform, false);
            // lying on its back, text up; A4-ish, a little bigger than real so it reads from the driver's seat
            sheet.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            sheet.transform.localScale = new Vector3(Plugin.NoteWidth, Plugin.NoteWidth * 0.75f, 1f);
            var r = sheet.GetComponent<MeshRenderer>();
            r.sharedMaterial = NoteMaterial();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        static Material NoteMaterial()
        {
            _noteTex ??= NoteText.Render(Plugin.Text, 1024, 768);
            // a lit shader the game ships: the one the game's own paper (the start letter) uses, else any opaque one
            Shader shader = null;
            var letter = itemdatabase.s != null ? itemdatabase.s.items[(int)itemdatabase.i.kezdolevel] : null;
            var lr = letter != null ? letter.GetComponentInChildren<MeshRenderer>(true) : null;
            if (lr != null && lr.sharedMaterial != null) shader = lr.sharedMaterial.shader;
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            var m = lr != null && lr.sharedMaterial != null ? new Material(lr.sharedMaterial) : new Material(shader);
            m.mainTexture = _noteTex;
            if (m.HasProperty("_Color")) m.color = Color.white;
            Plugin.Log.LogInfo("note material: " + m.shader.name);
            return m;
        }
    }
}
