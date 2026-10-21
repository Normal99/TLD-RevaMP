using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

// Offline check that every Harmony patch in the mod names a real type and a real method.
//
// A patch whose target does not resolve fails at load, in the game's own log, and nowhere else: the feature
// is silently dead and the mod carries on. That is exactly what happened to the three developer-access patches
// that wrote the type as a string ("HarmonyPatch("settingsscript", "LoadSettings")" resolves a type literally
// named ".settingsscript" and fails). This test reads the patch attributes out of the source and checks each
// target against the game's own assembly, so the mistake cannot be made again.
//
// The check is on the source text, not on the compiled attributes, so it needs no Harmony and no game.

static class PatchTargetTest
{
    static int checks, fails;

    static void Check(bool ok, string name)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("FAIL " + name); }
        else Console.WriteLine("PASS " + name);
    }

    /// The game's type for a name as the source writes it: "Outer.Inner" is a nested type, whose assembly name
    /// is "Outer+Inner".
    static Type FindType(Assembly asm, Dictionary<string, Type> types, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var direct = asm.GetType(name);
        if (direct != null) return direct;
        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            var nested = asm.GetType(name.Substring(0, dot) + "+" + name.Substring(dot + 1));
            if (nested != null) return nested;
        }
        return types.TryGetValue(name.Split('.').Last(), out var t) ? t : null;
    }

    /// A parameter type as the source writes it inside typeof(): the C# keyword for the built-in types.
    static string CSharpName(Type t)
    {
        switch (t.FullName)
        {
            case "System.String": return "string";
            case "System.Int32": return "int";
            case "System.UInt32": return "uint";
            case "System.Int16": return "short";
            case "System.UInt16": return "ushort";
            case "System.UInt64": return "ulong";
            case "System.SByte": return "sbyte";
            case "System.Char": return "char";
            case "System.Single": return "float";
            case "System.Double": return "double";
            case "System.Boolean": return "bool";
            case "System.Int64": return "long";
            case "System.Byte": return "byte";
            case "System.Object": return "object";
            default: return t.Name;
        }
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string src = Path.Combine(root, "src", "TLDRevamp");
        string game = Path.Combine(root, "lib", "Assembly-CSharp.dll");
        Check(File.Exists(game), "the game's assembly is in lib/");
        if (!File.Exists(game)) { Console.WriteLine($"{fails} of {checks} checks failed"); return 1; }

        var asm = Assembly.LoadFrom(game);
        // a name can appear twice in the assembly (different namespaces): the first wins, and the lookup by
        // the full name is tried first anyway
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var t in asm.GetTypes()) if (!types.ContainsKey(t.Name)) types[t.Name] = t;

        // every patch attribute in the tree
        var attrs = new List<(string file, string text)>();
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            attrs.Add((file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, '/'), File.ReadAllText(file)));

        int patchCount = 0;
        foreach (var (file, text) in attrs)
        {
            foreach (Match m in Regex.Matches(text, @"HarmonyPatch\(\s*(?<type>typeof\((?<tn>[A-Za-z0-9_.]+)\)|""(?<sn>[^""]+)"")\s*(?:,\s*(?:nameof\((?<nm>[A-Za-z0-9_.]+)\)|""(?<mn>[^""]+)"")\s*(?:,\s*(?<args>new\s*(?:Type)?\s*\[\s*\d*\s*\]\s*(?:\{[^}]*\})?|Type\.EmptyTypes)\s*)?)?\)"))
            {
                patchCount++;
                string typeName = m.Groups["tn"].Success ? m.Groups["tn"].Value : m.Groups["sn"].Value;
                bool isTypeof = m.Groups["tn"].Success;
                Check(isTypeof, file + ": the patch target is a Type, not a name: " + m.Value);

                // the type must exist in the game. A nested type is written "Outer.Inner" in C# and
                // "Outer+Inner" in the assembly's own names, so both are tried.
                var target = FindType(asm, types, typeName);
                Check(target != null, file + ": the patched type exists in the game: " + typeName);

                // and the method, when one is named, must exist on it
                string method = m.Groups["nm"].Success ? m.Groups["nm"].Value.Split('.').Last() : m.Groups["mn"].Value;
                if (!string.IsNullOrEmpty(method) && target != null)
                {
                    var named = target.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Where(x => x.Name == method).ToList();
                    bool found = named.Count > 0 || target.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .Any(x => x.Name == method);
                    Check(found, file + ": the patched method exists on " + typeName + ": " + method);
                    if (!m.Groups["args"].Success)
                    {
                        // Harmony resolves a name without argument types to ONE method; an overloaded name throws
                        // "Ambiguous match" at load and the whole patch class is dead (seen live: menuhandler.UpdMapName).
                        Check(named.Count <= 1, file + ": the patched method is not overloaded, or the patch names its argument types: " +
                            typeName + "." + method + " has " + named.Count + " overloads");
                    }
                    else
                    {
                        // the argument types the patch names must pick exactly one overload
                        var wanted = Regex.Matches(m.Groups["args"].Value, @"typeof\(([A-Za-z0-9_.]+)\)").Cast<Match>()
                            .Select(t => t.Groups[1].Value.Split('.').Last()).ToList();
                        var picked = named.Where(x => x.GetParameters().Select(q => CSharpName(q.ParameterType)).SequenceEqual(wanted)).ToList();
                        Check(picked.Count == 1, file + ": the argument types pick exactly one overload of " + typeName + "." + method +
                            " (" + string.Join(", ", wanted) + ")");
                    }
                }
            }
        }
        Check(patchCount >= 50, "the tree's patches were all found (" + patchCount + ")");
        Console.WriteLine(fails == 0 ? $"all {checks} checks passed" : $"{fails} of {checks} checks failed");
        return fails == 0 ? 0 : 1;
    }
}
