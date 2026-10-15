using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// The dev menu's spawn list (kaposztaleves.InitSpawnButtons) makes a button for every item number from 1 to the
    /// last. Mods add items at high numbers, clear of the game's own (the car trailer at 9200, head units at
    /// 9100-9102: a game update adding items can't take them), so the list got about 8,700 "ErrorPrefab" buttons for the
    /// empty numbers in between, and the mods' items came after all of them (playtest 2026-10-14: "i try spawning in
    /// many radio1 from the dev menu but no dice"). Here an empty number gets no button.
    ///
    /// The loop is `for (int i = 1; i < itemdatabase.s.items.Length; i++)`; at the top of its body goes
    /// `if (itemdatabase.s.items[i] == null) continue;`.
    [HarmonyPatch(typeof(kaposztaleves), nameof(kaposztaleves.InitSpawnButtons))]
    public static class SpawnMenuGaps
    {
        public static bool Patched;

        private static readonly FieldInfo DbS = AccessTools.Field(typeof(itemdatabase), nameof(itemdatabase.s));
        private static readonly FieldInfo Items = AccessTools.Field(typeof(itemdatabase), nameof(itemdatabase.items));
        private static readonly MethodInfo Empty = AccessTools.Method(typeof(SpawnMenuGaps), nameof(IsEmpty));

        public static bool IsEmpty(int i) => itemdatabase.s.items[i] == null;

        // Label (System.Reflection.Emit) doesn't resolve against this project's references: labels are handled as objects
        private static readonly FieldInfo LabelsField = AccessTools.Field(typeof(CodeInstruction), "labels");
        private static System.Collections.IList Labels(CodeInstruction c) => (System.Collections.IList)LabelsField.GetValue(c);

        private static bool IsLdloc(CodeInstruction c) =>
            c.opcode == OpCodes.Ldloc || c.opcode == OpCodes.Ldloc_S || c.opcode == OpCodes.Ldloc_0 || c.opcode == OpCodes.Ldloc_1 || c.opcode == OpCodes.Ldloc_2 || c.opcode == OpCodes.Ldloc_3;

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = new List<CodeInstruction>(instructions);
            // the loop's test: ldloc i; ldsfld s; ldfld items; ldlen; conv.i4; blt body
            for (int k = 4; k + 5 < list.Count; k++)
            {
                if (!IsLdloc(list[k]) || !list[k + 1].LoadsField(DbS) || !list[k + 2].LoadsField(Items) || list[k + 3].opcode != OpCodes.Ldlen
                    || list[k + 4].opcode != OpCodes.Conv_I4 || (list[k + 5].opcode != OpCodes.Blt && list[k + 5].opcode != OpCodes.Blt_S)) continue;
                object body = list[k + 5].operand;
                int start = list.FindIndex(c => Labels(c).Contains(body));
                // the increment just before the test: ldloc i; ldc.i4.1; add; stloc i
                int inc = k - 4;
                if (start < 0 || list[inc + 2].opcode != OpCodes.Add) break;
                object next = typeof(ILGenerator).GetMethod("DefineLabel").Invoke(il, null);
                Labels(list[inc]).Add(next);
                var check = new List<CodeInstruction>
                {
                    new CodeInstruction(list[k].opcode, list[k].operand),   // ldloc i
                    new CodeInstruction(OpCodes.Call, Empty),
                    new CodeInstruction(OpCodes.Brtrue, next),
                };
                var moved = Labels(list[start]);   // the loop now jumps to the check
                foreach (var l in moved) Labels(check[0]).Add(l);
                moved.Clear();
                list.InsertRange(start, check);
                Patched = true;
                break;
            }
            if (!Patched) Plugin.Log.LogWarning("SpawnMenuGaps: the spawn list loop not found in kaposztaleves.InitSpawnButtons; not patched");
            return list;
        }
    }
}
