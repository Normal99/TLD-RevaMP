#!/usr/bin/env python3
"""The negative proof for the settings lane: each fix must be CAUGHT by the tests.

Every mutation breaks ONE property of the production source in a scratch COPY of the tree (the repository is
never touched), then runs the settings test suite there. A mutation the suite does NOT catch is a hole in the
suite.

The suite is three projects:
  SettingsFeatureSourceTest  the source-level properties (any tree, including the pre-fix one)
  SettingsFeatureTest        the behavioural properties (the real console's sinks, the one session path)
  SettingsGateTest           the gate's rule, driven as code

  python3 tools/test_settingsfeature_mutations.py
"""
import os, re, shutil, subprocess, sys, tempfile, json

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
COPY_DIRS = ["src", "tools", "lib"]
PROJECTS = [
    "tools/unit/SettingsFeatureSourceTest.csproj",
    "tools/unit/SettingsFeatureTest.csproj",
    "tools/unit/SettingsGateTest.csproj",
    "tools/unit/SettingsLaneAuditTest.csproj",
]

MUTATIONS = [
 {
  "id": "M4-console-echo-not-redacted",
  "desc": "the admin console's file echo writes the raw command line (the password reaches console.txt and the log)",
  "file": "src/TLDRevamp/Net/Admin.cs",
  "old": 'Out("> " + Net.SessionSettingsRules.RedactCommand(line, Mp.Server?.Password));',
  "new": 'Out("> " + line);',
  "must_fail": ["S-P0.1", "P0.1", "P0.2", "P0.3", "P0.4", "P0.5"],
 },
 {
  "id": "M13-policy-exemption-not-host-only",
  "desc": "the policy command is exempt from the gate on any machine, so a joiner gets a different signal",
  "file": "src/TLDRevamp/Net/SessionSettingsData.cs",
  "old": """        public static bool IsHostControlCommand(bool hosting, string cmd, string arg)
        {
            if (!hosting) return false;""",
  "new": """        public static bool IsHostControlCommand(bool hosting, string cmd, string arg)
        {
            if (false) return false;""",
  "must_fail": ["G.1", "G.2", "G.3", "S-P1.13"],
 },
 {
  "id": "M13b-bridge-gate-spelled-out-again",
  "desc": "the bridge decides the exemption itself again, where it can drift from the rule",
  "file": "src/TLDRevamp/DebugBridge.cs",
  "old": """            if (!Net.SessionSettingsPolicy.BridgeAllowed(Net.Mp.Server != null, Net.DeveloperAccess.Allowed, ReadOnlyCmd(cmd, arg), cmd, arg))
            {
                Net.DeveloperAccess.DenyBridge();
                return Net.SessionSettingsPolicy.PolicyDenied;
            }""",
  "new": """            bool hostControl = cmd == "mp" && (arg == "policy" || arg.StartsWith("policy "));
            if (!ReadOnlyCmd(cmd, arg) && !hostControl && !Net.DeveloperAccess.Allowed)
            {
                Net.DeveloperAccess.DenyBridge();
                return "{\"error\":\"developer access is disabled by the host's session policy\"}";
            }""",
  "must_fail": ["S-P1.10", "S-P1.11", "S-P1.12", "S-P1.15"],
 },
 {
  "id": "M6-dedicated-server-second-password-path",
  "desc": "the dedicated server assigns the password field directly, bypassing the session path",
  "file": "src/TLDRevamp/Net/DedicatedServer.cs",
  "old": "                Mp.Server.SetSessionPassword(Password, persistDefault: false);",
  "new": "                Mp.Server.Password = Password;",
  "must_fail": ["S-P1.1", "S-P1.2"],
 },
 {
  "id": "M6b-dedicated-server-hand-edits-the-snapshot",
  "desc": "the dedicated server edits the snapshot by hand instead of through the session path",
  "file": "src/TLDRevamp/Net/DedicatedServer.cs",
  "old": "                Mp.Server.UpdateSessionSettings(s => { s.TpaEnabled = tpaOn; s.TpaCooldownS = tpaCooldownS; }, persistDefaults: false);",
  "new": """                Mp.Server.SessionSettings.TpaEnabled = tpaOn;
                Mp.Server.SessionSettings.TpaCooldownS = tpaCooldownS;
                Mp.Server.SessionSettings.HasPassword = Password.Length > 0;
                Mp.Server.SessionSettings.Revision++;""",
  "must_fail": ["S-P1.3", "S-P1.4", "S-P1.5"],
 },
 {
  "id": "M7-malformed-snapshot-silent",
  "desc": "a malformed snapshot in Welcome is dropped with no visible reason (the join hangs)",
  "file": "src/TLDRevamp/Net/Mp.cs",
  "old": """                        RejectReason = SessionSettingsPolicy.MalformedSnapshotReason();
                        RejectCode = Protocol.RejectOther;
                        Plugin.Log.LogWarning("join refused: " + RejectReason);
                        break;""",
  "new": "                    if (!SessionSettingsSnapshot.TryRead(r, out var hostSettings)) return;",
  "must_fail": ["S-P1.17", "S-P1.18", "S-P1.19", "S-P1.20"],
 },
 {
  "id": "M7b-malformed-reason-reworded",
  "desc": "the malformed-snapshot reason is reworded (the player-facing text of the integrated build must not drift)",
  "file": "src/TLDRevamp/Net/SessionSettingsData.cs",
  "old": """            return "the host's session settings could not be read \u2014 the host and this copy " +
                   "are not the same build; update the mod on both and join again";""",
  "new": """            return "The host sent a malformed session settings message (protocol 48).";""",
  "must_fail": ["G.17", "G.20c", "S-P1.18b"],
 },
 {
  "id": "M10-joiner-view-unreachable",
  "desc": "the settings label is put on the shared button behind a saved-player condition a joiner cannot satisfy",
  "file": "src/TLDRevamp/Net/MpScreen.cs",
  "old": "            if (inGame && _settingsButton != null) Show(_settingsButton.GetComponent<Button>(), true, SessionSettingsPolicy.SettingsButtonLabel(_settingsOpen));",
  "new": "            if (inGame && sel2?.Saved != null) Show(_secondary, true, _settingsOpen ? \"Close settings\" : \"Session settings\");",
  "must_fail": ["S-P1.26", "S-P1.27", "S-P1.28", "S-P1.30"],
 },
 {
  "id": "M11-rows-frozen-while-closed",
  "desc": "the row labels are only computed while the panel is open, so a closed panel is stale",
  "file": "src/TLDRevamp/Net/MpScreen.cs",
  "old": "            FillSettingRows();\n        }",
  "new": "            if (!showButton || !_settingsOpen) return;\n            FillSettingRows();\n        }",
  "must_fail": ["S-P1.32"],
 },
 {
  "id": "M11b-joiner-rows-not-refreshed-on-a-snapshot",
  "desc": "the joiner's rows are not refreshed when the host's snapshot arrives, so they stay stale",
  "file": "src/TLDRevamp/Net/Mp.cs",
  "old": """                        DeveloperAccess.ApplyClientSnapshot(updated, false);
                        // The panel's rows are filled on every refresh now, but the host may have changed a
                        // setting while this client's panel was closed: the rows are refreshed here so the
                        // panel never shows a stale value.
                        MpScreen.RefreshSessionRows();""",
  "new": """                        DeveloperAccess.ApplyClientSnapshot(updated, false);""",
  "must_fail": ["S-P1.36"],
 },
]


