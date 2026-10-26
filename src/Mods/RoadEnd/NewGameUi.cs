using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace RoadEnd
{
    /// The choice on the game's new-game screen: a row "Road end:" with Off / the distance, built from a clone of the
    /// game's own map dropdown, so it looks and behaves like the game's other rows (as TLD Revamp's terrain row).
    /// It goes under the map row, or under TLD Revamp's terrain row when that is there. Off at every start of the
    /// game; the pick is for the next new game only (World.ChosenForNewGame). If the screen isn't the layout we know,
    /// no row is added and new games have no end.
    [HarmonyPatch]
    public static class NewGameUi
    {
        public const string DropdownName = "DropdownRoadEnd";
        const float RowStep = 85f;   // the game's rows on that screen are this far apart

        static TMP_Dropdown _dropdown;
        public static string LastError = "";

        public static void Choose(bool on)
        {
            World.ChosenForNewGame = on && Plugin.Enabled;
            if (_dropdown != null) { _dropdown.SetValueWithoutNotify(World.ChosenForNewGame ? 1 : 0); _dropdown.RefreshShownValue(); }
        }

        public static string Status() =>
            _dropdown == null ? "no row: " + LastError
                : $"row '{_dropdown.captionText?.text}' at {((RectTransform)_dropdown.transform).anchoredPosition}, active {_dropdown.gameObject.activeInHierarchy}";

        static string OnLabel() => $"{Plugin.EndKm:0.###} km";

        public static void TryBuild(menuhandler menu)
        {
            if (menu == null || !Plugin.Enabled) return;
            try
            {
                TMP_Dropdown map = null;
                foreach (var dd in menu.GetComponentsInChildren<TMP_Dropdown>(true))
                {
                    var ds = dd.GetComponent<dropdownsetting>();
                    if (ds != null && ds.dtype == dropdownsetting.dropdowntype.maps) { map = dd; break; }
                }
                if (map == null) { LastError = "the new-game screen has no map dropdown"; return; }
                var parent = map.transform.parent;
                if (parent == null) { LastError = "the map dropdown has no parent"; return; }
                var mapRect = (RectTransform)map.transform;
                // under the lowest row there: the map's, or TLD Revamp's terrain row (and its hint line) under it
                float y = mapRect.anchoredPosition.y - RowStep;
                var terrain = parent.Find("DropdownTerrain");
                if (terrain != null)
                {
                    y = ((RectTransform)terrain).anchoredPosition.y - RowStep;
                    var hint = terrain.Find("TextTerrainOld2023Hint");
                    if (hint != null && hint.gameObject.activeSelf) y -= RowStep * 0.6f;
                }
                var existing = parent.Find(DropdownName);
                if (existing != null)
                {
                    var er = (RectTransform)existing;
                    er.anchoredPosition = new Vector2(mapRect.anchoredPosition.x, y);
                    return;
                }

                var go = UnityEngine.Object.Instantiate(map.gameObject, parent);
                go.name = DropdownName;
                var rect = (RectTransform)go.transform;
                rect.anchoredPosition = new Vector2(mapRect.anchoredPosition.x, y);
                rect.sizeDelta = mapRect.sizeDelta;
                // the clone's own dropdown script would fill it with the maps and write the map choice
                foreach (var c in go.GetComponentsInChildren<dropdownsetting>(true)) UnityEngine.Object.Destroy(c);
                StripLanguage(go);
                var dd2 = go.GetComponent<TMP_Dropdown>();
                if (dd2 == null) { UnityEngine.Object.Destroy(go); LastError = "the map dropdown has no TMP_Dropdown"; return; }
                dd2.onValueChanged = new TMP_Dropdown.DropdownEvent();
                // the map dropdown carries its row label ("Map:") as a child: on the clone it names our row
                foreach (var t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (t == dd2.captionText || t == dd2.itemText) continue;
                    if (dd2.template != null && t.transform.IsChildOf(dd2.template)) continue;
                    t.text = "Road end:";
                }
                dd2.ClearOptions();
                dd2.AddOptions(new List<string> { "Off", OnLabel() });
                dd2.SetValueWithoutNotify(World.ChosenForNewGame ? 1 : 0);
                dd2.RefreshShownValue();
                dd2.onValueChanged.AddListener(v => World.ChosenForNewGame = v == 1);
                _dropdown = dd2;
                LastError = "";
                Plugin.Log.LogInfo("the new-game screen has the Road end row");
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Plugin.Log.LogWarning("the Road end row could not be added to the new-game screen: " + e.Message);
            }
        }

        /// The game's language script on a clone would write the original's word back ("Map:").
        static void StripLanguage(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<languagetext>(true))
            {
                if (ls.l != null) { ls.l.texts.Remove(c); ls.l.tmps.Remove(c); }
                UnityEngine.Object.DestroyImmediate(c);
            }
        }

        // after TLD Revamp's own row (same hooks), so ours goes under it
        [HarmonyPatch(typeof(menuhandler), "Start")]
        [HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void AfterMenuStart(menuhandler __instance) => TryBuild(__instance);

        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadMainMenu))]
        [HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void AfterLoadMainMenu(menuhandler __instance) => TryBuild(__instance);

        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.UpdMapName), new Type[0])]
        [HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void AfterMapName(menuhandler __instance) => TryBuild(__instance);
    }
}
