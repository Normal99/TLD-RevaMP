using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp.Net
{
    /// The chat on screen, made of the game's own HUD parts so it looks like the game: lines are copies of its key-hint
    /// text (white with a shadow, left side between the clock and the hints), the input is a copy of the save screen's
    /// name field, the "who is talking" list uses the microphone icon of the game's own MP voice chat (top right).
    ///   Enter (or the game's own "Multiplayer chat" key when bound) opens the input; Enter sends, Escape closes.
    ///   While typing the game takes no input (mainscript.SomeInputSelected — the game's own check for its text fields).
    ///   Lines fade after FadeS; while typing the last MaxShown stay.
    [HarmonyPatch]
    public static class Chat
    {
        public static float FadeS = 12f;
        public const int MaxShown = 10;
        public static bool Typing { get; private set; }
        private static GameObject _root, _lineTpl, _bg, _talkRoot;
        private static TMP_InputField _input;
        private static readonly List<Text> _lines = new List<Text>();
        private static readonly List<GameObject> _talkRows = new List<GameObject>();
        private static Sprite _mic;
        private static int _openedFrame;
        private static menuhandler _builtFor;

        /// Runner, every frame.
        public static void Tick()
        {
            if (!Build()) return;
            bool world = mainscript.s != null && mainscript.s.player != null && DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu;
            if (Typing)
            {
                if (!Session.InGame || !world || mainscript.s.pauseMenuOpen) Close();
                else if (Time.frameCount > _openedFrame + 2 && !_input.isFocused) Close();   // focus lost (alt-tab, a click)
            }
            else if (Session.InGame && world && !mainscript.s.pauseMenuOpen && !mainscript.s.SomeInputSelected() && OpenKey()) Open();
            if (_root.activeSelf != world) _root.SetActive(world);
            if (_talkRoot != null && _talkRoot.activeSelf != world) _talkRoot.SetActive(world);   // not over the main menu (a kick's notice showed behind its buttons)
            if (!world) return;
            Layout();
            Talkers();
        }

        private static bool OpenKey()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) return true;
            try { return Keys.Bound(inputscript.IN.multiplayerchat) && settingsscript.s.S.inputs[(int)inputscript.IN.multiplayerchat].GetD(); }
            catch { return false; }
        }

        public static void Open()
        {
            if (!Build()) return;
            Typing = true; _openedFrame = Time.frameCount;
            _input.gameObject.SetActive(true);
            _input.SetTextWithoutNotify("");
            _input.ActivateInputField();
        }

        public static void Close()
        {
            Typing = false;
            if (_input != null) { _input.DeactivateInputField(); _input.gameObject.SetActive(false); }
        }

        private static void Submit(string text)
        {
            if (!Typing || Time.frameCount == _openedFrame) return;   // the Enter that opened it
            Session.Say(text);
            Close();
        }

        // ---- building (once per menuhandler: the game scene's HUD)
        private static bool Build()
        {
            var mh = menuhandler.s;
            if (mh == null || mh.GE == null) return false;
            if (_builtFor == mh && _root != null) return true;
            _builtFor = mh; _lines.Clear(); _talkRows.Clear(); Typing = false;
            var hud = mh.GE.transform.parent;
            _root = new GameObject("TLDRevampChat", typeof(RectTransform));
            _root.transform.SetParent(hud, false);
            _root.transform.SetSiblingIndex(mh.GE.transform.GetSiblingIndex() + 1);   // under the menus
            var rt = (RectTransform)_root.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(-940f, 560f); rt.sizeDelta = new Vector2(900f, 380f);

            // a shade behind the lines while typing: the save screen's dark name bar colour
            _bg = new GameObject("Shade", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _bg.transform.SetParent(_root.transform, false);
            var bimg = _bg.GetComponent<Image>(); bimg.color = new Color(0f, 0f, 0f, 0.35f); bimg.raycastTarget = false;
            var brt = (RectTransform)_bg.transform; brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = new Vector2(-12f, -12f); brt.offsetMax = new Vector2(12f, 12f);
            _bg.SetActive(false);

            _lineTpl = Object.Instantiate(mh.GE, _root.transform);
            _lineTpl.name = "LineTemplate";
            foreach (var c in _lineTpl.GetComponentsInChildren<languagetext>(true)) Object.DestroyImmediate(c);
            var tt = _lineTpl.GetComponent<Text>();
            tt.horizontalOverflow = HorizontalWrapMode.Wrap; tt.verticalOverflow = VerticalWrapMode.Overflow;
            tt.alignment = TextAnchor.LowerLeft; tt.raycastTarget = false; tt.supportRichText = true;
            Plugin.Log.LogInfo("Chat: hint text size " + tt.fontSize + (tt.resizeTextForBestFit ? " (best fit " + tt.resizeTextMaxSize + ")" : ""));
            if (tt.resizeTextForBestFit) { tt.fontSize = tt.resizeTextMaxSize; tt.resizeTextForBestFit = false; }
            tt.fontSize = Mathf.RoundToInt(tt.fontSize * 0.75f);
            foreach (var sh in _lineTpl.GetComponents<Shadow>()) sh.effectDistance *= 0.75f;   // Outline too: its edge was sized for the larger text // the hint text's size reads as an instruction; chat sits a step below it
            var lrt = (RectTransform)_lineTpl.transform;
            lrt.anchorMin = lrt.anchorMax = lrt.pivot = Vector2.zero; lrt.sizeDelta = new Vector2(900f, 40f);
            _lineTpl.SetActive(false);

            // the input: the save screen's name field
            var saveName = savedatascript.s != null && savedatascript.s.saveScreen != null ? savedatascript.s.saveScreen.InputFieldSaveName : null;
            if (saveName == null) saveName = Object.FindObjectOfType<newSaveScreenScript>(true)?.InputFieldSaveName;
            if (saveName != null)
            {
                var go = Object.Instantiate(saveName.gameObject, _root.transform);
                go.name = "ChatInput";
                foreach (var c in go.GetComponentsInChildren<languagetext>(true)) Object.DestroyImmediate(c);
                _input = go.GetComponent<TMP_InputField>();
                _input.onEndEdit = new TMP_InputField.SubmitEvent(); _input.onSubmit = new TMP_InputField.SubmitEvent();
                _input.onValueChanged = new TMP_InputField.OnChangeEvent(); _input.onSelect = new TMP_InputField.SelectionEvent();
                _input.onDeselect = new TMP_InputField.SelectionEvent();
                _input.characterLimit = MpServer.ChatMaxLen; _input.lineType = TMP_InputField.LineType.SingleLine;
                _input.contentType = TMP_InputField.ContentType.Standard; _input.richText = false;
                _input.onSubmit.AddListener(Submit);
                if (_input.placeholder is TMP_Text ph) { ph.text = "Say something…"; ph.enableAutoSizing = false; ph.fontSize = 30f; }
                if (_input.textComponent != null) { _input.textComponent.enableAutoSizing = false; _input.textComponent.fontSize = 30f; }
                var irt = (RectTransform)go.transform;
                irt.anchorMin = irt.anchorMax = irt.pivot = Vector2.zero;
                irt.anchoredPosition = new Vector2(0f, -70f); irt.sizeDelta = new Vector2(900f, 56f);
                go.SetActive(false);
            }
            else Plugin.Log.LogWarning("Chat: no save-name field to copy — chat input unavailable");

            // who is talking: the game's MP microphone icon, top right
            var micImg = Object.FindObjectOfType<SteamP2PMenuHandlerScript>(true)?.GMicrophone?.GetComponent<Image>();
            _mic = micImg != null ? micImg.sprite : null;
            _talkRoot = new GameObject("TLDRevampTalking", typeof(RectTransform));
            _talkRoot.transform.SetParent(hud, false);
            _talkRoot.transform.SetSiblingIndex(_root.transform.GetSiblingIndex() + 1);
            var trt = (RectTransform)_talkRoot.transform;
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(1f, 1f);
            trt.anchoredPosition = new Vector2(940f, -110f); trt.sizeDelta = new Vector2(500f, 400f);
            Plugin.Log.LogInfo("Chat: HUD built (" + (_input != null ? "with" : "without") + " input, mic icon " + (_mic != null) + ")");
            return true;
        }

        private static string Esc(string s) => s.Replace("<", "‹").Replace(">", "›");   // no rich-text tags from players

        private static void Layout()
        {
            float now = Time.unscaledTime;
            var all = Session.Lines;
            int first = Mathf.Max(0, all.Count - MaxShown);
            bool any = false;
            int li = 0;
            float y = 0f;
            for (int i = all.Count - 1; i >= first; i--)
            {
                var l = all[i];
                float age = now - l.At;
                float a = Typing ? 1f : Mathf.Clamp01(FadeS + 1f - age);
                if (a <= 0f) break;
                any = true;
                while (_lines.Count <= li)
                {
                    var go = Object.Instantiate(_lineTpl, _root.transform);
                    go.name = "Line"; go.SetActive(true);
                    _lines.Add(go.GetComponent<Text>());
                }
                var t = _lines[li++];
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                string s = l.Kind == 0 ? "<color=#F0DC96>" + Esc(l.Name) + ":</color> " + Esc(l.Text) : "<color=#C8C8C8><i>" + Esc(l.Text) + "</i></color>";
                if (t.text != s) t.text = s;
                var c = t.color; c.a = a; t.color = c;
                float h = t.preferredHeight;
                var rt = (RectTransform)t.transform;
                rt.anchoredPosition = new Vector2(0f, y); rt.sizeDelta = new Vector2(900f, h);
                y += h + 4f;
                if (y > 380f) break;
            }
            for (int i = li; i < _lines.Count; i++) if (_lines[i].gameObject.activeSelf) _lines[i].gameObject.SetActive(false);
            if (_bg.activeSelf != (Typing && any)) _bg.SetActive(Typing && any);
            if (Typing && any) { var brt = (RectTransform)_bg.transform; brt.offsetMax = new Vector2(12f, y - 380f + 12f); }
        }

        private static readonly List<string> _talkNames = new List<string>();

        private static void Talkers()
        {
            _talkNames.Clear();
            if (Session.InGame)
            {
                if (Voice.Sending) _talkNames.Add(Session.MyName());
                foreach (var rp in RemotePlayers.Each()) if (Voice.Talking(rp.id)) _talkNames.Add(rp.name);
            }
            while (_talkRows.Count < _talkNames.Count) _talkRows.Add(TalkRow());
            for (int i = 0; i < _talkRows.Count; i++)
            {
                bool on = i < _talkNames.Count;
                if (_talkRows[i].activeSelf != on) _talkRows[i].SetActive(on);
                if (!on) continue;
                var t = _talkRows[i].GetComponentInChildren<Text>();
                string s = Esc(_talkNames[i]);
                if (t.text != s) t.text = s;
            }
        }

        private static GameObject TalkRow()
        {
            int i = _talkRows.Count;
            var row = new GameObject("Talking", typeof(RectTransform));
            row.transform.SetParent(_talkRoot.transform, false);
            var rt = (RectTransform)row.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(0f, -i * 52f); rt.sizeDelta = new Vector2(500f, 48f);
            if (_mic != null)
            {
                var ic = new GameObject("Mic", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Shadow));
                ic.transform.SetParent(row.transform, false);
                var img = ic.GetComponent<Image>(); img.sprite = _mic; img.preserveAspect = true; img.raycastTarget = false;
                var irt = (RectTransform)ic.transform; irt.anchorMin = irt.anchorMax = irt.pivot = new Vector2(1f, 0.5f);
                irt.anchoredPosition = Vector2.zero; irt.sizeDelta = new Vector2(56f, 40f);
            }
            var label = Object.Instantiate(_lineTpl, row.transform);
            label.name = "Name"; label.SetActive(true);
            var t = label.GetComponent<Text>(); t.alignment = TextAnchor.MiddleRight;
            var lrt = (RectTransform)label.transform; lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(1f, 0.5f);
            lrt.anchoredPosition = new Vector2(-64f, 0f); lrt.sizeDelta = new Vector2(430f, 48f);
            return row;
        }

        /// Typing: the game takes no input (its own check for its text fields).
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.SomeInputSelected))]
        [HarmonyPostfix]
        private static void InputSelected(ref bool __result) { if (Typing) __result = true; }

        /// Escape while typing closes the chat; on the multiplayer screen it goes back to the menu — not the game's
        /// own escape (which would close the whole pause menu).
        [HarmonyPatch(typeof(mainscript), nameof(mainscript.PressedEscape))]
        [HarmonyPrefix]
        private static bool Escape()
        {
            if (Typing) { Close(); return false; }
            if (MpScreen.IsOpen) MpScreen.Close();   // then the game's own escape, as from its Save/Load screen
            return true;
        }

        /// Diagnostics / test hooks (bridge `mp chat`, `mp chat say <text>`, `mp chat open|close`).
        public static string Status()
        {
            int shown = 0; foreach (var t in _lines) if (t != null && t.gameObject.activeInHierarchy && t.color.a > 0f) shown++;
            return "{\"built\":" + (_root != null ? "true" : "false") + ",\"typing\":" + (Typing ? "true" : "false") + ",\"focused\":" + (_input != null && _input.isFocused ? "true" : "false") +
                   ",\"shownLines\":" + shown + ",\"talkRows\":" + _talkNames.Count + ",\"session\":" + Session.Status() + "}";
        }
    }
}
