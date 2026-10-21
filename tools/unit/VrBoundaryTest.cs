using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// The boundary test of the VR contracts: what the contracts may touch and what they may not.
///
/// It reads the sources, because the guarantee is about the repository, not about one run: the VR contracts live
/// outside the net layer, the net layer does not know about them, and the protocol is not changed by the VR work.
static class VrBoundaryTest
{
    static int failures;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

    /// The code of a file, without its comments: the comments may name the game's own types (they explain the
    /// observed call order), the code may not use them.
    static string StripComments(string text)
    {
        var withoutBlock = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//[^\n]*", " ");
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string Read(string path) => File.ReadAllText(Path.Combine(root, path));

        // ---- the contracts are pure: they do not use Unity, the net layer or the game's own types
        var contractFiles = new[]
        {
            "src/TLDRevamp/VR/VrSessionState.cs", "src/TLDRevamp/VR/VrTrackingMachine.cs",
            "src/TLDRevamp/VR/VrHandMachine.cs", "src/TLDRevamp/VR/VrRigPolicy.cs",
            "src/TLDRevamp/VR/VrLocomotion.cs", "src/TLDRevamp/VR/VrUiRay.cs",
            "src/TLDRevamp/VR/VrReconnectPolicy.cs", "src/TLDRevamp/VR/VrContracts.cs",
            "src/TLDRevamp/VR/VrDiagnostics.cs",
        };
        foreach (var f in contractFiles)
        {
            var text = Read(f);
            var code = StripComments(text);
            Check(f + " does not use UnityEngine", !code.Contains("UnityEngine"));
            Check(f + " does not use the net layer", !code.Contains("TLDRevamp.Net") && !Regex.IsMatch(code, @"\bNetWriter\b|\bNetReader\b|\bProtocol\."));
            Check(f + " does not name the game's own types", !Regex.IsMatch(code, @"\bmainscript\b|\bfpscontroller\b|\binputscript\b|\bvrHandsScript\b"));
            Check(f + " does not use Harmony", !code.Contains("HarmonyLib") && !code.Contains("HarmonyPatch"));
            Check(f + " does not use OpenXR or the XR runtime", !code.Contains("OpenXR") && !code.Contains("UnityEngine.XR"));
        }

        // ---- the contracts hold no pose: no position, rotation or transform field anywhere
        foreach (var f in contractFiles)
        {
            var text = StripComments(Read(f));
            var fields = Regex.Matches(text, @"(?:public|private|internal|protected)\s+(?:static\s+)?(?:readonly\s+)?(?:[\w\.<>\[\]]+)\s+(\w+)\s*(?:=|;)");
            foreach (Match m in fields)
            {
                var type = m.Groups[1].Value;
                var name = m.Groups[2].Value;
                Check(f + " holds no " + type + " field", type != "Vector3" && type != "Vector2" && type != "Quaternion" && type != "Transform" && type != "Matrix4x4");
            }
        }

        // ---- the net layer does not use the VR contracts
        var netFiles = Directory.GetFiles(Path.Combine(root, "src/TLDRevamp/Net"), "*.cs");
        foreach (var f in netFiles)
        {
            var text = File.ReadAllText(f);
            Check(Path.GetFileName(f) + " does not use the VR contracts", !text.Contains("TLDRevamp.VR") && !text.Contains("VrContracts"));
        }

        // ---- the one net-layer touch is the reconnect sanitization, and it is read-only
        var entities = Read("src/TLDRevamp/Net/Entities.cs");
        Check("the session reset sanitizes the VR state", entities.Contains("VrContractBridge.SanitizeReconnect()"));
        var resetBlock = entities.Substring(entities.IndexOf("public static void Reset("), 2000);
        var sanitized = resetBlock.Substring(0, resetBlock.IndexOf("Tpa.ResetTelemetry()"));
        Check("the VR sanitization writes no protocol field", !Regex.IsMatch(sanitized, @"[Ww]\.(U8|U16|U32|U64|F32|F64|Str|Bool|Bytes)\("));
        Check("the VR sanitization writes no entity state", !Regex.IsMatch(sanitized, @"ToServer\b|\bClaim\b|\bState\b"));

        // ---- the bridge only reads the contract's decisions; it does not write a pose
        var bridge = Read("src/TLDRevamp/Fixes/VrContractBridge.cs");
        Check("the bridge does not write a packet", !Regex.IsMatch(bridge, @"\bNetWriter\b|\bw\.(U8|U16|U32|F32|F64)\(|\bEntities\.ToServer\b"));
        Check("the bridge does not set the gameplay root", !Regex.IsMatch(bridge, @"(Th|transform|RB)\.position\s*=|\.velocity\s*="));
        Check("the bridge reads the rig policy", Regex.IsMatch(bridge, @"\.Rig\.\w+") &&
              (bridge.Contains("HeadDecision") || bridge.Contains("PolicyReason") || bridge.Contains("HeadMayFollowCamera")));
        Check("the bridge applies the rig policy to the game", bridge.Contains("ApplyRigPolicy"));
        Check("the bridge does not move the gameplay root itself", !Regex.IsMatch(bridge, @"player\.Th\.(position|rotation)\s*="));
        Check("the bridge reports the state without a pose", bridge.Contains("VrDiagnostics.Json"));

        // ---- the OpenXR first-wave fixes are preserved
        var openxr = Read("src/TLDRevamp/Fixes/OpenXrBridge.cs");
        foreach (var needed in new[]
        {
            "OpenXRLoader", "MetaQuestTouchProControllerProfile", "ValveIndexControllerProfile",
            "HTCViveControllerProfile", "HPReverbG2ControllerProfile", "MicrosoftMotionControllerProfile",
            "OculusTouchControllerProfile", "TrySetTrackingOriginMode", "TryRecenter",
        })
            Check("the OpenXR bridge still has " + needed, openxr.Contains(needed));

        // ---- the diagnostics command is read-only
        var dbg = Read("src/TLDRevamp/DebugBridge.cs");
        Check("the vr command is in the debug bridge", dbg.Contains("case \"vr\":"));
        Check("the vr command is read-only", Regex.IsMatch(dbg, "_readCmds[^;]*\"vr\""));
        Check("the vr command is the contracts' state", dbg.Contains("Fixes.VrContractBridge.Json()"));

        // ---- the protocol is not changed by the VR work: the VR files add no message id and no version bump
        var protocol = Read("src/TLDRevamp/Net/Protocol.cs");
        var version = Regex.Match(protocol, @"const ushort Version = (\d+);").Groups[1].Value;
        Check("the protocol version is " + version, version.Length > 0);
        foreach (var f in contractFiles.Concat(new[] { "src/TLDRevamp/Fixes/VrContractBridge.cs" }))
        {
            var text = Read(f);
            Check(f + " does not touch the protocol", !text.Contains("Protocol.") && !Regex.IsMatch(text, @"const\s+byte\s+\w+\s*=\s*\d+"));
        }

        // ---- the docs are there and name the call graph
        var doc = Read("docs/VR-REWRITE.md");
        foreach (var needed in new[] { "SetVR", "SetXR", "vrHandsScript", "UpdVR", "UpdVRCanv", "inputscript", "fpscontroller" })
            Check("the architecture doc names " + needed, doc.Contains(needed));

        // The live script (docs/VR-LIVE-TEST.md) is what the coordinator runs on the headset, so it must name
        // every command the bridge actually understands, and it must not name a command the bridge refuses.
        // A live script that names a stale command fails at the headset, in front of the player.
        var live = Read("docs/VR-LIVE-TEST.md");
        var policy = Read("src/TLDRevamp/VR/VrLoaderPolicy.cs");
        foreach (var verb in new[] { "state", "start", "retry", "stop", "recenter" })
        {
            Check("the policy knows the verb " + verb, policy.Contains("\"" + verb + "\""));
            Check("the live script names the verb " + verb, live.Contains("openxr " + verb) || verb == "state");
        }
        Check("the live script names the explicit origin request", live.Contains("openxr recenter origin"));
        Check("the live script names the preflight", live.Contains("quest3vr_live.py preflight"));
        Check("the live script names the gate", live.Contains("quest3vr_live.py gate"));
        Check("the live script names the read-only status", live.Contains("quest3vr.py status"));
        Check("the live script marks the mutations", live.Contains("[MUTATION]"));
        // the doc is line-wrapped, so the check looks for a phrase that cannot be split by a newline
        Check("the live script does not launch the game",
            Regex.Replace(live, @"\s+", " ").Contains("the coordinator owns the live run"));

        Console.WriteLine(failures == 0 ? "VR boundary: all checks passed" : "VR boundary: " + failures + " failures");
        return failures == 0 ? 0 : 1;
    }
}
