using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp.WorldGen
{
    /// The terrain choice in the game's own new-game screen (issue #32).
    ///
    /// The game shows the map, the biome and the start car in its new-game screen. We put the terrain choice next to
    /// them, built from a clone of the game's own map dropdown, so it looks and behaves like the game's other
    /// dropdowns (same sprites, fonts, sounds and layout) instead of a mod-made widget.
    ///
    /// The dropdown is only a front end: it writes the mode into TerrainChoice.Selected, and the world is generated
    /// with it when the new game starts (TerrainHooks). Nothing here touches the game's own settings file, so the
    /// choice is ours alone and a save keeps its own terrain.
    ///
    /// This dropdown is the only place the terrain is picked: it is a choice for a new game, so it is not in Settings.
    /// The last pick is remembered (RevampSettings.NewGameTerrainKey). If the new-game screen is not the layout we know
    /// (the game is updated, the screen is arranged differently), the dropdown is not added and new games use the last
    /// pick (Crossroads when there is none); the bridge's `terrain select` still works for tests. The game is never left
    /// in a broken state by a layout we did not recognise.
    ///
    /// The old terrain (2023) runs the 2023 build's own code from the player's Steam download (Old2023.OldBuild).
    /// Without those files its entry is greyed out and can't be picked, and one line under the row says how to get them.
    [HarmonyPatch]
    public static class TerrainNewGameUi
    {
        public const string DropdownName = "DropdownTerrain";
        public const string LabelName = "TextTerrainLabel";
        public const string HintName = "TextTerrainOld2023Hint";

        /// The dropdown we added, so its value can be refreshed when the mode changes elsewhere.
        private static TMP_Dropdown _dropdown;
        private static TextMeshProUGUI _label;
        private static TextMeshProUGUI _hint;
        private static readonly List<string> _options = new List<string>();

        public static bool Added { get; private set; }
        public static string LastError = "";

        /// Build the dropdown on the game's new-game screen. Safe to call more than once.
        public static void TryBuild(menuhandler menu)
        {
            if (menu == null) return;
            if (TerrainHooks.OtherModPicksTerrain) { Added = false; LastError = "the TerrainChoice mod has its own terrain row"; return; }
            try
            {
                var mapDropdown = FindMapDropdown(menu);
                if (mapDropdown == null) { LastError = "the new-game screen has no map dropdown"; return; }
                var parent = mapDropdown.transform.parent;
                if (parent == null) { LastError = "the map dropdown has no parent"; return; }
                if (parent.Find(DropdownName) != null) { Added = true; return; }   // already there
                _label = null;

                // The template: the game's own map dropdown. Its own listener is removed (it would change the map).
                var template = UnityEngine.Object.Instantiate(mapDropdown.gameObject, parent);
                template.name = DropdownName;
                var rect = (RectTransform)template.transform;
                var mapRect = (RectTransform)mapDropdown.transform;
                rect.anchoredPosition = mapRect.anchoredPosition + new Vector2(0f, -85f);
                rect.sizeDelta = mapRect.sizeDelta;

                // The clone carries the game's own dropdown script, which would keep filling the list with the maps
                // and would write the map choice into the game's settings. It goes, and its listeners with it.
                foreach (var c in template.GetComponentsInChildren<dropdownsetting>(true)) UnityEngine.Object.Destroy(c);
                StripLanguage(template);

                var dd = template.GetComponent<TMP_Dropdown>();
                if (dd == null) { UnityEngine.Object.Destroy(template); LastError = "the map dropdown has no TMP_Dropdown"; return; }
                dd.onValueChanged = new TMP_Dropdown.DropdownEvent();   // our listener only
                // The map dropdown carries its own row label ("Map:") as a child; on the clone it names our row.
                foreach (var t in template.GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (t == dd.captionText || t == dd.itemText) continue;
                    if (dd.template != null && t.transform.IsChildOf(dd.template)) continue;
                    t.text = "Terrain:";
                    _label = t;
                }
                dd.ClearOptions();
                _options.Clear();
                foreach (var m in TerrainModes.Selectable()) _options.Add(TerrainModes.Label(m));
                dd.AddOptions(_options);
                dd.SetValueWithoutNotify(Mathf.Clamp(Array.IndexOf(TerrainModes.Selectable(), TerrainChoice.Selected), 0, Mathf.Max(0, _options.Count - 1)));
                dd.RefreshShownValue();
                dd.onValueChanged.AddListener(v =>
                {
                    var modes = TerrainModes.Selectable();
                    if (v < 0 || v >= modes.Length) return;
                    if (!Offered(modes[v])) { Refresh(); return; }   // greyed out: the old pick stays
                    TerrainHooks.Select(modes[v]);
                    RevampSettings.SetInt(RevampSettings.NewGameTerrainKey, (int)modes[v]);
                    RevampSettings.Save();
                });
                _dropdown = dd;

                // A label, cloned from the game's own map name text, so the row reads like the others.
                // The map name text is looked up by name in the whole menu: it is the game's own wording for the
                // row next to the map dropdown, and it is where the player reads the map they picked.
                // Only when the dropdown brought no label of its own (the game's map dropdown does: "Map:").
                var mapLabel = _label == null ? FindMapNameText(menu) : null;
                if (mapLabel != null)
                {
                    var labelGo = UnityEngine.Object.Instantiate(mapLabel.gameObject, mapLabel.transform.parent);
                    labelGo.name = LabelName;
                    var lrt = (RectTransform)labelGo.transform;
                    lrt.anchoredPosition = mapRect.anchoredPosition + new Vector2(0f, -85f) + new Vector2(-260f, 0f);
                    // The game's language script on the clone would write "Map:" back every time the screen opens.
                    StripLanguage(labelGo);
                    var tmp = labelGo.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) { tmp.text = "Terrain:"; _label = tmp; }
                }

                BuildHint(rect);
                Added = true;
                LastError = "";
                Plugin.Log.LogInfo("terrain: the new-game terrain dropdown is ready");
            }
            catch (Exception e)
            {
                Added = false;
                LastError = e.Message;
                Plugin.Log.LogWarning("terrain: the new-game dropdown could not be built: " + e.Message);
            }
        }

        /// Can this mode be picked on this machine? The old terrain needs the 2023 game files.
        public static bool Offered(TerrainMode m) => m != TerrainMode.OldTerrain || Old2023.OldBuild.Available;

        /// One line under the terrain row while the old terrain can't be picked: how to get the 2023 game files.
        /// It spans the row, from its "Terrain:" label to the dropdown's right edge, so the game's panel never clips it.
        private static void BuildHint(RectTransform dropdown)
        {
            if (Offered(TerrainMode.OldTerrain) || _label == null) { _hint = null; return; }
            var existing = dropdown.Find(HintName);
            var go = existing != null ? existing.gameObject : UnityEngine.Object.Instantiate(_label.gameObject, dropdown);
            go.name = HintName;
            StripLanguage(go);
            var label = new Vector3[4];
            var row = new Vector3[4];
            ((RectTransform)_label.transform).GetWorldCorners(label);   // bottom-left, top-left, top-right, bottom-right
            dropdown.GetWorldCorners(row);
            Vector2 topLeft = dropdown.InverseTransformPoint(new Vector3(label[0].x, Mathf.Min(label[0].y, row[0].y), row[0].z));
            float right = dropdown.InverseTransformPoint(row[2]).x;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = topLeft - dropdown.rect.center + new Vector2(0f, -6f);
            rt.sizeDelta = new Vector2(Mathf.Max(100f, right - topLeft.x), 90f);
            _hint = go.GetComponent<TextMeshProUGUI>();
            if (_hint == null) return;
            _hint.text = Old2023.OldBuildFiles.NewGameHint;
            _hint.enableAutoSizing = false;
            _hint.fontSize = _label.fontSize * 0.5f;
            _hint.enableWordWrapping = true;
            _hint.overflowMode = TextOverflowModes.Overflow;
            _hint.alignment = TextAlignmentOptions.TopLeft;
            _hint.raycastTarget = false;
            go.SetActive(true);
        }

        /// The dropdown's list is made each time it opens: grey out the entries that can't be picked.
        [HarmonyPatch(typeof(TMP_Dropdown), nameof(TMP_Dropdown.Show))]
        [HarmonyPostfix]
        private static void AfterShow(TMP_Dropdown __instance)
        {
            if (_dropdown == null || __instance != _dropdown) return;
            try
            {
                var list = __instance.transform.Find("Dropdown List");   // TMP_Dropdown's list: one toggle per option, in order
                if (list == null) return;
                var modes = TerrainModes.Selectable();
                int index = 0;
                foreach (var toggle in list.GetComponentsInChildren<Toggle>(false))
                {
                    if (index < modes.Length && !Offered(modes[index]))
                    {
                        toggle.interactable = false;
                        foreach (var text in toggle.GetComponentsInChildren<TMP_Text>(true))
                            text.color = new Color(text.color.r, text.color.g, text.color.b, 0.4f);
                    }
                    index++;
                }
            }
            catch (Exception e) { LastError = e.Message; }
        }

        /// Remove the game's language script (languagetext) from a cloned object: it registered itself with the
        /// game's language list when the clone was made, and it rewrites its text with the original's word whenever
        /// the object is enabled or the language changes.
        private static void StripLanguage(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<languagetext>(true))
            {
                if (ls.l != null) { ls.l.texts.Remove(c); ls.l.tmps.Remove(c); }
                UnityEngine.Object.DestroyImmediate(c);
            }
        }

        /// The game's own map name text (the row that says which map is chosen).
        private static TextMeshProUGUI FindMapNameText(menuhandler menu)
        {
            if (menu == null) return null;
            var all = menu.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true);
            foreach (var t in all) if (t != null && t.name == "TextMapName") return t;
            return null;
        }

        /// The game's own map dropdown on the new-game screen.
        private static TMP_Dropdown FindMapDropdown(menuhandler menu)
        {
            // The new-game screen's map dropdown is the only one that lists the maps.
            foreach (var dd in FindDropdowns(menu))
            {
                var ds = dd.GetComponent<dropdownsetting>();
                if (ds != null && ds.dtype == dropdownsetting.dropdowntype.maps) return dd;
            }
            return null;
        }

        private static IEnumerable<TMP_Dropdown> FindDropdowns(menuhandler menu)
        {
            var all = menu.GetComponentsInChildren<TMP_Dropdown>(includeInactive: true);
            foreach (var dd in all) yield return dd;
        }

        /// Keep the dropdown in step with the mode (the settings screen can change it too).
        public static void Refresh()
        {
            if (_dropdown == null) return;
            var modes = TerrainModes.Selectable();
            int index = Array.IndexOf(modes, TerrainChoice.Selected);
            if (index < 0 || !Offered(modes[index])) index = 0;
            if (_dropdown.value != index) _dropdown.SetValueWithoutNotify(index);
            _dropdown.RefreshShownValue();
        }

        /// The menu builds its screen when it starts: add the dropdown then.
        [HarmonyPatch(typeof(menuhandler), "Start")]
        [HarmonyPostfix]
        private static void AfterMenuStart(menuhandler __instance)
        {
            TryBuild(__instance);
        }

        /// The player came back to the main menu: the screen may have been rebuilt, so make sure the dropdown is there.
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.LoadMainMenu))]
        [HarmonyPostfix]
        private static void AfterLoadMainMenu(menuhandler __instance)
        {
            TryBuild(__instance);
        }

        /// The player picked a different map in the game's own dropdown: the row under it stays where it is, but its
        /// value has to follow the mode the player chose elsewhere (the settings screen).
        // The overload without arguments: UpdMapName(string) calls it too, so every name change comes through here.
        [HarmonyPatch(typeof(menuhandler), nameof(menuhandler.UpdMapName), new Type[0])]
        [HarmonyPostfix]
        private static void AfterMapName(menuhandler __instance)
        {
            if (_dropdown == null) TryBuild(__instance);
            else Refresh();
        }
    }
}
