using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// What a car shows and sounds like while its owner drives it: ignition, brake, handbrake, gear, horn. A copy got
    /// none of these - its carscript runs with no inputs - so on the other screens a car's horn was silent, its brake
    /// and reverse lights never came on, and with ignition off (localIgnition = ignition && electronics) not even its
    /// headlamps or dashboard lit up. The game's own MP sends them as the car's values (carscript.UpdMultiSendAll →
    /// SendCarValues / SendCarHandbrake) and feeds them to the other side's car as inputs. Here they are display only:
    /// a copy is moved by its owner's states, and its wheels by the owner's torques - inputs must not drive it. So:
    ///   ignition  set on the copy (rear lamps, headlamps and LEDs follow localIgnition)
    ///   horn      the copy's horn input (carscript eases currenthorn to it and plays the horn)
    ///   gear      the game's gear change (ShiftTo: gear stick, shift sound)
    ///   handbrake the game's receiver (UpdMultiHandbrake: lever, sound, kickstands)
    ///   brake     only while the rear lights read it (rearlights.Upd) - the copy's own brake stays its own
    public static partial class Entities
    {
        public static long SignalsApplied;
        private static readonly Dictionary<carscript, Ent> SignalCars = new Dictionary<carscript, Ent>();

        /// After the engine in every state: u8 flags (1 car, 2 ignition, 4 brake, 8 handbrake), i8 gear, u8 horn.
        private static void WriteSignals(NetWriter w, Ent e)
        {
            var c = e.PartIndex < 0 && e.Root != null ? e.Root.car : null;
            if (c == null) { w.U8(0); return; }
            w.U8((byte)(1 | (c.ignition ? 2 : 0) | (c.brake > 0.01f ? 4 : 0) | (c.handbrake >= 0.5f ? 8 : 0)));
            w.U8((byte)(sbyte)Mathf.Clamp(c.gear, -127, 127));
            w.U8((byte)Mathf.RoundToInt(Mathf.Clamp01(c.currenthorn) * 255f));
        }

        /// Engine running/starter, ignition, brake, handbrake, gear and horn of an owned car, packed: what a copy shows of
        /// it beyond its pose. -1: not a car.
        internal static int CarSignalKey(Ent e)
        {
            var c = e.PartIndex < 0 && e.Root != null ? e.Root.car : null;
            if (c == null) return -1;
            var eng = c.Engine;
            int k = (eng != null && eng.running ? 1 : 0) | (eng != null && eng.start ? 2 : 0) | (c.ignition ? 4 : 0) | (c.brake > 0.01f ? 8 : 0) | (c.handbrake >= 0.5f ? 16 : 0);
            k |= ((byte)(sbyte)Mathf.Clamp(c.gear, -127, 127)) << 8;
            k |= Mathf.RoundToInt(Mathf.Clamp01(c.currenthorn) * 15f) << 16;
            return k;
        }

        private static void ReadSignals(NetReader r, Ent e)
        {
            if (r.Pos >= r.End) return;   // a sender without them (bots)
            byte f = r.U8();
            if ((f & 1) == 0) return;
            int gear = (sbyte)r.U8(); float horn = r.U8() / 255f;
            if (r.Bad) return;
            e.SigHas = true; e.SigIgnition = (f & 2) != 0; e.SigBrake = (f & 4) != 0; e.SigHandbrake = (f & 8) != 0;
            e.SigGear = gear; e.SigHorn = horn;
            var c = e.Root != null ? e.Root.car : null;
            if (c != null) SignalCars[c] = e;
        }

        private static bool Signals(carscript c, out Ent e) =>
            SignalCars.TryGetValue(c, out e) && e.Proxy && e.SigHas && e.Root != null && e.Root.car == c;

        /// Every frame the car starts from ResetInput: the owner's signals go in here.
        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.ResetInput))]
        private static class SignalsOnCopy
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(carscript __instance)
            {
                if (SignalCars.Count == 0 || !InSession) return;
                if (!Signals(__instance, out var e)) return;
                var c = __instance;
                c.ignition = e.SigIgnition;
                c.ihorn = c.ihorn || e.SigHorn > 0.5f;
                if (c.gear != e.SigGear && e.SigGear + 1 >= 0 && e.SigGear + 1 < c.gears.Length) c.ShiftTo(e.SigGear);
                if (e.SigHandbrake != (c.handbrake >= 0.5f)) c.UpdMultiHandbrake(e.SigHandbrake ? 1f : 0f);
                SignalsApplied++;
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(rearlights), nameof(rearlights.Upd))]
        private static class BrakeLightsOnCopy
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(carscript c, out float __state)
            {
                __state = float.NaN;
                if (SignalCars.Count == 0 || c == null || !Signals(c, out var e)) return;
                __state = c.brake;
                c.brake = e.SigBrake ? 1f : 0f;
            }

            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(carscript c, float __state)
            {
                if (!float.IsNaN(__state) && c != null) c.brake = __state;
            }
        }

        /// Test hooks (bridge `set`): what the driver's hands do in the car the local player drives - brake pedal, horn,
        /// handbrake lever, ignition - entered where the keyboard's input lands (GetPlayerInput). No gear hook: the
        /// automatic gearbox shifts over it every frame (carsigtest run 1); gears are tested by what the gearbox does.
        /// TestRoll: the car gets this forward speed once (m/s) - a car rolling, to brake from.
        public static float TestBrake, TestRoll; public static bool TestHorn;
        public static int TestHandbrake = -1, TestIgnition = -1;

        [HarmonyLib.HarmonyPatch(typeof(carscript), nameof(carscript.GetPlayerInput))]
        private static class SignalsTestInput
        {
            [HarmonyLib.HarmonyPostfix]
            private static void Postfix(carscript __instance)
            {
                var c = __instance;
                if (!c.isPlayerDriving) return;
                if (TestBrake > 0f) c.ibrake = Mathf.Max(c.ibrake, TestBrake);
                if (TestHorn) c.ihorn = true;
                if (TestRoll > 0f && c.RB != null) { c.RB.velocity = c.transform.forward * TestRoll; TestRoll = 0f; }
                if (TestHandbrake >= 0 && (c.handbrake >= 0.5f) != (TestHandbrake == 1)) c.ihandbrakeBDown = true;
                if (TestIgnition >= 0) c.ignition = TestIgnition == 1;
            }
        }

        /// Test (bridge `mp carset <net> ignition 0|1`): a car's ignition switched with nobody in it (a parked car at rest).
        public static string CarSet(uint net, string what, int v)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null || e.Root.car == null) return "{\"error\":\"no such car\"}";
            var c = e.Root.car;
            if (what == "ignition") c.ignition = v == 1;
            else return "{\"error\":\"ignition only\"}";
            return "{\"ignition\":" + (c.ignition ? "true" : "false") + ",\"key\":" + CarSignalKey(e) + "}";
        }

        /// the light switch (0 off, 1 dipped, 2 main) and the headlamps' Lights shining (enabled, intensity > 0)
        private static string HeadlampState(tosaveitemscript root, carscript c)
        {
            int on = 0, all = 0, hs = 0; var states = new System.Collections.Generic.List<string>();
            foreach (var h in root.GetComponentsInChildren<headlightscript>(true))
            {
                hs++; states.Add(h.currentState.ToString());
                foreach (var L in h.GetComponentsInChildren<Light>(true)) { all++; if (L.enabled && L.gameObject.activeInHierarchy && L.intensity > 0.01f) on++; }
            }
            return ",\"lightSwitch\":" + (c.lightUsable != null ? c.lightUsable.currentTurnState : -1) + ",\"headlamps\":" + hs +
                   ",\"headlampStates\":\"" + string.Join(" ", states) + "\",\"lampLights\":[" + on + "," + all + "]";
        }

        /// Diagnostics (bridge `mp carsig net`): a car's signals here, and on a copy what was received.
        public static string CarSignals(uint net)
        {
            if (!ByNet.TryGetValue(net, out var e) || e.Root == null || e.Root.car == null) return "{\"error\":\"no such car\"}";
            var c = e.Root.car;
            var rl = e.Root.GetComponentsInChildren<rearlights>(true);
            int lit = 0, lamps = 0;
            foreach (var x in rl)
            {
                if (x.FLight != null) foreach (var g in x.FLight) if (g != null) { lamps++; if (g.activeSelf) lit++; }
            }
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"ignition\":" + (c.ignition ? "true" : "false") +
                   ",\"localIgnition\":" + (c.localIgnition ? "true" : "false") + ",\"gear\":" + c.gear +
                   ",\"handbrake\":" + c.handbrake.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"horn\":" + c.currenthorn.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                   ",\"hornPlaying\":" + (c.SHornSound != null && c.SHornSound.isPlaying && c.SHornSound.volume > 0.01f ? "true" : "false") +
                   ",\"brakeLights\":[" + lit + "," + lamps + "]" + HeadlampState(e.Root, c) +
                   ",\"got\":" + (e.SigHas ? "{\"ignition\":" + (e.SigIgnition ? "true" : "false") + ",\"brake\":" + (e.SigBrake ? "true" : "false") +
                                  ",\"handbrake\":" + (e.SigHandbrake ? "true" : "false") + ",\"gear\":" + e.SigGear + ",\"horn\":" +
                                  e.SigHorn.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}" : "null") +
                   ",\"applied\":" + SignalsApplied + "}";
        }
    }
}
