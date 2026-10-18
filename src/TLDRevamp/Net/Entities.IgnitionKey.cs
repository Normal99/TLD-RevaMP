using System.Collections.Generic;
using UnityEngine;

namespace TLDRevamp.Net
{
    /// A car's ignition key turned by a player who doesn't own the car (a passenger, someone reaching in through the
    /// window). The game reads the key from whoever holds it, driver or not (carscript: iignition ||= ignitionUsable.
    /// Holding(); IgnitionStuff: a short hold toggles the ignition, a long one cranks the engine). On a copy that only
    /// changed the copy, and the owner's signals set it back the next frame: the key did nothing (playtest 2026-10-18,
    /// "ignition not turned on"). The key isn't one of the car's saved usables, so no edit carried it either. Now the
    /// holding goes to the owner (on change, again every KeyResendS while held), whose game is made to see the key held
    /// for as long: the toggle or the start happens there, the game's own way, and comes back with its signals.
    public static partial class Entities
    {
        public const byte IgnitionKey = 96;   // u32 net, u8 held: a non-owner holding a car's key (reliable)
        public static bool KeyForward = true;   // A/B: false = before v0.69.2 (a non-owner's key turn stays on their copy)
        public static float KeyResendS = 0.5f, KeyHoldTimeoutS = 1.5f;
        public static long KeyHoldsSent, KeyHoldsApplied, KeyHoldsRefused;

        // ---- the copy's side: the local player holds a copy's key
        private static readonly Dictionary<carscript, float> _keySeen = new Dictionary<carscript, float>();
        private static carscript _keyCar; private static uint _keyNet; private static float _keySentAt;

        [HarmonyLib.HarmonyPatch(typeof(carscript), "IgnitionStuff")]
        private static class KeyOnCopy
        {
            [HarmonyLib.HarmonyPrefix]
            private static void Prefix(carscript __instance)
            {
                if (!KeyForward || !InSession || __instance.ignitionUsable == null || !__instance.ignitionUsable.Holding() || !IsProxy(__instance)) return;
                _keySeen[__instance] = Time.time;
            }
        }

        private static void KeyTick()
        {
            if (_keyCar == null && _keySeen.Count == 0 && _keyHolds.Count == 0) return;
            // copy side
            carscript held = null;
            foreach (var kv in _keySeen) if (kv.Key != null && Time.time - kv.Value < 0.1f) { held = kv.Key; break; }
            if (held == null) _keySeen.Clear();
            if (held != _keyCar)
            {
                // let go: how long it was held (from the first to the last frame the game saw the hand on it)
                if (_keyCar != null) SendKey(_keyNet, false, Mathf.Max(0f, _keyLastAt - _keyFirstAt));
                _keyCar = held; _keyNet = 0;
                uint net = 0;
                if (held != null) foreach (var e in ByNet.Values) if (e.Proxy && e.Root != null && e.Root.car == held && e.PartIndex < 0) { net = e.NetId; break; }
                if (net != 0) { _keyNet = net; _keyFirstAt = Time.time; SendKey(net, true, 0f); }
                else _keyCar = null;
            }
            else if (held != null && Time.realtimeSinceStartup - _keySentAt >= KeyResendS) SendKey(_keyNet, true, 0f);
            if (held != null) _keyLastAt = Time.time;
            // owner side: the remote hand on the key, as the game's own hold
            if (_keyHolds.Count == 0) return;
            _keyDone.Clear();
            float now = Time.realtimeSinceStartup;
            foreach (var kv in _keyHolds)
            {
                var c = kv.Key; var h = kv.Value;
                if (c == null || c.ignitionUsable == null || IsProxy(c)) { _keyDone.Add(c); continue; }
                // still held past the game's toggle time: a start (cranking while held), not a switch
                if (!h.Active && now - h.PressAt > c.startingTime + KeyTapMarginS) { h.Active = true; h.Until = Mathf.Max(h.Until, now); }
                if (!h.Active) { if (now > h.PressAt + KeyHoldTimeoutS) _keyDone.Add(c); continue; }
                if (h.Frames >= 2 && now > h.Until) { _keyDone.Add(c); continue; }
                c.ignitionUsable.lastHoldTime = Time.time;
                h.Frames++;
            }
            foreach (var c in _keyDone) _keyHolds.Remove(c);
        }
        private static float _keyFirstAt, _keyLastAt;
        /// How long past the game's toggle time (carscript.startingTime) a press may take to be let go and still count as
        /// a switch: the release has a network trip of its own.
        public static float KeyTapMarginS = 0.15f;

