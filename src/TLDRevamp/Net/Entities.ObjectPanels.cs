using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        internal static uint PanelFocus(bool held)
        {
            if (!InSession) return 0;
            // Telemetry already maintains these addresses at 10 Hz; opening a panel adds no focus scan.
            return ObjectPanelState.Select(TelemetryFocus[2], TelemetryFocus[0], held ? TelemetryFocus[3] != 0 ? TelemetryFocus[3] : TelemetryFocus[4] : 0);
        }
        internal static ObjectPanelState PanelCapture(uint net)
        {
            var s = new ObjectPanelState { Net = net };
            if (!ByNet.TryGetValue(net, out var e)) return s;
            s.Flags = ObjectPanelState.Bits.Found;
            s.Epoch = e.Epoch; s.Owner = e.OwnerId; s.Part = e.PartIndex; s.Parent = e.ParentNet;
            var root = e.Root;
            if (e.Proxy) s.Flags |= ObjectPanelState.Bits.Proxy;
            if (e.Proxy ? e.Held : root != null && IsHeld(root)) s.Flags |= ObjectPanelState.Bits.Held;
            if (e.Proxy ? e.Stored : root != null && IsStored(root)) s.Flags |= ObjectPanelState.Bits.Stored;
            var pos = root != null ? mainscript.GlobalFromUnityPos(root.transform.position) : e.FarPos;
            var rot = root != null ? root.transform.rotation : e.FarRot;
            var v = e.Proxy ? e.LastVel : root != null && root.RB != null ? root.RB.velocity : Vector3.zero;
            s.X = pos.x; s.Y = pos.y; s.Z = pos.z; s.Qx = rot.x; s.Qy = rot.y; s.Qz = rot.z; s.Qw = rot.w; s.Vx = v.x; s.Vy = v.y; s.Vz = v.z;
            // A dormant copy has no live controls to inspect; do not load or resolve it for diagnostics.
            if (root == null) { s.Parent = e.ParentNet; s.LockedTo = e.LockFrame; if (e.PoseLocked) s.Flags |= ObjectPanelState.Bits.Locked; return s; }
            s.Flags |= ObjectPanelState.Bits.Live;
            var at = root.attachable;
            if (at != null && at.attached)
            {
                s.Flags |= ObjectPanelState.Bits.Attached;
                var parent = SharedGroupOf(Fixes.SlotOwnerSave.Owner(at));
                s.Parent = parent != null ? parent.NetId : 0;
            }
            var mount = root.P != null ? root.P.physlock : null;
            if (mount != null)
            {
                s.Flags |= ObjectPanelState.Bits.Locked;
                var parent = SharedGroupOf(mount.parentTosave);
                s.LockedTo = parent != null ? parent.NetId : 0;
            }
            var car = root.car;
            if (car != null)
            {
                s.Flags |= ObjectPanelState.Bits.Car;
                if (e.Proxy ? e.EngRunning : car.Engine != null && car.Engine.running) s.Flags |= ObjectPanelState.Bits.Engine;
                if (car.ignition) s.Flags |= ObjectPanelState.Bits.Ignition;
                s.Rpm = e.Proxy ? e.EngRpm : car.Engine != null ? car.Engine.rpm : float.NaN;
                s.Key = car.ignitionUsable != null ? car.ignitionUsable.currentTurnState : float.NaN;
                s.Fuel = car.Tank != null && car.Tank.F != null ? car.Tank.F.GetAmount() : float.NaN;
            }
            Dictionary<tosaveitemscript, (Ent Entity, int Member)> contents = null;
            var radio = root.radio;
            int members = System.Math.Min(e.Items.Count, 128);
            if (e.Items.Count > members) s.Truncated++;
            for (int i = 0; i < members; i++)
            {
                var it = e.Items[i]; if (it == null || e.Split != null && e.Split.Contains(i)) continue;
                if (radio == null && it.radio != null) radio = it.radio;
                // Read the save's registered list directly, without building a hierarchy or altering the live cache.
                if (it.usables != null) for (int j = 0; j < it.usables.Count; j++)
                {
                    if (s.UsableCount == ObjectPanelState.UsableLimit) { s.Truncated++; break; }
                    var u = it.usables[j]; if (u == null) continue;
                    s.Usables[s.UsableCount++] = new ObjectPanelState.Usable { Item = i, Index = j, X = u.xRot, Y = u.yRot, Slide = u.slideValue, Turn = u.currentTurnState, Tuned = u.tunedFloat };
                }
                var bus = it.busdoorscript;
                if (bus != null && bus.doors != null) for (int j = 0; j < bus.doors.Length; j++)
                {
                    if (s.UsableCount == ObjectPanelState.UsableLimit) { s.Truncated++; break; }
                    var door = bus.doors[j]; if (door == null) continue;
                    float fraction = 0; int n = 0;
                    if (door.doors != null) foreach (var leaf in door.doors) if (leaf != null)
                    {
                        var span = leaf.opened - leaf.closed;
                        if (span.sqrMagnitude > .0001f) { fraction += Mathf.Clamp01(Vector3.Dot(leaf.current - leaf.closed, span) / span.sqrMagnitude); n++; }
                    }
                    s.Usables[s.UsableCount++] = new ObjectPanelState.Usable { Item = i, Index = j, Bus = true, Slide = n > 0 ? fraction / n : float.NaN, Turn = door.Bopened ? 1 : 0 };
                }
                if (it.partslotscripts == null) continue;
                for (int j = 0; j < it.partslotscripts.Count; j++)
                {
                    if (s.SlotCount == ObjectPanelState.SlotLimit) { s.Truncated++; break; }
                    var slot = it.partslotscripts[j]; if (slot == null) continue;
                    var part = slot.hasPart() ? slot.part() : null;
                    Ent content = null; int member = -1;
                    if (part != null && part.tosave != null)
                    {
                        member = e.Items.IndexOf(part.tosave);
                        if (member >= 0 && (e.Split == null || !e.Split.Contains(member))) content = e;
                        else
                        {
                            if (contents == null) contents = PanelContents();
                            if (contents.TryGetValue(part.tosave, out var found)) { content = found.Entity; member = found.Member; }
                            else member = -1;
                        }
                    }
                    bool driven = car != null && car.DrivenWheelSlots != null && car.DrivenWheelSlots.Contains(slot);
                    s.Slots[s.SlotCount++] = new ObjectPanelState.Slot { Item = i, Index = j, Content = content != null ? content.NetId : 0,
                        Member = member, Attached = part != null && part.attached,
                        DrivenWheel = driven, Wheel = driven || PanelWheelSlot(slot) };
                }
            }
            if (radio != null)
            {
                s.Radio = radio.FR;
                if (radio.ON) s.Flags |= ObjectPanelState.Bits.RadioOn;
                if (radio.amfm) s.Flags |= ObjectPanelState.Bits.RadioAm;
            }
            return s;
        }
        private static Dictionary<tosaveitemscript, (Ent Entity, int Member)> PanelContents()
        {
            var contents = new Dictionary<tosaveitemscript, (Ent, int)>();
            foreach (var e in ByNet.Values) if (e.Root != null) contents[e.Root] = (e, e.Items.IndexOf(e.Root));
            foreach (var e in ByNet.Values) for (int i = 0; i < e.Items.Count; i++)
            {
                var it = e.Items[i];
                if (it != null && (e.Split == null || !e.Split.Contains(i)) && !contents.ContainsKey(it)) contents.Add(it, (e, i));
            }
            return contents;
        }
        private static bool PanelWheelSlot(partslotscript slot)
        {
            if (slot.tipus == null) return false;
            foreach (string type in slot.tipus) if (type != null && (type.IndexOf("wheel", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                type.IndexOf("kerek", System.StringComparison.OrdinalIgnoreCase) >= 0 || type.IndexOf("kerék", System.StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return false;
        }
    }
}
