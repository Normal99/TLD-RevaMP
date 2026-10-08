using UnityEngine;

namespace TLDRevamp.Net
{
    /// Items held by a player seated in a car travel in the car's frame. The holder's machine shows the car through its
    /// own delayed copy (when someone else drives), so the item's world position was already ~0.15 s old there, and
    /// the other machines then showed it another ~0.15 s late: at 50 km/h a passenger's lamp hung 5.5 m behind its
    /// seat on the driver's screen (heldincar.py, 2026-10-08). Now the owner also sends where the item is in the car
    /// (state flag 16), and each machine places it in its own view of that car. The world position stays in the state
    /// (the server's records and distance rules, a machine without the car).
    public static partial class Entities
    {
        public static bool CarriedFrames = true;   // A/B: false = world positions only (before v0.65.45)
        public static long CarriedSent, CarriedSwitches, CarriedNoCar, CarriedReleasedClaims, CarriedTakeovers;

        /// Owner: the car our player sits in, if this held item rides in it; its pose in the car's frame.
        private static uint CarriedBy(Ent e, Transform t, Quaternion q, out Vector3 lp, out Quaternion lq)
        {
            lp = default; lq = Quaternion.identity;
            if (!CarriedFrames) return 0;
            LocalSeat(out uint car, out _);
            if (car == 0 || car == e.NetId || !ByNet.TryGetValue(car, out var c) || c.Root == null) return 0;
            var ct = c.Root.transform;
            lp = ct.InverseTransformPoint(t.position); lq = Quaternion.Inverse(ct.rotation) * q;
            CarriedSent++;
            return car;
        }

        /// Copy, a state just read: in the car's frame, the interpolator runs on car-frame poses (a fresh one when the
        /// frame changes: the two kinds of positions never mix on one curve). True when it became ours (see below).
        private static bool TakeCarried(Ent e, ref PoseInterpolator.Sample s, Vector3d global, Quaternion globalRot)
        {
            uint cb = ReadCarriedBy;
            // let go in a car this machine drives: loose cargo in our car — ours at once, from where it is shown (in
            // the car). Left to the holder, its world states put it metres behind the car here until it touched it, and
            // the claim then made a still item of it in a car at 50 km/h: the car rammed it (stopped dead, 3 parts off,
            // the lamp thrown 50 m; heldincar B2, 2026-10-08)
            if (cb == 0 && e.CarriedBy != 0 && ByNet.TryGetValue(e.CarriedBy, out var car) && !car.Proxy && car.Root != null && CargoClaims && e.Root != null)
            {
                if (ClaimCargo(e, released: true)) { CarriedReleasedClaims++; return true; }
            }
            if (cb != e.CarriedBy) { e.CarriedBy = cb; if (e.Ip != null) e.Ip = new PoseInterpolator(); CarriedSwitches++; }
            if (cb == 0) return false;
            e.CarriedGlobal = global; e.CarriedGlobalRot = globalRot;
            var l = ReadLocalPos;
            s.Pos = new Vector3d(l.x, l.y, l.z); s.Rot = ReadLocalRot; s.Vel = Vector3.zero;   // still in the car: a stall holds it in place
            return false;
        }

        /// A carried copy becomes ours (a release claim, a pickup): it starts where it is shown, moving with the car it
        /// is in — the last state's velocity is the holder's view of it (a kinematic right hand: zero).
        private static void TakeOverCarried(Ent e)
        {
            if (ByNet.TryGetValue(e.CarriedBy, out var car) && car.Root != null && e.Root != null)
            {
                // our car: its body's speed at that point; a copy car is kinematic and reads 0 km/h — its owner's
                // velocity from the last state (a still item in a car at speed gets rammed)
                var crb = car.Root.GetComponent<Rigidbody>();
                e.LastVel = !car.Proxy && crb != null && !crb.isKinematic ? crb.GetPointVelocity(e.Root.transform.position) : car.LastVel;
            }
            e.CarriedBy = 0;
            CarriedTakeovers++;
        }

        /// The pose a copy is shown at (every mover): its interpolated state, a car-frame one put into this machine's car.
        private static void EvalShown(Ent e, double now, float dt, out Vector3d pos, out Quaternion rot)
        {
            Coast(e); e.Ip.Evaluate(now, dt, out pos, out rot);
            if (e.CarriedBy == 0) return;
            if (ByNet.TryGetValue(e.CarriedBy, out var c) && c.Root != null)
            {
                var ct = c.Root.transform;
                pos = mainscript.GlobalFromUnityPos(ct.TransformPoint((Vector3)pos)); rot = ct.rotation * rot;
            }
            else { pos = e.CarriedGlobal; rot = e.CarriedGlobalRot; CarriedNoCar++; }
        }
    }
}
