using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        /// A one-shot message from an object's owner, stamped with the epoch it saw: the current one, or — from the player
        /// whose claim was the last ownership change — the one before it. A claimer owns the object here at once
        /// (provisional) and keeps sending with the old epoch until the server's Owner reply: the grab that claims an item
        /// also takes it off what it was stuck to, and that DetachSync, dropped for its epoch, left the item stuck on every
        /// other screen and in the server's record for later joiners (d71ad45, the 2026-10-22 bug hunt: gluepick.py).
        private static bool ClaimEpochOk(SEnt se, int from, uint epoch) =>
            se.Epoch == epoch || se.ClaimedBy == from && se.OwnerId == from && se.ClaimedFromEpoch == epoch && se.Epoch == epoch + 1
                                 && Time.realtimeSinceStartup - se.ClaimedAt < ClaimEpochGraceS;
        /// How long after its claim a claimer's old-epoch messages still count: its Owner reply's round trip, with room.
        public static float ClaimEpochGraceS = 3f;
        public static bool PartOffMotionSlack = true;   // A/B: false = fixed 50 m from the last accepted car pose

        private static string CheckPartOff(SEnt car, int idx, Vector3d pos, Quaternion rot)
        {
            if (!Finite(pos) || !Finite(rot)) return "non-finite pose";
            double reach = 50;
            if (PartOffMotionSlack && car.HasState && car.ReceivedStateEpoch == car.Epoch)
            {
                // Beyond InterestM the owner sends poses every 5 s, but a crash reports PartOff immediately.
                // DrivenAt is the server receipt time of that pose. Allow travel at the game's speed cap,
                // including acceleration since the sample, for at most 6 s (refresh + delivery), never over 500 m.
                double age = Time.realtimeSinceStartup - car.DrivenAt;
                double cap = mainscript.s != null ? mainscript.s.maxCarVelocity : 70;
                if (!Finite(cap) || cap <= 1) cap = 70;
                if (Finite(age)) reach = System.Math.Min(500, reach + cap * System.Math.Min(6, System.Math.Max(0, age)));
            }
            double dx = pos.x - car.Where.x, dy = pos.y - car.Where.y, dz = pos.z - car.Where.z;
            if (!Finite(car.Where) || dx * dx + dy * dy + dz * dz > reach * reach) return "pose too far";
            double q = (double)rot.x * rot.x + (double)rot.y * rot.y + (double)rot.z * rot.z + (double)rot.w * rot.w;
            if (q < 0.25 || q > 4) return "invalid rotation";
            var group = GroupRecord(car);
            if (idx <= 0 || group == null || group.items == null || idx >= group.items.Count || group.items[idx] == null) return "item index";
            if (car.SplitOff.Contains(idx) || (car.Gone != null && car.Gone.Contains(idx))) return "already detached or gone";
            foreach (uint net in car.Parts)
                if (Server.TryGetValue(net, out var part) && part.PartIndex == idx) return "already detached";
            var state = group;
            bool canonical = false;
            if (car.ItemState.TryGetValue(idx, out var bytes))
            {
                state = RecordCodec.Decode(bytes);
                if (state == null) return "item record";
                canonical = true;   // resync/handover records contain state only, addressed by group index
            }
            if (state.attachable == null) return "not attached";
            foreach (var at in state.attachable)
            {
                if (at == null || at.id != (canonical ? CanonBase + (uint)idx : group.items[idx].id)) continue;
                if (at.attachType != 1 || at.indexType != 2 || at.index < 0) return "not attached to a slot";
                for (int p = 0; p < group.items.Count; p++)
                {
                    var parent = group.items[p];
                    if (parent == null || (canonical ? CanonBase + (uint)p : parent.id) != at.parentid || p == idx || (car.Gone != null && car.Gone.Contains(p))) continue;
                    // A door split into its own entity can be bolted back onto this car. Its mirror still belongs
                    // to the original group; consumed membership alone does not mean its slot parent is absent.
                    if (car.SplitOff.Contains(p))
                    {
                        bool reattached = false;
                        foreach (uint pn in car.Parts)
                            if (Server.TryGetValue(pn, out var split) && split.PartIndex == p && split.AttachParent == car.NetId)
                            { reattached = true; break; }
                        if (!reattached) continue;
                    }
                    GameObject prefab;
                    try { prefab = itemdatabase.s != null ? itemdatabase.s.Item(parent.prefabID) : null; }
                    catch { return "prefab slot"; }
                    var root = prefab != null ? prefab.GetComponent<tosaveitemscript>() : null;
                    if (prefab != null && (root == null || root.partslotscripts == null || at.index >= root.partslotscripts.Count || root.partslotscripts[at.index] == null)) return "prefab slot";
                    return null;
                }
                return "slot parent";
            }
            return "not attached";
        }

        private static void ServerPartOff(int from, NetReader r)
        {
            uint carNet = r.U32(), epoch = r.U32(); int idx = (int)r.VarU32();
            var pos = new Vector3d(r.F64(), r.F64(), r.F64()); var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            Server.TryGetValue(carNet, out var car);
            string why = r.Bad || r.Remaining != 0 ? "malformed" : car == null ? "missing car" : car.OwnerId != from ? "wrong owner" : !ClaimEpochOk(car, from, epoch) ? "epoch" : CheckPartOff(car, idx, pos, rot);
            if (why != null)
            {
                PartOffRejected++;
                SessionLog.Event("part-off-refused", carNet, item: idx, from: from, epoch: epoch, result: why);
                PartRejects += "srv(car" + carNet + " from" + from + " " + why + ") ";
                // The game already detached locally; do not leave that disagreement silent on the owner.
                Plugin.Log.LogWarning($"player {from}: part-off refused: car {carNet}, item {idx}, epoch {epoch}: {why}; their local detach was not shared");
                Mp.Server?.Tell(from, "A part that came off your car could not be shared with the other players, so for them it is still on.");
                return;
            }
            // Membership is consumed before broadcasting and survives both ownership changes and part removal.
            car.SplitOff.Add(idx);
            var part = new SEnt { NetId = _nextNet++, OwnerId = from, Epoch = 1, ParentNet = carNet, PartIndex = idx, HasState = true, Pos = pos, Rot = rot, Where = pos };
            Server[part.NetId] = part;
            foreach (var kv in Known) if (kv.Value.Contains(carNet)) kv.Value.Add(part.NetId);
            car.Parts.Add(part.NetId);
            car.ItemState.Remove(idx);
            WritePartDetached(W, part);
            ServerSendAll(W, true, -1);
        }
    }
}
