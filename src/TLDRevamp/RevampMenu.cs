using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TLDRevamp
{
    /// A "Revamp" tab inside the game's own settings menu, built by cloning the game's Misc tab, tab button, toggles and
    /// dropdowns (same sprites, fonts, sounds, layout), so it looks and behaves like a vanilla tab. The clones' game hooks
    /// (languagetext, togglesetting, dropdownsetting, persistent listeners into settingsscript) are removed and replaced
    /// with listeners that change RevampSettings. Hovering a row shows its explanation in a paper panel next to the tab;
    /// the text stays after the pointer leaves the row, and a long one scrolls inside the panel.
    [HarmonyPatch]
    public static class RevampMenu
    {
        public const int TabIndex = 90;
        // Per settings menu instance (the game can have more than one: main menu scene, game scene)
        private sealed class Ui { public GameObject Tab, LedOn; public TextMeshProUGUI Info; public ScrollRect InfoScroll; public readonly List<System.Action> Refreshers = new List<System.Action>(); }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<settingsscript, Ui> Uis =
            new System.Runtime.CompilerServices.ConditionalWeakTable<settingsscript, Ui>();
        private const string DefaultInfo = "<b>TLD Revamp</b>\n\nHover over a setting to see what it does.\n\nSettings are saved by the mod, separately from the game's own settings.";

        [HarmonyPatch(typeof(settingsscript), "Start")]
        [HarmonyPostfix]
        private static void AfterStart(settingsscript __instance)
        {
            try { Build(__instance); }
            catch (System.Exception e) { Plugin.Log.LogError("RevampMenu build failed: " + e); }
        }

        [HarmonyPatch(typeof(settingsscript), nameof(settingsscript.PressedTab))]
        [HarmonyPostfix]
        private static void AfterPressedTab(settingsscript __instance, int _i)
        {
            if (!Uis.TryGetValue(__instance, out var ui) || ui.Tab == null)
            {
                try { Build(__instance); } catch (System.Exception e) { Plugin.Log.LogError("RevampMenu build failed: " + e); }
                if (!Uis.TryGetValue(__instance, out ui) || ui.Tab == null) return;
            }
            bool open = _i == TabIndex;
            if (open)
            {
                Refresh(ui);
                var sr = ui.Tab.GetComponentInChildren<ScrollRect>(true);
                if (sr != null) sr.verticalNormalizedPosition = 1f; // open at the top, like a fresh tab
            }
            ui.Tab.SetActive(open);
            if (ui.LedOn != null) ui.LedOn.SetActive(open);
            SetInfo(ui, DefaultInfo);
            // A TLD Loader mod draws its own settings window with IMGUI in Config();
            // the host may only call that while a settings screen is open (Unity runs
            // the IMGUI pass several times a frame).
            ModHost.ConfigScreenOpen = open;
        }

        private static void Build(settingsscript s)
        {
            var root = s.transform.Find("NewSettings");
            if (root == null || root.Find("TabRevamp") != null) return;
            var ui = new Ui();
            Uis.Remove(s);
            Uis.Add(s, ui);
            var miscTab = root.Find("TabMisc");
            var miscButton = root.Find("ButtonMisc");
            if (miscTab == null || miscButton == null) { Plugin.Log.LogWarning("RevampMenu: settings layout not recognised"); return; }

            // --- tab button (between Player and Back)
            var button = Object.Instantiate(miscButton.gameObject, root);
            button.name = "ButtonRevamp";
            StripGameHooks(button);
            ((RectTransform)button.transform).anchoredPosition = new Vector2(-870f, 150f);
            button.GetComponentInChildren<TextMeshProUGUI>().text = "Revamp";
            var b = button.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(() => s.PressedTab(TabIndex));
            var led = button.transform.Find("ImageLedOff/ImageLedOn");
            ui.LedOn = led != null ? led.gameObject : null;
            if (ui.LedOn != null) ui.LedOn.SetActive(false);

            // --- tab panel (clone of Misc: paper background, title, scroll list, Reset button)
            ui.Tab = Object.Instantiate(miscTab.gameObject, root);
            ui.Tab.name = "TabRevamp";
            ui.Tab.SetActive(false);
            var bg = ui.Tab.transform.Find("BG");
            var content = bg.Find("Scroll/Viewport/Content");
            var toggleTpl = content.Find("ToggleAutoSave").gameObject;
            var choiceTpl = content.Find("DropdownLUT").gameObject;
            StripGameHooks(ui.Tab);
            bg.Find("Text (TMP)").GetComponent<TextMeshProUGUI>().text = "Revamp";
            var reset = bg.Find("ButtonReset");
            if (reset != null)
            {
                var rb = reset.GetComponent<Button>();
                rb.onClick = new Button.ButtonClickedEvent();
                rb.onClick.AddListener(() => { RevampSettings.ResetToDefaults(); Refresh(ui); });
                var rt = reset.GetComponentInChildren<TextMeshProUGUI>();
                if (rt != null) rt.text = "Reset";
                AddHover(ui, reset.gameObject, "<b>Reset</b>\n\nPut every Revamp setting back to its recommended default.");
            }
            toggleTpl.transform.SetParent(null, false);
            choiceTpl.transform.SetParent(null, false);
            for (int i = content.childCount - 1; i >= 0; i--) Object.DestroyImmediate(content.GetChild(i).gameObject);

            // --- explanation panel: same paper as the tab, under the Reset button. The text sits in a clone of the tab's
            // (now empty) scroll list, so a long explanation scrolls with a vanilla scrollbar instead of running off the paper.
            var infoGo = new GameObject("RevampInfo", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            infoGo.transform.SetParent(bg, false);
            var bgImg = bg.GetComponent<Image>();
            var infoImg = infoGo.GetComponent<Image>();
            infoImg.sprite = bgImg.sprite; infoImg.type = bgImg.type; infoImg.color = bgImg.color; infoImg.raycastTarget = false;
            var irt = (RectTransform)infoGo.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f); irt.pivot = new Vector2(0f, 1f);
            irt.anchoredPosition = new Vector2(1050f, -130f); irt.sizeDelta = new Vector2(380f, 700f);
            var title = bg.Find("Text (TMP)").GetComponent<TextMeshProUGUI>();
            var scrollGo = Object.Instantiate(bg.Find("Scroll").gameObject, infoGo.transform, false);
            scrollGo.name = "Scroll";
            var srt = (RectTransform)scrollGo.transform;
            srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one; srt.pivot = new Vector2(0.5f, 0.5f);
            srt.anchoredPosition = Vector2.zero; srt.offsetMin = new Vector2(24f, 24f); srt.offsetMax = new Vector2(-12f, -24f);
            var isr = ui.InfoScroll = scrollGo.GetComponent<ScrollRect>();
            var oldContent = isr.content;
            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(ContentSizeFitter));
            textGo.transform.SetParent(isr.viewport != null ? isr.viewport : srt, false);
            if (oldContent != null) Object.DestroyImmediate(oldContent.gameObject);
            var info = ui.Info = textGo.GetComponent<TextMeshProUGUI>();
            info.font = title.font; info.fontSharedMaterial = title.fontSharedMaterial;
            info.fontSize = 28f; info.color = new Color32(0x40, 0x40, 0x40, 0xFF);
            info.alignment = TextAlignmentOptions.TopLeft; info.enableWordWrapping = true; info.richText = true; info.raycastTarget = false;
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = Vector2.one; trt.pivot = new Vector2(0.5f, 1f);
            trt.anchoredPosition = Vector2.zero; trt.sizeDelta = new Vector2(isr.verticalScrollbar != null ? -40f : -12f, 0f);
            textGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            isr.content = trt; isr.horizontal = false; isr.vertical = true;
            if (isr.verticalScrollbar != null) isr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            SetInfo(ui, DefaultInfo);

            // --- rows
            // Browse on demand; opening settings alone never makes a network request.
            var mods = Object.Instantiate(reset.gameObject, content);
            mods.name = "ButtonMods";
            Place(mods, 0f, -40f, 900f, 80f);
            mods.GetComponentInChildren<TextMeshProUGUI>().text = "Mods";
            var mb = mods.GetComponent<Button>(); mb.onClick = new Button.ButtonClickedEvent();
            mb.onClick.AddListener(() => Net.ModsScreen.Open(root.gameObject));
            AddHover(ui, mods, "<b>Mods</b>\n\nBrowse approved single-file mods. Install, update, enable, disable or remove, then restart the game. Updates keep disabled mods disabled.\n\nThe catalog is fetched only when you open Mods or press Refresh. Downloads must match the approved files shipped with your RevaMP version. Mods installed by hand stay under your control.");
            var marks = Object.Instantiate(reset.gameObject, content);
            marks.name = "ButtonMarks"; Place(marks, 0f, -140f, 900f, 80f);
            var markText = marks.GetComponentInChildren<TextMeshProUGUI>();
            ui.Refreshers.Add(() => markText.text = "Session marks (" + Plugin.KeyName(Plugin.KeyMark) + ")");
            var markButton = marks.GetComponent<Button>(); markButton.onClick = new Button.ButtonClickedEvent();
            markButton.onClick.AddListener(MarkPanel.Open);
            AddHover(ui, marks, "Save a note with recent game state, browse this session's marks, edit notes or generate a comparison. Enter saves; Escape cancels. Change the Mark key in BepInEx/config/tldrevamp.core.cfg, alongside DebugReport.");
            float y = -250f;
            foreach (var e in RevampSettings.Entries)
            {
                switch (e.Kind)
                {
                    case RevampSettings.Kind.Header:
                        y -= 30f;
                        var h = new GameObject("Header", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                        h.transform.SetParent(content, false);
                        var ht = h.GetComponent<TextMeshProUGUI>();
                        ht.font = title.font; ht.fontSharedMaterial = title.fontSharedMaterial; ht.fontSize = 48f; ht.fontStyle = FontStyles.Bold;
                        ht.color = title.color; ht.alignment = TextAlignmentOptions.Left; ht.text = e.Label; ht.raycastTarget = true;
                        Place(h, 0f, y, 900f, 70f);
                        AddHover(ui, h, "<b>" + e.Label + "</b>\n\n" + e.Help);
                        y -= 100f;
                        break;
                    case RevampSettings.Kind.Toggle:
                        var t = Object.Instantiate(toggleTpl, content);
                        t.name = "Toggle_" + e.Key;
                        ((RectTransform)t.transform).anchoredPosition = new Vector2(0f, y);
                        var label = t.transform.Find("ToggleBackground/ToggleLabel");
                        var lrt = (RectTransform)label;
                        lrt.sizeDelta = new Vector2(lrt.sizeDelta.x + 450f, lrt.sizeDelta.y);
                        label.GetComponent<TextMeshProUGUI>().text = e.Label;
                        var tg = t.GetComponent<Toggle>();
                        tg.onValueChanged = new Toggle.ToggleEvent();
                        var te = e;
                        tg.onValueChanged.AddListener(on => { te.Set(on ? 1 : 0); RevampSettings.Save(); if (te.RuntimeOnly) Refresh(ui); });
                        ui.Refreshers.Add(() => tg.SetIsOnWithoutNotify(te.Get() == 1));
                        AddHover(ui, t, "<b>" + e.Label + "</b>\n\n" + e.Help);
                        y -= 100f;
                        break;
                    case RevampSettings.Kind.Choice:
                        var c = Object.Instantiate(choiceTpl, content);
                        c.name = "Choice_" + e.Key;
                        ((RectTransform)c.transform).anchoredPosition = new Vector2(500f, y);
                        c.transform.Find("Label").GetComponent<TextMeshProUGUI>().text = e.Label;
                        var dd = c.GetComponent<TMP_Dropdown>();
                        dd.onValueChanged = new TMP_Dropdown.DropdownEvent();
                        dd.ClearOptions();
                        dd.AddOptions(new List<string>(e.Options));
                        var ce = e;
                        dd.onValueChanged.AddListener(v => { ce.Set(v); RevampSettings.Save(); if (ce.RuntimeOnly) Refresh(ui); });
                        ui.Refreshers.Add(() => { dd.SetValueWithoutNotify(ce.Get()); dd.RefreshShownValue(); });
                        AddHover(ui, c, "<b>" + e.Label + "</b>\n\n" + e.Help);
                        y -= 100f;
                        break;
                }
            }
            var crt = (RectTransform)content;
            crt.sizeDelta = new Vector2(crt.sizeDelta.x, -y + 40f);
            Object.Destroy(toggleTpl);
            Object.Destroy(choiceTpl);
            Refresh(ui);
            Plugin.Log.LogInfo("RevampMenu: Revamp tab added to " + UiLab.PathOf(s.transform) + " in scene " + s.gameObject.scene.name + " — to the settings menu (" + RevampSettings.Entries.Count + " rows)");
        }

        /// Show an explanation from its top (a new text never opens scrolled halfway down the previous one).
        private static void SetInfo(Ui ui, string text)
        {
            if (ui.Info == null) return;
            ui.Info.text = text;
            if (ui.InfoScroll != null) { ui.InfoScroll.StopMovement(); ui.InfoScroll.content.anchoredPosition = Vector2.zero; }
        }

        private static void Refresh(Ui ui)
        {
            foreach (var r in ui.Refreshers) { try { r(); } catch { } }
        }

        private static void Place(GameObject go, float x, float y, float w, float h)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, h);
        }

        /// Remove the game's own behaviour from cloned UI: language texts (would overwrite our labels), setting scripts
        /// (would write into the game's settings) and persistent listeners (replaced per control above).
        private static void StripGameHooks(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<languagetext>(true)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<togglesetting>(true)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<slidersettingscript>(true)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<dropdownsetting>(true)) Object.DestroyImmediate(c);
        }

        private static bool UnderTemplate(Transform t, Transform stop)
        {
            for (; t != null && t != stop; t = t.parent) if (t.name == "Template") return true;
            return false;
        }

        /// Hover explanation: an invisible hit area over the whole row (so the label counts too) + pointer enter. No reset on
        /// exit: the text stays so the pointer can move to the panel and scroll a long one.
        private static void AddHover(Ui ui, GameObject row, string text)
        {
            var rrt = (RectTransform)row.transform;
            // union of the row's own rect and its children, in the row's local space
            var min = rrt.rect.min; var max = rrt.rect.max;
            foreach (var child in row.GetComponentsInChildren<RectTransform>(false))
            {
                if (child == rrt || UnderTemplate(child, rrt)) continue; // dropdown list template isn't part of the row
                var corners = new Vector3[4];
                child.GetWorldCorners(corners);
                for (int i = 0; i < 4; i++)
                {
                    var p = rrt.InverseTransformPoint(corners[i]);
                    min = Vector2.Min(min, p); max = Vector2.Max(max, p);
                }
            }
            var hit = new GameObject("HoverArea", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hit.transform.SetParent(row.transform, false);
            hit.transform.SetAsFirstSibling();
            hit.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var hrt = (RectTransform)hit.transform;
            hrt.anchorMin = hrt.anchorMax = rrt.pivot; hrt.pivot = new Vector2(0.5f, 0.5f);
            hrt.anchoredPosition = (min + max) * 0.5f; // local point relative to the pivot (anchors placed at the pivot)
            hrt.sizeDelta = (max - min) + new Vector2(20f, 10f);

            var trigger = row.GetComponent<EventTrigger>() ?? row.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => { if (ui.Info == null || ui.Info.text == text) return; SetInfo(ui, text); });
            trigger.triggers.Add(enter);
        }
    }
}
