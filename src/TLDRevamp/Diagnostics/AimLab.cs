using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// Test (tools/radiodial.py): can a seated player hit a car radio's tuning dial with the game's look ray while the car
    /// moves? Every frame, from the camera, towards the dial as it is DRAWN (its transform), the game's own ray
    /// (fpscontroller: Cam forward, FrayRange, useLayer): a hit is the dial's collider. Also the gap between where the
    /// dial is drawn and where its collider is (physics). Playtest 2026-10-09/10 (d): dials hard to turn as a passenger at
    /// speed. Bridge `aimlab start <car net>` / `aimlab` (status) / `aimlab stop`.
    public class AimLab : MonoBehaviour
    {
        private static AimLab _run;
        private Collider _dial;
        private string _what = "";
        private int _frames, _hits;
        private float _gapMax, _gapSum;

        public static string Start(uint net)
        {
            if (!Net.Entities.TryRoot(net, out var root)) return "{\"error\":\"no such object here\"}";
            radioscript radio = root.GetComponentInChildren<radioscript>(true);
            if (radio == null)
                foreach (var r in Object.FindObjectsOfType<radioscript>())
                    if ((r.transform.position - root.transform.position).sqrMagnitude < 9f) { radio = r; break; }
            if (radio == null || radio.Btune == null) return "{\"error\":\"no radio in that car\"}";
            var col = radio.Btune.GetComponent<Collider>() ?? radio.Btune.GetComponentInChildren<Collider>();
            if (col == null) return "{\"error\":\"the dial has no collider\"}";
            Stop();
            _run = new GameObject("TLDRevamp.AimLab").AddComponent<AimLab>();
            _run._dial = col; _run._what = radio.name + "/" + col.name;
            return Status();
        }

        public static string Stop()
        {
            if (_run != null) Object.Destroy(_run.gameObject);
            string s = Status();
            _run = null;
            return s;
        }

        public static string Status()
        {
            if (_run == null) return "{\"running\":false}";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            return "{\"running\":true,\"dial\":" + Json.Str(_run._what) + ",\"frames\":" + _run._frames + ",\"hits\":" + _run._hits +
                   ",\"gapAvg\":" + (_run._frames > 0 ? _run._gapSum / _run._frames : 0f).ToString("F3", ic) + ",\"gapMax\":" + _run._gapMax.ToString("F3", ic) + "}";
        }

        private void Update()
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || p.Cam == null || _dial == null) return;
            // the dial as drawn: its collider's centre through the (interpolated) transform
            Vector3 local = _dial is BoxCollider b ? b.center : _dial is SphereCollider sc ? sc.center : _dial is CapsuleCollider cc ? cc.center : Vector3.zero;
            Vector3 drawn = _dial.transform.TransformPoint(local);
            Vector3 phys = _dial.bounds.center;   // where physics has it
            float gap = (drawn - phys).magnitude;
            _gapSum += gap; if (gap > _gapMax) _gapMax = gap;
            Vector3 from = p.Cam.transform.position;
            _frames++;
            if (Physics.Raycast(from, (drawn - from).normalized, out var hit, p.FrayRange, p.useLayer) && hit.collider == _dial) _hits++;
        }
    }
}
