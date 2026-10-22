using System.Collections.Generic;
using UnityEngine;

namespace CarTrailer
{
    /// Test hooks for TLD Revamp's debug bridge (`call CarTrailer.Probe.Wheels`): what the car trailers in the world
    /// carry, as text (tools/cartrailersave.py).
    public static class Probe
    {
        /// Each car trailer: its wheel slots and how many parts sit in each, and its save id.
        public static string Wheels()
        {
            var rows = new List<string>();
            foreach (var save in Object.FindObjectsOfType<tosaveitemscript>())
            {
                if (save.id != Plugin.ItemId) continue;
                int slots = 0, parts = 0, most = 0;
                foreach (var ps in save.GetComponentsInChildren<partslotscript>(true))
                {
                    if (!ps.name.Contains("Wheel")) continue;
                    slots++; int n = 0;
                    foreach (var p in ps.parts) if (p != null) n++;
                    parts += n; most = Mathf.Max(most, n);
                }
                int tyres = 0;
                foreach (var a in save.GetComponentsInChildren<attachablescript>(true)) if (a.GetComponent<tosaveitemscript>() != null && a.gameObject != save.gameObject) tyres++;
                rows.Add($"{save.name} at {save.transform.position}: wheel slots {slots}, parts in them {parts}, most in one {most}, attached items {tyres}");
            }
            return rows.Count == 0 ? "none" : string.Join(" | ", rows);
        }

        /// The save ids of the game's trailer prefabs (a full variant saves its base id).
        public static string Ids()
        {
            var db = itemdatabase.s;
            string Id(int i) { var s = db.items[i] != null ? db.items[i].GetComponent<tosaveitemscript>() : null; return s != null ? s.id.ToString() : "none"; }
            return $"utanfuto {Id((int)itemdatabase.i.utanfuto)}, utanfutofull {Id((int)itemdatabase.i.utanfutofull)}, cartrailer {Id(Plugin.ItemId)}; holder children {Plugin.HolderChildren}";
        }
    }
}
