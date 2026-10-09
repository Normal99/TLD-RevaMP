using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Status and test helpers (bridge `mp entities|entdebug|detachpart|worldcar|carsnear`).
    public static partial class Entities
    {
        /// The shared object whose group holds this item (diagnostics).
        internal static Ent SharedGroupOf(tosaveitemscript it)
        {
            foreach (var e in ByNet.Values) if (e.Root == it || (e.Items != null && e.Items.Contains(it))) return e;
            return null;
        }
        /// Every owner change the server makes, with why (bridge `mp ownerlog [net]`): the last 200.
        private static readonly System.Collections.Generic.List<string> _ownerLog = new System.Collections.Generic.List<string>();
        private static void OwnerLog(SEnt se, int to, string why)
        {
            if (_ownerLog.Count >= 200) _ownerLog.RemoveAt(0);
            _ownerLog.Add("t" + Time.realtimeSinceStartup.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " net " + se.NetId + " " + se.OwnerId + " -> " + to + ": " + why);
        }
        public static string OwnerLogJson(uint net)
        {
            var rows = new System.Collections.Generic.List<string>();
            foreach (var l in _ownerLog) if (net == 0 || l.Contains(" net " + net + " ")) rows.Add(Json.Str(l));
            return "{\"log\":[" + string.Join(",", rows) + "]}";
        }

        // ------------------------------------------------------------------ status

        public static string ResetStats()
        {
            foreach (var e in ByNet.Values) { e.Frames = 0; e.Jumps = 0; e.ExtrapFrames = 0; e.MaxDevCm = 0; }
            _jumpN = 0;
            return "{\"ok\":true}";
        }

        // jump log: what a copy was doing in each frame it visibly jumped (> 20 cm off smooth motion)
        private const int JumpCap = 512;
        private static readonly uint[] _jNet = new uint[JumpCap];
        private static readonly float[] _jDev = new float[JumpCap], _jDevY = new float[JumpCap], _jDt = new float[JumpCap], _jAhead = new float[JumpCap], _jCorr = new float[JumpCap], _jT = new float[JumpCap];
        private static readonly bool[] _jBody = new bool[JumpCap];
        private static int _jumpN;
        private static void LogJump(Ent e, float dev, float devY, float dt)
        {
            int i = _jumpN++ % JumpCap;
            _jNet[i] = e.NetId; _jDev[i] = dev; _jDevY[i] = devY; _jDt[i] = dt * 1000f; _jT[i] = Time.realtimeSinceStartup;
            _jAhead[i] = e.Ip != null ? e.Ip.AheadMs : 0f; _jCorr[i] = e.Ip != null ? e.Ip.CorrCm : 0f; _jBody[i] = e.Physical || e.MovedByBody;
        }

        /// bridge `mp jumps`: the jump log since `mp entstats`, each [t, net, dev cm, vertical cm, frame ms, ahead of newest
        /// state ms, correction cm, body-moved]; MaxExtrapolate = holding still past it
        public static string JumpLog()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("{\"count\":" + _jumpN + ",\"jumps\":[");
            int from = Math.Max(0, _jumpN - JumpCap);
            for (int j = from; j < _jumpN; j++)
            {
                int i = j % JumpCap;
                if (j > from) sb.Append(',');
                sb.Append('[').Append(_jT[i].ToString("F2", ic)).Append(',').Append(_jNet[i]).Append(',').Append(_jDev[i].ToString("F0", ic)).Append(',')
                  .Append(_jDevY[i].ToString("F0", ic)).Append(',').Append(_jDt[i].ToString("F1", ic)).Append(',').Append(_jAhead[i].ToString("F0", ic)).Append(',')
                  .Append(_jCorr[i].ToString("F0", ic)).Append(',').Append(_jBody[i] ? "true" : "false").Append(']');
            }
            return sb.Append("]}").ToString();
        }

        private static Vector3d StoredPos(uint id)
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            return data != null && data.itemData != null && id != 0 && savedatascript.IndexOfID(data.itemData.items, id, out int k) ? data.itemData.items[k].transform.pos : new Vector3d(0, 0, 0);
        }

        /// What this machine thinks the entity's velocity is: its own body (simulated here), the owner's last (a copy).
        private static string Vel(Ent e)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rb = e.Root.GetComponent<Rigidbody>();
            var v = e.Proxy ? e.LastVel : rb != null ? rb.velocity : Vector3.zero;
            return "[" + v.x.ToString("F2", ic) + "," + v.y.ToString("F2", ic) + "," + v.z.ToString("F2", ic) + "]";
        }

        private static string V3(Vector3d v)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "[" + v.x.ToString("F2", ic) + "," + v.y.ToString("F2", ic) + "," + v.z.ToString("F2", ic) + "]";
        }

        public static string Status()
        {
            var rows = new List<string>();
            foreach (var e in ByNet.Values)
            {
                var g = e.Root != null ? mainscript.GlobalFromUnityPos(e.Root.transform.position) : StoredPos(e.RootId);
                int attached = 0;
                foreach (var it in e.Items) if (it != null && it.attachable != null && it.attachable.attached) attached++;
                rows.Add("{\"net\":" + e.NetId + ",\"car\":" + (e.Root != null && e.Root.car != null ? "true" : "false") + ",\"owner\":" + e.OwnerId + ",\"name\":" + Json.Str(e.Root != null ? e.Root.name : "") + ",\"parent\":" + e.ParentNet + ",\"part\":" + e.PartIndex + ",\"attached\":" + attached + ",\"owner\":" + e.OwnerId + ",\"epoch\":" + e.Epoch + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"upy\":" + (e.Root != null ? e.Root.transform.up.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "1") +
                         ",\"items\":" + e.Items.Count + ",\"driven\":" + (e.Driven ? "true" : "false") + ",\"held\":" + (e.Proxy ? (e.Held ? "true" : "false") : (e.Root != null && IsHeld(e.Root) ? "true" : "false")) +
                         ",\"dormant\":" + (e.Root == null ? "true" : "false") + ",\"stored\":" + (e.Proxy ? (e.Stored ? "true" : "false") : (IsStored(e.Root) ? "true" : "false")) +
                         ",\"shown\":" + (e.Root != null && e.Root.P != null && e.Root.P.disableThisWhenStored != null ? (e.Root.P.disableThisWhenStored.gameObject.activeSelf ? "true" : "false") : "null") + ",\"pos\":[" + g.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + g.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "," + g.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "]" +
                         (e.Ip != null ? ",\"delayMs\":" + e.Ip.DelayMs.ToString("F0") + ",\"jitterMs\":" + e.Ip.JitterMs.ToString("F0") + ",\"extrapolating\":" + (e.Ip.Extrapolating ? "true" : "false") : "") +
                         // desync in the owner's clock (twocars): an owner's row says when (its state stamps' clock), a copy's row
                         // which moment of the owner's clock it shows — the bridge call's own timing drops out
                         (!e.Proxy ? ",\"clk\":" + (Time.unscaledTimeAsDouble - (Time.timeAsDouble - Time.fixedTimeAsDouble)).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                                   : e.Ip != null && e.Ip.Ready ? ",\"rt\":" + e.Ip.RenderTime.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "") +
                         (e.Root != null ? ",\"yaw\":" + e.Root.transform.eulerAngles.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",\"vel\":" + Vel(e) : "") +
                         (e.Root != null && e.Root.car != null && e.Root.car.Engine != null
                             ? ",\"engRunning\":" + (e.Root.car.Engine.running ? "true" : "false") + ",\"engRpm\":" + e.Root.car.Engine.rpm.ToString("F0") +
                               ",\"fuel\":" + (e.Root.car.Tank != null && e.Root.car.Tank.F != null ? e.Root.car.Tank.F.GetAmount().ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "-1")
                             : "") +
                         ",\"frames\":" + e.Frames + ",\"extrapFrames\":" + e.ExtrapFrames + ",\"jumps\":" + e.Jumps + ",\"maxDevCm\":" + e.MaxDevCm.ToString("F1") +
                         ",\"physical\":" + (e.Physical ? "true" : "false") + ",\"partsColOff\":" + (e.PartsColOff ? "true" : "false") + ",\"byBody\":" + (e.MovedByBody ? "true" : "false") +
                         ",\"seqOut\":" + e.SeqOut + ",\"recv\":" + e.Recv + ",\"why\":[" + e.WhyAwake + "," + e.WhyPos + "," + e.WhyRot + "," + e.WhyStored + "]" + ",\"sentRest\":" + (e.SentAtRest ? "true" : "false") +
                         ",\"lastSent\":" + V3(e.LastSentPos) + ",\"lastIn\":" + (e.Ip != null && e.Ip.Ready ? V3(e.Ip.LastPos) : "null") + "}");
            }
            return "{\"me\":" + MyId + ",\"resync\":{\"sent\":" + ResyncsSent + ",\"applied\":" + ResyncsApplied + ",\"itemsLoaded\":" + ResyncItemsLoaded + ",\"unchanged\":" + ResyncSkipped + ",\"partStates\":" + PartStatesChanged + ",\"bytes\":" + ResyncBytes + ",\"itemsSent\":" + ResyncItemsSent + ",\"captureMsMax\":" + ResyncCaptureMsMax.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",\"captureMsTotal\":" + ResyncCaptureMsTotal.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ",\"held\":" + ResyncHeld + ",\"listsApplied\":" + StateListsApplied + ",\"listsSame\":" + StateListsSame + ",\"attachReapplied\":" + AttachReapplied + ",\"splitSkipped\":" + ResyncSplitSkipped + ",\"foreignAttachDropped\":" + ResyncForeignAttachDropped + ",\"handoversSent\":" + HandoversSent + ",\"handoversApplied\":" + HandoversApplied + ",\"handoversLate\":" + HandoversLate + ",\"handoversCrashSkipped\":" + HandoversCrashSkipped + ",\"attachesKept\":" + AttachesKept + ",\"ragdollLimbsRestored\":" + RagdollLimbsRestored + ",\"attachesReplayed\":" + AttachesReplayed + ",\"handoverLateWhy\":" + Json.Str(HandoverLateWhy) + ",\"handoverDamageSuppressed\":" + HandoverDamageSuppressed + "},\"edits\":{\"marked\":" + EditsMarked + ",\"sent\":" + EditsSent + ",\"applied\":" + EditsApplied + ",\"rejected\":" + EditsRejected + ",\"marks\":" + Json.Str(EditMarks) + "},\"rebound\":" + Rebound + ",\"membersRebound\":" + MembersRebound + ",\"farRecordUpdates\":" + FarRecordUpdates + ",\"farAdds\":" + FarAdds + ",\"farResyncs\":" + FarResyncs + ",\"farResyncItems\":" + FarResyncItems + ",\"farRechunked\":" + FarRechunked + ",\"farAttachedMoved\":" + FarAttachedMoved + ",\"reshared\":" + Reshared + ",\"orphansCleared\":" + OrphansCleared + ",\"leaseKeys\":" + Json.Str(string.Join(" | ", LeaseAsked)) + ",\"leasesDone\":" + LeasesDone + ",\"leasesFreed\":" + LeasesFreed + ",\"builtInReEnabled\":" + BuiltInReEnabled + ",\"spawnShared\":" + SpawnShared + ",\"contactSent\":" + ContactSent + ",\"contactRelayed\":" + ContactRelayed + ",\"contactApplied\":" + ContactApplied + ",\"count\":" + ByNet.Count + ",\"pendingShare\":" + PendingShare.Count + ",\"serverEnts\":" + Server.Count + ",\"claimsSent\":" + ClaimsSent + ",\"claimsRefused\":" + ClaimsRefused + ",\"refusedWhy\":{\"crash\":" + RefusedCrash + ",\"held\":" + RefusedHeld + ",\"stored\":" + RefusedStored + ",\"driven\":" + RefusedDriven + "}" + ",\"partsReported\":" + PartsReported + ",\"partsApplied\":" + PartsApplied + ",\"partsAppliedStored\":" + PartsAppliedStored + ",\"partRejects\":" + Json.Str(PartRejects) + ",\"suppressedDamage\":" + SuppressedDamage + ",\"suppressedFallOff\":" + SuppressedFallOff + ",\"attach\":{\"sent\":" + AttachesSent + ",\"applied\":" + AttachesApplied + ",\"same\":" + AttachesSame + ",\"failed\":" + AttachesFailed + ",\"fails\":" + Json.Str(AttachFails.Length > 300 ? AttachFails.Substring(AttachFails.Length - 300) : AttachFails) + ",\"detachSyncsSent\":" + DetachSyncsSent + ",\"detachSyncsApplied\":" + DetachSyncsApplied + ",\"detachReqsSent\":" + DetachReqsSent + ",\"detachReqsApplied\":" + DetachReqsApplied + ",\"autoDetachesIgnored\":" + AutoDetachesIgnored + ",\"cargoClaimed\":" + CargoClaimed + ",\"cargoNear\":" + CargoClaimedNear + ",\"gameFrozenReleased\":" + GameFrozenReleased + ",\"cargoOnContact\":" + CargoClaimedOnContact + ",\"heldShown\":" + HeldShown + "}" + ",\"statesSent\":" + StatesSent + ",\"statesIn\":" + StatesIn + ",\"stale\":" + StatesStale + ",\"spawned\":" + Spawned +
                   ",\"addRejects\":" + AddRejects + ",\"addRejectLast\":" + Json.Str(AddRejectLast) + ",\"ownerChanges\":" + OwnerChanges + ",\"silentTakeovers\":" + SilentTakeovers + ",\"pushes\":[" + PushesSent + "," + PushesApplied + "," + PushFrames + "]" + ",\"entities\":[" + string.Join(",", rows) + "]}";
        }

        /// Load test (bridge `mp copies render|colliders|scripts on|off`): switch one kind of work off on every car copy here,
        /// to split what 16 players' cars cost each machine — drawing, physics colliders, the game's own scripts.
        public static string CopiesToggle(string what, bool on)
        {
            int n = 0, cars = 0;
            foreach (var e in ByNet.Values)
            {
                if (!e.Proxy || e.Root == null || e.PartIndex >= 0 || e.Root.car == null) continue;
                cars++;
                var root = e.Root.transform.root;
                if (what == "render") foreach (var r in root.GetComponentsInChildren<Renderer>(true)) { r.enabled = on; n++; }
                else if (what == "colliders")
                {
                    foreach (var c in root.GetComponentsInChildren<Collider>(true)) { if (c is WheelCollider) continue; c.enabled = on; n++; }
                    if (on) { e.PartsColOff = false; e.PartCols = null; }   // all back on: the collider LOD decides again
                }
                else if (what == "wheelcols" || what == "jointcols" || what == "restcols")
                {
                    // bisecting which colliders cost the frame's physics sync: under the wheel graphics / on parts hung on
                    // joints (attachablescript without parentAtAttach: re-placed every frame) / everything else
                    var wheelT = new HashSet<Transform>();
                    foreach (var wg in root.GetComponentsInChildren<wheelgraphicsscript>(true)) if (wg.T != null) wheelT.Add(wg.T);
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                    {
                        if (c is WheelCollider) continue;
                        bool inWheel = false, jointed = false;
                        for (var t = c.transform; t != null && t != root; t = t.parent)
                        {
                            if (wheelT.Contains(t)) inWheel = true;
                            var a = t.GetComponent<attachablescript>();
                            if (a != null && a.attached && !a.parentAtAttach) jointed = true;
                        }
                        bool mine = what == "wheelcols" ? inWheel : what == "jointcols" ? jointed && !inWheel : !inWheel && !jointed;
                        if (mine) { c.enabled = on; n++; }
                    }
                }
                else if (what == "scripts")
                    foreach (var m in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (m == null || m.GetType().Assembly != typeof(mainscript).Assembly) continue;   // the game's scripts only
                        if (m is tosaveitemscript) continue;                                              // identity: the entity needs it
                        m.enabled = on; n++;
                    }
                else return "{\"error\":\"render|colliders|wheelcols|jointcols|restcols|scripts\"}";
            }
            int cols = 0, rends = 0, scripts = 0;
            foreach (var e in ByNet.Values)
                if (e.Proxy && e.Root != null && e.PartIndex < 0 && e.Root.car != null)
                {
                    var root = e.Root.transform.root;
                    cols += root.GetComponentsInChildren<Collider>(true).Length; rends += root.GetComponentsInChildren<Renderer>(true).Length;
                    foreach (var m in root.GetComponentsInChildren<MonoBehaviour>(true)) if (m != null && m.GetType().Assembly == typeof(mainscript).Assembly) scripts++;
                }
            return "{\"what\":" + Json.Str(what) + ",\"on\":" + (on ? "true" : "false") + ",\"cars\":" + cars + ",\"switched\":" + n +
                   ",\"colliders\":" + cols + ",\"renderers\":" + rends + ",\"gameScripts\":" + scripts + "}";
        }

        /// Why is a copy (not) visible: object state, renderers, distance to the camera (bridge `mp entdebug`).
        public static string Debug()
        {
            var rows = new List<string>();
            var cam = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.transform.position : Vector3.zero; // the local player (Camera.main isn't the player's camera)
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null) { rows.Add("{\"net\":" + e.NetId + ",\"root\":null}"); continue; }
                var go = e.Root.gameObject;
                var rs = go.GetComponentsInChildren<Renderer>(true);
                int en = 0, act = 0; foreach (var rr in rs) { if (rr.enabled) en++; if (rr.gameObject.activeInHierarchy) act++; }
                var disabledParents = new List<string>();
                for (var t = e.Root.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) disabledParents.Add(t.name);
                rows.Add("{\"net\":" + e.NetId + ",\"name\":" + Json.Str(go.name) + ",\"active\":" + (go.activeInHierarchy ? "true" : "false") +
                         ",\"activeSelf\":" + (go.activeSelf ? "true" : "false") + ",\"inactiveChain\":" + Json.Str(string.Join("/", disabledParents)) +
                         ",\"renderers\":" + rs.Length + ",\"enabled\":" + en + ",\"inActiveObjects\":" + act + ",\"layer\":" + go.layer +
                         ",\"parent\":" + Json.Str(e.Root.transform.parent != null ? e.Root.transform.parent.name : "") +
                         ",\"distToPlayer\":" + (e.Root.transform.position - cam).magnitude.ToString("F1") + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"part\":" + e.PartIndex + "}");
            }
            return "{\"entities\":[" + string.Join(",", rows) + "]}";
        }

        /// Owner side: take part `index` of the newest owned car off, as a wrench would (bridge `mp detachpart <i>`).
        public static string DetachPart(int index)
        {
            Ent car = null;
            foreach (var e in ByNet.Values) if (!e.Proxy && e.PartIndex < 0 && (car == null || e.NetId > car.NetId)) car = e;
            if (car == null) return "{\"error\":\"no owned car\"}";
            if (index < 0 || index >= car.Items.Count || car.Items[index] == null || car.Items[index].attachable == null || !car.Items[index].attachable.attached)
                return "{\"error\":\"part " + index + " not attached\"}";
            car.Items[index].attachable.Detach();
            return "{\"car\":" + car.NetId + ",\"part\":" + index + ",\"name\":" + Json.Str(car.Items[index].name) + "}";
        }

        /// Wrench part `idx` of car `net` off THIS machine's copy through the game's own Detach — on someone else's
        /// car that's the non-owner path (the DetachHook forwards it to the owner).
        public static string Wrench(uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such car here\"}";
            if (idx < 0 || idx >= e.Items.Count || e.Items[idx] == null || e.Items[idx].attachable == null || !e.Items[idx].attachable.attached)
                return "{\"error\":\"part " + idx + " not attached\"}";
            // the player's dismount: slot.UnCraft() for a bolted part (fpscontroller's E-press), else Detach
            var at = e.Items[idx].attachable;
            bool ok = at.slot != null ? at.slot.UnCraft() : Detach(at);
            return "{\"car\":" + net + ",\"part\":" + idx + ",\"name\":" + Json.Str(e.Items[idx].name) + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"ok\":" + (ok ? "true" : "false") + "}";
        }

        /// The player's mount (fpscontroller.MountFunc): drop what's held, slot.Craft it — into the first empty slot of
        /// car `carNet`'s group that takes part `partNet` (CanCraft), or slot `slot` of the car root when >= 0.
        public static string AttachTo(uint partNet, uint carNet, int slot)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !ByNet.TryGetValue(partNet, out var pe) || !Resolve(pe) || pe.Root == null || pe.Root.attachable == null) return "{\"error\":\"no such part here\"}";
            if (!ByNet.TryGetValue(carNet, out var car) || !Resolve(car) || car.Root == null) return "{\"error\":\"no such car here\"}";
            var at = pe.Root.attachable;
            partslotscript target = null; string where = "";
            if (slot >= 0 && slot < car.Root.partslotscripts.Count) { target = car.Root.partslotscripts[slot]; where = car.Root.name + "#" + slot; }
            else
                foreach (var it in car.Items)
                {
                    if (it == null || it.partslotscripts == null) continue;
                    for (int i = 0; i < it.partslotscripts.Count; i++)
                    {
                        var ps = it.partslotscripts[i];
                        if (ps != null && !ps.hasPart() && ps.CanCraft(at)) { target = ps; where = it.name + "#" + i; break; }
                    }
                    if (target != null) break;
                }
            if (target == null) return "{\"error\":\"no free slot for it\"}";
            if (pl.pickedUp == pe.Root.P) pl.Drop();
            if (ForceHeld == pe.Root) ForceHeld = null;   // let go in the same frame it's bolted on (the attach key)
            target.Craft(at);
            return "{\"part\":" + Json.Str(pe.Root.name) + ",\"slot\":" + Json.Str(where) + ",\"attached\":" + (at.attached ? "true" : "false")
                   + ",\"partWasProxy\":" + (pe.Proxy ? "true" : "false") + ",\"carProxy\":" + (car.Proxy ? "true" : "false") + "}";
        }

        /// Is part `partNet` bolted into car `carNet`'s group on this copy (and which slot)?
        public static string PartIn(uint partNet, uint carNet)
        {
            if (!ByNet.TryGetValue(partNet, out var pe) || !Resolve(pe) || pe.Root == null || pe.Root.attachable == null) return "{\"error\":\"no such part here\"}";
            if (!ByNet.TryGetValue(carNet, out var car) || !Resolve(car)) return "{\"error\":\"no such car here\"}";
            var at = pe.Root.attachable; var to = at.AttachedToTosave();
            bool inCar = to != null && (to == car.Root || car.Items.Contains(to));
            return "{\"attached\":" + (at.attached ? "true" : "false") + ",\"inSlot\":" + (at.slot != null ? "true" : "false") + ",\"crafted\":" + (pe.Root.P != null && pe.Root.P.crafted ? "true" : "false")
                   + ",\"inCar\":" + (inCar ? "true" : "false") + ",\"to\":" + Json.Str(to != null ? to.name : "") + ",\"slotIdx\":" + (at.slot != null && to != null ? to.partslotscripts.IndexOf(at.slot) : -1)
                   + ",\"owner\":" + pe.OwnerId + ",\"proxy\":" + (pe.Proxy ? "true" : "false") + ",\"parented\":" + (RidesParent(pe) ? "true" : "false") + "}";
        }

        /// Owner side: car `net` stops dead, as against a wall — every body of the group loses its velocity, so the
        /// game's own crash check (carscript: DamageStuff(|speed − lspeed|)) sees the full impact on its next step.
        public static string CrashStop(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Root.car == null) return "{\"error\":\"no such car here\"}";
            if (e.Proxy) return "{\"error\":\"not simulated here\"}";
            float kmh = e.Root.car.speed; int n = 0;
            foreach (var rb in e.Root.transform.root.GetComponentsInChildren<Rigidbody>())
                if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; n++; }
            foreach (var it in e.Items)
                if (it != null && it.transform.root != e.Root.transform.root)
                    foreach (var rb in it.GetComponentsInChildren<Rigidbody>())
                        if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; n++; }
            return "{\"speedBefore\":" + kmh.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ",\"bodies\":" + n + "}";
        }

        private static bool Detach(attachablescript at) { at.Detach(); return true; }

        /// Owner side: part `idx` of car `net` falls off as in a crash (the game's attachablescript.FallOFf).
        public static string FallOff(uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such car here\"}";
            if (idx < 0 || idx >= e.Items.Count || e.Items[idx] == null || e.Items[idx].attachable == null || e.Items[idx].attachable.slot == null)
                return "{\"error\":\"part " + idx + " not in a slot\"}";
            e.Items[idx].attachable.FallOFf();
            return "{\"car\":" + net + ",\"part\":" + idx + ",\"name\":" + Json.Str(e.Items[idx].name) + ",\"proxy\":" + (e.Proxy ? "true" : "false") + "}";
        }

        /// This machine's copy of loose part `net`: bolted state, colliders, and whether the player's pickup would work —
        /// the game's pickup ray (pickupLayer, FrayRange) cast at it from above and four sides, first hit that resolves
        /// to the part wins; with `pick` the player stands there and picks it up (fpscontroller.Pickup) if the game's
        /// own conditions (fpscontroller.cs pickup branch) pass.
        public static string LookPick(uint net, bool pick)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Root.P == null) return "{\"error\":\"no such item here\"}";
            var p = e.Root.P; var at = e.Root.attachable;
            int cols = 0, on = 0; Collider target = null;
            foreach (var c in e.Root.GetComponentsInChildren<Collider>(true)) { cols++; if (c.enabled && c.gameObject.activeInHierarchy && !c.isTrigger) { on++; if (target == null) target = c; } }
            bool gameOk = p.CanPickup() && !p.cantPickupWhileMount && (at == null || (at.slot == null && !at.attached));
            string hitFrom = "", firstHit = "";
            if (target != null)
            {
                var ctr = target.bounds.center;
                var dirs = new[] { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
                foreach (var d in dirs)
                {
                    var from = ctr + d * 1.3f + (d == Vector3.up ? Vector3.zero : Vector3.up * 0.4f);
                    if (!Physics.Raycast(from, (ctr - from).normalized, out var hit, pl.FrayRange, pl.pickupLayer)) continue;
                    if (firstHit.Length == 0) firstHit = hit.collider.name;
                    if (hit.collider.GetComponentInParent<pickupable>() != p) continue;
                    hitFrom = d.ToString();
                    if (pick && gameOk)
                    {
                        pl.Teleport(from - Vector3.up * 1.6f);
                        pl.Pickup(p, hit.point);
                    }
                    break;
                }
            }
            return "{\"name\":" + Json.Str(e.Root.name) + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"crafted\":" + (p.crafted ? "true" : "false")
                   + ",\"attached\":" + (at != null && at.attached ? "true" : "false") + ",\"inSlot\":" + (at != null && at.slot != null ? "true" : "false")
                   + ",\"colliders\":" + cols + ",\"collidersOn\":" + on + ",\"gameAllows\":" + (gameOk ? "true" : "false")
                   + ",\"rayHitsIt\":" + Json.Str(hitFrom) + ",\"firstHit\":" + Json.Str(firstHit) + ",\"picked\":" + (pick && gameOk && hitFrom.Length > 0 ? "true" : "false") + "}";
        }

        /// The local player picks up shared object `net` (fpscontroller.Pickup, as a player's grab — the claim hook sees it).
        public static string PickupNet(uint net, bool here = false)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Root.P == null) return "{\"error\":\"no such item here\"}";
            if (here)
            {
                // as a player does: where it lies, within reach (the 1 m-before-the-camera shortcut, in a passenger seat
                // facing the side window, put the item outside the car's glass — the user saw it held out of the window)
                var p0 = e.Root.transform.position;
                var camT = pl.Cam != null ? pl.Cam.transform : pl.transform;
                float d = Vector3.Distance(camT.position, p0);
                if (d > pl.dropDist) return "{\"error\":\"out of reach: " + d.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m\"}";
                pl.Pickup(e.Root.P, p0);
                return "{\"picked\":" + Json.Str(e.Root.name) + ",\"here\":true,\"dist\":" + d.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
            }
            // the item comes to the player (1 m in front of the camera), not the player to the item: teleporting the player
            // next to it at a building put them against a wall, the game moved them off, and the held item was past
            // dropDist — dropped at once (leasepair 4 failed every --site run: hand empty 2 s after the pick-up)
            var cam = pl.Cam != null ? pl.Cam.transform : pl.transform;
            var at = cam.position + cam.forward * 1f;
            var rb = e.Root.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            e.Root.transform.position = at;
            pl.Pickup(e.Root.P, at);
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"picked\":" + Json.Str(e.Root.name) + ",\"wasProxy\":" + (e.Proxy ? "true" : "false") +
                   ",\"dist\":" + Vector3.Distance(pl.transform.position, at).ToString("F2", ic) + ",\"dropDist\":" + pl.dropDist.ToString("F2", ic) + "}";
        }

        /// Attached-part indices of car `net` here (for tests picking a part to wrench).
        /// Everything bolted into car `net` on this machine, independent of network ids (a part that came off and went back
        /// is an entity of its own): slot path (slot name under its owner's name) → item model, sorted. meetagain.py
        /// compares it between machines.
        public static string CarParts(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return "{\"error\":\"no such car here\"}";
            if (!Resolve(e) || e.Root == null) return "{\"dormant\":true,\"parts\":[]}";
            var rows = CensusRows(e).ConvertAll(Json.Str);
            // wheels where they are shown: each item in a wheel slot, in the car body's frame (cm)
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var wheels = new List<string>();
            var rt = e.Root.transform;
            foreach (var it in rt.root.GetComponentsInChildren<tosaveitemscript>(true))
            {
                if (it == null || it.attachable == null || !it.attachable.attached || it.attachable.slot == null || !it.attachable.slot.name.Contains("Wheel")) continue;
                var lp = rt.InverseTransformPoint(it.transform.position);
                wheels.Add("[" + Json.Str(it.attachable.slot.name) + "," + lp.x.ToString("F2", ic) + "," + lp.y.ToString("F2", ic) + "," + lp.z.ToString("F2", ic) + "]");
            }
            wheels.Sort(System.StringComparer.Ordinal);
            return "{\"dormant\":false,\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"n\":" + rows.Count + ",\"parts\":[" + string.Join(",", rows) + "],\"wheels\":[" + string.Join(",", wheels) + "]}";
        }

        /// `mp caps <net>`: the tank caps of a car here (radiator, fuel, oil — playtest 2026-10-09: "radiator caps
        /// untouchable by both players and the mouse gets locked"): which item, the usable's kind and state, the colliders
        /// a player's ray needs (enabled, layer), and whether the item is a copy here.
        public static string Caps(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such car here\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var tc in e.Root.transform.root.GetComponentsInChildren<tankcapscript>(true))
            {
                var u = tc.usable;
                var it = tc.GetComponentInParent<tosaveitemscript>();
                var cols = new List<string>();
                foreach (var c in tc.GetComponentsInChildren<Collider>(true)) cols.Add(Json.Str(c.name + ":" + (c.enabled ? "on" : "off") + ":" + LayerMask.LayerToName(c.gameObject.layer) + (c.gameObject.activeInHierarchy ? "" : ":inactive")));
                if (u != null && u.col != null && !tc.GetComponentsInChildren<Collider>(true).Contains(u.col))
                    cols.Add(Json.Str("usable.col " + u.col.name + ":" + (u.col.enabled ? "on" : "off") + ":" + LayerMask.LayerToName(u.col.gameObject.layer)));
                rows.Add("{\"item\":" + Json.Str(it != null ? it.name : "?") + ",\"cap\":" + Json.Str(tc.name) + ",\"proxy\":" + (it != null && IsProxy(it) ? "true" : "false") +
                         ",\"usable\":" + (u == null ? "null" : Json.Str((u.rotateAble ? "rot " : "") + (u.turnable ? "turn " : "") + (u.tuneAble ? "tune " : "") + (u.slideAble ? "slide " : "") +
                                                                       "state " + u.currentTurnState + " x " + u.xRot.ToString("F0", ic) + " enabled " + u.enabled)) +
                         ",\"valve\":" + tc.valve.ToString("F2", ic) + ",\"active\":" + (tc.gameObject.activeInHierarchy ? "true" : "false") + ",\"cols\":[" + string.Join(",", cols) + "]}");
            }
            return "{\"caps\":[" + string.Join(",", rows) + "]}";
        }

        /// `mp breakent <net> [force]`: break a shared object as a player would. Here its owner: the game's Break(); a copy:
        /// TryBreak(force) — the hit a player's blow or shot makes, which goes to the owner (Entities.Ai CopyBreakForce).
        /// Lists what the group holds first (a crate's loot).
        public static string BreakEnt(uint net, float force)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            var br = e.Root.GetComponentInChildren<breakablescript>(true);
            if (br == null) return "{\"error\":\"nothing breakable on " + e.Root.name + "\"}";
            var names = new List<string>();
            foreach (var it in e.Items) names.Add(Json.Str(it != null ? it.name : "null"));
            bool proxy = e.Proxy;
            if (proxy) br.TryBreak(force); else br.Break();
            return "{\"broke\":" + Json.Str(e.Root.name) + ",\"proxy\":" + (proxy ? "true" : "false") + ",\"items\":[" + string.Join(",", names) + "]}";
        }

        /// `mp sent <net>` (host): the server's record of one object — who owns it and the flags that refuse claims.
        public static string ServerEntity(uint net)
        {
            if (!IsHost) return "{\"error\":\"not the host\"}";
            if (!Server.TryGetValue(net, out var se)) return "{\"error\":\"no such object on the server\"}";
            return "{\"net\":" + net + ",\"owner\":" + se.OwnerId + ",\"epoch\":" + se.Epoch + ",\"held\":" + (se.Held ? "true" : "false") + ",\"stored\":" + (se.Stored ? "true" : "false") +
                   ",\"driven\":" + (se.Driven ? "true" : "false") + ",\"crashReturnTo\":" + se.CrashReturnTo + ",\"parentNet\":" + se.ParentNet + ",\"attachParent\":" + se.AttachParent + "}";
        }

        /// `mp usecap <net> <i>`: the player's action on the car's i-th tank cap (as `mp caps` lists them): a click if it
        /// turns, a drag to the other end if it rotates, a step up if it tunes — each through the game's own call, which
        /// syncs (SyncMulti) as a player's does.
        public static string UseCap(uint net, int i)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such car here\"}";
            var caps = e.Root.transform.root.GetComponentsInChildren<tankcapscript>(true);
            if (i < 0 || i >= caps.Length) return "{\"error\":\"no cap " + i + " of " + caps.Length + "\"}";
            var u = caps[i].usable;
            if (u == null) return "{\"error\":\"cap " + i + " has no usable\"}";
            string did;
            if (u.turnable) { u.Turn(); did = "turn"; }
            else if (u.rotateAble)
            {
                float dx = u.maxX - u.minX > 1f ? (u.xRot - u.minX < u.maxX - u.xRot ? u.maxX - u.xRot : u.minX - u.xRot) : 0f;
                float dy = u.maxY - u.minY > 1f ? (u.yRot - u.minY < u.maxY - u.yRot ? u.maxY - u.yRot : u.minY - u.yRot) : 0f;
                u.Rot(dx, dy, true, true); did = "rot " + dx.ToString("F0") + "," + dy.ToString("F0");
            }
            else if (u.tuneAble) { u.Tuned(1f, true); did = "tune"; }
            else return "{\"error\":\"cap " + i + ": not turnable, rotatable or tunable\"}";
            return "{\"did\":" + Json.Str(did) + ",\"state\":" + u.currentTurnState + ",\"x\":" + u.xRot.ToString("F0") + ",\"valve\":" + caps[i].valve.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// `mp pcs <net>`: each member's part conditions — the random look a part rolled (whitewall tyre or not:
        /// slectedRandomTipus, the material type it shows: tipus), state, colour.
        public static string PartConds(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            var rows = new List<string>();
            for (int i = 0; i < e.Items.Count; i++)
            {
                var it = e.Items[i];
                if (it == null || it.partconditions == null) continue;
                for (int k = 0; k < it.partconditions.Count; k++)
                {
                    var pc = it.partconditions[k];
                    if (pc == null) continue;
                    rows.Add("[" + i + "," + k + "," + Json.Str(PrefabName(it.id)) + "," + pc.slectedRandomTipus + "," + Json.Str(pc.tipus.ToString()) + "," + pc.state + "]");
                }
            }
            return "{\"pcs\":[" + string.Join(",", rows) + "]}";
        }

        /// `mp wheelpose <net>`: each wheel mesh of a car as drawn (wheelgraphicsscript.T, hierarchy order) in the car's
        /// frame — height (suspension), steer (its heading against the car's) — and the tyre in the slot (playtest
        /// 2026-10-09: "tires desynced when both drive").
        public static string WheelPose(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var ct = e.Root.transform;
            var rows = new List<string>();
            foreach (var wg in e.Root.GetComponentsInChildren<wheelgraphicsscript>(true))
            {
                if (wg.T == null) { rows.Add("null"); continue; }
                var lp = ct.InverseTransformPoint(wg.T.position);
                var fwd = ct.InverseTransformDirection(wg.T.rotation * Vector3.forward);
                // the wheel's heading in the car's plane, whatever axis it spins around: its right axis (the axle) turned
                var axle = ct.InverseTransformDirection(wg.T.rotation * Vector3.right);
                float steer = Mathf.Atan2(-axle.z, axle.x) * Mathf.Rad2Deg;
                var pt = wg.slot != null && wg.slot.hasPart() ? wg.slot.part() : null;   // by prefab: a spawned car's own wheels are named "Wheel", a copy's "TireFelni(Clone)"
                var part = pt == null ? "" : pt.tosave != null ? PrefabName(pt.tosave.id) : pt.name;
                rows.Add("[" + lp.x.ToString("F3", ic) + "," + lp.y.ToString("F3", ic) + "," + lp.z.ToString("F3", ic) + "," + steer.ToString("F1", ic) + "," + Json.Str(part) + "]");
            }
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"wheels\":[" + string.Join(",", rows) + "]}";
        }

        /// `mp farrec <net>`: a dormant copy's far-store side — its record (there? where?), filed in the game's chunk index
        /// under that place, a live object holding its id (the game's PlaceStuff drops a record whose id a live,
        /// non-map item holds — it would never be placed), the nearest player to it (place range itemSpawnDist).
        public static string FarRec(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return "{\"error\":\"no such entity\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            var sb = new System.Text.StringBuilder("{\"rootId\":" + e.RootId + ",\"dormant\":" + (e.Root == null ? "true" : "false"));
            bool has = data != null && data.itemData != null && savedatascript.IndexOfID(data.itemData.items, e.RootId, out int k);
            sb.Append(",\"record\":" + (has ? "true" : "false"));
            if (has)
            {
                var pos = data.itemData.items[savedatascript.IndexOfID(data.itemData.items, e.RootId, out k) ? k : 0].transform.pos;
                var key = savedatascript.SnapChunkPos(pos);
                bool filed = savedatascript.s.itemChunks.TryGetValue(key, out var ids) && ids.Contains(e.RootId);
                int filedElsewhere = 0;
                foreach (var kv in savedatascript.s.itemChunks) if (!(kv.Key.x == key.x && kv.Key.y == key.y) && kv.Value.Contains(e.RootId)) filedElsewhere++;
                double best = double.MaxValue;
                if (menuhandler.s != null && menuhandler.s.currentMainMap != null)
                    foreach (var ga in menuhandler.s.currentMainMap.genArounds) best = System.Math.Min(best, (mainscript.GlobalFromUnityPos(ga.upos) - pos).magnitude);
                sb.Append(",\"pos\":[" + pos.x.ToString("F1", ic) + "," + pos.y.ToString("F1", ic) + "," + pos.z.ToString("F1", ic) + "],\"filed\":" + (filed ? "true" : "false") +
                          ",\"filedElsewhere\":" + filedElsewhere + ",\"nearestGenAround\":" + best.ToString("F1", ic) +
                          ",\"spawnDist\":" + (itemPlaceRemoveScript.s != null ? itemPlaceRemoveScript.s.itemSpawnDist.ToString("F0", ic) : "-1"));
            }
            bool live = savedatascript.s.items.TryGetValue(e.RootId, out var lv);
            sb.Append(",\"liveEntry\":" + (live ? (lv == null ? "\"destroyed\"" : Json.Str(lv.name + (lv.mapSpawned ? " (map)" : ""))) : "null"));
            int members = 0, memberRecords = 0;
            if (e.ItemIds != null) foreach (var id in e.ItemIds) { if (id == 0) continue; members++; if (has && savedatascript.IndexOfID(data.itemData.items, id, out _)) memberRecords++; }
            sb.Append(",\"members\":" + members + ",\"memberRecords\":" + memberRecords + ",\"farDirty\":" + (e.FarDirty ? "true" : "false") + "}");
            return sb.ToString();
        }

        /// `mp claim <net>`: take over a copy the way a push grip or a car's cargo claim does (provisional, the server decides)
        public static string ClaimTest(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return "{\"error\":\"no such entity\"}";
            if (!e.Proxy) return "{\"claimed\":false,\"why\":\"ours already\"}";
            SetProxy(e, false);
            e.OwnerId = MyId;
            W.Reset(); W.U8(Claim); W.U32(e.NetId); ToServer(W, true);
            ClaimsSent++;
            return "{\"claimed\":true}";
        }

        public static string SetForceHeld(uint net)
        {
            if (net == 0) { ForceHeld = null; return "{\"forceHeld\":null}"; }
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such item here\"}";
            ForceHeld = e.Root;
            return "{\"forceHeld\":" + Json.Str(e.Root.name) + ",\"proxy\":" + (e.Proxy ? "true" : "false") + "}";
        }

        public static string AttachedParts(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such car here\"}";
            var rows = new List<string>();
            for (int i = 0; i < e.Items.Count; i++)
                if (e.Items[i] != null && e.Items[i].attachable != null && e.Items[i].attachable.attached)
                    rows.Add("{\"i\":" + i + ",\"name\":" + Json.Str(e.Items[i].name) + "}");
            return "{\"parts\":[" + string.Join(",", rows) + "]}";
        }

        /// Nearest car that is not shared and wasn't spawned by a test (world generation made it) — scenario 6.
        public static string WorldCar()
        {
            if (mainscript.s == null || mainscript.s.player == null) return "{\"error\":\"not in game\"}";
            var p = mainscript.s.player.transform.position;
            tosaveitemscript best = null; float bd = float.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || it.car == null || it.transform.parent != null || !it.gameObject.activeInHierarchy) continue;
                bool shared = false; foreach (var e in ByNet.Values) if (e.Root == it) shared = true;
                if (shared || it.name.StartsWith("Car01Full")) continue;   // Car01Full = the tests' spawned car
                float d = (it.transform.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = it; }
            }
            if (best == null) return "{\"error\":\"no world car\"}";
            var g = mainscript.GlobalFromUnityPos(best.transform.position);
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"name\":" + Json.Str(best.name) + ",\"pos\":[" + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "]}";
        }

        /// All active items within `r` metres of a global point: count, how many are shared, and a fingerprint of
        /// (model, position to 10 cm) — two machines showing the same world contents print the same fingerprint.
        private static readonly List<string> Unshared = new List<string>();

        public static string ItemsNear(double x, double z, double r)
        {
            Unshared.Clear();
            var rows = new List<string>();
            int shared = 0;
            var sharedSet = new HashSet<tosaveitemscript>();
            foreach (var e in ByNet.Values) foreach (var it in e.Items) if (it != null) sharedSet.Add(it);
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                var g = mainscript.GlobalFromUnityPos(it.transform.position);
                if ((g.x - x) * (g.x - x) + (g.z - z) * (g.z - z) > r * r) continue;
                if (!sharedSet.Contains(it) && Unshared.Count < 40)
                    Unshared.Add(it.name + (it.transform.parent != null ? " (in " + it.transform.parent.name + ")" : "") + (it.attachable != null && it.attachable.attached ? " attached" : "") +
                                 (it.P != null && (it.P.pickedUp || it.P.inInventory() != 0) ? " held/inventory" : ""));
                rows.Add(it.id + "@" + Math.Round(g.x, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + Math.Round(g.z, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (sharedSet.Contains(it)) shared++;
            }
            rows.Sort(StringComparer.Ordinal);
            ulong h = 14695981039346656037UL;
            foreach (var row in rows) { foreach (char c in row) { h ^= c; h *= 1099511628211UL; } h ^= 0xFF; h *= 1099511628211UL; }
            return "{\"unshared\":[" + string.Join(",", Unshared.ConvertAll(Json.Str)) + "],\"count\":" + rows.Count + ",\"shared\":" + shared + ",\"hash\":\"" + h.ToString("X16") + "\",\"leases\":{\"asked\":" + LeasesAsked +
                   ",\"granted\":" + LeasesGranted + ",\"spawns\":" + LeaseSpawns + ",\"itemsShared\":" + LeaseItemsShared + ",\"hostShared\":" + HostItemsShared + ",\"builtInShared\":" + BuiltInShared + ",\"builtInDisabled\":" + BuiltInDisabled + ",\"done\":" + LeasesDone + ",\"freed\":" + LeasesFreed + ",\"reEnabled\":" + BuiltInReEnabled + "}}";
        }

        /// Every active item within `r` m: model id and global position (tolerant comparisons in the tools).
        public static string ItemsList(double x, double z, double r)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                var g = mainscript.GlobalFromUnityPos(it.transform.position);
                if ((g.x - x) * (g.x - x) + (g.z - z) * (g.z - z) > r * r) continue;
                rows.Add("[" + it.id + "," + g.x.ToString("F3", ic) + "," + g.y.ToString("F3", ic) + "," + g.z.ToString("F3", ic) + "]");
            }
            return "{\"items\":[" + string.Join(",", rows) + "]}";
        }

        /// A vehicle's seats: its own hierarchy's (indices as before), then those of every body outside it that carries its
        /// part slots — Bus01's rear section (BusBack: its own body on a joint, 33 of the bus's 81 seats). Before, a player
        /// in the back of a bus counted as not seated: the other machines showed them standing, trailing the bus.
        public static bool RearSeats = true;   // off: the old behaviour (A/B in tools/busrear.py)
        private static readonly Dictionary<Transform, seatscript[]> _seats = new Dictionary<Transform, seatscript[]>();
        private static readonly Dictionary<Transform, seatscript[]> _seatsOwn = new Dictionary<Transform, seatscript[]>();
        internal static seatscript[] VehicleSeats(tosaveitemscript vehicle, bool always = false)
        {
            bool all = RearSeats || always;
            var cache = all ? _seats : _seatsOwn;
            var root = vehicle.transform.root;
            if (cache.TryGetValue(root, out var have) && have.Length > 0 && have[0] != null) return have;
            if (cache.Count > 256) cache.Clear();   // destroyed vehicles' entries
            var list = new List<seatscript>(root.GetComponentsInChildren<seatscript>(true));
            if (all && vehicle.partslotscripts != null)
            {
                var extra = new List<Transform>();
                foreach (var sl in vehicle.partslotscripts)
                    if (sl != null && sl.transform.root != root && !extra.Contains(sl.transform.root)) extra.Add(sl.transform.root);
                extra.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
                foreach (var t in extra) list.AddRange(t.GetComponentsInChildren<seatscript>(true));
            }
            var arr = list.ToArray();
            cache[root] = arr;
            return arr;
        }

        /// `seats <net>`: the vehicle's seat list as the seat sync numbers it — index, seat, the body it sits on, driver
        /// flags, whether it redirects (mainseat) and whether someone can sit there now.
        public static string SeatList(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no entity " + net + "\"}";
            var seats = VehicleSeats(e.Root, true);
            var rows = new List<string>();
            for (int i = 0; i < seats.Length; i++)
            {
                var st = seats[i];
                rows.Add("[" + i + "," + Json.Str(st.name) + "," + Json.Str(st.transform.root.name) + "," + (st.driverSeat0 ? 1 : 0) + "," +
                         (st.mainseat != null ? 1 : 0) + "," + (st.FreeCanSit() ? 1 : 0) + "," + (st.gameObject.activeInHierarchy ? 1 : 0) + "]");
            }
            return "{\"root\":" + Json.Str(e.Root.transform.root.name) + ",\"own\":" + e.Root.transform.root.GetComponentsInChildren<seatscript>(true).Length +
                   ",\"slotsOutside\":" + (e.Root.partslotscripts == null ? -1 : e.Root.partslotscripts.FindAll(x => x != null && x.transform.root != e.Root.transform.root).Count) +
                   ",\"outside\":[" + string.Join(",", e.Root.partslotscripts == null ? new string[0] : e.Root.partslotscripts.FindAll(x => x != null && x.transform.root != e.Root.transform.root)
                        .ConvertAll(x => Json.Str(x.name + " root " + x.transform.root.name + " (" + x.transform.root.GetComponentsInChildren<seatscript>(true).Length + " seats) parent " +
                                                  (x.transform.parent != null ? x.transform.parent.name : "-") + " rb " + (x.GetComponentInParent<Rigidbody>() != null ? x.GetComponentInParent<Rigidbody>().name : "-"))).ToArray()) + "]" +
                   ",\"seats(i,name,body,driver,redirect,free,active)\":[" + string.Join(",", rows) + "]}";
        }

        /// The local player's seat, if it belongs to a shared object: (network id, seat index), else net 0.
        public static void LocalSeat(out uint net, out int idx)
        {
            net = 0; idx = 0;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !pl.Bsitting || pl.seat == null) return;
            var root = pl.seat.transform.root;
            foreach (var e in ByNet.Values)
            {
                if (e.Root == null || e.PartIndex >= 0) continue;
                // only the vehicle itself: an item in the seated player's hand is in the car's hierarchy too (hand →
                // player → seat → car), and VehicleSeats of it is the car's seats — a lamp held in the right hand was
                // sent as the seat, the others found no seat 1 on a lamp and drew the passenger trailing the car on the
                // road (heldincar.py, 2026-10-08)
                if (e.Root.car == null && e.Root.transform != root) continue;
                var seats = e.Root.transform.root == root ? VehicleSeats(e.Root) : null;
                if (seats == null)
                {
                    // a seat on a body outside the vehicle's hierarchy (the back of a bus): one of its slot bodies
                    if (e.Root.partslotscripts == null || e.Root.car == null) continue;
                    bool mine = false;
                    foreach (var sl in e.Root.partslotscripts) if (sl != null && sl.transform.root == root) { mine = true; break; }
                    if (!mine) continue;
                    if (!RearSeats) continue;
                    seats = VehicleSeats(e.Root);
                }
                int i = Array.IndexOf(seats, pl.seat);
                if (i < 0) return;
                net = e.NetId; idx = i;
                return;
            }
        }

        /// Where a player seated in shared object `net`, seat `idx`, sits on this machine (null if not here).
        /// Per frame for every seated remote player: the seat found once per (car copy, seat) and kept. Searching the
        /// car's hierarchy every frame cost the laptop 223 ms/s at 16 seated players (~5 ms a frame; spread load test,
        /// v0.57.89) — before the bots sat in their cars no test had seated players in numbers.
        public static Transform SeatTransformCached(uint net, int idx, ref Transform carRoot, ref Transform seat)
        {
            if (!ByNet.TryGetValue(net, out var e) || (e.Root == null && !Resolve(e))) return null;
            var root = e.Root.transform.root;
            if (seat != null && carRoot == root) return seat;
            carRoot = root;
            seat = SeatTransform(net, idx);
            return seat;
        }

        public static Transform SeatTransform(uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return null;
            var seats = VehicleSeats(e.Root);
            if (idx < 0 || idx >= seats.Length) return null;
            return seats[idx].sitPos != null ? seats[idx].sitPos : seats[idx].transform;
        }

        /// Raw seat data of the car nearest to the player (no session needed): what each seatscript redirects to
        /// (mainseat), whether it has its own sitPos, driver flags and look limits — the prefab truth.
        public static string SeatRaw()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            tosaveitemscript car = null; float best = float.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.car != null && it.transform.parent == null) { float d = (it.transform.position - pl.transform.position).sqrMagnitude; if (d < best) { best = d; car = it; } }
            if (car == null) return "{\"error\":\"no car\"}";
            var seats = car.GetComponentsInChildren<seatscript>(true);
            var rows = new List<string>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < seats.Length; i++)
            {
                var st = seats[i];
                string main = st.mainseat == null ? "null" : st.mainseat == st ? "self" : ("#" + System.Array.IndexOf(seats, st.mainseat));
                rows.Add("{\"i\":" + i + ",\"name\":" + Json.Str(st.name) + ",\"main\":" + Json.Str(main)
                         + ",\"d0\":" + (st.driverSeat0 ? "true" : "false") + ",\"d1\":" + (st.driverSeat1 ? "true" : "false")
                         + ",\"ownSitPos\":" + (st.sitPos != null ? "true" : "false")
                         + ",\"rotLimit\":" + st.rotLimit.ToString(ci) + ",\"limitless\":" + (st.limitLessRot ? "true" : "false")
                         + ",\"lookInstead\":" + (st.lookInsteadOfLean ? "true" : "false")
                         + ",\"collider\":" + (st.GetComponent<Collider>() != null ? "true" : "false") + "}");
            }
            return "{\"car\":" + Json.Str(car.name) + ",\"seats\":[" + string.Join(",", rows) + "]}";
        }

        /// Sit (the game's own GetIn) in seat `idx` of the nearest car — as if E was pressed on that seatscript,
        /// including the mainseat redirect the game applies when you look at a seat.
        public static string SitSeat(int idx, bool redirect, uint net = 0)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            tosaveitemscript car = null; float best = float.MaxValue;
            if (net != 0) { if (!ByNet.TryGetValue(net, out var se) || !Resolve(se)) return "{\"error\":\"no entity " + net + "\"}"; car = se.Root; best = 0f; }
            else foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.car != null && it.transform.parent == null) { float d = (it.transform.position - pl.transform.position).sqrMagnitude; if (d < best) { best = d; car = it; } }
            if (car == null) return "{\"error\":\"no car\"}";
            var seats = VehicleSeats(car, true);
            if (idx < 0 || idx >= seats.Length) return "{\"error\":\"no seat " + idx + "\"}";
            var st = seats[idx];
            if (redirect && st.mainseat != null) st = st.mainseat;
            if (!st.FreeCanSit()) return "{\"error\":\"seat not free\"}";
            pl.GetIn(st);
            return "{\"sat\":" + System.Array.IndexOf(seats, st) + "}";
        }

        /// The local player's seated look state.
        public static string SeatLook()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var st = pl.seat;
            return "{\"died\":" + (mainscript.s.died ? "true" : "false") + ",\"god\":" + (kaposztaleves.s != null && kaposztaleves.s.settings.god ? "true" : "false")
                   + ",\"sitting\":" + (pl.Bsitting ? "true" : "false") + ",\"seat\":" + Json.Str(st != null ? st.name : "")
                   + ",\"rotLimit\":" + (st != null ? st.rotLimit.ToString(ci) : "0") + ",\"limitless\":" + (st != null && st.limitLessRot ? "true" : "false")
                   + ",\"driver\":" + (st != null && st.driverSeat0 ? "true" : "false") + ",\"shotgun\":" + (st != null && st.driverSeat1 && !st.driverSeat0 ? "true" : "false")
                   + ",\"FxRot\":" + pl.FxRot.ToString("F1", ci) + ",\"FyRot\":" + pl.FyRot.ToString("F1", ci)
                   + (st != null && st.Car != null ? ",\"carSteer\":" + st.Car.steer.ToString("F2", ci) + ",\"steerAngle\":" + st.Car.steerAngle.ToString("F1", ci)
                      + ",\"lights\":" + (st.Car.lightUsable != null ? st.Car.lightUsable.currentTurnState : -1) : "") + "}";
        }

        /// Sit in the DRIVER seat of shared object `net` (for the drive-claim test: entering it flips ownership).
        /// Per-seat diagnostics for shared object `net`: why can't the driver seat be taken?
        public static string SeatDump(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            var seats = e.Root.transform.root.GetComponentsInChildren<seatscript>(true);
            var rows = new List<string>();
            for (int i = 0; i < seats.Length; i++)
            {
                var st = seats[i];
                rows.Add("{\"i\":" + i + ",\"main\":" + (st.mainseat == st ? "true" : "false")
                         + ",\"d0\":" + (st.driverSeat0 ? "true" : "false") + ",\"d1\":" + (st.driverSeat1 ? "true" : "false")
                         + ",\"free\":" + (st.FreeCanSit() ? "true" : "false") + ",\"inUse\":" + (st.inUse ? "true" : "false")
                         + ",\"sitPos\":" + (st.sitPos != null ? "true" : "false")
                         + ",\"lookInstead\":" + (st.lookInsteadOfLean ? "true" : "false") + "}");
            }
            return "{\"seats\":[" + string.Join(",", rows) + "]}";
        }

        private static float FlatDist(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }

        public static string SitDriver(uint net)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            var seats = e.Root.transform.root.GetComponentsInChildren<seatscript>(true);
            // already sitting in this vehicle (a test's second call after the first one sat the player): done
            if (pl.Bsitting && pl.seat != null)
                for (int k = 0; k < seats.Length; k++)
                    if (seats[k] == pl.seat && seats[k].driverSeat0 == true) return "{\"seat\":" + k + ",\"already\":true}";
            for (int i = 0; i < seats.Length; i++)
            {
                var st = seats[i];
                if (!st.driverSeat0 || !st.FreeCanSit()) continue;
                // across the ground only: a bus's seat is ~2 m over it, and the player stepped beside it lands below —
                // every later call stepped again and never sat (busdrive.py: the laptop never took the bus's wheel)
                var flat = pl.transform.position - st.transform.position; flat.y = 0f;
                if (flat.magnitude > 8f)
                {
                    // teleport OUTSIDE the body, away from the car's centre through the seat — a point beside the
                    // seat itself is INSIDE the van: the player dies in the geometry, the scene reloads and the
                    // session dies (the laptop "disconnect" on teleport)
                    var root = st.transform.root.position;
                    var away = st.transform.position - root; away.y = 0f;
                    if (away.sqrMagnitude < 0.01f) away = st.transform.right; away.Normalize();
                    var was = FlatDist(pl.transform.position, st.transform.position);
                    pl.Teleport(st.transform.position + away * 2.2f + Vector3.up * 0.5f);
                    return "{\"stepped\":true,\"seat\":" + i + ",\"flatBefore\":" + was.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                           ",\"seatUp\":" + (st.transform.position.y - (DebugBridge.StandHit(st.transform.position.x, st.transform.position.z, null)?.point.y ?? st.transform.position.y)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
                }
                pl.GetIn(st);
                return "{\"seat\":" + i + "}";
            }
            return "{\"error\":\"no free driver seat\"}";
        }

        /// Sit in a free passenger seat of shared object `net` (steps next to it first, like DriveLab.GetIn).
        /// What a bullet can change on a car, as this machine has it: every breakable part (id, name, global position, the
        /// three healths) and every tank (global position of its collider, amount) — tools/shootcar.py compares machines.
        /// Test: `mp paint <net> [r g b]` — the game's own spray stroke (partconditionscript.Paint, what sprayscript.Fire
        /// calls each frame it hits) on the first paintable part of shared object <net>; without a colour: every
        /// paintable part's colour, in a stable order (path in the object), to compare machines.
        public static string PaintLab(uint net, Color? c)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no entity " + net + "\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<partconditionscript>(e.Root.GetComponentsInChildren<partconditionscript>(true));
            parts.RemoveAll(x => x == null || !x.gameObject.activeInHierarchy);
            parts.Sort((x, y) => string.CompareOrdinal(PathIn(x.transform, e.Root.transform), PathIn(y.transform, e.Root.transform)));
            if (c != null)
            {
                foreach (var pc in parts)
                    if (pc.CanPaint()) { pc.Paint(c.Value); return "{\"painted\":" + Json.Str(PathIn(pc.transform, e.Root.transform) + "#" + System.Array.IndexOf(pc.GetComponents<partconditionscript>(), pc)) + ",\"sameObject\":" + pc.GetComponents<partconditionscript>().Length + "}"; }
                return "{\"error\":\"nothing paintable\"}";
            }
            var rows = new List<string>();
            foreach (var pc in parts)
            {
                // what's on screen: the first renderer's material colour (Refresh puts the part's colour there)
                Renderer rd = null; if (pc.renderers != null) foreach (var x in pc.renderers) if (x != null) { rd = x; break; }
                var sc = rd != null && rd.sharedMaterial != null && rd.sharedMaterial.HasProperty("_Color") ? rd.sharedMaterial.color : new Color(-1f, -1f, -1f);
                rows.Add("[" + Json.Str(PathIn(pc.transform, e.Root.transform) + "#" + System.Array.IndexOf(pc.GetComponents<partconditionscript>(), pc)) + "," + pc.color.r.ToString("F3", ic) + "," + pc.color.g.ToString("F3", ic) + "," + pc.color.b.ToString("F3", ic) + "," + pc.state +
                         "," + sc.r.ToString("F3", ic) + "," + sc.g.ToString("F3", ic) + "," + sc.b.ToString("F3", ic) + "]");
            }
            return "{\"parts\":[" + string.Join(",", rows) + "]}";
        }

        private static string PathIn(Transform t, Transform root)
        {
            if (t == root) return "";   // the object itself
            var sb = new System.Text.StringBuilder(t.name);
            for (var x = t.parent; x != null && x != root; x = x.parent) sb.Insert(0, x.name + "/");
            return sb.ToString();
        }

        public static string InterpDump(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return "{\"error\":\"no entity\"}";
            return e.Ip != null ? e.Ip.Dump() : "{\"error\":\"not a copy here\"}";
        }

        public static string CarBreak(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var root = e.Root.transform.root;
            var parts = new List<string>();
            // every breakablescript under the car (on a part's save script or on a bare child object)
            foreach (var br in root.GetComponentsInChildren<breakablescript>(true))
            {
                if (br == null) continue;
                var it = br.GetComponentInParent<tosaveitemscript>();
                var c = br.GetComponentInChildren<Collider>();
                var g = mainscript.GlobalFromUnityPos(c != null ? c.bounds.center : br.transform.position);
                parts.Add("{\"id\":" + (it != null ? it.idInSave : 0) + ",\"name\":" + Json.Str(br.name) + ",\"item\":" + Json.Str(it != null ? it.name : "") +
                          ",\"pos\":[" + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "]" +
                          ",\"hp\":" + br.health.ToString("F1", ic) + ",\"shootHp\":" + br.shootHealth.ToString("F1", ic) + ",\"active\":" + (br.gameObject.activeInHierarchy ? "true" : "false") +
                          ",\"destroyed\":" + (br.destroyed ? "true" : "false") + ",\"attached\":" + (it != null && it.attachable != null && it.attachable.attached ? "true" : "false") + "}");
            }
            int bc = root.GetComponentsInChildren<breakchilds>(true).Length;
            var tanks = new List<string>();
            foreach (var t in root.GetComponentsInChildren<tankscript>(true))
            {
                if (t == null || t.F == null) continue;
                var c = t.GetComponentInChildren<Collider>();
                var g = mainscript.GlobalFromUnityPos(c != null ? c.bounds.center : t.transform.position);
                int holes = t.GetComponentsInChildren<tankcapscript>(true).Length;
                // where it hangs: up to the nearest item (tosaveitemscript) — vehiclezoo: a fresh Car06Full had a tank its copy lacked
                string path = t.name; var up = t.transform.parent; int hops = 0;
                while (up != null && hops++ < 6) { path = up.name + "/" + path; if (up.GetComponent<tosaveitemscript>() != null) break; up = up.parent; }
                var owner = t.GetComponentInParent<tosaveitemscript>();
                tanks.Add("{\"name\":" + Json.Str(t.name) + ",\"path\":" + Json.Str(path) + ",\"item\":" + Json.Str(owner != null ? owner.name + " " + owner.idInSave : "-") + ",\"active\":" + (t.gameObject.activeInHierarchy ? "true" : "false") + ",\"pos\":[" + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "]" +
                          ",\"amount\":" + t.F.GetAmount().ToString("F3", ic) + ",\"caps\":" + holes + "}");
            }
            return "{\"net\":" + net + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"breakchilds\":" + bc + ",\"parts\":[" + string.Join(",", parts) + "],\"tanks\":[" + string.Join(",", tanks) + "]}";
        }

        public static string SitPassenger(uint net)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || !ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            var seats = e.Root.transform.root.GetComponentsInChildren<seatscript>(true);
            // already sitting in this vehicle (a test's second call after the first one sat the player): done
            if (pl.Bsitting && pl.seat != null)
                for (int k = 0; k < seats.Length; k++)
                    if (seats[k] == pl.seat && seats[k].driverSeat0 == false) return "{\"seat\":" + k + ",\"already\":true}";
            for (int i = 0; i < seats.Length; i++)
            {
                var st = seats[i];
                // a real seat (the seatcol colliders redirect to one through mainseat, like the game's E-press), not the driver's
                if (st.driverSeat0 || (st.mainseat != null && st.mainseat != st) || !st.FreeCanSit()) continue;
                if (FlatDist(pl.transform.position, st.transform.position) > 8f)
                {
                    var root = st.transform.root.position;
                    var away = st.transform.position - root; away.y = 0f;
                    if (away.sqrMagnitude < 0.01f) away = st.transform.right; away.Normalize();
                    var was = FlatDist(pl.transform.position, st.transform.position);
                    pl.Teleport(st.transform.position + away * 2.2f + Vector3.up * 0.5f);
                    return "{\"stepped\":true,\"seat\":" + i + ",\"flatBefore\":" + was.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                           ",\"seatUp\":" + (st.transform.position.y - (DebugBridge.StandHit(st.transform.position.x, st.transform.position.z, null)?.point.y ?? st.transform.position.y)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
                }
                pl.GetIn(st);
                return "{\"seat\":" + i + "}";
            }
            return "{\"error\":\"no free passenger seat\"}";
        }

        /// Every active item within `r` m of a GLOBAL point, as this machine's game has it — model, position and the
        /// state a save keeps (fuel, part conditions, attached, switches). Independent of network ids: compares an area
        /// across machines and across a server restart (tools/farworld.py, "is it as it was left?").
        /// Bridge `mp partposes <net>`: every item of shared object `net`, its pose relative to the group root (local
        /// position, rotation as euler) — compared between machines (tools/vanparts.py: a client's van whose doors and
        /// parts were "attached but in the wrong direction" on the host, first play session). sitestate has positions
        /// only: a hinged part turned the wrong way passes it.
        public static string PartPoses(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rt = e.Root.transform;
            var rows = new List<string>();
            for (int i = 0; i < e.Items.Count; i++)
            {
                var it = e.Items[i];
                if (it == null) { rows.Add("[" + i + ",null]"); continue; }
                var lp = rt.InverseTransformPoint(it.transform.position);
                var lr = (Quaternion.Inverse(rt.rotation) * it.transform.rotation).eulerAngles;
                var a = it.attachable;
                rows.Add("[" + i + "," + Json.Str(it.name) + "," + lp.x.ToString("F3", ic) + "," + lp.y.ToString("F3", ic) + "," + lp.z.ToString("F3", ic) + "," +
                         lr.x.ToString("F1", ic) + "," + lr.y.ToString("F1", ic) + "," + lr.z.ToString("F1", ic) + "," +
                         (a != null && a.attached ? "true" : "false") + "," + Json.Str(it.transform.parent != null ? it.transform.parent.name : "") + "," + it.id + "]");
            }
            return "{\"net\":" + net + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"root\":" + Json.Str(e.Root.name) + ",\"items\":[" + string.Join(",", rows) + "]}";
        }

        /// `itement gx gy gz`: the item nearest that global point and the shared object it belongs to (diagnostics for
        /// `physlocks gx gz r`: every item within r m — where it is, whether a container (mountStuff: crates, shelves,
        /// trunks; the game's physics lock) holds it and which, and the shared object it belongs to here. The game stores
        /// and releases locally on every machine; tools/lockprobe.py watches it over time.
        public static string PhysLocks(double x, double z, double r)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var ent = new Dictionary<tosaveitemscript, Ent>();
            foreach (var e in ByNet.Values) foreach (var it in e.Items) if (it != null && !ent.ContainsKey(it)) ent[it] = e;
            var rows = new List<string>();
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                var g = mainscript.GlobalFromUnityPos(it.transform.position);
                if ((g.x - x) * (g.x - x) + (g.z - z) * (g.z - z) > r * r) continue;
                var ms = it.P != null ? it.P.physlock : null;
                var ctr = ms != null ? ms.GetComponentInParent<tosaveitemscript>() : null;
                ent.TryGetValue(it, out var e);
                var rb = it.GetComponent<Rigidbody>();
                rows.Add("[" + it.idInSave + "," + Json.Str(it.name) + "," + g.x.ToString("F2", ic) + "," + g.y.ToString("F2", ic) + "," + g.z.ToString("F2", ic) + "," +
                         Json.Str(ms != null ? ms.sType.ToString() : "") + "," + (ctr != null ? ctr.idInSave : 0) + "," + Json.Str(ctr != null ? ctr.name : ms != null ? ms.transform.root.name : "") + "," +
                         (e != null ? e.NetId : 0) + "," + (e != null ? e.Items.IndexOf(it) : -1) + "," + (e != null && e.Proxy ? "true" : "false") + "," +
                         (rb == null ? "\"none\"" : rb.isKinematic ? "\"kin\"" : rb.IsSleeping() ? "\"sleep\"" : "\"awake\"") + "]");
            }
            return "{\"t\":" + Time.time.ToString("F1", ic) + ",\"items\":[" + string.Join(",", rows) + "]}";
        }

        /// leasepair drift4: in a session ~42 bodies at the laptop's station stay awake without moving (single player on
        /// the same machine: 8). sleepall puts every free body near a point to sleep; awake then shows who woke again,
        /// how fast it moves and what it touches (owned / copy, kinematic / awake / asleep).
        private static IEnumerable<Rigidbody> BodiesNear(double x, double z, double r)
        {
            foreach (var rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (rb == null || rb.isKinematic) continue;
                var g = mainscript.GlobalFromUnityPos(rb.position);
                if ((g.x - x) * (g.x - x) + (g.z - z) * (g.z - z) <= r * r) yield return rb;
            }
        }

        public static string SleepAll(double x, double z, double r)
        {
            int n = 0;
            foreach (var rb in BodiesNear(x, z, r)) { rb.Sleep(); n++; }
            return "{\"slept\":" + n + "}";
        }

        public static string Awake(double x, double z, double r)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var entOf = new Dictionary<Transform, Ent>();
            foreach (var e in ByNet.Values) if (e.Root != null) entOf[e.Root.transform.root] = e;
            string Tag(Collider c)
            {
                var root = c.transform.root; var rb = c.attachedRigidbody;
                entOf.TryGetValue(root, out var e);
                return root.name + (e == null ? " (no ent)" : e.Proxy ? " COPY" : " own") +
                       (rb == null ? " static" : rb.isKinematic ? " KIN" : rb.IsSleeping() ? " asleep" : " awake");
            }
            var rows = new List<string>(); int asleep = 0;
            foreach (var rb in BodiesNear(x, z, r))
            {
                if (rb.IsSleeping()) { asleep++; continue; }
                var touch = new HashSet<string>();
                foreach (var c in rb.GetComponentsInChildren<Collider>())
                {
                    if (c == null || !c.enabled || c.isTrigger || c.attachedRigidbody != rb) continue;
                    var b = c.bounds;
                    foreach (var o in Physics.OverlapBox(b.center, b.extents + Vector3.one * 0.02f, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (o == null || o.transform.root == rb.transform.root) continue;
                        if (Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, o, o.transform.position, o.transform.rotation, out _, out float d) || d >= 0f)
                            touch.Add(Tag(o));
                        if (touch.Count >= 6) break;
                    }
                }
                entOf.TryGetValue(rb.transform.root, out var me);
                rows.Add("[" + Json.Str(rb.name) + "," + (me != null ? me.NetId : 0) + "," + (me != null && me.Proxy ? "true" : "false") + "," +
                         rb.velocity.magnitude.ToString("F4", ic) + "," + rb.angularVelocity.magnitude.ToString("F4", ic) + "," +
                         rb.sleepThreshold.ToString("F4", ic) + ",[" + string.Join(",", touch.Select(Json.Str)) + "]]");
            }
            return "{\"t\":" + Time.time.ToString("F1", ic) + ",\"asleep\":" + asleep + ",\"awake\":[" + string.Join(",", rows) + "]}";
        }

        /// leasepair 3: one loose item 1.5-3 m apart per run that the root compare (3c) never sees).
        public static string ItemEnt(double x, double y, double z)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            tosaveitemscript best = null; double bd = double.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                var g = mainscript.GlobalFromUnityPos(it.transform.position);
                double d = (g.x - x) * (g.x - x) + (g.y - y) * (g.y - y) + (g.z - z) * (g.z - z);
                if (d < bd) { bd = d; best = it; }
            }
            if (best == null) return "{\"error\":\"none\"}";
            Ent owner = null; int idx = -1;
            foreach (var e in ByNet.Values) { int k = e.Items.IndexOf(best); if (k >= 0) { owner = e; idx = k; break; } }
            var rb = best.GetComponent<Rigidbody>();
            var p = best.transform.parent;
            return "{\"name\":" + Json.Str(best.name) + ",\"dist\":" + System.Math.Sqrt(bd).ToString("F2", ic) +
                   ",\"parent\":" + Json.Str(p != null ? p.name : "") + ",\"root\":" + Json.Str(best.transform.root.name) +
                   ",\"kinematic\":" + (rb != null ? (rb.isKinematic ? "true" : "false") : "null") + ",\"sleeping\":" + (rb != null ? (rb.IsSleeping() ? "true" : "false") : "null") +
                   ",\"net\":" + (owner != null ? owner.NetId : 0) + ",\"index\":" + idx + ",\"members\":" + (owner != null ? owner.Items.Count : 0) +
                   ",\"entRoot\":" + Json.Str(owner != null && owner.Root != null ? owner.Root.name : "") + ",\"proxy\":" + (owner != null && owner.Proxy ? "true" : "false") +
                   ",\"physical\":" + (owner != null && owner.Physical ? "true" : "false") +
                   ",\"lock\":" + Json.Str(best.P != null && best.P.physlock != null ? best.P.physlock.sType + " in " + best.P.physlock.transform.root.name : "") +
                   ",\"pos\":" + V3(mainscript.GlobalFromUnityPos(best.transform.position)) +
                   (owner != null ? ",\"entPos\":" + V3(owner.Root != null ? mainscript.GlobalFromUnityPos(owner.Root.transform.position) : default) + ",\"recv\":" + owner.Recv + ",\"seqOut\":" + owner.SeqOut : "") + "}";
        }

        public static string SiteState(double x, double z, double r)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>();
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || !it.gameObject.activeInHierarchy) continue;
                var g = mainscript.GlobalFromUnityPos(it.transform.position);
                if ((g.x - x) * (g.x - x) + (g.z - z) * (g.z - z) > r * r) continue;
                float fuel = 0f; bool hasTank = false;
                if (it.tanks != null) foreach (var t in it.tanks) if (t != null && t.F != null) { hasTank = true; foreach (var f in t.F.fluids) fuel += f.amount; }
                var pcs = new List<string>();
                if (it.partconditions != null) foreach (var pc in it.partconditions) if (pc != null) pcs.Add(pc.state.ToString(ic));
                var us = new List<string>();
                if (it.usables != null) foreach (var u in it.usables) if (u != null) us.Add(u.slideValue.ToString("F2", ic));
                string st = (hasTank ? "fuel " + fuel.ToString("F2", ic) + " " : "") + (pcs.Count > 0 ? "parts " + string.Join(".", pcs) + " " : "") +
                            (us.Count > 0 ? "use " + string.Join("/", us) + " " : "") + (it.attachable != null && it.attachable.attached ? "attached" : "");
                rows.Add("[" + it.id + "," + g.x.ToString("F3", ic) + "," + g.y.ToString("F3", ic) + "," + g.z.ToString("F3", ic) + "," + Json.Str(st.Trim()) + "," + Json.Str(it.name) + "]");
            }
            return "{\"count\":" + rows.Count + ",\"items\":[" + string.Join(",", rows) + "]}";
        }

        /// The far store (items the game streamed out: records in the save, no GameObject) within `r` m of a GLOBAL
        /// point: how many, by model, and the part conditions they keep — what this machine's save would bring back.
        public static string FarStore(double x, double z, double r)
        {
            var data = savedatascript.s != null ? savedatascript.s.data : null;
            if (data == null || data.itemData == null) return "{\"error\":\"no save data\"}";
            var ids = new HashSet<uint>(); var models = new Dictionary<int, int>(); double ySum = 0;
            foreach (var it in data.itemData.items)
            {
                if (it == null) continue;
                var p = it.transform.pos;
                if ((p.x - x) * (p.x - x) + (p.z - z) * (p.z - z) > r * r) continue;
                ids.Add(it.id); ySum += p.y;
                models.TryGetValue(it.prefabID, out int n); models[it.prefabID] = n + 1;
            }
            int pcs = 0, pcNonZero = 0;
            foreach (var pc in data.itemData.partconditions) if (pc != null && ids.Contains(pc.id)) { pcs++; if (pc.state != 0) pcNonZero++; }
            float fuel = 0f;
            foreach (var t in data.itemData.tanks) if (t != null && ids.Contains(t.id) && t.amount != null) foreach (var a in t.amount) fuel += a;
            var ms = new List<string>(); foreach (var kv in models) ms.Add(kv.Key + ":" + kv.Value);
            return "{\"records\":" + ids.Count + ",\"partConditions\":" + pcs + ",\"partConditionsNonZero\":" + pcNonZero +
                   ",\"fuel\":" + fuel.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ",\"yAvg\":" + (ids.Count > 0 ? ySum / ids.Count : 0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",\"models\":" + Json.Str(string.Join(" ", ms)) + "}";
        }

        /// Non-pose state of shared object `net`, flattened over its group in order (tests compare it between machines).
        // ---------------------------------------------------------------- fluids (tools/fluids.py)
        /// The entity's tanks in EntState order (its items in order, each item's tanks): amount per fluid, caps open.
        private static List<tankscript> TanksOf(Ent e)
        {
            var l = new List<tankscript>();
            foreach (var it in e.Items) if (it != null && it.tanks != null) foreach (var t in it.tanks) l.Add(t);
            return l;
        }

        /// For logs: which shared object a root is — net, owner, copy or own — or "local" if it isn't shared.
        internal static string DescribeRoot(Transform root)
        {
            foreach (var e in ByNet.Values)
                if (e.Root != null && e.Root.transform.root == root) return "net " + e.NetId + " owner " + e.OwnerId + (e.Proxy ? " copy" : " own");
            return "local";
        }

        public static Transform RootOf(uint net) => ByNet.TryGetValue(net, out var e) && Resolve(e) && e.Root != null ? e.Root.transform.root : null;

        public static pickupable PickupableOf(uint net) => ByNet.TryGetValue(net, out var e) && Resolve(e) && e.Root != null ? e.Root.P : null;

        public static tankscript TankOf(uint net, int ti)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return null;
            var l = TanksOf(e);
            return ti >= 0 && ti < l.Count ? l[ti] : null;
        }

        public static string Fluids(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var rows = new List<string>(); int ti = 0;
            foreach (var t in TanksOf(e))
            {
                int i = ti++;
                if (t == null || t.F == null) { rows.Add("{\"ti\":" + i + "}"); continue; }
                var by = new List<string>();
                foreach (var f in t.F.fluids) by.Add(f.type + ":" + f.amount.ToString("F3", ci));
                var caps = new List<string>();
                if (t.TC != null) foreach (var c in t.TC) if (c != null) caps.Add((c.tube ? "tube" : c.valve > 0f ? "open" : "shut") + (c.em.enabled ? "+pouring" : ""));
                rows.Add("{\"ti\":" + i + ",\"tank\":" + Json.Str(t.name) + ",\"amount\":" + t.F.GetAmount().ToString("F3", ci) + ",\"max\":" + t.F.maxC.ToString("F1", ci) +
                         ",\"by\":" + Json.Str(string.Join(" ", by)) + ",\"caps\":" + Json.Str(string.Join(" ", caps)) + "}");
            }
            return "{\"net\":" + net + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"tanks\":[" + string.Join(",", rows) + "]}";
        }

        /// Test setup on one's OWN container: tank ti holds `amount` of one fluid (like having filled it at a pump).
        public static string Fill(uint net, int ti, string fluid, float amount)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            if (e.Proxy) return "{\"error\":\"not mine\"}";
            var tanks = TanksOf(e);
            if (ti < 0 || ti >= tanks.Count || tanks[ti] == null) return "{\"error\":\"no tank " + ti + "\"}";
            var t = tanks[ti];
            t.F.fluids.Clear();
            t.F.fluids.Add(new mainscript.fluid { type = (mainscript.fluidenum)Enum.Parse(typeof(mainscript.fluidenum), fluid), amount = amount });
            t.Upd();
            return "{\"tank\":" + Json.Str(t.name) + ",\"amount\":" + t.F.GetAmount() + "}";
        }

        private static tankcapscript _pourFrom, _pourInto;
        private static Rigidbody _pourBody;
        /// A player pouring: the container's (net `can`) cap opened through its usable (the game's sync point), the
        /// container held still with that cap pointing straight down 0.35 m above the cap of tank ti of `car` — opened the
        /// same way. The fluid travels as the game's own particles. `stop` closes both caps and lets go.
        /// The pour as this machine has it: every cap of the container (usable state, valve, angle, emitting), the
        /// container's place and body, the target cap's valve.
        public static string PourStatus()
        {
            if (_pourFrom == null) return "{\"error\":\"not pouring\"}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var root = _pourFrom.transform.root;
            var caps = new List<string>();
            foreach (var c in root.GetComponentsInChildren<tankcapscript>(true))
                caps.Add("{\"name\":" + Json.Str(c.name) + ",\"mine\":" + (c == _pourFrom ? "true" : "false") + ",\"state\":" + (c.usable != null ? c.usable.currentTurnState : -1) +
                         ",\"valve\":" + c.valve.ToString("F2", ci) + ",\"angle\":" + c.angle.ToString("F0", ci) + ",\"em\":" + (c.em.enabled ? "true" : "false") + ",\"tube\":" + (c.tube ? "true" : "false") + "}");
            var g = mainscript.GlobalFromUnityPos(root.position);
            var rb = root.GetComponentInChildren<Rigidbody>();
            return "{\"root\":" + Json.Str(root.name) + ",\"pos\":[" + g.x.ToString("F2", ci) + "," + g.y.ToString("F2", ci) + "," + g.z.ToString("F2", ci) + "]" +
                   ",\"aboveCap\":" + (_pourInto != null ? (_pourFrom.transform.position - _pourInto.transform.position).ToString("F2") : "null") +
                   ",\"body\":" + Json.Str(rb == null ? "none" : rb.name + (rb.isKinematic ? " kinematic" : " dynamic") + (rb.transform == root ? " at root" : " below root")) +
                   ",\"intoValve\":" + (_pourInto != null ? _pourInto.valve.ToString("F2", ci) : "null") + ",\"intoState\":" + (_pourInto != null && _pourInto.usable != null ? _pourInto.usable.currentTurnState : -1) +
                   ",\"caps\":[" + string.Join(",", caps) + "]}";
        }

        public static string Pour(uint can, uint car, int ti, bool start)
        {
            if (!start)
            {
                if (_pourFrom == null) return "{\"error\":\"not pouring\"}";
                if (_pourFrom.usable != null) _pourFrom.usable.UpdTurnState(0, syncinmulti: true);
                if (_pourInto != null && _pourInto.usable != null && _pourInto.usable.turnable) _pourInto.usable.UpdTurnState(0, syncinmulti: true);
                if (_pourBody != null) _pourBody.isKinematic = false;
                var st = PourStatus();
                _pourFrom = _pourInto = null; _pourBody = null;
                return "{\"stopped\":" + st + "}";
            }
            if (!ByNet.TryGetValue(can, out var ec) || !Resolve(ec)) return "{\"error\":\"no container here\"}";
            if (!ByNet.TryGetValue(car, out var et) || !Resolve(et)) return "{\"error\":\"no target here\"}";
            var tanks = TanksOf(et);
            if (ti < 0 || ti >= tanks.Count || tanks[ti] == null || tanks[ti].TC == null) return "{\"error\":\"no tank " + ti + "\"}";
            _pourInto = null;
            foreach (var c in tanks[ti].TC) if (c != null && !c.tube) { _pourInto = c; break; }
            _pourFrom = null;
            foreach (var c in ec.Root.GetComponentsInChildren<tankcapscript>(true))
                if (c != null && !c.tube && !c.noPour && c.usable != null && c.usable.turnable) { _pourFrom = c; break; }
            if (_pourInto == null || _pourFrom == null) return "{\"error\":\"caps: into " + (_pourInto != null) + " from " + (_pourFrom != null) + "\"}";
            if (_pourInto.usable != null && _pourInto.usable.turnable && _pourInto.usable.currentTurnState == 0) _pourInto.usable.UpdTurnState(1, syncinmulti: true);
            var root = ec.Root.transform;
            _pourBody = root.GetComponentInChildren<Rigidbody>();
            if (_pourBody != null) { _pourBody.isKinematic = true; _pourBody.velocity = Vector3.zero; }
            root.rotation = Quaternion.FromToRotation(_pourFrom.transform.forward, Vector3.down) * root.rotation;
            root.position += _pourInto.transform.position + Vector3.up * 0.35f - _pourFrom.transform.position;
            _pourFrom.usable.UpdTurnState(1, syncinmulti: true);
            return "{\"from\":" + Json.Str(_pourFrom.Tank != null ? _pourFrom.Tank.name : "?") + ",\"fromAmount\":" + (_pourFrom.Tank != null ? _pourFrom.Tank.F.GetAmount() : -1f) +
                   ",\"into\":" + Json.Str(tanks[ti].name) + ",\"intoAmount\":" + tanks[ti].F.GetAmount() + ",\"intoValve\":" + _pourInto.valve + "}";
        }

        public static string EntState(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            var tanks = new List<string>(); var parts = new List<string>(); var us = new List<string>(); var doors = new List<string>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var it in e.Items)
            {
                if (it == null) continue;
                if (it.tanks != null) foreach (var t in it.tanks) { float a = 0; if (t != null && t.F != null) foreach (var f in t.F.fluids) a += f.amount; tanks.Add(a.ToString("F2", ci)); }
                if (it.partconditions != null) foreach (var pc in it.partconditions) parts.Add(pc == null ? "-1" : (pc.state + "." + pc.state2 + "." + pc.state3));
                if (it.usables != null) foreach (var u in it.usables) us.Add(u == null ? "0" : u.slideValue.ToString("F2", ci) + "/" + u.xRot.ToString("F0", ci) + "/" + u.yRot.ToString("F0", ci) + "/" + u.currentTurnState);
                if (it.door_rots != null) foreach (var d in it.door_rots) doors.Add(d == null ? "0" : d.state.ToString("F2", ci));
            }
            return "{\"net\":" + net + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"tanks\":" + Json.Str(string.Join(" ", tanks)) + ",\"parts\":" + Json.Str(string.Join(" ", parts)) +
                   ",\"usables\":" + Json.Str(string.Join(" ", us)) + ",\"doors\":" + Json.Str(string.Join(" ", doors)) + "}";
        }

        private static readonly Dictionary<int, string> _diffLast = new Dictionary<int, string>();
        /// Which parts of the items' records change between two calls (as the resync sees them: canonical ids, no transform).
        public static string ResyncDiff(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            var map = new Dictionary<uint, uint>();
            for (int i = 0; i < e.Items.Count; i++) if (e.Items[i] != null) map[e.Items[i].idInSave] = CanonBase + (uint)i;
            var outs = new List<string>(); int changed = 0;
            for (int i = 0; i < e.Items.Count; i++)
            {
                var it = e.Items[i]; if (it == null) continue;
                var d = StateOnly(ItemSnapshot.Capture(it)); ItemSnapshot.Remap(d, map);
                string j = JsonUtility.ToJson(d);
                if (_diffLast.TryGetValue(i, out var was) && was != j)
                {
                    changed++;
                    int k = 0; while (k < j.Length && k < was.Length && j[k] == was[k]) k++;
                    if (outs.Count < 8) outs.Add("{\"i\":" + i + ",\"name\":" + Json.Str(it.name) + ",\"was\":" + Json.Str(was.Substring(Math.Max(0, k - 120), Math.Min(200, was.Length - Math.Max(0, k - 120)))) + ",\"now\":" + Json.Str(j.Substring(Math.Max(0, k - 120), Math.Min(200, j.Length - Math.Max(0, k - 120)))) + "}");
                }
                _diffLast[i] = j;
            }
            return "{\"changed\":" + changed + ",\"diffs\":[" + string.Join(",", outs) + "]}";
        }

        /// Items of shared object `net` here: loaded or stored, how attached, how far from the root.
        /// Why would SeatTransform(net, idx) fail right now (the seat-flicker probe).
        public static string SeatWhy(uint net, int idx)
        {
            if (!ByNet.TryGetValue(net, out var e)) return "{\"why\":\"net absent from ByNet\"}";
            if (!Resolve(e)) return "{\"why\":\"Resolve failed (dormant)\"}";
            if (e.Root == null) return "{\"why\":\"root null\"}";
            var seats = e.Root.transform.root.GetComponentsInChildren<seatscript>(true);
            return "{\"why\":\"idx " + idx + "/" + seats.Length + "\"}";
        }

        public static string EntItems(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e)) return "{\"error\":\"no such object\"}";
            Resolve(e);
            var rows = new List<string>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < (e.ItemIds != null ? e.ItemIds.Count : e.Items.Count); i++)
            {
                var it = i < e.Items.Count ? e.Items[i] : null;
                uint id = e.ItemIds != null ? e.ItemIds[i] : (it != null ? it.idInSave : 0);
                if (it == null) { rows.Add("{\"i\":" + i + ",\"id\":" + id + ",\"loaded\":false,\"stored\":" + (IsStored(id) ? "true" : "false") + "}"); continue; }
                var a = it.attachable;
                float d = e.Root != null ? (it.transform.position - e.Root.transform.position).magnitude : -1f;
                rows.Add("{\"i\":" + i + ",\"id\":" + id + ",\"name\":" + Json.Str(it.name) + ",\"loaded\":true,\"attached\":" + (a != null && a.attached ? "true" : "false") +
                         ",\"parented\":" + (a != null && a.parentAtAttach ? "true" : "false") + ",\"hasParent\":" + (it.transform.parent != null ? "true" : "false") +
                         ",\"fromRoot\":" + d.ToString("F2", ci) + ",\"kin\":" + (it.GetComponent<Rigidbody>() != null && it.GetComponent<Rigidbody>().isKinematic ? "true" : "false") + "}");
            }
            return "{\"net\":" + net + ",\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"items\":[" + string.Join(",", rows) + "]}";
        }

        /// Change owner-side state for tests: `fuel <liters>` (first tank that holds anything), `part <i> <state>`
        /// (i-th part condition over the group), `usable <i> <slide>`.
        public static string EntPoke(uint net, string what, int i, float v)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            bool asPlayer = what.StartsWith("p:");   // end the change the way a player's interaction does (the game's sync points)
            if (asPlayer) what = what.Substring(2);
            int k = 0;
            foreach (var it in e.Items)
            {
                if (it == null) continue;
                if (what == "fuel" && it.tanks != null)
                    foreach (var t in it.tanks)
                        if (t != null && t.F != null && t.F.fluids.Count > 0) { var f = t.F.fluids[0]; float was = f.amount; f.amount = v; t.Upd(); if (asPlayer) t.SyncMulti(); return "{\"item\":" + Json.Str(it.name) + ",\"was\":" + was + "}"; }
                if (what == "part" && it.partconditions != null)
                    foreach (var pc in it.partconditions)
                        if (pc != null && k++ == i) { int was = pc.state; pc.state = (int)v; pc.Refresh(asPlayer); return "{\"item\":" + Json.Str(it.name) + ",\"was\":" + was + "}"; }
                if (what == "usable" && it.usables != null)
                    foreach (var u in it.usables)
                        if (u != null && k++ == i) { float was = u.slideValue; u.Refresh(v, u.xRot, u.yRot, u.currentTurnState); if (asPlayer) u.SyncMulti(true); return "{\"item\":" + Json.Str(it.name) + ",\"was\":" + was + "}"; }
            }
            return "{\"error\":\"nothing to change\"}";
        }

        /// Pick up the nearest loose item within `r` m the way a player does (fpscontroller.Pickup) — tests of ownership.
        public static string PickupNear(float r)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            pickupable best = null; float bd = r * r;
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || it.P == null || it.car != null || !it.gameObject.activeInHierarchy || it.transform.parent != null) continue;
                if (it.attachable != null && it.attachable.attached) continue;
                float d = (it.transform.position - pl.transform.position).sqrMagnitude;
                if (d < bd) { bd = d; best = it.P; }
            }
            if (best == null) return "{\"error\":\"nothing within " + r + " m\"}";
            uint net = 0;
            foreach (var e in ByNet.Values) if (e.Root != null && e.Root.gameObject == best.gameObject) net = e.NetId;
            pl.Pickup(best, best.transform.position);
            return "{\"picked\":" + Json.Str(best.name) + ",\"net\":" + net + "}";
        }

        /// Owner side: put shared object `net` `dx`,`dz` metres away, 1 m up, and let it fall (tests of replication).
        public static string MoveItem(uint net, float dx, float dz)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null) return "{\"error\":\"no such object here\"}";
            if (e.Proxy) return "{\"error\":\"not the owner\"}";
            var t = e.Root.transform;
            t.position += new Vector3(dx, 1f, dz);
            var rb = e.Root.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.WakeUp(); }
            return "{\"moved\":" + net + "}";
        }

        /// Test: throw what the player holds at a car's passenger seat, through the game's own throw (fpscontroller.Drop
        /// with throwForce: pickedUp.Drop(Th.forward * throwForce * throwForceM)). `force` 0..1 like a held mouse button.
        public static string ThrowAt(uint net, float force)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.pickedUp == null) return "{\"error\":\"holding nothing\"}";
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e)) return "{\"error\":\"no such object here\"}";
            Transform target = e.Root.transform;
            foreach (var st in e.Root.transform.root.GetComponentsInChildren<seatscript>(true)) if (!st.driverSeat0 && st.mainseat == null) { target = st.transform; break; }
            var from = pl.Th.position;
            var dir = (target.position + Vector3.up * 0.4f - from).normalized;
            pl.Th.rotation = Quaternion.LookRotation(dir + Vector3.up * 0.15f);
            pl.throwForce = Mathf.Clamp(force, pl.minThrowForce + 0.01f, 1f);
            var held = pl.pickedUp.name;
            pl.Drop();
            return "{\"thrown\":" + Json.Str(held) + ",\"dist\":" + (target.position - from).magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        /// Test: throw what the player holds at a global point (chest height), through the game's own throw.
        public static string ThrowAtPoint(double gx, double gz, float force)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null || pl.pickedUp == null) return "{\"error\":\"holding nothing\"}";
            var t = mainscript.UnityPosFromGlobal(new Vector3d(gx, 0, gz)); t.y = pl.transform.position.y + 1.0f;
            var from = pl.Th.position;
            pl.Th.rotation = Quaternion.LookRotation((t - from).normalized + Vector3.up * 0.12f);
            pl.throwForce = Mathf.Clamp(force, pl.minThrowForce + 0.01f, 1f);
            var held = pl.pickedUp.name;
            pl.Drop();
            return "{\"thrown\":" + Json.Str(held) + ",\"dist\":" + (t - from).magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        public static string DropHeld()
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"no player\"}";
            if (pl.pickedUp != null) { pl.Drop(); return "{\"dropped\":true}"; }
            // TLD moves picked items to the inventory: drop the last picked one at the player's feet
            var lp = Net.Entities.LastPicked;
            if (lp == null || !lp.gameObject.activeInHierarchy) return "{\"error\":\"holding nothing\"}";
            lp.Drop(pl.transform.position + pl.transform.forward * 0.6f + Vector3.up * 0.6f);
            return "{\"dropped\":true,\"from\":\"inventory\"}";
        }

        /// Active cars within `r` metres of a global point (scenario checks for doubled cars).
        public static string CarsNear(double x, double z, double r)
        {
            int n = 0;
            foreach (var it in savedatascript.s.items.Values)
            {
                if (it == null || it.car == null || it.transform.parent != null || !it.gameObject.activeInHierarchy) continue;
                var g = mainscript.GlobalFromUnityPos(it.transform.position);
                if ((g.x - x) * (g.x - x) + (g.z - z) * (g.z - z) < r * r) n++;
            }
            return "{\"active\":" + n + "}";
        }

        /// Nearest car to the local player (for scenarios).
        public static tosaveitemscript NearestCar()
        {
            if (savedatascript.s == null || mainscript.s == null || mainscript.s.player == null) return null;
            var p = mainscript.s.player.transform.position;
            tosaveitemscript best = null; float bd = float.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.car != null && it.transform.parent == null)
                {
                    float d = (it.transform.position - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = it; }
                }
            return best;
        }

        /// `wheelinfo [r]`: every car within r m — per wheel the suspension as the network sends it: travel (collider origin
        /// → wheel pose along its up; the state packs it into ±25 cm), the collider's center offset and suspension distance.
        public static string WheelInfo(float r)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var p = mainscript.s.player.transform.position;
            var sb = new System.Text.StringBuilder("{\"cars\":[");
            bool first = true;
            foreach (var car in UnityEngine.Object.FindObjectsOfType<carscript>())
            {
                if ((car.transform.position - p).magnitude > r) continue;
                if (!first) sb.Append(','); first = false;
                Ent ce = null; foreach (var x in ByNet.Values) if (x.Root != null && x.Root.car == car && x.PartIndex < 0) { ce = x; break; }
                var gp = mainscript.GlobalFromUnityPos(car.transform.position);
                sb.Append("{\"name\":").Append(Json.Str(car.name)).Append(",\"net\":").Append(ce != null ? ce.NetId : 0)
                  .Append(",\"proxy\":").Append(ce != null && ce.Proxy ? "true" : "false")
                  .Append(",\"pos\":[").Append(gp.x.ToString("F1", ci)).Append(',').Append(gp.z.ToString("F1", ci)).Append(']')
                  .Append(",\"got\":[");   // a copy: the suspension fractions it last received
                if (ce != null && ce.WTravel != null) for (int k = 0; k < ce.WTravel.Length; k++) sb.Append(k > 0 ? "," : "").Append(ce.WTravel[k].ToString("F3", ci));
                sb.Append("],\"wheels\":[");
                bool f2 = true;
                foreach (var wg in car.GetComponentsInChildren<wheelgraphicsscript>(true))
                {
                    var W = wg.W; if (W == null) continue;
                    W.GetWorldPose(out var pos, out _);
                    if (wg.W2 != null) { wg.W2.GetWorldPose(out var p2, out _); pos = (pos + p2) * 0.5f; }
                    float travel = Vector3.Dot(W.transform.position - pos, W.transform.up);
                    float frac = Vector3.Dot(W.transform.TransformPoint(W.center) - pos, W.transform.up) / Mathf.Max(W.suspensionDistance, 0.01f);
                    float shown = wg.T != null ? Vector3.Dot(W.transform.TransformPoint(W.center) - wg.T.position, W.transform.up) / Mathf.Max(W.suspensionDistance, 0.01f) : -9f;
                    if (!f2) sb.Append(','); f2 = false;
                    sb.Append('[').Append(Json.Str(W.name)).Append(',').Append(travel.ToString("F3", ci)).Append(',')
                      .Append(W.center.y.ToString("F3", ci)).Append(',').Append(W.suspensionDistance.ToString("F3", ci)).Append(',')
                      .Append(W.isGrounded ? "true" : "false").Append(',').Append(wg.W2 != null ? "true" : "false").Append(',')
                      .Append(frac.ToString("F3", ci)).Append(',').Append(shown.ToString("F3", ci)).Append(']');   // physics' fraction, the mesh's
                }
                sb.Append("]}");
            }
            return sb.Append("]}").ToString();
        }
    }
}
