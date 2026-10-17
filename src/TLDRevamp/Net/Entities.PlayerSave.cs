using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace TLDRevamp.Net
{
    /// What PlayerSave (per-player saves on the host) needs from the shared objects.
    public static partial class Entities
    {
        /// Local: the network id of the shared object this item is the root of.
        internal static bool RootNetOf(tosaveitemscript it, out uint net)
        {
            net = 0;
            if (it == null) return false;
            foreach (var e in ByNet.Values) if (e.Root == it && !e.Proxy) { net = e.NetId; return true; }
            return false;
        }

        /// Server: does this player own the object?
        internal static bool ServerOwns(uint net, int player) => Server.TryGetValue(net, out var se) && se.OwnerId == player;

        /// Server: in this player's inventory or hands, or on their head?
        internal static bool ServerInPocket(uint net, int player) =>
            Server.TryGetValue(net, out var se) && se.OwnerId == player && (se.Stored || se.Held || (WornSync && ServerWornNow(se)));

        /// Host: the local save ids of a shared object's copy here (every member, or the root while it is dormant).
        internal static void LocalIdsOf(uint net, HashSet<uint> into)
        {
            if (!ByNet.TryGetValue(net, out var e)) return;
            if (e.RootId != 0) into.Add(e.RootId);
            if (e.Items != null) foreach (var it in e.Items) if (it != null) into.Add(it.idInSave);
        }

        /// Host save: every record of these ids out of the world save (items, and their rows in the other lists).
        internal static int DropFromSave(itemDataClass d, HashSet<uint> ids)
        {
            int n = 0;
            foreach (var f in ItemSnapshot.ListFields)
            {
                if (!(f.GetValue(d) is IList list) || list.Count == 0) continue;
                var idf = list[0]?.GetType().GetField("id", BindingFlags.Public | BindingFlags.Instance);
                if (idf == null || idf.FieldType != typeof(uint)) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] != null && ids.Contains((uint)idf.GetValue(list[i]))) { list.RemoveAt(i); n++; }
            }
            return n;
        }
    }
}
