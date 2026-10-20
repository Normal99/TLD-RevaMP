using System;

namespace TLDRevamp
{
    internal static class ObjectPanelEvents
    {
        private static uint _tpaNet;
        private static EventRing _tpa;
        internal static void FocusTpa(uint net)
        {
            if (_tpaNet == net) return;
            _tpaNet = net; _tpa?.Clear();
        }
        internal static void TpaObject(uint net, bool sent, int player, double now)
        {
            if (net == 0 || net != _tpaNet) return;
            if (_tpa == null) _tpa = new EventRing(ObjectPanelState.EventLimit);
            _tpa.Add(new TelemetryEvent { Local = now, Net = net, Kind = sent ? "tpa-object-sent" : "tpa-object-applied", From = sent ? 0 : player, To = sent ? player : -1, Result = "ok" });
        }
        internal static readonly string[] Kinds = {
            "tpa-request-sent", "tpa-accept-sent", "tpa-refuse-sent", "add-applied", "add-assigned",
            "attach-applied", "attach-received", "attach-refused", "attach-request", "attach-sent",
            "claim-refused", "claim-sent", "detach-applied", "detach-refused", "detach-request",
            "detach-sent", "edit-applied", "edit-refused", "edit-sent", "handover-applied",
            "handover-refused", "lease-refused", "lease-done-refused", "hitch-request", "hitch-accept",
            "hitch-refuse", "hitch-refused", "hitch-frozen", "hitch-ownership-claim", "speed-anomaly",
            "handover-sent", "join", "joint-broken", "joint-created", "leave",
            "live-applied", "live-sent", "owner-applied", "owner-change", "owner-decision",
            "owner-teleport", "physlock-lock", "physlock-unlock", "part-detached-received", "part-off-applied",
            "part-off-deduped", "part-off-refused", "part-off-sent", "part-state", "player-damage",
            "player-damage-queued", "player-death", "proxy-correction", "proxy-snap", "remove-applied",
            "remove-relayed", "remove-sent", "resync-applied", "resync-sent", "seat-enter",
            "seat-leave", "spawn-add", "speed-high", "speed-jump", "test-desync",
            "tpa-accept-request", "tpa-accepted", "tpa-applied", "tpa-arrived", "tpa-failure",
            "tpa-pending", "tpa-refuse-request", "tpa-refused", "tpa-request", "tpa-object-sent", "tpa-object-applied",
        };
        internal static readonly string[] Reasons = {
            "reason unavailable",
            "accepted", "entity absent", "not a trailer and car", "stale epoch",
            "held or stored", "slot taken", "already hitched", "slot index",
            "not hitched to car", "hitch pending", "owner changed", "not loaded",
            "slot taken or incompatible", "joint change failed", "hitch changed during transfer", "old simulator frozen",
            "hitch handover", "hitched trailer", "hitch player handover", "hitch crash handover",
            "hitched to car", "unshared target or automatic attach", "connect", "disconnect",
            "ok", "disabled", "cooldown", "declined",
            "detach", "between sent states", "joint absent or parented", "invalid destination",
            "no ground after 30 s", "no local player", "arrival check superseded", "player lost before arrival",
            "within 5 m", "outside 5 m", "car or player lost", "target left",
            "request expired", "requester left", "missing target", "ambiguous target",
            "target absent", "ask rate limit", "target busy", "no pending request",
            "target position unknown", "teleport send failed", "requester left before send", "not connected or ready",
            "far store", "held or attach failed", "part absent", "parent absent",
            "already attached", "loader did not attach", "missing car, wrong owner or epoch", "car absent",
            "item index", "malformed", "missing car", "wrong owner",
            "epoch", "non-finite pose", "pose too far", "invalid rotation",
            "already detached or gone", "already detached", "item record", "not attached to a slot",
            "prefab slot", "slot parent", "not attached", "requester not ready",
            "building key", "building reach", "building type", "building type mismatch",
            "missing lease or wrong holder", "car copy had it bolted on twice", "already exists", "part absent, not in far store",
            "owner already detached", "local unbolt; sync suppressed", "timestamp", "missing entity or already owner",
            "edit rate", "edit reach", "malformed record", "malformed, missing item or no longer owner",
            "record decode failed", "servo release blend", "physics servo", "late, wrong epoch or no longer owner",
            "forced", "command flood limit", "cause unavailable", "fall",
            "remote creature", "remote melee", "remote shot", "held",
            "stored", "worn", "driven", "claim",
            "crash claim", "crash return", "hand-off (nearer player)", "rabbit: nearest player / moving car",
            "record unreadable", "usable value", "door value", "lamp value",
            "tank", "tank fluids", "part value", "colour edit, the item's part conditions unknown",
            "colour value", "painting", "food value", "ammo value",
            "plate", "bus doors", "blood: an edit only washes it off",
        };
        internal static void Capture(EventRing ring, ObjectPanelState into, double now)
        {
            into.EventCount = 0;
            int i = ring.Count - 1, j = _tpa != null ? _tpa.Count - 1 : -1;
            while ((i >= 0 || j >= 0) && into.EventCount < ObjectPanelState.EventLimit)
            {
                var e = j >= 0 && (i < 0 || _tpa.Row(j).Local >= ring.Row(i).Local) ? _tpa.Row(j--) : ring.Row(i--);
                double age = now - e.Local;
                if (age > 35) break;
                if (into.Net == 0 || age < 0 || e.Net != into.Net && e.Other != into.Net) continue;
                int kind = Array.IndexOf(Kinds, e.Kind); if (kind < 0) continue;
                int reason = Array.IndexOf(Reasons, e.Result);
                into.Events[into.EventCount++] = new ObjectPanelState.Recent { Age = (float)age, Kind = (ushort)kind, Reason = (ushort)Math.Max(0, reason), Net = e.Net, Other = e.Other, Epoch = e.Epoch,
                    Item = e.Item, Slot = e.Slot, From = e.From, To = e.To, Count = e.Count, Value = e.Value, Dx = e.Dx, Dy = e.Dy, Dz = e.Dz };
            }
        }
    }
}
