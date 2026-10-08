using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Players hurting each other. The official MP hits a remote player through its body colliders (layer 9) and sends
    /// the damage to them (syncScript.SendDmgPlayer → survival.Damage on the victim). Our remote bodies have NO live
    /// colliders — a live collider inside a car's real body pushes the car (the passenger freak-out) — so bullets and
    /// blades passed through. Here the hit test is geometric instead, with no physics side effects:
    ///  - guns: weaponscript.Shot's own RaycastNonAlloc is replaced by Raycast(): the same call, then the SAME ray
    ///    (per pellet) is tested against every remote body as a feet→head capsule, cut off at the first real obstacle
    ///    the game's ray hit; damage = the shot's energy × jouleToHp, the game's own formula.
    ///  - melee: while the weapon attacks, its hit box is tested against the capsules; damage × dt × the game's
    ///    playerDamageMultiplier, as the official MP does.
    /// The victim's machine applies it with the game's survivalscript.Damage (god mode, death — all the game's rules).
    public static class PlayerCombat
    {
        public const byte PlayerDamage = 40;
        public static long ShotsTested, ShotHits, MeleeHits, DamageSent, DamageTaken, DamageRejected;
        const float BodyRadius = 0.35f;

        // ---------------------------------------------------------------- guns
        private static weaponscript _shooter;
        private static readonly FieldInfo TempForce = AccessTools.Field(typeof(weaponscript), "tempForce");

        [HarmonyPatch(typeof(weaponscript), nameof(weaponscript.Shot))]
        private static class ShotHook
        {
            [HarmonyPrefix] private static void Prefix(weaponscript __instance) => _shooter = __instance;
            [HarmonyPostfix] private static void Postfix() => _shooter = null;

            private static readonly MethodInfo Original = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastNonAlloc),
                new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit[]), typeof(float), typeof(int) });

            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                bool done = false;
                foreach (var ins in code)
                {
                    if (!done && ins.Calls(Original)) { ins.operand = AccessTools.Method(typeof(PlayerCombat), nameof(Raycast)); done = true; }
                    yield return ins;
                }
                if (!done) Plugin.Log.LogWarning("PlayerCombat: weaponscript.Shot raycast not found — shots can't hit players");
            }
        }

        // the last shot ray, for ShotFx (Entities.ShotFx): hits returned, the ray, the player it stopped in (-1: none)
        internal static int LastN, LastPlayerHit = -1; internal static float LastPlayerT; internal static Vector3 LastOrigin, LastDir;

        /// The game's own call, then: does this ray reach a remote player before it reaches anything solid?
        public static int Raycast(Vector3 origin, Vector3 dir, RaycastHit[] results, float maxDistance, int mask)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, results, maxDistance, mask);
            LastN = n; LastOrigin = origin; LastDir = dir; LastPlayerHit = -1;
            if (!Entities.InSession || _shooter == null || RemotePlayers.Count == 0) return n;
            ShotsTested++;
            float block = maxDistance;
            for (int i = 0; i < n; i++)
            {
                var c = results[i].collider;
                if (c == null || c.isTrigger || c.CompareTag("Ignore")) continue;
                if (mainscript.s.player != null && c.transform.root == mainscript.s.player.transform.root) continue;   // our own body
                if (results[i].distance < block) block = results[i].distance;
            }
            var ray = new Ray(origin, dir.normalized);
            int bestId = -1; float bestT = block;
            foreach (var b in RemotePlayers.BodyCapsules())
            {
                float t = RayCapsule(ray, b.feet, b.head, BodyRadius);
                if (t >= 0f && t < bestT) { bestT = t; bestId = b.id; }
            }
            if (bestId < 0) return n;
            float energy = TempForce != null ? (float)TempForce.GetValue(_shooter) : 0f;
            Send(bestId, energy * mainscript.s.jouleToHp, dir.normalized);
            ShotHits++;
            // the bullet stops in the body, as the game's own MP has it (its hit boxes end the shot): only what lies
            // before the player is still hit — before, a bullet went through and holed the wall behind
            int m = 0;
            for (int i = 0; i < n; i++) if (results[i].distance < bestT) results[m++] = results[i];
            LastN = m; LastPlayerHit = bestId; LastPlayerT = bestT;
            return m;
        }

        // ---------------------------------------------------------------- melee
        private static readonly Dictionary<int, float> _meleeAcc = new Dictionary<int, float>();
        private static float _meleeFlushAt;

        [HarmonyPatch(typeof(meleeweaponscript), "Update")]
        private static class MeleeHook
        {
            [HarmonyPostfix]
            private static void Postfix(meleeweaponscript __instance)
            {
                if (!Entities.InSession || RemotePlayers.Count == 0 || __instance.handling == null || !__instance.handling.isAttacking || __instance.Trigger == null) return;
                // only the local player's weapon (the one in our hands)
                var pl = mainscript.s != null ? mainscript.s.player : null;
                if (pl == null || __instance.transform.root != pl.transform.root) return;
                var box = __instance.Trigger;
                foreach (var b in RemotePlayers.BodyCapsules())
                {
                    if (!BoxTouchesCapsule(box, b.feet, b.head, BodyRadius)) continue;
                    _meleeAcc.TryGetValue(b.id, out float acc);
                    _meleeAcc[b.id] = acc + __instance.damage * Time.deltaTime * meleeweaponscript.playerDamageMultiplier;
                }
                if (_meleeAcc.Count > 0 && Time.unscaledTime >= _meleeFlushAt)
                {
                    _meleeFlushAt = Time.unscaledTime + 0.1f;
                    foreach (var kv in _meleeAcc) { Send(kv.Key, kv.Value, box.forward); MeleeHits++; }
                    _meleeAcc.Clear();
                }
            }
        }

        // ---------------------------------------------------------------- network
        private static readonly NetWriter W = new NetWriter();

        private static void Send(int target, float dmg, Vector3 dir)
        {
            if (dmg <= 0f) return;
            W.Reset(); W.U8(PlayerDamage); W.VarU32((uint)target); W.F32(dmg); W.F32(dir.x); W.F32(dir.y); W.F32(dir.z);
            Entities.SendToServer(W, true);
            DamageSent++;
        }

        /// A creature's attack on a remote player (Entities.Ai): the game's DamageInstant on the victim (a creature hits
        /// at once and kills by its own rule), not the gradual Damage a weapon does.
        internal static void SendInstant(int target, float dmg, Vector3 dir)
        {
            if (dmg <= 0f) return;
            W.Reset(); W.U8(PlayerDamage); W.VarU32((uint)target); W.F32(dmg); W.F32(dir.x); W.F32(dir.y); W.F32(dir.z); W.U8(1);
            Entities.SendToServer(W, true);
            DamageSent++;
        }

        /// Server: to the victim (0 = the host itself).
        internal static void ServerReceive(int from, NetReader r)
        {
            int target = (int)r.VarU32();
            if (r.Bad || target == from) return;
            r.Pos = 0;
            Entities.ServerForwardRaw(target, r);
        }

        /// Victim: the game's own damage (hp, direction marker, death with all the game's rules — god mode included).
        internal static void ClientReceive(NetReader r)
        {
            r.VarU32(); float dmg = r.F32(); var dir = new Vector3(r.F32(), r.F32(), r.F32());
            bool instant = r.Pos < r.End && r.U8() == 1;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (r.Bad || pl == null || pl.survival == null || mainscript.s.died) return;
            // a non-finite or negative amount (a bad message, or a NaN from the attacker's side) made hp NaN - no death,
            // no health bar - or healed
            if (float.IsNaN(dmg) || float.IsInfinity(dmg) || dmg <= 0f) { DamageRejected++; return; }
            if (float.IsNaN(dir.x) || float.IsNaN(dir.y) || float.IsNaN(dir.z) || float.IsInfinity(dir.sqrMagnitude)) dir = Vector3.zero;
            if (instant) pl.survival.DamageInstant(dmg, dir, show: true);
            else pl.survival.Damage(dmg, dir, show: true);
            DamageTaken++;
        }

        /// Test (bridge `mp shootat <id>`): the nearest gun to the local player aims its muzzle at remote player `id`
        /// and fires through the game's own weaponscript.Shot (so the transpiled raycast is what's tested).
        public static string ShootAt(int id, float aboveGround = -1f)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            weaponscript gun = null; float best = float.MaxValue;
            foreach (var w in Object.FindObjectsOfType<weaponscript>())
            {
                if (w == null || w.S == null || w.ammo == null) continue;
                float d = (w.transform.position - pl.transform.position).sqrMagnitude;
                if (d < best) { best = d; gun = w; }
            }
            if (gun == null) return "{\"error\":\"no gun nearby\"}";
            (int id, Vector3 feet, Vector3 head)? target = null;
            foreach (var b in RemotePlayers.BodyCapsules()) if (b.id == id) target = b;
            if (target == null) return "{\"error\":\"no body for player " + id + "\"}";
            var chest = Vector3.Lerp(target.Value.feet, target.Value.head, 0.6f);
            if (aboveGround >= 0f)   // a height over the ground under them (not from the body segment under test): knees, hips…
                foreach (var t in RemotePlayers.AiTargets())
                {
                    if (t.id != id) continue;
                    var root = t.body.position;
                    if (Physics.Raycast(root + Vector3.up * 0.5f, Vector3.down, out var gh, 5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        chest = new Vector3(root.x, gh.point.y + aboveGround, root.z);
                }
            var rot = gun.S.rotation;
            bool inf = gun.infinite;
            long t0 = ShotsTested, h0 = ShotHits;
            bool could = gun.CanShoot();
            try { gun.infinite = true; gun.S.rotation = Quaternion.LookRotation(chest - gun.S.position); gun.Shot(0); }
            finally { gun.S.rotation = rot; gun.infinite = inf; }
            return "{\"gun\":" + Json.Str(gun.name) + ",\"canShoot\":" + (could ? "true" : "false") + ",\"rayTested\":" + (ShotsTested - t0)
                   + ",\"hits\":" + (ShotHits - h0) + ",\"dist\":" + (chest - gun.S.position).magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// Test tooling: the nearest gun fires once at a GLOBAL point (a car part, a tank) through the game's own Shot.
        public static string ShootPoint(double gx, double gy, double gz, string from = "")
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            weaponscript gun = null; float best = float.MaxValue;
            foreach (var w in Object.FindObjectsOfType<weaponscript>())
            {
                if (w == null || w.S == null || w.ammo == null) continue;
                float d = (w.transform.position - pl.transform.position).sqrMagnitude;
                if (d < best) { best = d; gun = w; }
            }
            // every weapon in the scene (diagnosing a shot that meets nothing): name, distance, ammo, can it shoot
            var cands = new List<string>();
            foreach (var w in Object.FindObjectsOfType<weaponscript>())
                if (w != null) cands.Add("[" + Json.Str(w.name) + "," + (w.transform.position - pl.transform.position).magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                                         + "," + (w.S != null ? "true" : "false") + "," + (w.ammo != null ? "true" : "false") + "," + (w.CanShoot() ? "true" : "false") + "]");
            if (gun == null) return "{\"error\":\"no gun nearby\",\"weapons\":[" + string.Join(",", cands) + "]}";
            bool canShoot = gun.CanShoot();
            var at = mainscript.UnityPosFromGlobal(new Vector3d(gx, gy, gz));
            var rot = gun.S.rotation; var spos = gun.S.position;
            // `eye`: fired from the player's eye as if aimed (a gun lying on the floor next to a wall can't hit anything)
            // `near`: from 1.2 m short of the target on the line from the eye (test placement next to walls doesn't matter)
            bool fromEye = from == "eye" || from == "near";
            if (fromEye)
            {
                var cam = pl.Cam != null ? pl.Cam.transform : pl.transform;
                var dn = (at - cam.position).normalized;
                // never inside the shooter's own body (a creature pressed against them: 1.2 m short is behind the eye)
                float along = from == "near" ? Mathf.Max(0.3f, (at - cam.position).magnitude - 1.2f) : 0.3f;
                gun.S.position = cam.position + dn * along;
            }
            bool inf = gun.infinite;
            string first = "";
            // in the right hand the game fires from the player's head along the view (weaponscript.Shot: P.inRightHand →
            // player.Th): aim the view at the target like a player would — the muzzle doesn't matter then
            bool inHand = gun.P != null && gun.P.inRightHand && pl.Th != null;
            var thRot = inHand ? pl.Th.rotation : Quaternion.identity;
            var dir = at - (inHand ? pl.Th.position : gun.S.position);
            if (inHand) pl.Th.rotation = Quaternion.LookRotation(dir);
            if (Physics.Raycast(inHand ? pl.Th.position : gun.S.position, dir, out var h, dir.magnitude + 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                first = (h.collider.transform.parent != null ? h.collider.transform.parent.name + "/" : "") + h.collider.name;
            var hitNames = new List<string>();
            LastN = -1;   // stays -1 if the game's Shot returned before its ray
            try
            {
                gun.infinite = true; gun.S.rotation = Quaternion.LookRotation(dir); gun.Shot(0);
                // what the shot's own ray met (sorted by Shot), nearest first
                var hits = AccessTools.FieldRefAccess<weaponscript, RaycastHit[]>("hit")(gun);
                for (int i = 0; i < LastN && i < hits.Length && i < 6; i++)
                    if (hits[i].collider != null) hitNames.Add(hits[i].collider.transform.root.name + "/" + hits[i].collider.name + "@" + hits[i].distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            finally { gun.S.rotation = rot; gun.infinite = inf; if (fromEye) gun.S.position = spos; if (inHand) pl.Th.rotation = thRot; }
            // everything on the shot's line, any layer, triggers too (what the gun's mask leaves out)
            var all = new List<string>();
            if (LastN >= 0)
                foreach (var hh in Physics.RaycastAll(LastOrigin, LastDir, 60f, ~0, QueryTriggerInteraction.Collide))
                    all.Add(Json.Str(hh.collider.transform.root.name + "/" + hh.collider.name + " L" + hh.collider.gameObject.layer + (hh.collider.isTrigger ? " trig" : "")
                                     + ((gun.LM.value & (1 << hh.collider.gameObject.layer)) != 0 ? " inMask" : "") + "@" + hh.distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
            var og = mainscript.GlobalFromUnityPos(LastOrigin);
            string ray = "{\"from\":[" + og.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + og.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + og.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                         + "],\"dir\":[" + LastDir.normalized.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + LastDir.normalized.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + LastDir.normalized.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                         + "],\"mask\":" + gun.LM.value + ",\"all\":[" + string.Join(",", all) + "]}";
            return "{\"shotHits\":[" + string.Join(",", hitNames.ConvertAll(Json.Str)) + "],\"gun\":" + Json.Str(gun.name) + ",\"hithole\":" + (gun.hithole ? "true" : "false") + ",\"explodeStuffs\":" + (gun.explodeStuffs ? "true" : "false") + ",\"inHand\":" + (inHand ? "true" : "false") + ",\"canShoot\":" + (canShoot ? "true" : "false") + ",\"rayN\":" + LastN + ",\"ray\":" + ray + ",\"weapons\":[" + string.Join(",", cands) + "],\"dist\":" + dir.magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ",\"firstHit\":" + Json.Str(first) + "}";
        }

        public static string Stats() => "{\"shotsTested\":" + ShotsTested + ",\"shotHits\":" + ShotHits + ",\"meleeHits\":" + MeleeHits
                                        + ",\"damageSent\":" + DamageSent + ",\"damageTaken\":" + DamageTaken + "}";

        // ---------------------------------------------------------------- geometry
        /// Distance along the ray to a capsule (segment a–b, radius r), or -1.
        internal static float RayCapsule(Ray ray, Vector3 a, Vector3 b, float r)
        {
            // closest points between the ray and the segment
            Vector3 d1 = ray.direction, d2 = b - a, w = ray.origin - a;
            float A = Vector3.Dot(d1, d1), B = Vector3.Dot(d1, d2), C = Vector3.Dot(d2, d2), D = Vector3.Dot(d1, w), E = Vector3.Dot(d2, w);
            float den = A * C - B * B, s, t;
            if (den < 1e-6f) { s = 0f; t = Mathf.Clamp01(C > 1e-6f ? E / C : 0f); }
            else
            {
                s = (B * E - C * D) / den;
                t = Mathf.Clamp01((A * E - B * D) / den);
                if (s < 0f) { s = 0f; t = Mathf.Clamp01(C > 1e-6f ? E / C : 0f); }
            }
            Vector3 onSeg = a + d2 * t;
            // project the segment point back onto the ray (after clamping) for the true closest distance
            s = Mathf.Max(0f, Vector3.Dot(onSeg - ray.origin, d1) / A);
            Vector3 onRay = ray.origin + d1 * s;
            float dist = (onRay - onSeg).magnitude;
            if (dist > r) return -1f;
            return Mathf.Max(0f, s - Mathf.Sqrt(r * r - dist * dist));   // entry point
        }

        /// Oriented box (the melee trigger: centre = position, half extents = localScale, as its OverlapBox) vs capsule.
        internal static bool BoxTouchesCapsule(Transform box, Vector3 a, Vector3 b, float r)
        {
            var half = box.localScale;
            // sample the capsule's axis; enough for a swing box the size of a blade
            for (int i = 0; i <= 8; i++)
            {
                var p = Vector3.Lerp(a, b, i / 8f);
                var lp = Quaternion.Inverse(box.rotation) * (p - box.position);
                var q = new Vector3(Mathf.Clamp(lp.x, -half.x, half.x), Mathf.Clamp(lp.y, -half.y, half.y), Mathf.Clamp(lp.z, -half.z, half.z));
                if ((lp - q).sqrMagnitude <= r * r) return true;
            }
            return false;
        }
    }
}