        private static void SendKey(uint net, bool held, float heldS)
        {
            if (net == 0) return;
            W.Reset(); W.U8(IgnitionKey); W.U32(net); W.U8((byte)(held ? 1 : 0)); W.U16((ushort)Mathf.Clamp(Mathf.RoundToInt(heldS * 1000f), 0, 65535));
            ToServer(W, true);
            _keySentAt = Time.realtimeSinceStartup;
            KeyHoldsSent++;
        }

        /// Server: from a player at the car (seated in it, or within reach) to its owner.
        private static void ServerKey(int from, NetReader r)
        {
            uint net = r.U32(); byte held = r.U8(); ushort ms = r.U16();
            if (r.Bad || !Server.TryGetValue(net, out var se) || se.OwnerId == from || !se.Car) { KeyHoldsRefused++; return; }
            if (EditValidation && from != 0 && !TakeToken(_editBuckets, from, EditRatePerS, EditBurst)) { KeyHoldsRefused++; return; }
            if (EditRangeCheck && from != 0 && !AtObject(from, se)) { KeyHoldsRefused++; Refuse(ref EditsOutOfReach, "key reach", $"ignition key from player {from}: they aren't at net {net}"); return; }
            W.Reset(); W.U8(IgnitionKey); W.U32(net); W.U8(held); W.U16(ms);
            ServerSendTo(se.OwnerId, W, true);
        }

        // ---- the owner's side
        /// A press waits (not Active) until it is let go — then it is played back for as long as it was held, a tap
        /// stays a tap whatever the release's trip took — or until it has been held past the toggle time: then the key
        /// is held here for as long as it is held there (the engine cranks).
        private sealed class KeyHold { public float PressAt, Until; public bool Active; public int Frames; }
        private static readonly Dictionary<carscript, KeyHold> _keyHolds = new Dictionary<carscript, KeyHold>();
        private static readonly List<carscript> _keyDone = new List<carscript>();

        private static void ApplyKey(NetReader r)
        {
            uint net = r.U32(); bool held = r.U8() != 0; float heldS = r.U16() / 1000f;
            if (r.Bad || !ByNet.TryGetValue(net, out var e) || e.Proxy || !Resolve(e) || e.Root == null || e.Root.car == null) return;
            var c = e.Root.car;
            float now = Time.realtimeSinceStartup;
            if (!_keyHolds.TryGetValue(c, out var h)) { if (!held) return; _keyHolds[c] = h = new KeyHold { PressAt = now }; }
            if (held) h.Until = now + KeyHoldTimeoutS;   // pressed, or still held (every KeyResendS)
            else if (!h.Active)   // let go before we began: played back as long as it was held — a tap stays a tap
            { h.Active = true; h.Frames = 0; h.Until = now + (heldS < c.startingTime ? Mathf.Min(heldS, c.startingTime * 0.75f) : heldS); }
            else h.Until = Mathf.Min(h.Until, now);   // let go after a start: no longer than now
            KeyHoldsApplied++;
        }

        /// Test (bridge `mp key <net> <seconds>`): the local player holds car <net>'s ignition key for that long, as
        /// fpscontroller does while the use button is down on it (`mp key <net>`: the car's ignition and engine here).
        private static float _keyTestUntil; private static carscript _keyTestCar;
        public static string KeyTest(uint net, float secs)
        {
            if (!ByNet.TryGetValue(net, out var e) || !Resolve(e) || e.Root == null || e.Root.car == null) return "{\"error\":\"no such car here\"}";
            var c = e.Root.car;
            if (secs > 0f) { _keyTestCar = c; _keyTestUntil = Time.time + secs; }
            var eng = c.Engine;
            return "{\"proxy\":" + (e.Proxy ? "true" : "false") + ",\"ignition\":" + (c.ignition ? "true" : "false") + ",\"running\":" + (eng != null && eng.running ? "true" : "false") +
                   ",\"key\":" + (c.ignitionUsable != null ? "true" : "false") + ",\"sent\":" + KeyHoldsSent + ",\"applied\":" + KeyHoldsApplied + ",\"refused\":" + KeyHoldsRefused + "}";
        }

        private static void KeyTestTick()
        {
            if (_keyTestCar == null) return;
            if (Time.time > _keyTestUntil || _keyTestCar.ignitionUsable == null) { _keyTestCar = null; return; }
            _keyTestCar.ignitionUsable.lastHoldTime = Time.time;
        }
    }
}
