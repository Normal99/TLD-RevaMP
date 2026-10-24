using UnityEngine;

namespace TLDRevamp.Diagnostics
{
    /// Test (tools/radiodial.py): can a seated player hit a car radio's tuning dial with the game's look ray while the car
    /// moves? Every frame, from the camera, towards the dial as it is DRAWN (its transform), the game's own ray
    /// (fpscontroller: Cam forward, FrayRange, useLayer): a hit is the dial's collider. Also the gap between where the
    /// dial is drawn and where its collider is (physics). Playtest 2026-10-09/10 (d): dials hard to turn as a passenger at
    /// speed. Seated in a copy the ray is cast as the game now casts it, from the vehicle's physics frame (Entities.SeatAim;
    /// `physicsFrame` counts those frames). Bridge `aimlab start <car net>` / `aimlab` (status) / `aimlab stop`.
    /// Also where the dial is drawn in the camera's frame as the camera renders it (Camera.onPreCull): a seated player's
    /// camera rides with the car, so frame-to-frame steps there are the dial jittering on screen (tools/dialjitter.py,
    /// playtest 2026-10-24) — `camStep*` the step, `camJerk*` its change from the previous step (back-and-forth), in mm.
    public class AimLab : MonoBehaviour
    {
        private static AimLab _run;
        private Collider _dial;
        private string _what = "";
        private int _frames, _hits, _remapped;
        private float _gapMax, _gapSum;
        private int _camFrames, _camFrame = -1;
        private bool _hasPrev, _hasStep;
        private Vector3 _camPrev, _stepPrev;
        private float _camStepSum, _camStepMax, _camJerkSum, _camJerkMax;

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
            Camera.onPreCull += _run.OnPreCull;
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
                   ",\"gapAvg\":" + (_run._frames > 0 ? _run._gapSum / _run._frames : 0f).ToString("F3", ic) + ",\"gapMax\":" + _run._gapMax.ToString("F3", ic) +
                   ",\"camFrames\":" + _run._camFrames + ",\"camStepAvg\":" + (_run._camFrames > 1 ? _run._camStepSum / (_run._camFrames - 1) : 0f).ToString("F2", ic) +
                   ",\"camStepMax\":" + _run._camStepMax.ToString("F2", ic) + ",\"camJerkAvg\":" + (_run._camFrames > 2 ? _run._camJerkSum / (_run._camFrames - 2) : 0f).ToString("F2", ic) +
                   ",\"camJerkMax\":" + _run._camJerkMax.ToString("F2", ic) + ",\"physicsFrame\":" + _run._remapped + "}";
        }

        private void OnDestroy() => Camera.onPreCull -= OnPreCull;

        private void OnPreCull(Camera c)
        {
            var p = mainscript.s != null ? mainscript.s.player : null;
            if (p == null || p.Cam == null || _dial == null || (c != p.Cam && c != p.mainCam) || _camFrame == Time.frameCount) return;
            _camFrame = Time.frameCount;
            Vector3 local = _dial is BoxCollider b ? b.center : _dial is SphereCollider sc ? sc.center : _dial is CapsuleCollider cc ? cc.center : Vector3.zero;
            Vector3 at = c.transform.InverseTransformPoint(_dial.transform.TransformPoint(local)) * 1000f;   // mm in the camera's frame
            _camFrames++;
            if (_hasPrev)
            {
                Vector3 step = at - _camPrev; float m = step.magnitude;
                _camStepSum += m; if (m > _camStepMax) _camStepMax = m;
                if (_hasStep) { float j = (step - _stepPrev).magnitude; _camJerkSum += j; if (j > _camJerkMax) _camJerkMax = j; }
                _stepPrev = step; _hasStep = true;
            }
            _camPrev = at; _hasPrev = true;
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
            // the ray as the game casts it now: from the vehicle's physics frame while seated in a copy (Entities.SeatAim)
            if (Net.Entities.SeatAimFrame(out var frame, out var body))
            {
                from = Net.Entities.SeatAimPoint(frame, body, from); drawn = Net.Entities.SeatAimPoint(frame, body, drawn);
                _remapped++;
            }
            _frames++;
            if (Physics.Raycast(from, (drawn - from).normalized, out var hit, p.FrayRange, p.useLayer) && hit.collider == _dial) _hits++;
        }
    }
}
