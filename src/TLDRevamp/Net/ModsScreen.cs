using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp.Net
{
    /// Like MpScreen, reuse the game's tapes, paper card, buttons and sounds from Save/Load.
    [HarmonyPatch]
    public static class ModsScreen
    {
        private static GameObject _root, _settings, _confirm;
        private static menuhandler _builtFor;
        private static TextMeshProUGUI _title, _count, _name, _version, _message, _question;
        private static readonly List<TextMeshProUGUI> Lines = new List<TextMeshProUGUI>();
        private sealed class Tile { internal GameObject Go; internal TextMeshProUGUI Name; }
        private static readonly List<Tile> Tiles = new List<Tile>();
        private static Button _install, _remove, _source, _refresh, _up, _down;
        private static int _page, _selected;
        private static float _refreshAt;
        public static bool IsOpen => _root != null && _root.activeSelf;
        internal static bool IsVisible => _root != null && _root.activeInHierarchy;

        internal static void Open(GameObject settings)
        {
            try
            {
                if (menuhandler.s == null || menuhandler.s.SaveLoadObject == null) return;
                MpScreen.Close();
                if (_root == null || _builtFor != menuhandler.s) Build(menuhandler.s);
                _settings = settings; _settings.SetActive(false);
                _root.transform.SetAsLastSibling(); _root.SetActive(true);
                _page = 0; _selected = 0; _confirm.SetActive(false);
                ModCatalog.Refresh(); Refresh();
            }
            catch (Exception e) { Plugin.Log.LogError("ModsScreen: " + e); Close(); }
        }

        public static void Close()
        {
            ModCatalog.Cancel();
            if (_root != null) _root.SetActive(false);
            if (_settings != null) _settings.SetActive(true);
            _settings = null;
        }

        [HarmonyPatch(typeof(menuhandler), "Update")]
        [HarmonyPostfix]
        private static void MenuUpdate(menuhandler __instance)
        {
            if (!IsOpen || __instance != _builtFor) return;
            if (DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu && mainscript.s != null && !mainscript.s.pauseMenuOpen)
            { Close(); return; }
            if (__instance.Menu != null) __instance.Menu.SetActive(false);
            if (Time.unscaledTime >= _refreshAt) Refresh();
        }

        private static TextMeshProUGUI Text(Transform p, string n) => p.Find(n).GetComponent<TextMeshProUGUI>();
        private static string Esc(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");
        private static void Label(Button b, string s) => b.GetComponentInChildren<TextMeshProUGUI>(true).text = s;
        private static Button Button(Transform p, string n, Action action, bool icon = false)
        {
            var b = p.Find(n).GetComponent<Button>();
            b.onClick.AddListener(() => action());
            var image = b.transform.Find("Image");
            if (!icon && image != null) image.gameObject.SetActive(false);
            return b;
        }

        private static void Build(menuhandler mh)
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _builtFor = mh; Tiles.Clear(); Lines.Clear();
            var src = mh.SaveLoadObject;
            var save = src.GetComponent<newSaveScreenScript>();
            _root = UnityEngine.Object.Instantiate(src, src.transform.parent);
            _root.name = "TLDRevampMods"; _root.SetActive(false);
            var script = _root.GetComponent<newSaveScreenScript>();
            if (script != null) UnityEngine.Object.DestroyImmediate(script);
            foreach (var c in _root.GetComponentsInChildren<languagetext>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var b in _root.GetComponentsInChildren<Button>(true)) b.onClick = new Button.ButtonClickedEvent();
            var t = _root.transform;
            foreach (var n in new[] { "TextLoad", "TextSaved", "TextSaveOverwrite", "TextLoadLast", "TextLoad (1)", "TMP_IF_ScreenShotPath",
                "TMP_IF_SaveName", "ButtonFastUp", "ButtonFastDown", "ButtonJumpUp", "ButtonJumpDown", "ButtonSetActive" })
            { var o = t.Find(n); if (o != null) o.gameObject.SetActive(false); }
            _title = Text(t, "PageText"); _count = Text(t, "SaveCountText");
            var card = t.Find("InfoBackGround");
            _name = Text(card, "Text (1)"); _version = Text(card, "TextMapname");
            _name.enableAutoSizing = true; _name.fontSizeMax = 50; _name.fontSizeMin = 20;
            _name.overflowMode = TextOverflowModes.Ellipsis;
            var info = card.Find("InfoObj"); info.gameObject.SetActive(true);
            foreach (var n in new[] { "TMP_IF_Name", "TMP_IF_Date", "TMP_IF_Seed", "TMP_IF_Driven", "TMP_IF_Version" })
            {
                var f = info.Find(n).GetComponent<TMP_InputField>(); f.enabled = false;
                if (f.placeholder != null) f.placeholder.gameObject.SetActive(false);
                var txt = (TextMeshProUGUI)f.textComponent;
                txt.enableAutoSizing = true; txt.fontSizeMax = 34; txt.fontSizeMin = 16;
                txt.richText = false; txt.overflowMode = TextOverflowModes.Ellipsis; Lines.Add(txt);
            }
            _message = Text(t, "TextCantSave");
            var mr = (RectTransform)_message.transform; mr.anchoredPosition = new Vector2(439, 603); mr.sizeDelta = new Vector2(501, 100);
            _message.enableAutoSizing = true; _message.fontSizeMax = 32; _message.fontSizeMin = 16;
            _message.alignment = TextAlignmentOptions.Center; _message.richText = false;
            _install = Button(t, "ButtonSave", Install);
            _remove = Button(t, "ButtonDelete", AskRemove, true); Label(_remove, "Remove");
            _source = Button(t, "ButtonLoad", Source); Label(_source, "Source");
            _refresh = Button(t, "ButtonLoadLast", () => { _confirm.SetActive(false); ModCatalog.Refresh(); Refresh(); }); Label(_refresh, "Refresh");
            Button(t, "ButtonBack", Close);
            _up = Button(t, "ButtonUp", () => { _page--; Refresh(); }, true);
            _down = Button(t, "ButtonDown", () => { _page++; Refresh(); }, true);
            _confirm = t.Find("TextDelete").gameObject; _question = _confirm.GetComponent<TextMeshProUGUI>();
            Button(_confirm.transform, "ButtonDeleteYes", Remove); Button(_confirm.transform, "ButtonDeleteNo", () => _confirm.SetActive(false));
            var tapes = new List<Transform>();
            for (int i = 0; i < t.childCount; i++) if (t.GetChild(i).name.StartsWith("ButtonFile")) tapes.Add(t.GetChild(i));
            tapes.Sort((a, b) =>
            {
                var pa = ((RectTransform)a).anchoredPosition; var pb = ((RectTransform)b).anchoredPosition;
                return Mathf.Abs(pa.y - pb.y) > 1 ? pb.y.CompareTo(pa.y) : pa.x.CompareTo(pb.x);
            });
            foreach (var tp in tapes)
            {
                int i = Tiles.Count;
                var tile = new Tile { Go = tp.gameObject, Name = Text(tp, "ButtonNameTextBackground/ButtonNameText") };
                var image = tp.Find("Mask2/ButtonImage").GetComponent<Image>(); image.sprite = save != null ? save.spriteNoImage : null;
                tp.GetComponent<Button>().onClick.AddListener(() => { _selected = _page * Tiles.Count + i; _confirm.SetActive(false); Refresh(); });
                Tiles.Add(tile);
            }
        }

        private static ModCatalog.Mod Selected() => ModCatalog.Current != null && _selected >= 0 && _selected < ModCatalog.Current.Mods.Count ? ModCatalog.Current.Mods[_selected] : null;

        private static string Installed(ModCatalog.Mod m, out bool manual)
        {
            manual = false; string newest = null;
            // Include BepInEx plugins which have not registered with the multiplayer API.
            foreach (var p in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                if (p.Metadata.GUID == m.Id && File.Exists(p.Location))
                {
                    string v = p.Metadata.Version.ToString();
                    if (!ModFiles.Managed(ModTransfer.InstallDir, p.Location)) { manual = true; return v; }
                    if (newest == null || Newer(v, newest)) newest = v;
                }
            foreach (var p in MpApi.Mods)
                if (p.Id == m.Id && File.Exists(p.File))
                {
                    if (!ModFiles.Managed(ModTransfer.InstallDir, p.File)) { manual = true; return p.Version; }
                    if (newest == null || Newer(p.Version, newest)) newest = p.Version;
                }
            // Pending installs/removals are reflected immediately, before BepInEx runs again.
            foreach (var f in ModTransfer.ManagedFiles(m.Id))
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                if (!stem.StartsWith(m.Id + "-", StringComparison.Ordinal)) continue;
                string v = stem.Substring(m.Id.Length + 1);
                if (newest == null || Newer(v, newest)) newest = v;
            }
            return newest;
        }

        private static bool Newer(string offered, string installed) => Version.TryParse(offered, out var a) && Version.TryParse(installed, out var b) ? a > b : offered != installed;

        private static void Refresh()
        {
            _refreshAt = Time.unscaledTime + 0.25f;
            var mods = ModCatalog.Current?.Mods;
            int count = mods?.Count ?? 0, pages = Math.Max(1, (count + Tiles.Count - 1) / Math.Max(1, Tiles.Count));
            _page = Math.Max(0, Math.Min(_page, pages - 1));
            _title.text = "Mods  " + (_page + 1) + "/" + pages; _count.text = count + " mods";
            for (int i = 0; i < Tiles.Count; i++)
            {
                int index = _page * Tiles.Count + i; var tile = Tiles[i]; tile.Go.SetActive(index < count);
                if (index >= count) continue;
                var item = mods[index]; bool approved = ModCatalog.Reason(item, ModCatalog.Offered(item)) == null;
                tile.Name.text = Esc(item.Name ?? item.Id);
                tile.Name.color = !approved ? Color.gray : index == _selected ? new Color32(0xC8, 0xFF, 0xC8, 0xFF) : Color.white;
            }
            _up.gameObject.SetActive(_page > 0); _down.gameObject.SetActive(_page < pages - 1);
            var m = Selected();
            _install.gameObject.SetActive(m != null || ModCatalog.Busy); _remove.gameObject.SetActive(false); _source.gameObject.SetActive(m != null && ModCatalog.SourceUrl(m.Source));
            _refresh.interactable = !ModCatalog.Busy;
            Label(_install, ModCatalog.Busy ? "Cancel" : "Install"); _install.interactable = ModCatalog.Busy;
            _name.text = m != null ? Esc(m.Name ?? m.Id) : "Approved mods"; _version.text = "";
            foreach (var line in Lines) line.text = "";
            if (m != null)
            {
                var v = ModCatalog.Offered(m); string reason = ModCatalog.Reason(m, v);
                string installed = Installed(m, out bool manual);
                bool update = installed != null && Newer(v.Version, installed);
                _version.text = Esc(v.Version);
                Lines[0].text = "By " + (m.Author ?? "unknown") + "  |  " + ModTransfer.Size(v.Size);
                Lines[1].text = m.Description ?? "Single-file mod.";
                Lines[2].text = m.Source ?? "No source link provided.";
                Lines[3].text = "Installed version: " + (installed ?? "none");
                Lines[4].text = manual ? "Installed by hand (not managed)." : reason ?? (installed == null ? "Not installed." : update ? "Update available." : "Installed.");
                if (!ModCatalog.Busy)
                {
                    Label(_install, update ? "Update" : "Install");
                    _install.interactable = !manual && reason == null && (installed == null || update);
                }
                _remove.gameObject.SetActive(!manual && !_confirm.activeSelf && ModTransfer.ManagedFiles(m.Id).Count > 0);
                _remove.interactable = !ModCatalog.Busy;
            }
            _message.text = ModCatalog.Progress + (ModCatalog.RestartRequired ? "\nRestart the game to load it (or finish removal)." : "");
            _message.gameObject.SetActive(_message.text.Length > 0);
        }

        private static void Install()
        {
            if (ModCatalog.Busy) ModCatalog.Cancel();
            else
            {
                var m = Selected();
                if (m != null)
                {
                    string installed = Installed(m, out bool manual);
                    var v = ModCatalog.Offered(m);
                    if (manual || installed != null && !Newer(v.Version, installed)) return;
                    ModCatalog.Download(m, v);
                }
            }
            _confirm.SetActive(false); Refresh();
        }
        private static void Source() { var m = Selected(); if (m != null && ModCatalog.SourceUrl(m.Source)) Application.OpenURL(m.Source); }
        private static void AskRemove()
        {
            if (ModCatalog.Busy || Selected() == null) return;
            _question.text = "Remove " + Esc(Selected().Name) + " at the next start?"; _confirm.SetActive(true); Refresh();
        }
        private static void Remove()
        {
            _confirm.SetActive(false);
            var m = Selected(); if (ModCatalog.Busy || m == null) return;
            Installed(m, out bool manual); if (manual) return;
            try
            {
                foreach (var f in ModTransfer.ManagedFiles(m.Id)) { ModTransfer.Remove(f); ModCatalog.RestartRequired = true; }
                ModCatalog.Message = "Removed " + m.Name + ".";
            }
            catch (Exception e) { ModCatalog.Message = "Could not remove: " + e.Message; }
            Refresh();
        }
    }
}
