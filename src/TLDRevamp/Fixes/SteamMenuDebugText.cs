using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GeneszSteamP2PBase;
using HarmonyLib;
using TMPro;

namespace TLDRevamp.Fixes
{
    /// SteamP2PMenuHandlerScript.UpdateRefresh (the game's multiplayer menu; it runs every frame, also in single player
    /// with the menu closed) rebuilds a debug line every frame:
    ///   deguText.text = "IsSteam(): " + IsSteam() + "\nIsConnected(): " + IsConnected() + "\nIsHost(): " + IsHost() +
    ///                   "\nHasCorrectPassword(): " + HasCorrectPassword();
    /// an 8-part concatenation (~280 bytes of garbage a frame) that TMP then ignores because the text is equal.
    ///
    /// Transpiler on that statement only: Text(deguText) calls the same four functions in the same order and builds the
    /// same string (the game's own literals, read from the method) only when one of them changed or the text was changed
    /// by something else; otherwise the text keeps the equal string it has. Exact.
    ///
    /// Runtime switch (bridge `set`): TLDRevamp.Fixes.SteamMenuDebugText.Enabled
    [HarmonyPatch(typeof(SteamP2PMenuHandlerScript), "UpdateRefresh")]
    public static class SteamMenuDebugText
    {
        public static bool Enabled = true;
        public static long Built, Skipped, Patched;

        private static readonly FieldInfo Field = AccessTools.Field(typeof(SteamP2PMenuHandlerScript), "deguText");
        private static readonly MethodInfo SetText = AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text));
        private static readonly MethodInfo Helper = AccessTools.Method(typeof(SteamMenuDebugText), nameof(Text));
        private static readonly string[] Labels = new string[4];
        private static int _last = -1;
        private static string _text;
        private static TMP_Text _target;

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].LoadsField(Field)) continue;
                int end = list.FindIndex(i, x => x.Calls(SetText));
                if (end < 0) break;
                var strings = new List<string>();
                for (int k = i + 1; k < end; k++) if (list[k].opcode == OpCodes.Ldstr) strings.Add((string)list[k].operand);
                if (strings.Count != 4 || end - i > 60) break;
                for (int k = 0; k < 4; k++) Labels[k] = strings[k];
                // (this, deguText) stay on the stack; the concatenation and set_text become Text(deguText)
                list.RemoveRange(i + 1, end - i);
                list.Insert(i + 1, new CodeInstruction(OpCodes.Call, Helper));
                Patched++;
                break;
            }
            if (Patched == 0) Plugin.Log.LogWarning("SteamMenuDebugText: debug text statement not found in UpdateRefresh; not patched");
            return list;
        }

        public static void Text(TMP_Text target)
        {
            bool steam = SteamP2PNetworkingUnderTheHoodScript.IsSteam(), connected = SteamP2PNetworkingUnderTheHoodScript.IsConnected(),
                 host = SteamP2PNetworkingUnderTheHoodScript.IsHost(), password = SteamP2PNetworkingUnderTheHoodScript.HasCorrectPassword();
            int state = (steam ? 1 : 0) | (connected ? 2 : 0) | (host ? 4 : 0) | (password ? 8 : 0);
            if (Enabled && state == _last && ReferenceEquals(target, _target) && ReferenceEquals(target.text, _text)) { Skipped++; return; }
            Built++;
            _text = Labels[0] + steam.ToString() + Labels[1] + connected.ToString() + Labels[2] + host.ToString() + Labels[3] + password.ToString();
            _last = state; _target = target;
            target.text = _text;
        }

        public static string Stats => "{\"enabled\":" + (Enabled ? "true" : "false") + ",\"patched\":" + Patched + ",\"built\":" + Built + ",\"skipped\":" + Skipped + "}";
    }
}