def copy_tree(dest):
    for d in COPY_DIRS:
        src = os.path.join(ROOT, d)
        dst = os.path.join(dest, d)
        if os.path.isdir(src):
            shutil.copytree(src, dst, ignore=shutil.ignore_patterns("bin", "obj", ".git"))
    shutil.copy(os.path.join(ROOT, "tools", "unit", "Directory.Build.props"),
                os.path.join(dest, "tools", "unit", "Directory.Build.props"))


def run_project(tree, project, name):
    """Run one unit project in the scratch tree.

    The per-project output paths come from tools/unit/Directory.Build.props. `--artifacts-path` is NOT used:
    it overrides that props file and writes a shared tree into the repository's tools/unit/bin, which the
    UnitIsolationTest then reads (and fails on).
    """
    r = subprocess.run(["dotnet", "run", "--project", project, "-c", "Release", "--disable-build-servers",
                        "--property:BaseIntermediateOutputPath=obj/%s/" % name,
                        "--property:OutputPath=bin/%s/" % name],
                       cwd=tree, capture_output=True, text=True, timeout=900)
    out = r.stdout + r.stderr
    failed = set(re.findall(r"^FAIL  (\S+)", out, re.M))
    return r.returncode != 0, failed, out


def run_suite(tree):
    failed = set()
    outs = []
    for project in PROJECTS:
        name = os.path.basename(project).replace(".csproj", "")
        ok, f, out = run_project(tree, project, name)
        failed |= f
        outs.append((name, ok, out))
    return failed, outs


def main():
    results = []
    for m in MUTATIONS:
        with tempfile.TemporaryDirectory(prefix="tld-accel-tree-") as tmp:
            copy_tree(tmp)
            path = os.path.join(tmp, m["file"])
            with open(path, encoding="utf-8") as f:
                text = f.read()
            if m["old"] not in text:
                results.append({"id": m["id"], "verdict": "ERROR: the mutation target is not in the file"})
                print(f"ERROR {m['id']}: the target text is not in {m['file']}")
                continue
            with open(path, "w", encoding="utf-8") as f:
                f.write(text.replace(m["old"], m["new"], 1))
            failed, outs = run_suite(tmp)
            missing = [c for c in m["must_fail"] if c not in failed]
            verdict = "CAUGHT" if (failed and not missing) else ("PARTIAL" if failed else "MISSED")
            results.append({"id": m["id"], "verdict": verdict, "failed_checks": sorted(failed),
                            "expected": m["must_fail"], "missing": missing})
            print(f"{verdict:8} {m['id']}: failed={sorted(failed)} missing={missing}")
            if verdict != "CAUGHT":
                for name, ok, out in outs:
                    if not ok:
                        print(f"--- {name} ---")
                        print(out[-4000:])
    print()
    print(json.dumps(results, indent=1))
    return 0 if all(r["verdict"] == "CAUGHT" for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
