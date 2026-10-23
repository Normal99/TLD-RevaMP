using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace TLDRevamp.Fixes
{
    /// The fixes that change only a few calls inside a game method do it at runtime: a transpiler on the game's own method
    /// swaps those call sites for ours, matched by the method or field they use (never by position), so the source keeps
    /// no copy of the game's code. Fail closed: when a site isn't there as expected (a game update moved things), the
    /// method is left exactly as the game has it, the log says which site, and the miss is counted (the fix's Stats
    /// "patched", Plugin.PatchFailures in the bridge's ping).
    ///
    ///   var p = new GamePatch("TankcapIdle", instructions);
    ///   p.Calls(gameMethod, ourStaticMethod, 2);   // ours takes the same stack (the instance first)
    ///   return p.Result(ref Patched);
    internal sealed class GamePatch
    {
        private readonly string _fix;
        private readonly List<CodeInstruction> _original;
        private readonly List<CodeInstruction> _code;   // copies: the original list stays as it came
        private string _miss;

        public GamePatch(string fix, IEnumerable<CodeInstruction> instructions)
        {
            _fix = fix;
            _original = instructions.ToList();
            _code = _original.Select(i => new CodeInstruction(i)).ToList();
        }

        /// Every call to `from` becomes a call to the static `to`, which takes the same stack (an instance method's
        /// instance first) and leaves the same result; with `withThis` the patched method's own instance is passed last.
        /// There must be exactly `expect` such calls.
        public GamePatch Calls(MethodBase from, MethodInfo to, int expect, bool withThis = false) =>
            Swap(i => i.Calls((MethodInfo)from), to, expect, withThis, from.DeclaringType?.Name + "." + from.Name);

        /// The calls to `from` in order, each to its own replacement: the k-th call becomes `to[k]`; there must be exactly
        /// as many calls as replacements.
        public GamePatch CallsEach(MethodBase from, params MethodInfo[] to)
        {
            if (_miss != null) return this;
            int n = 0;
            foreach (var i in _code)
                if (i.Calls((MethodInfo)from)) { if (n < to.Length) Set(i, to[n]); n++; }
            if (n != to.Length) Miss($"{from.DeclaringType?.Name}.{from.Name}: {n} sites, expected {to.Length}");
            return this;
        }

        /// Every store to `field` becomes a call to the static `to(instance, value)`.
        public GamePatch Stores(FieldInfo field, MethodInfo to, int expect) =>
            Swap(i => i.StoresField(field), to, expect, false, "store " + field.DeclaringType?.Name + "." + field.Name);

        /// Only the calls to `from` whose argument was pushed by `ldc.i4 value` (a bool/int constant just before the call).
        public GamePatch CallsWithConst(MethodBase from, int value, MethodInfo to, int expect)
        {
            if (_miss != null) return this;
            int n = 0;
            for (int k = 1; k < _code.Count; k++)
                if (_code[k].Calls((MethodInfo)from) && Const(_code[k - 1]) == value) { Set(_code[k], to); n++; }
            if (n != expect) Miss($"{from.DeclaringType?.Name}.{from.Name}({value}): {n} sites, expected {expect}");
            return this;
        }

        private GamePatch Swap(Func<CodeInstruction, bool> match, MethodInfo to, int expect, bool withThis, string what)
        {
            if (_miss != null) return this;
            int n = 0;
            for (int k = 0; k < _code.Count; k++)
            {
                if (!match(_code[k])) continue;
                if (withThis)
                {
                    // the instance goes on the stack right before the call; a jump to the call itself would skip it
                    if (HasLabels(_code[k])) { Miss(what + ": a jump lands on the call"); return this; }
                    _code.Insert(k++, new CodeInstruction(OpCodes.Ldarg_0));
                }
                Set(_code[k], to);
                n++;
            }
            if (n != expect) Miss($"{what}: {n} sites, expected {expect}");
            return this;
        }

        private static void Set(CodeInstruction i, MethodInfo to)
        {
            i.opcode = OpCodes.Call;   // its labels and exception blocks stay on the instruction
            i.operand = to;
        }

        // CodeInstruction.labels is a List<Label>: Label isn't in netstandard2.1's mscorlib facade, so not named here
        // (that is also why there is no CodeMatcher: its signatures use ILGenerator)
        private static readonly FieldInfo LabelsF = AccessTools.Field(typeof(CodeInstruction), "labels");
        private static bool HasLabels(CodeInstruction i) => LabelsF.GetValue(i) is System.Collections.ICollection c && c.Count > 0;

        private static int? Const(CodeInstruction i)
        {
            if (i.opcode == OpCodes.Ldc_I4_0) return 0;
            if (i.opcode == OpCodes.Ldc_I4_1) return 1;
            if ((i.opcode == OpCodes.Ldc_I4 || i.opcode == OpCodes.Ldc_I4_S) && i.operand is IConvertible c) return c.ToInt32(null);
            return null;
        }

        /// A check of the method's shape beyond the swapped sites (e.g. "the loop's second lookup is there").
        public GamePatch Require(bool ok, string what)
        {
            if (_miss == null && !ok) Miss(what);
            return this;
        }

        public bool Ok => _miss == null;

        private void Miss(string what)
        {
            _miss = what;
            Plugin.Log.LogError($"{_fix}: the game's method isn't as expected ({what}); left unpatched");
            lock (Plugin.FailedPatches) { Plugin.PatchFailures++; Plugin.FailedPatches.Add(_fix + " (site missing)"); }
        }

        /// The patched code, or the game's own unchanged when a site was missing. `patched` is set to 1 or 0.
        public IEnumerable<CodeInstruction> Result(ref int patched)
        {
            patched = _miss == null ? 1 : 0;
            return _miss == null ? _code : _original;
        }
    }
}
