using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Gunshots everyone sees and hears. weaponscript.Shot makes every effect of a shot on the shooter's machine only:
    /// the bang and muzzle flash (the game's own MP sent those — SShot/RShot — never in our session), the tracer
    /// (ShootStart/End), sparks or sand and the impact sounds, the bullet holes (a decal prefab parented to the collider
    /// hit) and tank holes (a leaking tankcapscript). Here the shooter's machine reads back what its Shot did and sends
    /// it as one message; every other machine replays it with the game's own pieces on its own copies: the gun copy's
    /// RShot, the tracer, the particles and sounds, the same decal at the same place on the same object (a car or item:
    /// relative to the object, matched to the same part; the world: global; a player: relative to the body part hit),
    /// and the same tank hole. A tank hole leaks on the tank's OWNER — the fuel travels by resync like any other — and is
    /// shown leaking on copies, whose loss isn't sent back as an edit (else two machines would drain one tank).
    /// Holes aren't saved (they never were, in single player either).
    public static partial class Entities
    {
        public const byte ShotFx = 45;
        public static long ShotFxSent, ShotFxApplied, ShotFxDecals, ShotFxHoles, ShotFxUnplaced, ShotFxNoGun;

        private const byte KObj = 1, KWorld = 2, KPlayer = 3, KHole = 4;
        private const byte SSand = 0, SGlass = 1, SFlesh = 2, SPlant = 3, SWood = 4, SOther = 5, SNone = 255;
        private const byte FReal = 1, FFalse = 2, FGun = 4;

        private static readonly AccessTools.FieldRef<weaponscript, RaycastHit[]> GunHits = AccessTools.FieldRefAccess<weaponscript, RaycastHit[]>("hit");
        private static readonly AccessTools.FieldRef<weaponscript, int> GunTempInt = AccessTools.FieldRefAccess<weaponscript, int>("tempInt");
        private static readonly AccessTools.FieldRef<weaponscript, bool> GunFalseShot = AccessTools.FieldRefAccess<weaponscript, bool>("alreadyShotFalse");

        private static weaponscript _fxGun;
        private static readonly List<tankcapscript> _fxHoles = new List<tankcapscript>();
        /// Tank holes on copies: shown leaking, not sent to the owner as an edit.
        private static readonly HashSet<tankcapscript> DisplayHoles = new HashSet<tankcapscript>();
        private static bool _displayPour;

        // ---------------------------------------------------------------- shooter
        [HarmonyPatch(typeof(weaponscript), nameof(weaponscript.Shot))]
        private static class ShotFxHook
        {
            [HarmonyPrefix]
            private static void Prefix(weaponscript __instance, out bool __state)
            {
                __state = GunFalseShot(__instance);
                _fxGun = __instance; _fxHoles.Clear();
                PlayerCombat.LastN = 0; PlayerCombat.LastPlayerHit = -1;
            }

            [HarmonyPostfix]
            private static void Postfix(weaponscript __instance, int _s, bool __state)
            {
                try { if (InSession) CaptureShot(__instance, _s, __state); }
                catch (System.Exception ex) { Plugin.Log.LogWarning("ShotFx capture: " + ex.Message); }
                finally { _fxGun = null; _fxHoles.Clear(); }
            }
        }

        /// The holes Shot makes (tankcaphole: a collider-less cap with a tank, started here).
        [HarmonyPatch(typeof(tankcapscript), nameof(tankcapscript.FStart))]
        private static class ShotFxHole
        {
            [HarmonyPostfix]
            private static void Postfix(tankcapscript __instance)
            {
                if (_fxGun == null || !__instance.disableCollider || __instance.Tank == null) return;
                _fxHoles.Add(__instance);
                if (IsProxy(__instance.Tank)) { DisplayHoles.RemoveWhere(h => h == null); DisplayHoles.Add(__instance); }
            }
        }

        private static void CaptureShot(weaponscript gun, int s, bool falseBefore)
        {
            float now = Time.time;
            bool real = gun.ActuallyShotTime == now;
            bool falseShot = !real && s == 0 && gun.ShotTime == now && (gun.automaticFalseShot || !falseBefore);
            if (!real && !falseShot) return;
            W.Reset(); W.U8(ShotFx);
            bool hasGun = GunRef(gun, out uint gnet, out int gidx);
            W.U8((byte)((real && s == 0 ? FReal : 0) | (falseShot ? FFalse : 0) | (hasGun ? FGun : 0)));
            if (hasGun) { W.U32(gnet); W.VarU32((uint)gidx); }
            W.U8((byte)GunTempInt(gun));
            var o = mainscript.GlobalFromUnityPos(PlayerCombat.LastOrigin);
            W.F64(o.x); W.F64(o.y); W.F64(o.z);
            V3(PlayerCombat.LastDir.normalized);
            int countAt = W.Len; W.U8(0);   // entries (patched below; a shot hits at most a few things)
            int count = 0;
            if (real)
            {
                var hits = GunHits(gun);
                for (int k = 0; k < PlayerCombat.LastN && k < hits.Length && count < 32; k++)
                {
                    var c = hits[k].collider;
                    if (c == null) continue;
                    // Shot's own rules: what it skips, what makes a decal, where the bullet stops
                    if (c.CompareTag("Ignore") || (c.isTrigger && !c.CompareTag("Flesh") && !c.CompareTag("Plant"))) continue;
                    int layer = c.gameObject.layer;
                    if (layer == 9) continue;
                    byte surf; int decal = -1;
                    if (layer == 17 || layer == 19) surf = SSand;
                    else
                    {
                        decal = mainscript.GetDecal(c);
                        surf = layer == 23 ? SGlass : c.CompareTag("Flesh") ? SFlesh : c.CompareTag("Plant") ? SPlant : c.CompareTag("Wood") ? SWood : SOther;
                    }
                    var prefab = decal >= 0 && decal < mainscript.s.ShootDecals.Length ? mainscript.s.ShootDecals[decal] : null;
                    var decalT = prefab != null ? LastChildNamed(c.transform, prefab.name + "(Clone)") : null;
                    if (decalT == null) decal = -1;
                    WriteHit(c, surf, decal, hits[k].point, hits[k].normal, decalT);
                    count++;
                    if (!c.CompareTag("Plant") && layer != 23 && !c.CompareTag("Cobweb")) break;
                }
                if (PlayerCombat.LastPlayerHit >= 0 && count < 32)
                {
                    int id = PlayerCombat.LastPlayerHit;
                    var ray = new Ray(PlayerCombat.LastOrigin, PlayerCombat.LastDir.normalized);
                    if (RemotePlayers.BodyHit(id, ray, PlayerCombat.LastPlayerT + 1f, out int part, out var bh))
                    {
                        // the shooter's own view: the game's flesh hit (its sound and a flesh decal) on the body part
                        if (gun.ShotHitFlesh != null && gun.ShotHitFlesh.Length > 0)
                            mainscript.PlayClipAtPoint(gun.ShotHitFlesh[Random.Range(0, gun.ShotHitFlesh.Length)], bh.point, 1f, mainscript.AudioPriorities[7]);
                        mainscript.s.ShootEnd(bh.point, bh.point - gun.S.position);
                        var bone = RemotePlayers.BodyPart(id, part);
                        var d = MakeDecal(2, bh.point, bh.normal, Quaternion.AngleAxis(Random.Range(0f, 360f), bh.normal) * Quaternion.LookRotation(bh.normal),
                                          0.3f * Random.Range(0.6f, 1.2f), bone);
                        W.U8(KPlayer); W.U8(SFlesh); W.U8(d != null ? (byte)2 : (byte)255);
                        W.VarU32((uint)id); W.VarU32((uint)part);
                        V3(bone.InverseTransformPoint(bh.point)); V3(bone.InverseTransformDirection(bh.normal));
                        if (d != null) { Q(Quaternion.Inverse(bone.rotation) * d.rotation); W.F32(d.lossyScale.x); }
                        count++;
                    }
                }
                foreach (var h in _fxHoles)
                {
                    if (h == null || count >= 32 || !TankRef(h.Tank, out uint tnet, out int ti)) continue;
                    var tt = h.Tank.transform;
                    W.U8(KHole); W.U8(SNone); W.U8(255);
                    W.U32(tnet); W.VarU32((uint)ti);
                    V3(tt.InverseTransformPoint(h.transform.position - h.transform.forward * 0.01f)); V3(tt.InverseTransformDirection(h.transform.forward));
                    count++;
                }
            }
            W.Buf[countAt] = (byte)count;
            ToServer(W, true);
            ShotFxSent++;
        }

        /// One hit: the object it's on (a shared object: relative to it, with the path to the collider; else the world).
        private static void WriteHit(Collider c, byte surf, int decal, Vector3 p, Vector3 n, Transform decalT)
        {
            var root = c.transform.root;
            var e = EntOfRoot(root);
            if (e != null)
            {
                W.U8(KObj); W.U8(surf); W.U8((byte)(decal < 0 ? 255 : decal));
                W.U32(e.NetId); W.Str(PathOf(c.transform, root));
                V3(root.InverseTransformPoint(p)); V3(root.InverseTransformDirection(n));
                if (decal >= 0) { Q(Quaternion.Inverse(root.rotation) * decalT.rotation); W.F32(decalT.lossyScale.x); }
            }
            else
            {
                W.U8(KWorld); W.U8(surf); W.U8((byte)(decal < 0 ? 255 : decal));
                var g = mainscript.GlobalFromUnityPos(p); W.F64(g.x); W.F64(g.y); W.F64(g.z); V3(n);
                if (decal >= 0) { Q(decalT.rotation); W.F32(decalT.lossyScale.x); }
            }
        }

        // ---------------------------------------------------------------- server / everyone else
        private static void ServerShotFx(int from, NetReader r)
        {
            r.Pos = 0;
            WS.Reset(); WS.Bytes(r.Buf, 0, r.End);
            ServerSendAll(WS, true, from);
        }

        private static void ApplyShotFx(NetReader r)
        {
            byte flags = r.U8();
            weaponscript gun = null;
            if ((flags & FGun) != 0)
            {
                uint gnet = r.U32(); int gidx = (int)r.VarU32();
                if (ByNet.TryGetValue(gnet, out var ge) && Resolve(ge) && gidx < ge.Items.Count && ge.Items[gidx] != null)
                    gun = ge.Items[gidx].GetComponentInChildren<weaponscript>();
            }
            int sound = r.U8();
            var muzzle = mainscript.UnityPosFromGlobal(new Vector3d(r.F64(), r.F64(), r.F64()));
            var dir = RV3(r);
            int count = r.U8();
            if (r.Bad || mainscript.s == null) return;
            if (gun == null) ShotFxNoGun++;
            if (gun != null && (flags & FReal) != 0) gun.RShot(0, sound);
            if (gun != null && (flags & FFalse) != 0) gun.RShot(1, sound);
            if ((flags & FReal) != 0 && dir != Vector3.zero) mainscript.s.ShootStart(gun != null && gun.S != null ? gun.S.position : muzzle, dir);
            for (int i = 0; i < count && !r.Bad; i++)
            {
                byte kind = r.U8(), surf = r.U8(), decal = r.U8();
                Transform parent = null; Vector3 p = default, n = Vector3.up; Quaternion rot = Quaternion.identity; float scale = 0f;
                bool placed = false; tankscript tank = null;
                switch (kind)
                {
                    case KObj:
                    {
                        uint net = r.U32(); string path = r.Str(); var lp = RV3(r); var ln = RV3(r);
                        Quaternion lr = Quaternion.identity; if (decal != 255) { lr = RQ(r); scale = r.F32(); }
                        if (r.Bad || !ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) break;
                        var root = e.Root.transform.root;
                        p = root.TransformPoint(lp); n = root.TransformDirection(ln); rot = root.rotation * lr;
                        parent = ColliderAt(root, path, p); placed = true;
                        break;
                    }
                    case KWorld:
                    {
                        var g = new Vector3d(r.F64(), r.F64(), r.F64()); n = RV3(r);
                        if (decal != 255) { rot = RQ(r); scale = r.F32(); }
                        if (r.Bad) break;
                        p = mainscript.UnityPosFromGlobal(g);
                        if (Physics.Raycast(p + n * 0.1f, -n, out var wh, 0.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)) { parent = wh.collider.transform; placed = true; }
                        break;
                    }
                    case KPlayer:
                    {
                        int id = (int)r.VarU32(), part = (int)r.VarU32(); var lp = RV3(r); var ln = RV3(r);
                        Quaternion lr = Quaternion.identity; if (decal != 255) { lr = RQ(r); scale = r.F32(); }
                        var bone = r.Bad ? null : RemotePlayers.BodyPart(id, part);   // null for our own body (first person)
                        if (bone == null) break;
                        p = bone.TransformPoint(lp); n = bone.TransformDirection(ln); rot = bone.rotation * lr; parent = bone; placed = true;
                        break;
                    }
                    case KHole:
                    {
                        uint net = r.U32(); int ti = (int)r.VarU32(); var lp = RV3(r); var ln = RV3(r);
                        tank = r.Bad ? null : TankOf(net, ti);
                        if (tank == null) break;
                        p = tank.transform.TransformPoint(lp); n = tank.transform.TransformDirection(ln); placed = true;
                        break;
                    }
                    default: r.Pos = r.End; break;   // unknown: stop reading
                }
                if (!placed) { ShotFxUnplaced++; continue; }
                if (kind == KHole) { MakeHole(tank, p, n); ShotFxHoles++; continue; }
                Impact(gun, surf, p, n, muzzle);
                if (decal != 255 && MakeDecal(decal, p, n, rot, scale, parent) != null) ShotFxDecals++;
            }
            ShotFxApplied++;
        }

        /// The impact as Shot plays it: tracer end, sand or sparks, the surface's sound (the clips are the gun's).
        private static void Impact(weaponscript gun, byte surf, Vector3 p, Vector3 n, Vector3 muzzle)
        {
            if (surf == SNone) return;
            mainscript.s.ShootEnd(p, p - muzzle);
            AudioClip[] clips = null;
            switch (surf)
            {
                case SSand: mainscript.s.Particle(mainscript.particle.sand, p, n, Random.Range(10, 20)); clips = gun != null ? gun.ShotHitSand : null; break;
                case SGlass: clips = gun != null ? gun.ShotHitGlass : null; break;
                case SFlesh: case SPlant: clips = gun != null ? gun.ShotHitFlesh : null; break;
                case SWood: clips = gun != null ? gun.ShotHitConcrete : null; break;
                default: mainscript.s.Particle(mainscript.particle.spark, p, n, Random.Range(10, 20)); clips = gun != null ? gun.ShotHitConcrete : null; break;
            }
            if (clips != null && clips.Length > 0) mainscript.PlayClipAtPoint(clips[Random.Range(0, clips.Length)], p, 1f, mainscript.AudioPriorities[7]);
        }

        /// A bullet hole as Shot makes it: the decal prefab, scaled, on the surface, parented to what was hit.
        private static Transform MakeDecal(int decal, Vector3 p, Vector3 n, Quaternion rot, float scale, Transform parent)
        {
            var decals = mainscript.s.ShootDecals;
            if (decals == null || decal < 0 || decal >= decals.Length || decals[decal] == null) return null;
            var go = Object.Instantiate(decals[decal], Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one * scale;
            go.transform.position = p + n * 0.01f;
            go.transform.rotation = rot;
            if (parent != null) go.transform.SetParent(parent, worldPositionStays: true);
            return go.transform;
        }

        /// A tank hole as Shot makes it (weaponscript.Shot: tankcaphole). On a copy it's shown, not counted.
        private static void MakeHole(tankscript tank, Vector3 p, Vector3 n)
        {
            if (mainscript.s.tankcaphole == null) return;
            var go = Object.Instantiate(mainscript.s.tankcaphole, p + n * 0.01f, Quaternion.LookRotation(n));
            var cap = go.GetComponent<tankcapscript>();
            if (cap == null) return;
            cap.disableCollider = true;
            go.transform.parent = tank.transform;
            cap.canToggle = false; cap.canTune = false; cap.radius = 1.5f; cap.valve = 1f; cap.noText = true;
            cap.Tank = tank; cap.defPourAngle = 5f;
            if (IsProxy(tank)) { DisplayHoles.RemoveWhere(h => h == null); DisplayHoles.Add(cap); }
            cap.FStart();
        }

        /// A copy's tank hole leaks for show: its tank change isn't an edit for the owner (EditTank checks this).
        [HarmonyPatch(typeof(tankcapscript), "Update")]
        private static class DisplayHolePour
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            private static void Prefix(tankcapscript __instance) { _displayPour = DisplayHoles.Count > 0 && DisplayHoles.Contains(__instance); }
            [HarmonyPostfix]
            private static void Postfix() { _displayPour = false; }
        }

        // ---------------------------------------------------------------- helpers
        private static Ent EntOfRoot(Transform root)
        {
            foreach (var e in ByNet.Values) if (e.Root != null && e.Root.transform.root == root) return e;
            return null;
        }

        private static bool GunRef(weaponscript gun, out uint net, out int idx)
        {
            net = 0; idx = -1;
            var it = gun.GetComponentInParent<tosaveitemscript>();
            if (it == null) return false;
            foreach (var e in ByNet.Values)
            {
                if (e.Items == null) continue;
                int i = e.Items.IndexOf(it);
                if (i >= 0) { net = e.NetId; idx = i; return true; }
            }
            return false;
        }

        private static bool TankRef(tankscript t, out uint net, out int ti)
        {
            net = 0; ti = -1;
            foreach (var e in ByNet.Values)
            {
                if (e.Items == null || e.Root == null) continue;
                int i = TanksOf(e).IndexOf(t);
                if (i >= 0) { net = e.NetId; ti = i; return true; }
            }
            return false;
        }

        private static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// The collider on this machine's copy that was hit: the same path from the root (parts share names — the one
        /// nearest the point), else the nearest collider of the object.
        private static Transform ColliderAt(Transform root, string path, Vector3 p)
        {
            Transform best = null, any = null; float bd = float.MaxValue, ad = float.MaxValue;
            foreach (var c in root.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled) continue;
                float d = c.bounds.SqrDistance(p);   // ClosestPoint refuses concave mesh colliders
                if (d < ad) { ad = d; any = c.transform; }
                if (d < bd && PathOf(c.transform, root) == path) { bd = d; best = c.transform; }
            }
            return best != null ? best : any != null ? any : root;
        }

        private static Transform LastChildNamed(Transform t, string name)
        {
            for (int i = t.childCount - 1; i >= 0; i--) if (t.GetChild(i).name == name) return t.GetChild(i);
            return null;
        }

        private static void V3(Vector3 v) { W.F32(v.x); W.F32(v.y); W.F32(v.z); }
        private static void Q(Quaternion q) { W.F32(q.x); W.F32(q.y); W.F32(q.z); W.F32(q.w); }
        private static Vector3 RV3(NetReader r) => new Vector3(r.F32(), r.F32(), r.F32());
        private static Quaternion RQ(NetReader r) => new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());

        /// Test: the bullet holes on object `net` on this machine (root-local positions), or within `r` m of a global
        /// point when net is 0, and tank holes on it.
        public static string Decals(uint net, double gx, double gy, double gz, float rad)
        {
            var names = new HashSet<string>();
            foreach (var d in mainscript.s.ShootDecals) if (d != null) names.Add(d.name + "(Clone)");
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>(); int holes = 0;
            if (net != 0)
            {
                if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
                var root = e.Root.transform.root;
                foreach (var t in root.GetComponentsInChildren<Transform>())
                {
                    if (names.Contains(t.name)) { var lp = root.InverseTransformPoint(t.position); rows.Add("{\"name\":" + Json.Str(t.name) + ",\"on\":" + Json.Str(t.parent != null ? t.parent.name : "") + ",\"p\":[" + lp.x.ToString("F3", ci) + "," + lp.y.ToString("F3", ci) + "," + lp.z.ToString("F3", ci) + "]}"); }
                    var cap = t.GetComponent<tankcapscript>();
                    if (cap != null && cap.disableCollider) holes++;
                }
            }
            else
            {
                var at = mainscript.UnityPosFromGlobal(new Vector3d(gx, gy, gz));
                foreach (var t in Object.FindObjectsOfType<Transform>())
                {
                    if (!names.Contains(t.name) || (t.position - at).magnitude > rad) continue;
                    var g = mainscript.GlobalFromUnityPos(t.position);
                    rows.Add("{\"name\":" + Json.Str(t.name) + ",\"on\":" + Json.Str(t.parent != null ? t.parent.name : "") + ",\"p\":[" + g.x.ToString("F3", ci) + "," + g.y.ToString("F3", ci) + "," + g.z.ToString("F3", ci) + "]}");
                }
            }
            return "{\"decals\":[" + string.Join(",", rows) + "],\"holes\":" + holes + "}";
        }

        public static string ShotFxStats() => "{\"sent\":" + ShotFxSent + ",\"applied\":" + ShotFxApplied + ",\"decals\":" + ShotFxDecals +
                                              ",\"holes\":" + ShotFxHoles + ",\"unplaced\":" + ShotFxUnplaced + ",\"noGun\":" + ShotFxNoGun + ",\"displayHoles\":" + DisplayHoles.Count + "}";
    }
}
