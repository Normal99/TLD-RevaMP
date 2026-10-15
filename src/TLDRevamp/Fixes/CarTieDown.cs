using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TLDRevamp.Fixes
{
    /// A car carried on another (the Brakas transporter's bed, the car trailer example mod) falls off when the carrier
    /// tips over. The game's physics lock for carried cars (mountStuff.FreezeUpdVehicle → carscript.Lock) doesn't
    /// freeze the car to the bed as it does small items: it only swaps the car's wheel colliders for boxes, so the
    /// wheels don't roll while its handbrake is on. Nothing holds the car down (playtest 2026-10-14: "when i flip over
    /// the trailer it doest stay and the cars handbrake is on").
    ///
    /// Here a car whose wheels the bed has locked is joined to the carrier, and let go when the lock ends (handbrake
    /// off, the car driven off the bed). Only the machine that simulates both does it (a copy moved for another player
    /// has a kinematic body).
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.CarTieDown.Enabled
    [HarmonyPatch(typeof(mountStuff), nameof(mountStuff.FreezeUpdVehicle))]
    public static class CarTieDown
    {
        public static bool Enabled = true;
        public static long Tied;

        [HarmonyPostfix]
        private static void Postfix(mountStuff __instance)
        {
            if (!Enabled || __instance.sType != mountStuff.storageType.trunk || __instance.storedcars.Count == 0) return;
            var t = __instance.GetComponent<Holder>();
            if (t == null) t = __instance.gameObject.AddComponent<Holder>();
            t.Mount = __instance;
            t.Check();
        }

        public sealed class Holder : MonoBehaviour
        {
            public mountStuff Mount;
            private Rigidbody _carrier;
            private float _next;
            private readonly Dictionary<carscript, FixedJoint> _tied = new Dictionary<carscript, FixedJoint>();
            private readonly List<carscript> _drop = new List<carscript>();

            private void Update()
            {
                if (_tied.Count > 0 && Time.time >= _next) Check();
            }

            public void Check()
            {
                _next = Time.time + 0.2f;
                if (_carrier == null) _carrier = Mount != null ? Mount.GetComponentInParent<Rigidbody>() : null;
                _drop.Clear();
                foreach (var kv in _tied)
                    if (!Enabled || kv.Key == null || kv.Value == null || !Holds(kv.Key)) _drop.Add(kv.Key);
                foreach (var car in _drop)
                {
                    if (_tied.TryGetValue(car, out var j) && j != null) Destroy(j);
                    _tied.Remove(car);
                }
                if (!Enabled || Mount == null || _carrier == null) return;
                foreach (var car in Mount.storedcars)
                {
                    if (car == null || _tied.ContainsKey(car) || !Holds(car)) continue;
                    var j = car.gameObject.AddComponent<FixedJoint>();
                    j.connectedBody = _carrier;
                    j.enableCollision = true;      // the car still rests on the bed's colliders
                    _tied[car] = j;
                    Tied++;
                }
            }

            private bool Holds(carscript car)
            {
                if (!car.whlocked || _carrier == null || _carrier.isKinematic) return false;
                var rb = car.GetComponent<Rigidbody>();
                return rb != null && rb != _carrier && !rb.isKinematic;
            }

            private void OnDisable()
            {
                foreach (var j in _tied.Values) if (j != null) Destroy(j);
                _tied.Clear();
            }
        }
    }
}
