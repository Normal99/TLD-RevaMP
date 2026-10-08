using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

namespace TLDRevamp.Net
{
    /// Binary codec for the game's save records (itemDataClass and everything below it): the same fields the save files'
    /// BinaryFormatter writes (every instance field, public or not, except [NonSerialized]), in metadata order, with
    /// doubles/floats as raw bits — exact, compact, and safe: the types come from the record's static structure on the
    /// receiving side, never from the bytes (BinaryFormatter over the network would let a peer run code).
    /// Both sides must have the same game build: SchemaHash (field names/types of the whole graph) is compared at join.
    public static class RecordCodec
    {
        private sealed class Schema { public FieldInfo[] Fields; }
        private static readonly Dictionary<Type, Schema> Schemas = new Dictionary<Type, Schema>();
        public static string Unsupported = "";

        private static Schema Of(Type t)
        {
            if (Schemas.TryGetValue(t, out var s)) return s;
            var fs = new List<FieldInfo>();
            for (var bt = t; bt != null && bt != typeof(object) && bt != typeof(ValueType); bt = bt.BaseType)
                fs.InsertRange(0, bt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => !f.IsNotSerialized).OrderBy(f => f.MetadataToken));
            s = new Schema { Fields = fs.ToArray() };
            Schemas[t] = s;
            return s;
        }

        private static bool IsList(Type t, out Type elem)
        {
            elem = null;
            if (t.IsArray) { elem = t.GetElementType(); return true; }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) { elem = t.GetGenericArguments()[0]; return true; }
            return false;
        }

        public static void Write(NetWriter w, object o, Type t)
        {
            if (t == typeof(bool)) { w.Bool((bool)o); return; }
            if (t == typeof(byte)) { w.U8((byte)o); return; }
            if (t == typeof(sbyte)) { w.U8((byte)(sbyte)o); return; }
            if (t == typeof(short)) { w.U16((ushort)(short)o); return; }
            if (t == typeof(ushort)) { w.U16((ushort)o); return; }
            if (t == typeof(char)) { w.U16((char)o); return; }
            if (t == typeof(int)) { w.VarI32((int)o); return; }
            if (t == typeof(uint)) { w.VarU32((uint)o); return; }
            if (t == typeof(long)) { w.I64((long)o); return; }
            if (t == typeof(ulong)) { w.U64((ulong)o); return; }
            if (t == typeof(float)) { w.F32((float)o); return; }
            if (t == typeof(double)) { w.F64((double)o); return; }
            if (t.IsEnum) { w.VarI32(Convert.ToInt32(o)); return; }
            if (t == typeof(string)) { w.Str((string)o); return; }
            if (!t.IsValueType) { w.Bool(o != null); if (o == null) return; }
            if (IsList(t, out var et))
            {
                var list = (IList)o;
                w.VarU32((uint)list.Count);
                for (int i = 0; i < list.Count; i++) Write(w, list[i], et);
                return;
            }
            if (t.IsAbstract || t.IsInterface || t == typeof(object)) { Note(t); return; }
            foreach (var f in Of(t).Fields) Write(w, f.GetValue(o), f.FieldType);
        }

        public static object Read(NetReader r, Type t)
        {
            if (t == typeof(bool)) return r.Bool();
            if (t == typeof(byte)) return r.U8();
            if (t == typeof(sbyte)) return (sbyte)r.U8();
            if (t == typeof(short)) return (short)r.U16();
            if (t == typeof(ushort)) return r.U16();
            if (t == typeof(char)) return (char)r.U16();
            if (t == typeof(int)) return r.VarI32();
            if (t == typeof(uint)) return r.VarU32();
            if (t == typeof(long)) return r.I64();
            if (t == typeof(ulong)) return r.U64();
            if (t == typeof(float)) return r.F32();
            if (t == typeof(double)) return r.F64();
            if (t.IsEnum) return Enum.ToObject(t, r.VarI32());
            if (t == typeof(string)) return r.Str();
            if (!t.IsValueType && !r.Bool()) return null;
            if (IsList(t, out var et))
            {
                int n = (int)r.VarU32();
                if (r.Bad || n < 0 || n > r.Remaining + 1) { r.Bad = true; return null; } // every element takes ≥ 1 byte (n < 0: over 2^31 from the wire)
                if (t.IsArray)
                {
                    var a = Array.CreateInstance(et, n);
                    for (int i = 0; i < n; i++) a.SetValue(Read(r, et), i);
                    return a;
                }
                var list = (IList)Activator.CreateInstance(t, n);
                for (int i = 0; i < n; i++) list.Add(Read(r, et));
                return list;
            }
            if (t.IsAbstract || t.IsInterface || t == typeof(object)) return null;
            object o = FormatterServices.GetUninitializedObject(t); // like BinaryFormatter: no constructor
            foreach (var f in Of(t).Fields) f.SetValue(o, Read(r, f.FieldType));
            return o;
        }

        private static void Note(Type t) { if (!Unsupported.Contains(t.Name)) Unsupported += t.Name + " "; }

        /// Hash of the field graph (names + types) under a root type.
        public static ulong SchemaHash(Type root)
        {
            ulong h = 14695981039346656037UL;
            var seen = new HashSet<Type>();
            void Mix(string s) { foreach (char c in s) { h ^= c; h *= 1099511628211UL; } }
            void Walk(Type t)
            {
                if (t.IsPrimitive || t.IsEnum || t == typeof(string) || !seen.Add(t)) { Mix(t.FullName ?? t.Name); return; }
                if (IsList(t, out var et)) { Mix("[]"); Walk(et); return; }
                Mix(t.FullName ?? t.Name);
                foreach (var f in Of(t).Fields) { Mix(f.Name); Walk(f.FieldType); }
            }
            Walk(root);
            return h;
        }

        public static byte[] Encode(itemDataClass d)
        {
            var w = new NetWriter();
            Write(w, d, typeof(itemDataClass));
            var b = new byte[w.Len];
            Buffer.BlockCopy(w.Buf, 0, b, 0, w.Len);
            return b;
        }

        public static itemDataClass Decode(byte[] b)
        {
            var r = new NetReader();
            r.Load(b.Length);
            Buffer.BlockCopy(b, 0, r.Buf, 0, b.Length);
            var d = (itemDataClass)Read(r, typeof(itemDataClass));
            return r.Bad || r.Remaining != 0 ? null : d;
        }
    }
}
