using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TLDRevamp
{
    /// Runtime get/set of any static-rooted member path, e.g.
    ///   get mainscript.s.player.transform.position
    ///   set UnityEngine.QualitySettings.vSyncCount 0
    /// Lets us inspect and tweak the game without rebuilding or restarting.
    public static class Reflect
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // Name → type table, built once. Scanning every assembly's types on each lookup cost 20–40 ms per bridge
        // command and caused most of the stutter/GC measured in the drive bench (2026-09-24).
        private static Dictionary<string, Type> _types;
        private static int _assemblyCount;

        public static Type FindType(string name)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            if (_types == null || asms.Length != _assemblyCount)
            {
                var map = new Dictionary<string, Type>();
                foreach (var asm in asms)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                    foreach (var t in types)
                    {
                        if (t.FullName != null && !map.ContainsKey(t.FullName)) map[t.FullName] = t;
                        if (!map.ContainsKey(t.Name)) map[t.Name] = t;  // short names: first wins, like before
                    }
                }
                _types = map;
                _assemblyCount = asms.Length;
            }
            return _types.TryGetValue(name, out var found) ? found : null;
        }

        /// Resolves the longest type prefix, then walks members. Returns (owner instance, member, owner type).
        private static void Resolve(string path, out object owner, out MemberInfo member, out Type ownerType)
        {
            var parts = path.Split('.');
            Type type = null;
            int i = parts.Length - 1;
            for (; i >= 1; i--)
            {
                type = FindType(string.Join(".", parts, 0, i));
                if (type != null) break;
            }
            if (type == null) throw new Exception("no type found in path " + path);

            owner = null;
            ownerType = type;
            member = null;
            for (; i < parts.Length; i++)
            {
                member = (MemberInfo)ownerType.GetField(parts[i], All) ?? ownerType.GetProperty(parts[i], All);
                if (member == null) throw new Exception($"{ownerType.Name} has no member {parts[i]}");
                if (i == parts.Length - 1) return;
                owner = GetValue(member, owner);
                if (owner == null) throw new Exception(parts[i] + " is null");
                ownerType = owner.GetType();
            }
        }

        private static object GetValue(MemberInfo m, object owner) =>
            m is FieldInfo f ? f.GetValue(owner) : ((PropertyInfo)m).GetValue(owner);

        private static Type MemberType(MemberInfo m) =>
            m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;

        public static string Get(string path)
        {
            Resolve(path, out var owner, out var member, out _);
            return Token(GetValue(member, owner));
        }

        /// Sets the leaf, then writes value-type owners back up the path: `kaposztaleves.s.settings.god` — settings is
        /// a struct, so the leaf lives in a boxed COPY; without the write-back the set silently did nothing.
        public static string Set(string path, string raw)
        {
            var chain = ResolveChain(path);
            var (owner, member) = chain[chain.Count - 1];
            object value = Parse(raw, MemberType(member));
            SetValue(member, owner, value);
            for (int i = chain.Count - 1; i >= 1; i--)
            {
                var (parentOwner, parentMember) = chain[i - 1];
                if (owner == null || !owner.GetType().IsValueType) break;   // a reference owner holds the change already
                SetValue(parentMember, parentOwner, owner);
                owner = parentOwner;
            }
            return Token(GetValue(member, chain[chain.Count - 1].owner));
        }

        private static void SetValue(MemberInfo m, object owner, object value)
        {
            if (m is FieldInfo f) f.SetValue(owner, value);
            else ((PropertyInfo)m).SetValue(owner, value);
        }

        /// (owner, member) for every member step of the path; owners of value-type steps are the boxed copies.
        private static List<(object owner, MemberInfo member)> ResolveChain(string path)
        {
            var parts = path.Split('.');
            Type type = null;
            int i = parts.Length - 1;
            for (; i >= 1; i--)
            {
                type = FindType(string.Join(".", parts, 0, i));
                if (type != null) break;
            }
            if (type == null) throw new Exception("no type found in path " + path);
            var chain = new List<(object, MemberInfo)>();
            object owner = null; var ownerType = type;
            for (; i < parts.Length; i++)
            {
                var member = (MemberInfo)ownerType.GetField(parts[i], All) ?? ownerType.GetProperty(parts[i], All);
                if (member == null) throw new Exception($"{ownerType.Name} has no member {parts[i]}");
                chain.Add((owner, member));
                if (i == parts.Length - 1) break;
                owner = GetValue(member, owner);
                if (owner == null) throw new Exception(parts[i] + " is null");
                ownerType = owner.GetType();
            }
            return chain;
        }

        /// Calls a method at a static-rooted path, e.g. `call mainscript.s.player.survival.HealInstant 25`.
        /// Picks the overload whose required parameter count fits the given args; missing optional args use defaults.
        public static string Call(string path, string[] args)
        {
            int dot = path.LastIndexOf('.');
            string ownerPath = path.Substring(0, dot), name = path.Substring(dot + 1);
            object owner = null;
            Type ownerType = FindType(ownerPath);
            if (ownerType == null)
            {
                Resolve(ownerPath, out var o, out var m, out _);
                owner = GetValue(m, o);
                if (owner == null) throw new Exception(ownerPath + " is null");
                ownerType = owner.GetType();
            }
            foreach (var method in ownerType.GetMethods(All))
            {
                if (method.Name != name || method.IsGenericMethodDefinition) continue;
                var ps = method.GetParameters();
                int required = ps.Count(p => !p.IsOptional);
                if (args.Length < required || args.Length > ps.Length) continue;
                if (method.IsStatic != (owner == null)) continue;
                var values = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    values[i] = i < args.Length ? Parse(args[i], ps[i].ParameterType) : ps[i].DefaultValue;
                return Token(method.Invoke(owner, values));
            }
            throw new Exception($"no {name} on {ownerType.Name} taking {args.Length} args");
        }

        private static object Parse(string raw, Type t)
        {
            var ci = CultureInfo.InvariantCulture;
            if (t == typeof(Vector3))
            {
                var v = raw.Split(',', ' ').Where(s => s.Length > 0).Select(s => float.Parse(s, ci)).ToArray();
                return new Vector3(v[0], v[1], v[2]);
            }
            if (t.IsEnum) return Enum.Parse(t, raw, true);
            return Convert.ChangeType(raw, t, ci);
        }

        /// A value as a JSON token: booleans, numbers and null unquoted (the harnesses compare real types), the rest
        /// as a quoted string.
        private static string Token(object v)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case int _: case long _: case short _: case byte _: case uint _: case ulong _: case ushort _: case sbyte _:
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? Json.Str(f.ToString(CultureInfo.InvariantCulture)) : f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? Json.Str(d.ToString(CultureInfo.InvariantCulture)) : d.ToString("R", CultureInfo.InvariantCulture);
                default: return Json.Str(Format(v));
            }
        }

        private static string Format(object v)
        {
            if (v == null) return "null";
            if (v is bool b) return b ? "true" : "false";   // JSON booleans: ToString()'s "False" was truthy in Python harnesses
            if (v is Vector3 p) return $"{p.x:F2},{p.y:F2},{p.z:F2}";
            if (v is float f) return f.ToString(CultureInfo.InvariantCulture);
            return v.ToString();
        }
    }
}
