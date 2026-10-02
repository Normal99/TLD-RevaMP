using System.Collections.Generic;
using HarmonyLib;

namespace TLDRevamp.Net
{
    /// Items a player's own action makes outside mainscript.Spawn (SpawnShare covers that): the game instantiates the
    /// prefab and starts it as a new item (tosaveitemscript.FStart() - a new save id; a stored or streamed item starts
    /// with its key instead). These existed on that player's machine only:
    ///   a turd (fpscontroller.Shit)
    ///   a cigarette taken out of its pack, in hand or in the hand's pack (usePickedUpStuff) or a pack looked at (RayStuff)
    /// A new item started during one of these is shared once the action is done (worn, picked up: finished).
    public static partial class Entities
    {
        public static long PlayerMadeShared;
        private static int _playerAction;
        private static readonly List<tosaveitemscript> _playerMade = new List<tosaveitemscript>();

        [HarmonyPatch]
        private static class PlayerActions
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(fpscontroller), nameof(fpscontroller.Shit));
                yield return AccessTools.Method(typeof(fpscontroller), nameof(fpscontroller.usePickedUpStuff));
                yield return AccessTools.Method(typeof(fpscontroller), nameof(fpscontroller.RayStuff));
            }

            [HarmonyPrefix] private static void Prefix(fpscontroller __instance) { if (InSession && mainscript.s != null && __instance == mainscript.s.player) _playerAction++; }

            [HarmonyFinalizer]
            private static void Finalizer(fpscontroller __instance)
            {
                if (!InSession || mainscript.s == null || __instance != mainscript.s.player || _playerAction == 0) return;
                if (--_playerAction > 0 || _playerMade.Count == 0) return;
                var made = _playerMade.ToArray(); _playerMade.Clear();
                foreach (var it in made)
                {
                    if (it == null) continue;
                    long before = SpawnShared;
                    ShareSpawn(it.gameObject);
                    if (SpawnShared > before) PlayerMadeShared++;
                }
            }
        }

        [HarmonyPatch(typeof(tosaveitemscript), nameof(tosaveitemscript.FStart), new System.Type[0])]
        private static class NewItemInAction
        {
            [HarmonyPrefix] private static void Prefix(tosaveitemscript __instance, out bool __state) => __state = __instance.started;

            [HarmonyPostfix]
            private static void Postfix(tosaveitemscript __instance, bool __state)
            {
                if (_playerAction > 0 && !__state && __instance.started && InSession) _playerMade.Add(__instance);
            }
        }
    }
}
