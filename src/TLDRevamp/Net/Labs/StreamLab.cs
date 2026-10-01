using System;
using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// Item streaming at the removal boundary (bridge `streamlab start [margin]`, `streamlab status`), one game, no MP.
    /// itemPlaceRemoveScript.RemoveStuff decides per item by its own distance (> itemRemoveDist, checked every second).
    /// A car root out of range is saved and destroyed; parts on joints (attachablescript.parentAtAttach off) aren't its
    /// children, so each goes by its own distance. One still in range stays — and next frame its attach point is gone
    /// (destroyed with the car): attachablescript.Update detaches it. The lab parks the player so the nearest car's root
    /// is just out of range and its nearest jointed part just in, runs RemoveStuff once, then comes back and counts
    /// the car's attached parts after it's placed again.
    public static class StreamLab
    {
        private static int _phase, _wait;
        private static uint _rootId;
        private static Vector3 _home;
        private static readonly List<uint> Jointed = new List<uint>();
        private static int _attachedBefore, _attachedAfter, _detachedLoose, _strandedIn;
        private static string _log = "";
        public static bool Fix = true;   // exposed for A/B

        private static int CountAttached(tosaveitemscript root)
        {
            int n = 0;
            foreach (var it in ItemSnapshot.Group(root)) if (it != root && it.attachable != null && it.attachable.attached) n++;
            return n;
        }

        public static string Start(float margin)
        {
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl == null) return "{\"error\":\"not in game\"}";
            tosaveitemscript car = null; float best = float.MaxValue;
            foreach (var it in savedatascript.s.items.Values)
                if (it != null && it.car != null && it.transform.parent == null) { float d = (it.transform.position - pl.transform.position).sqrMagnitude; if (d < best) { best = d; car = it; } }
            if (car == null) return "{\"error\":\"no car\"}";
            var group = ItemSnapshot.Group(car);
            Jointed.Clear();
            Vector3 u = pl.transform.position - car.transform.position; u.y = 0; u = u.sqrMagnitude > 0.01f ? u.normalized : Vector3.forward;
            float pmax = float.MinValue; tosaveitemscript near = null;
            foreach (var it in group)
                if (it != car && it.attachable != null && it.attachable.attached && !it.attachable.parentAtAttach)
                {
                    Jointed.Add(it.idInSave);
                    float p = Vector3.Dot(it.transform.position - car.transform.position, u);
                    if (p > pmax) { pmax = p; near = it; }
                }
            if (near == null)
            {
                var kinds = new List<string>();
                foreach (var it in group)
                    kinds.Add(it.name + (it.attachable == null ? "[noatt]" : it.attachable.attached ? (it.attachable.parentAtAttach ? "[par]" : "[joint]") : "[loose]") + (it.transform.parent != null ? "^" : ""));
                return "{\"error\":\"car has no jointed parts\",\"group\":" + Json.Str(string.Join(" ", kinds)) + "}";
            }
            _rootId = car.idInSave; _home = pl.transform.position;
            _attachedBefore = CountAttached(car);
            // root at R + margin·pmax: out; the nearest jointed part ≈ pmax closer: in
            float R = itemPlaceRemoveScript.s.itemRemoveDist;
            float D = R + Mathf.Clamp(pmax * margin, 0.05f, pmax - 0.05f);
            var target = car.transform.position + u * D;
            target.y = pl.transform.position.y + 50f;   // in the air: nothing to stand on 300 m out is needed for one check
            pl.Teleport(target);
            mainscript.s.player.godModeTillGrounded = true;
            _phase = 1; _wait = 3; _log = "jointed " + Jointed.Count + ", nearest " + near.name + " " + pmax.ToString("F2") + " m toward the player; root at " + D.ToString("F2") + " m";
            return "{\"started\":true,\"log\":" + Json.Str(_log) + "}";
        }

        public static void Tick()
        {
            if (_phase == 0 || --_wait > 0) return;
            var pl = mainscript.s.player;
            switch (_phase)
            {
                case 1:   // genArounds follow the player: one removal pass now
                    itemPlaceRemoveScript.s.RemoveStuff();
                    _phase = 2; _wait = 5; break;
                case 2:   // a few frames later: what is left of the car in the world?
                    _detachedLoose = 0; _strandedIn = 0;
                    foreach (var id in Jointed)
                        if (savedatascript.s.items.TryGetValue(id, out var it) && it != null)
                        { _strandedIn++; if (it.attachable == null || !it.attachable.attached) _detachedLoose++; }
                    _log += "; after removal: root loaded " + (savedatascript.s.items.TryGetValue(_rootId, out var r) && r != null) + ", jointed parts still in the world " + _strandedIn + ", of them fallen off " + _detachedLoose;
                    pl.Teleport(_home); _phase = 3; _wait = 10; break;
                case 3:
                    itemPlaceRemoveScript.s.PlaceStuff();
                    _phase = 4; _wait = 30; break;
                case 4:
                    if (savedatascript.s.items.TryGetValue(_rootId, out var root) && root != null) _attachedAfter = CountAttached(root);
                    else _attachedAfter = -1;
                    _log += "; back: attached parts " + _attachedBefore + " → " + _attachedAfter;
                    _phase = 0; break;
            }
        }

        public static string Status() =>
            "{\"running\":" + (_phase != 0 ? "true" : "false") + ",\"before\":" + _attachedBefore + ",\"after\":" + _attachedAfter + ",\"fallenOff\":" + _detachedLoose + ",\"stranded\":" + _strandedIn + ",\"log\":" + Json.Str(_log) + "}";
    }
}
