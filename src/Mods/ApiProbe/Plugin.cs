using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace ApiProbe
{
    /// A test mod for the multiplayer API's second step (tools/modapi2.py; never shipped): beacons with a label in their
    /// record, a needle in their state records (only players near get it), a far store range, and one session-wide
    /// setting. Driven through the debug bridge: `call ApiProbe.Probe.Spawn a 0 5`, `call ApiProbe.Probe.Status`.
    [BepInPlugin("tldrevamp.apiprobe", "API Probe", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.1.0";

        private void Awake()
        {
            Probe.Mod = MpApi.RegisterMod("apiprobe", "API Probe", Version);
            Probe.Type = MpApi.RegisterType(Probe.Mod, new BeaconType());
            Probe.RuleSetting = MpApi.RegisterSetting(Probe.Mod, "rule", () => Probe.Rule, v => { Probe.Rule = v; Probe.RuleApplied++; });
            Logger.LogInfo("API Probe registered (API " + MpApi.ApiVersion + ")");
        }
    }

    public sealed class Beacon : MonoBehaviour
    {
        public string Label = "";
        public float Needle;
        public int StatesRead;
    }

    public sealed class BeaconType : SharedType
    {
        public override string Name => "beacon";
        public override bool HasState => true;
        public override float StateHz => 10f;
        public override float StateRange => Probe.StateRangeM;
        public override float ViewRange => Probe.ViewRangeM;

        public override GameObject Create(RecordReader record, Vector3 position, Quaternion rotation)
        {
            string label = record.String();
            if (record.Bad) return null;
            return Probe.Make(label, position);
        }

        public override void Write(GameObject obj, RecordWriter record) => record.String(obj.GetComponent<Beacon>().Label);
        public override void Read(GameObject obj, RecordReader record) { var l = record.String(); if (!record.Bad) obj.GetComponent<Beacon>().Label = l; }
        public override void WriteState(GameObject obj, RecordWriter state) => state.Float(obj.GetComponent<Beacon>().Needle);

        public override void ReadState(GameObject obj, RecordReader state)
        {
            float v = state.Float();
            if (state.Bad) return;
            var b = obj.GetComponent<Beacon>();
            b.Needle = v; b.StatesRead++;
        }

        public override void OnPlaced(SharedObject obj) => Probe.Placed++;

        public override void OnStored(SharedObject obj)
        {
            Probe.Stored++;
            base.OnStored(obj);
        }
    }

    public static class Probe
    {
        public static ModInfo Mod;
        public static BeaconType Type;
        public static SessionSetting RuleSetting;
        public static float ViewRangeM = 800f, StateRangeM = 300f;
        public static string Rule = "easy";
        public static int Placed, Stored, RuleApplied;

        internal static GameObject Make(string label, Vector3 pos)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "ApiProbeBeacon " + label;
            go.transform.position = pos;
            go.AddComponent<Beacon>().Label = label;
            return go;
        }

        /// A beacon dx, dz metres from this player, shared and owned here.
        public static string Spawn(string label, float dx, float dz)
        {
            var p = mainscript.s.player.transform.position;
            var go = Make(label, p + new Vector3(dx, 1.5f, dz));
            go.AddComponent<visszarako>();   // moves with this machine's floating origin, as the session does for copies
            MpApi.Share(Type, go);
            return label;
        }

        private static Beacon Find(string label, out SharedObject so)
        {
            foreach (var o in MpApi.Objects)
                if (o.Type == Type && o.GameObject != null && o.GameObject.GetComponent<Beacon>().Label == label) { so = o; return o.GameObject.GetComponent<Beacon>(); }
            so = null; return null;
        }

        /// The owner moves the needle (copies near get it as a state record).
        public static string SetNeedle(string label, float v)
        {
            var b = Find(label, out var so);
            if (b == null) return "no " + label + " here";
            if (!so.IsMine) return label + " is not mine (owner " + so.Owner + ")";
            b.Needle = v;
            return "ok";
        }

        public static string SetRule(string v) { Rule = v; return Rule; }

        public static string Status()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"beacons\":[");
            bool first = true;
            foreach (var o in MpApi.Objects)
            {
                if (o.Type != Type || o.GameObject == null) continue;
                var b = o.GameObject.GetComponent<Beacon>();
                if (!first) sb.Append(','); first = false;
                sb.Append(string.Format(ci, "{{\"label\":\"{0}\",\"id\":{1},\"owner\":{2},\"mine\":{3},\"needle\":{4:F2},\"statesRead\":{5}}}",
                    b.Label, o.Id, o.Owner, o.IsMine ? "true" : "false", b.Needle, b.StatesRead));
            }
            sb.Append(string.Format(ci, "],\"placed\":{0},\"stored\":{1},\"rule\":\"{2}\",\"ruleFromHost\":{3},\"ruleApplied\":{4}}}",
                Placed, Stored, Rule, RuleSetting != null && RuleSetting.FromHost ? "true" : "false", RuleApplied));
            return sb.ToString();
        }
    }
}
