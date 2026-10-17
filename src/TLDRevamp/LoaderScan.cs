using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace TLDRevamp
{
    /// The TLD Loader mod scanner (docs/MODDING-MULTIPLAYER.md §7): reads a Loader mod's code (its IL, never running it)
    /// and reports what matters in a multiplayer session, in plain words, with a starting point for its author:
    ///  - objects it creates from its own assets (only on the machine that made them, until registered: §3)
    ///  - components it adds to objects (their state stays on one machine: records, §4)
    ///  - input it reacts to (runs only for the player who pressed: edits or events, §5)
    ///  - game state it writes directly (one machine's copy changes; shared only where TLD Revamp shares it already)
    ///  - world-wide values (time scale, gravity) and random numbers (the same on every machine only if chosen once)
    ///  - its own files (the host's save doesn't carry them)
    ///  - drawing only (OnGUI, cosmetic): works as is, on each machine for itself
    /// Writes <mod>.md (the report) and <mod>.MpSkeleton.cs (registration, a SharedType per component it adds, a
    /// setting per world-wide value, an event per input handler) to BepInEx/TLDRevampModScan. Never changes the mod.
    /// Runs from the config ([Mods] ScanLoaderMods = true: every Loader mod at startup) or the bridge (`modscan [dll]`).
    public static class LoaderScan
    {
        public static string OutDir => Path.Combine(BepInEx.Paths.BepInExRootPath, "TLDRevampModScan");

        public enum Kind { Creates, AddsComponent, Input, WritesGame, WorldWide, Random, OwnFiles, DrawOnly, GameSpawn }

        public sealed class Finding
        {
            public Kind Kind; public string What, Where;
            public override string ToString() => $"{Kind}: {What} ({Where})";
        }

        public sealed class Report
        {
            public string File, Name = "", Id = "", Version = "";
            public readonly List<Finding> Findings = new List<Finding>();
            public readonly HashSet<string> AddedComponents = new HashSet<string>();
            public readonly HashSet<string> InputMethods = new HashSet<string>();
            public readonly HashSet<string> WorldWide = new HashSet<string>();
            public int Methods, Unreadable;
            public string Error;
            internal Assembly Asm;
            public bool UsesApi;
        }

        private static Dictionary<short, OpCode> _ops;
        private static bool _uiHere, _itemHere;
        private static void InitOps()
        {
            if (_ops != null) return;
            _ops = new Dictionary<short, OpCode>();
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is OpCode op) _ops[op.Value] = op;
        }

        private static readonly string[] CreateCalls = { "UnityEngine.Object.Instantiate", "UnityEngine.GameObject..ctor", "UnityEngine.GameObject.CreatePrimitive", "UnityEngine.AssetBundle.LoadFromFile",
            "UnityEngine.AssetBundle.LoadFromMemory", "UnityEngine.AssetBundle.LoadAsset", "UnityEngine.AssetBundle.LoadFromStream", "UnityEngine.Resources.Load" };
        private static readonly string[] InputCalls = { "UnityEngine.Input.GetKey", "UnityEngine.Input.GetKeyDown", "UnityEngine.Input.GetKeyUp", "UnityEngine.Input.GetButton",
            "UnityEngine.Input.GetButtonDown", "UnityEngine.Input.GetButtonUp", "UnityEngine.Input.GetMouseButton", "UnityEngine.Input.GetMouseButtonDown", "UnityEngine.Input.GetMouseButtonUp",
            "UnityEngine.Input.GetAxis", "UnityEngine.Input.GetAxisRaw", "UnityEngine.Input.get_anyKeyDown" };
        private static readonly string[] WorldWideSets = { "UnityEngine.Time.set_timeScale", "UnityEngine.Time.set_fixedDeltaTime", "UnityEngine.Physics.set_gravity", "UnityEngine.RenderSettings.set_" };
        private static readonly string[] RandomCalls = { "UnityEngine.Random.", "System.Random." };
        private static readonly string[] FileCalls = { "System.IO.File.Write", "System.IO.File.AppendAll", "System.IO.File.Create", "System.IO.StreamWriter..ctor", "System.IO.File.Open",
            "UnityEngine.PlayerPrefs.Set" };
        private static readonly string[] DrawCalls = { "UnityEngine.GUI.", "UnityEngine.GUILayout.", "UnityEngine.Debug.Draw" };
        // the game's own ways to make things it saves (items, cars): such objects are shared by TLD Revamp already
        private static readonly string[] GameSpawnCalls = { "itemdatabase.", "spawnscript.", "mainscript.Spawn", "savedatascript." };

        /// Scan one DLL (loaded for reading; its code isn't run).
        public static Report Scan(string dll)
        {
            InitOps();
            var rep = new Report { File = Path.GetFileName(dll) };
            Assembly asm;
            try { asm = Assembly.LoadFrom(dll); }
            catch (Exception e) { rep.Error = "could not load: " + e.Message; return rep; }
            rep.Asm = asm;
            try { rep.UsesApi = asm.GetReferencedAssemblies().Any(n => n.Name == "TLDRevamp.Multiplayer"); } catch { }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); rep.Error = "some types could not load: " + e.LoaderExceptions.FirstOrDefault()?.Message; }
            var mod = types.FirstOrDefault(t => !t.IsAbstract && t.BaseType != null && t.BaseType.FullName == "TLDLoader.Mod");
            if (mod != null)
            {
                rep.Name = ConstString(mod, "Name") ?? mod.Name; rep.Id = ConstString(mod, "ID") ?? mod.Name; rep.Version = ConstString(mod, "Version") ?? "";
            }
            if (mod == null)   // a BepInEx plugin: its [BepInPlugin(guid, name, version)], read without running anything
                foreach (var t in types)
                {
                    try
                    {
                        foreach (var ca in t.GetCustomAttributesData())
                            if (ca.AttributeType.Name == "BepInPlugin" && ca.ConstructorArguments.Count >= 3)
                            { rep.Id = ca.ConstructorArguments[0].Value as string ?? ""; rep.Name = ca.ConstructorArguments[1].Value as string ?? ""; rep.Version = ca.ConstructorArguments[2].Value as string ?? ""; }
                    }
                    catch { }
                    if (rep.Name != "") break;
                }
            if (rep.Id == "") rep.Id = Path.GetFileNameWithoutExtension(dll);
            if (rep.Name == "") rep.Name = Path.GetFileNameWithoutExtension(dll);
            var seen = new HashSet<string>();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                IEnumerable<MethodBase> methods;
                try { methods = t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)); } catch { continue; }
                foreach (var m in methods)
                {
                    byte[] il;
                    try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { rep.Unreadable++; continue; }
                    if (il == null) continue;
                    rep.Methods++;
                    string where = t.Name + "." + m.Name;
                    if (m.Name == "OnGUI") Add(rep, seen, Kind.DrawOnly, "draws on screen (OnGUI)", where);
                    int before = rep.Findings.Count;
                    _uiHere = false; _itemHere = false;
                    ScanIl(rep, seen, m, il, where);
                    // a method that builds screen UI (it adds UnityEngine.UI components): the objects it makes are drawing
                    if (_uiHere)
                        for (int k = before; k < rep.Findings.Count; k++)
                            if (rep.Findings[k].Kind == Kind.Creates) { rep.Findings[k].Kind = Kind.DrawOnly; rep.Findings[k].What = "screen UI (" + rep.Findings[k].What + ")"; }
                    // a method that makes a game item (a save script on it, or the item database): the game saves it, so
                    // TLD Revamp shares it like the game's own (the Car Trailer example)
                    if (_itemHere)
                        for (int k = before; k < rep.Findings.Count; k++)
                            if (rep.Findings[k].Kind == Kind.Creates || rep.Findings[k].Kind == Kind.AddsComponent || rep.Findings[k].Kind == Kind.WritesGame && rep.Findings[k].What.StartsWith("tosaveitemscript."))
                            { rep.Findings[k].What = "a game item: " + rep.Findings[k].What; rep.Findings[k].Kind = Kind.GameSpawn; }
                }
            }
            return rep;
        }

        private static string ConstString(Type t, string prop)
        {
            // TLD Loader mods override ID/Name/Version as getters returning a constant: read it from the IL (ldstr; ret)
            try
            {
                var g = t.GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                var il = g?.GetMethodBody()?.GetILAsByteArray();
                if (il != null && il.Length >= 5 && il[0] == 0x72) return g.Module.ResolveString(BitConverter.ToInt32(il, 1));
            }
            catch { }
            return null;
        }

        private static void ScanIl(Report rep, HashSet<string> seen, MethodBase m, byte[] il, string where)
        {
            var mod = m.Module;
            Type[] targs = null, margs = null;
            try { targs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null; margs = m.IsGenericMethod ? m.GetGenericArguments() : null; } catch { }
            int i = 0;
            while (i < il.Length)
            {
                short v = il[i++];
                if (v == 0xFE && i < il.Length) v = (short)(0xFE00 | il[i++]);
                if (!_ops.TryGetValue(v, out var op)) return;   // not IL we know: stop reading this method
                int size = OperandSize(op, il, i);
                if (op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineField || op.OperandType == OperandType.InlineTok)
                {
                    int tok = BitConverter.ToInt32(il, i);
                    try
                    {
                        var mem = mod.ResolveMember(tok, targs, margs);
                        if (mem is MethodBase callee) Call(rep, seen, callee, where);
                        else if (mem is FieldInfo f && (op.Value == OpCodes.Stfld.Value || op.Value == OpCodes.Stsfld.Value)) Field(rep, seen, f, where);
                    }
                    catch { }
                }
                i += size;
            }
        }

        private static void Call(Report rep, HashSet<string> seen, MethodBase callee, string where)
        {
            var dt = callee.DeclaringType;
            if (dt == null) return;
            if (IsGame(dt) && (dt.Name == "itemdatabase" || dt.Name == "tosaveitemscript" || dt.Name == "savedatascript")) _itemHere = true;
            if (callee.IsGenericMethod && callee.Name == "AddComponent" && callee.GetGenericArguments()[0].Name == "tosaveitemscript") _itemHere = true;
            string full = (dt.FullName ?? dt.Name) + "." + callee.Name;
            if (CreateCalls.Any(c => full.StartsWith(c, StringComparison.Ordinal))) Add(rep, seen, Kind.Creates, Short(full), where);
            else if (InputCalls.Any(c => full == c)) { Add(rep, seen, Kind.Input, Short(full), where); rep.InputMethods.Add(where); }
            else if (WorldWideSets.Any(c => full.StartsWith(c, StringComparison.Ordinal))) { Add(rep, seen, Kind.WorldWide, Short(full).Replace("set_", ""), where); rep.WorldWide.Add(Short(full).Replace("set_", "")); }
            else if (RandomCalls.Any(c => full.StartsWith(c, StringComparison.Ordinal)) && callee.Name != "InitState") Add(rep, seen, Kind.Random, Short(full), where);
            else if (FileCalls.Any(c => full.StartsWith(c, StringComparison.Ordinal))) Add(rep, seen, Kind.OwnFiles, Short(full), where);
            else if (GameSpawnCalls.Any(c => full.StartsWith(c, StringComparison.Ordinal)) && IsGame(dt)) Add(rep, seen, Kind.GameSpawn, Short(full), where);
            else if (full.StartsWith("UnityEngine.GameObject.AddComponent", StringComparison.Ordinal) || full.StartsWith("UnityEngine.Component.AddComponent", StringComparison.Ordinal))
            {
                string comp = callee.IsGenericMethod ? callee.GetGenericArguments()[0].Name : "(a type chosen at run time)";
                if (callee.IsGenericMethod && callee.GetGenericArguments()[0].Assembly.GetName().Name == "UnityEngine.UI") { _uiHere = true; Add(rep, seen, Kind.DrawOnly, "screen UI (" + comp + ")", where); return; }
                Add(rep, seen, Kind.AddsComponent, comp, where);
                var ct = callee.IsGenericMethod ? callee.GetGenericArguments()[0] : null;
                if (ct != null && ct.Assembly == rep.Asm) rep.AddedComponents.Add(ct.Name);   // the mod's own components: theirs to describe
            }
            else if (callee.Name.StartsWith("set_", StringComparison.Ordinal) && IsGame(dt)) Add(rep, seen, Kind.WritesGame, dt.Name + "." + callee.Name.Substring(4), where);
            else if (DrawCalls.Any(c => full.StartsWith(c, StringComparison.Ordinal))) Add(rep, seen, Kind.DrawOnly, Short(full), where);
        }

        private static void Field(Report rep, HashSet<string> seen, FieldInfo f, string where)
        {
            var dt = f.DeclaringType;
            if (dt != null && IsGame(dt) && (dt.Name == "tosaveitemscript" || dt.Name == "itemdatabase")) _itemHere = true;
            if (dt != null && IsGame(dt)) Add(rep, seen, Kind.WritesGame, dt.Name + "." + f.Name, where);
        }

        // the game's own code (Assembly-CSharp): what it saves, TLD Revamp shares
        private static bool IsGame(Type t) => t.Assembly.GetName().Name == "Assembly-CSharp";

        private static string Short(string full) => full.Replace("UnityEngine.", "").Replace("System.IO.", "").Replace("System.", "");

        private static void Add(Report rep, HashSet<string> seen, Kind k, string what, string where)
        {
            if (!seen.Add(k + "|" + what + "|" + where)) return;
            rep.Findings.Add(new Finding { Kind = k, What = what, Where = where });
        }

        private static int OperandSize(OpCode op, byte[] il, int i)
        {
            switch (op.OperandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8: case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, i);
                default: return 4;
            }
        }

        // ------------------------------------------------------------------ the report and the skeleton

        private static readonly Dictionary<Kind, (string head, string verdict)> Words = new Dictionary<Kind, (string, string)>
        {
            [Kind.Creates] = ("Makes its own objects", "Needs registration (§3): until then they exist only on the machine that made them."),
            [Kind.GameSpawn] = ("Makes the game's own things", "Shared already: TLD Revamp shares what the game saves (items, cars)."),
            [Kind.AddsComponent] = ("Adds components to objects", "Their fields stay on one machine: put what others must see in a record (§4)."),
            [Kind.Input] = ("Reacts to keys or buttons", "Runs only for the player who pressed: send an edit to the owner or an event (§5)."),
            [Kind.WritesGame] = ("Writes the game's state directly", "Changes this machine's copy. Shared where TLD Revamp shares that state already (a car, an item, the player's own body); anything else stays local."),
            [Kind.WorldWide] = ("Changes world-wide values", "Must match for everyone: make it a session-wide setting (§6)."),
            [Kind.Random] = ("Rolls random numbers", "Each machine rolls its own: let the owner choose and send the result."),
            [Kind.OwnFiles] = ("Writes its own files", "The host's save doesn't carry them: what joiners need goes in records."),
            [Kind.DrawOnly] = ("Draws on screen", "Works as is: each player sees their own."),
        };

        private static readonly Kind[] Order = { Kind.Creates, Kind.AddsComponent, Kind.Input, Kind.WorldWide, Kind.Random, Kind.WritesGame, Kind.OwnFiles, Kind.GameSpawn, Kind.DrawOnly };

        public static string Markdown(Report r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {r.Name} {r.Version} ({r.File}) in multiplayer");
            sb.AppendLine();
            sb.AppendLine($"TLD Revamp read {r.Methods} methods of this TLD Loader mod (docs/MODDING-MULTIPLAYER.md §7). It changed nothing.");
            if (r.Error != null) sb.AppendLine().AppendLine("Note: " + r.Error);
            sb.AppendLine();
            var local = r.Findings.Any(f => f.Kind == Kind.Creates || f.Kind == Kind.AddsComponent || f.Kind == Kind.Input || f.Kind == Kind.WorldWide || f.Kind == Kind.Random);
            if (r.UsesApi) sb.AppendLine("It uses the multiplayer API already (it references TLDRevamp.Multiplayer.dll): what it registers is shared; the list below is what the scan found in its code either way.").AppendLine();
            sb.AppendLine(local
                ? "**In a session:** parts of it run on one machine only until it uses the multiplayer API (TLDRevamp.Multiplayer.dll). The sections below say which, and the skeleton next to this report is a starting point."
                : "**In a session:** nothing found that needs the multiplayer API. It should work as is, on each machine for itself.");
            foreach (var k in Order)
            {
                var fs = r.Findings.Where(f => f.Kind == k).ToList();
                if (fs.Count == 0) continue;
                sb.AppendLine().AppendLine($"## {Words[k].head}").AppendLine().AppendLine(Words[k].verdict).AppendLine();
                foreach (var g in fs.GroupBy(f => f.What).Take(40))
                    sb.AppendLine($"- `{g.Key}` in {string.Join(", ", g.Select(f => "`" + f.Where + "`").Distinct().Take(6))}{(g.Count() > 6 ? $" and {g.Count() - 6} more" : "")}");
            }
            return sb.ToString();
        }

        public static string Skeleton(Report r)
        {
            string ns = Safe(r.Id) + "Mp";
            var sb = new StringBuilder();
            sb.AppendLine($"// A starting point for {r.Name} in multiplayer, written by TLD Revamp's mod scanner. Fill it in; nothing here runs");
            sb.AppendLine("// until the mod calls Register() (from OnLoad). Reference TLDRevamp.Multiplayer.dll. See docs/MODDING-MULTIPLAYER.md.");
            sb.AppendLine("using TLDRevamp.Multiplayer;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    public static class Mp");
            sb.AppendLine("    {");
            sb.AppendLine("        public static ModInfo Mod;");
            foreach (var c in r.AddedComponents) sb.AppendLine($"        public static {Safe(c)}Type {Safe(c)};");
            sb.AppendLine();
            sb.AppendLine("        public static void Register()");
            sb.AppendLine("        {");
            sb.AppendLine($"            Mod = MpApi.RegisterMod(\"{Safe(r.Id).ToLowerInvariant()}\", \"{r.Name.Replace("\"", "")}\", \"{r.Version.Replace("\"", "")}\");");
            foreach (var c in r.AddedComponents) sb.AppendLine($"            {Safe(c)} = MpApi.RegisterType(Mod, new {Safe(c)}Type());");
            foreach (var w in r.WorldWide)
                sb.AppendLine($"            MpApi.RegisterSetting(Mod, \"{Safe(w)}\", () => /* read {w} */ \"\", v => {{ /* set {w} from v */ }});");
            foreach (var m in r.InputMethods)
                sb.AppendLine($"            MpApi.OnEvent(Mod, \"{Safe(m)}\", e => {{ /* what {m} does when a key is pressed, for the player e.From */ }});   // and in {m}: MpApi.SendEvent(Mod, \"{Safe(m)}\", w => {{ }})");
            if (r.Findings.Any(f => f.Kind == Kind.Creates))
                sb.AppendLine("            // objects the mod makes from its own assets: a SharedType each (like the ones below), then MpApi.Share(type, go) when it makes one");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            foreach (var c in r.AddedComponents)
            {
                sb.AppendLine();
                sb.AppendLine($"    /// {c}: what other players must see of it goes in the record.");
                sb.AppendLine($"    public sealed class {Safe(c)}Type : SharedType");
                sb.AppendLine("    {");
                sb.AppendLine($"        public override string Name => \"{Safe(c).ToLowerInvariant()}\";");
                sb.AppendLine("        public override GameObject Create(RecordReader record, Vector3 position, Quaternion rotation) { /* build it, then Read */ return null; }");
                sb.AppendLine("        public override void Write(GameObject obj, RecordWriter record) { /* its fields, in order */ }");
                sb.AppendLine("        public override void Read(GameObject obj, RecordReader record) { /* the same fields, same order */ }");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        /// Scan and write the report and skeleton. Returns a JSON summary (bridge `modscan`).
        public static string ScanAndWrite(string dll)
        {
            var r = Scan(dll);
            Directory.CreateDirectory(OutDir);
            string baseName = Path.Combine(OutDir, Path.GetFileNameWithoutExtension(dll));
            File.WriteAllText(baseName + ".md", Markdown(r));
            File.WriteAllText(baseName + ".MpSkeleton.cs", Skeleton(r));
            Plugin.Log.LogInfo($"modscan: {r.File}: {r.Findings.Count} finding(s) in {r.Methods} methods -> {baseName}.md");
            var counts = Order.Select(k => $"\"{k}\":{r.Findings.Count(f => f.Kind == k)}");
            return "{\"file\":" + Json.Str(r.File) + ",\"name\":" + Json.Str(r.Name) + ",\"methods\":" + r.Methods + ",\"findings\":" + r.Findings.Count +
                   ",\"kinds\":{" + string.Join(",", counts) + "},\"components\":" + Json.Str(string.Join(" ", r.AddedComponents)) +
                   ",\"error\":" + Json.Str(r.Error ?? "") + ",\"report\":" + Json.Str(baseName + ".md") + "}";
        }

        /// Every DLL in BepInEx/TLDLoaderMods.
        public static string ScanAll()
        {
            if (!Directory.Exists(ModHost.ModsDir)) return "{\"scanned\":[]}";
            var parts = new List<string>();
            foreach (var dll in Directory.GetFiles(ModHost.ModsDir, "*.dll"))
            {
                try { parts.Add(ScanAndWrite(dll)); }
                catch (Exception e) { Plugin.Log.LogWarning("modscan: " + Path.GetFileName(dll) + ": " + e.Message); }
            }
            return "{\"scanned\":[" + string.Join(",", parts) + "]}";
        }
    }
}
