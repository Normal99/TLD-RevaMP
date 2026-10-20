using System;
using System.Collections.Generic;

namespace TLDRevamp.Net
{
    // One index for all far-record flushes in a frame. Appends can extend it; replacements invalidate it.
    internal sealed class FarBoltIndex<T>
    {
        private readonly Func<T, uint> Parent;
        private readonly Dictionary<uint, List<T>> Index = new Dictionary<uint, List<T>>();
        private readonly Stack<List<T>> Pool = new Stack<List<T>>();
        private List<T> Source;
        private int Frame = -1, Count;
        internal FarBoltIndex(Func<T, uint> parent) { Parent = parent; }
        internal void Invalidate() { Frame = -1; }
        internal bool TryGetValue(uint id, out List<T> items) => Index.TryGetValue(id, out items);
        internal void Prepare(List<T> all, int frame)
        {
            if (Frame != frame || Source != all || all.Count < Count)
            {
                foreach (var list in Index.Values) { list.Clear(); Pool.Push(list); }
                Index.Clear(); Count = 0;
            }
            for (int i = Count; i < all.Count; i++)
            {
                var a = all[i]; uint parent = Parent(a);
                if (parent == 0) continue;
                if (!Index.TryGetValue(parent, out var list)) Index[parent] = list = Pool.Count > 0 ? Pool.Pop() : new List<T>();
                list.Add(a);
            }
            Source = all; Frame = frame; Count = all.Count;
        }
    }
}
