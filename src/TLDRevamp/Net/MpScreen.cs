using System;
using System.Collections.Generic;
using HarmonyLib;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TLDRevamp.Net
{
    /// The multiplayer screen, opened by the game's own "Multiplayer" button (main menu and pause menu) in place of the
    /// game's lobby window. It is a copy of the game's Save/Load screen with its parts given new jobs, so it looks and
    /// sounds like the game:
    ///   tapes          friends playing The Long Drive (hosting ones first) — or, in a game, its players — with their
    ///                  Steam pictures; click one to select it (its name turns green, like a selected save)
    ///   note card      the selected friend or player: hosting? version, password, players / ping, muted, talking
    ///   name field     the password (to host with, or to join with), or an address (Address)
    ///   Save button    the main action: Host / Join / Leave / Stop hosting / Cancel
    ///   Delete button  Kick (the host), with the save screen's own "…?" Yes / No question
    ///   Load Last      Mute / Unmute (in a game), Refresh (otherwise)
    ///   Load button    Join by address / Back to friends; the host's own card: where joiners appear (next to me / at the start)
    [HarmonyPatch]
    public static class MpScreen
    {
        public static bool IsOpen => _root != null && _root.activeSelf;
        private static menuhandler _builtFor;
        private static GameObject _root;
        private static TextMeshProUGUI _title, _count, _cardName, _cardRight, _msg, _confirmText;
        private static readonly List<TextMeshProUGUI> _cardLines = new List<TextMeshProUGUI>();
        private static TMP_InputField _input;
        private static TextMeshProUGUI _inputPlaceholder;
        private static Button _primary, _secondary, _kick, _mute, _up, _down;
        private static GameObject _confirm;
        private sealed class Tile { public GameObject Go; public Button B; public Image Img; public TextMeshProUGUI Name; public Image NameBg; }
        private static readonly List<Tile> _tiles = new List<Tile>();
        private static Sprite _noImage;
        private static float _refreshAt;
        private static int _page;

        // what the screen shows
        private sealed class Entry { public ulong SteamId; public int PlayerId = -1; public string Name; public Session.Hosted Hosts; public Session.Player P; }
        private static readonly List<Entry> _entries = new List<Entry>();
        private static ulong _selSteam; private static int _selPlayer = -1; private static bool _selAny;
        private static bool _addressMode;
        private static string _message = "";
        private static float _friendsAt = -10f;

        // ---- opening: the game's Multiplayer button, F11 (Plugin.KeyMpScreen)
        [HarmonyPatch(typeof(SteamP2PMenuHandlerScript), nameof(SteamP2PMenuHandlerScript.APressedMulti))]
        [HarmonyPrefix]
        private static bool InsteadOfGameLobby() { Open(); return false; }

        public static void Toggle()
        {
            if (IsOpen) { Close(); return; }
            if (mainscript.s != null && !mainscript.s.pauseMenuOpen && DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu)
                mainscript.s.PressedEscape(false, false);   // the pause menu first, as when pressing Escape
            Open();
        }

        public static void Open()
        {
            if (!Build()) return;
            _root.SetActive(true);
            _page = 0; _addressMode = false; _confirm.SetActive(false);
            _selAny = false; _selPlayer = -1; _selSteam = 0;
            _message = Session.EndReason ?? "";
            if (Session.EndCode == Protocol.RejectPassword && Session.LastJoinSteamId != 0) { _selAny = true; _selSteam = Session.LastJoinSteamId; }
            Session.EndReason = null; Session.EndCode = 0;
            _input.SetTextWithoutNotify("");
            _friendsAt = -10f; _refreshAt = 0f;
            Refresh();
        }

        public static void Close()
        {
            if (_root != null) _root.SetActive(false);
            if (_input != null) _input.DeactivateInputField();
        }

        /// Keep the pause menu's buttons hidden while this screen is up (the game re-shows them every frame, the way it
        /// hides them for its own Save/Load screen); close with the pause menu.
        [HarmonyPatch(typeof(menuhandler), "Update")]
        [HarmonyPostfix]
        private static void MenuUpdate(menuhandler __instance)
        {
            if (Session.ShowInMenu && !IsOpen && __instance == menuhandler.s && __instance != Session.ShowInMenuNotOn && DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu && Time.timeSinceLevelLoad > 1f)
            {
                Session.ShowInMenu = false; Session.ShowInMenuNotOn = null;   // sent back to the menu (kicked, the host gone): the screen says why, as a game's own dialog would
                Open();
            }
            if (!IsOpen || __instance != _builtFor) return;
            bool menu = DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu;   // the main menu has a mainscript too, never paused
            if (!menu && mainscript.s != null && !mainscript.s.pauseMenuOpen) { Close(); return; }
            if (__instance.Menu != null && __instance.Menu.activeSelf) __instance.Menu.SetActive(false);
            if (Time.unscaledTime >= _refreshAt) Refresh();
        }

        // ---- building: a copy of the Save/Load screen
        private static bool Build()
        {
            var mh = menuhandler.s;
            if (mh == null || mh.SaveLoadObject == null) return false;
            if (_builtFor == mh && _root != null) return true;
            try { BuildFrom(mh); return true; }
            catch (Exception e) { Plugin.Log.LogError("MpScreen build failed: " + e); if (_root != null) UnityEngine.Object.Destroy(_root); _root = null; return false; }
        }

        private static void BuildFrom(menuhandler mh)
        {
            _builtFor = mh; _tiles.Clear(); _cardLines.Clear();
            var src = mh.SaveLoadObject;
            var save = src.GetComponent<newSaveScreenScript>();
            _noImage = save != null ? save.spriteNoImage : null;
            _root = UnityEngine.Object.Instantiate(src, src.transform.parent);
            _root.name = "TLDRevampMultiplayer";
            _root.SetActive(false);
            var s2 = _root.GetComponent<newSaveScreenScript>();
            if (s2 != null) UnityEngine.Object.DestroyImmediate(s2);
            foreach (var c in _root.GetComponentsInChildren<languagetext>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var b in _root.GetComponentsInChildren<Button>(true)) b.onClick = new Button.ButtonClickedEvent();
            var t = _root.transform;

            // parts with no job here
            foreach (var n in new[] { "TextLoad", "TextSaved", "TextSaveOverwrite", "TextLoadLast", "TextLoad (1)", "TMP_IF_ScreenShotPath", "ButtonFastUp", "ButtonFastDown",
                                      "ButtonJumpUp", "ButtonJumpDown", "ButtonSetActive" })
                Hide(t.Find(n));

            _title = Tmp(t, "PageText"); _count = Tmp(t, "SaveCountText");
            // the note card: band (title left, detail right) and its five lines (the save's name/date/seed/driven/version)
            var card = t.Find("InfoBackGround");
            _cardName = Tmp(card, "Text (1)"); _cardRight = Tmp(card, "TextMapname");
            _cardName.enableAutoSizing = true; _cardName.fontSizeMax = 50f; _cardName.fontSizeMin = 20f;
            var crt = (RectTransform)_cardName.transform; crt.sizeDelta = new Vector2(crt.sizeDelta.x + 200f, crt.sizeDelta.y); crt.anchoredPosition += new Vector2(100f, 0f);
            _cardName.alignment = TextAlignmentOptions.BottomLeft; _cardName.overflowMode = TextOverflowModes.Ellipsis;
            var info = card.Find("InfoObj");
            info.gameObject.SetActive(true);
            foreach (var n in new[] { "TMP_IF_Name", "TMP_IF_Date", "TMP_IF_Seed", "TMP_IF_Driven", "TMP_IF_Version" })
            {
                var f = info.Find(n).GetComponent<TMP_InputField>();
                var txt = (TextMeshProUGUI)f.textComponent;
                f.enabled = false;   // a line of text on the card, not a field
                var ph = f.placeholder; if (ph != null) ph.gameObject.SetActive(false);
                txt.richText = true; txt.enableAutoSizing = true; txt.fontSizeMax = 34f; txt.fontSizeMin = 16f; txt.overflowMode = TextOverflowModes.Ellipsis;
                _cardLines.Add(txt);
            }

            // the input
            _input = t.Find("TMP_IF_SaveName").GetComponent<TMP_InputField>();
            _input.onEndEdit = new TMP_InputField.SubmitEvent(); _input.onSubmit = new TMP_InputField.SubmitEvent();
            _input.onValueChanged = new TMP_InputField.OnChangeEvent(); _input.onSelect = new TMP_InputField.SelectionEvent(); _input.onDeselect = new TMP_InputField.SelectionEvent();
            _input.characterLimit = 64; _input.lineType = TMP_InputField.LineType.SingleLine; _input.richText = false;
            _input.onSubmit.AddListener(_ => Primary());
            _inputPlaceholder = _input.placeholder as TextMeshProUGUI;
            if (_inputPlaceholder != null) { _inputPlaceholder.gameObject.SetActive(true); _inputPlaceholder.fontStyle = FontStyles.Italic; }

            // messages: the save screen's orange "Can't Save" text, just above the main button
            _msg = Tmp(t, "TextCantSave");
            var mrt = (RectTransform)_msg.transform; mrt.anchoredPosition = new Vector2(439f, 603f); mrt.sizeDelta = new Vector2(501f, 52f);
            _msg.enableAutoSizing = true; _msg.fontSizeMax = 40f; _msg.fontSizeMin = 18f; _msg.alignment = TextAlignmentOptions.Center;
            _msg.gameObject.SetActive(false);

            _primary = Btn(t, "ButtonSave", Primary, hideIcon: true);
            _secondary = Btn(t, "ButtonLoad", Secondary, hideIcon: true);
            _kick = Btn(t, "ButtonDelete", AskKick, hideIcon: false);
            _mute = Btn(t, "ButtonLoadLast", MuteOrRefresh, hideIcon: true);
            Btn(t, "ButtonBack", Close, hideIcon: true);
            _up = Btn(t, "ButtonUp", () => { _page = Math.Max(0, _page - 1); Refresh(); }, hideIcon: false);
            _down = Btn(t, "ButtonDown", () => { _page++; Refresh(); }, hideIcon: false);
            Label(_kick, "Kick");

            // the save screen's "Delete Save?" question → "Kick NAME?"
            _confirm = t.Find("TextDelete").gameObject;
            _confirmText = _confirm.GetComponent<TextMeshProUGUI>();
            var yes = _confirm.transform.Find("ButtonDeleteYes").GetComponent<Button>(); yes.onClick.AddListener(DoKick);
            var no = _confirm.transform.Find("ButtonDeleteNo").GetComponent<Button>(); no.onClick.AddListener(() => { _confirm.SetActive(false); Refresh(); });
            Label(yes, "Yes"); Label(no, "No");
            _confirm.SetActive(false);

            // the tapes, in reading order
            var tapes = new List<Transform>();
            for (int i = 0; i < t.childCount; i++) if (t.GetChild(i).name.StartsWith("ButtonFile")) tapes.Add(t.GetChild(i));
            tapes.Sort((a, b) =>
            {
                var pa = ((RectTransform)a).anchoredPosition; var pb = ((RectTransform)b).anchoredPosition;
                return Mathf.Abs(pa.y - pb.y) > 1f ? pb.y.CompareTo(pa.y) : pa.x.CompareTo(pb.x);
            });
            foreach (var tp in tapes)
            {
                int idx = _tiles.Count;
                var tile = new Tile { Go = tp.gameObject, B = tp.GetComponent<Button>() };
                tile.Img = tp.Find("Mask2/ButtonImage").GetComponent<Image>(); tile.Img.preserveAspect = true;
                tile.Name = Tmp(tp, "ButtonNameTextBackground/ButtonNameText");
                tile.NameBg = tp.Find("ButtonNameTextBackground").GetComponent<Image>();
                tile.B.onClick.AddListener(() => SelectTile(idx));
                _tiles.Add(tile);
            }
            Plugin.Log.LogInfo("MpScreen: built from the save screen (" + _tiles.Count + " tapes)");
        }

        private static void Hide(Transform x) { if (x != null) x.gameObject.SetActive(false); }
        private static TextMeshProUGUI Tmp(Transform parent, string path) => parent.Find(path).GetComponent<TextMeshProUGUI>();

        private static Button Btn(Transform parent, string name, Action click, bool hideIcon)
        {
            var b = parent.Find(name).GetComponent<Button>();
            b.onClick.AddListener(() => click());
            if (hideIcon) { var icon = b.transform.Find("Image"); if (icon != null) icon.gameObject.SetActive(false); }
            return b;
        }

        private static void Label(Button b, string text)
        {
            var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
            if (t != null && t.text != text) t.text = text;
        }

        private static void Show(Button b, bool on, string text = null)
        {
            if (b.gameObject.activeSelf != on) b.gameObject.SetActive(on);
            if (on && text != null) Label(b, text);
        }

        // ---- what is listed
        private static void Collect()
        {
            _entries.Clear();
            if (Session.InGame)
            {
                foreach (var p in Session.Players()) _entries.Add(new Entry { SteamId = p.SteamId, PlayerId = p.Id, Name = p.Name, P = p });
                return;
            }
            if (!SteamTransport.SteamReady) return;
            bool ask = Time.unscaledTime - _friendsAt > 5f;
            if (ask) _friendsAt = Time.unscaledTime;
            uint app = SteamUtils.GetAppID().m_AppId;
            int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < n; i++)
            {
                var fid = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(fid, out var gi) || gi.m_gameID.AppID().m_AppId != app) continue;
                if (ask) SteamFriends.RequestFriendRichPresence(fid);
                _entries.Add(new Entry { SteamId = fid.m_SteamID, Name = SteamFriends.GetFriendPersonaName(fid), Hosts = Session.FriendHosts(fid.m_SteamID) });
            }
            _entries.Sort((a, b) => (a.Hosts != null) != (b.Hosts != null) ? (a.Hosts != null ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static Entry Selected()
        {
            if (!_selAny) return null;
            foreach (var e in _entries)
                if (Session.InGame ? e.PlayerId == _selPlayer : e.SteamId == _selSteam) return e;
            return null;
        }

        private static void SelectTile(int i)
        {
            int k = _page * _tiles.Count + i;
            if (k >= _entries.Count) return;
            var e = _entries[k];
            _selAny = true; _selSteam = e.SteamId; _selPlayer = e.PlayerId; _addressMode = false;
            _confirm.SetActive(false); _message = "";
            Refresh();
            if (!Session.InGame && e.Hosts != null && e.Hosts.Locked) _input.ActivateInputField();
        }

        // ---- the screen
        private static void Refresh()
        {
            _refreshAt = Time.unscaledTime + 1f;
            Collect();
            var c = Mp.Client;
            bool connecting = c != null && !c.Ready;
            bool inGame = Session.InGame;
            bool host = Mp.Server != null;
            bool world = DataFromMenuScript.s != null && !DataFromMenuScript.s.mainmenu && mainscript.s != null && mainscript.s.player != null;

            _title.text = "Multiplayer";
            int hosting = 0; foreach (var e in _entries) if (e.Hosts != null) hosting++;
            _count.text = inGame ? "Players: " + _entries.Count + "/" + MpServer.MaxPlayers : "Friends hosting: " + hosting;

            // tapes
            int per = _tiles.Count, pages = Math.Max(1, (_entries.Count + per - 1) / per);
            if (_page >= pages) _page = pages - 1;
            for (int i = 0; i < per; i++)
            {
                var tile = _tiles[i];
                int k = _page * per + i;
                bool on = k < _entries.Count && !connecting;
                if (tile.Go.activeSelf != on) tile.Go.SetActive(on);
                if (!on) continue;
                var e = _entries[k];
                string name = e.Name + (e.P != null && e.P.Host ? "  (host)" : "") + (e.P != null && e.P.Me ? "  (you)" : "");
                if (!inGame && e.Hosts == null) name += "  — not hosting";
                tile.Name.text = name;
                bool sel = _selAny && (inGame ? e.PlayerId == _selPlayer : e.SteamId == _selSteam);
                tile.Name.color = sel ? new Color32(0xC8, 0xFF, 0xC8, 0xFF) : new Color32(0xFF, 0xFF, 0xFF, 0xC8);   // the save screen's selected save: green
                var av = Avatars.Get(e.SteamId);
                tile.Img.sprite = av != null ? av : _noImage;
                tile.Img.color = !inGame && e.Hosts == null ? new Color(1f, 1f, 1f, 0.45f) : Color.white;
            }
            Show(_up, _page > 0); Show(_down, _page < pages - 1);

            var sel2 = Selected();
            string msg = _message;
            Show(_kick, false); Show(_mute, false); Show(_secondary, false);
            bool showInput = false; string placeholder = "";

            if (connecting)
            {
                Card(Session.LastJoinSteamId != 0 ? SteamFriends.GetFriendPersonaName(new CSteamID(Session.LastJoinSteamId)) : Session.LastJoinAddress ?? "", "",
                     "Joining…", c.MyId > 0 ? "Loading the host's world" : "Connecting", "", "", "");
                Show(_primary, true, "Cancel");
            }
            else if (inGame)
            {
                var p = sel2?.P ?? Session.Players().Find(x => x.Me);
                if (p != null)
                {
                    bool talking = p.Me ? Voice.Sending : Voice.Talking(p.Id);
                    bool muted = !p.Me && p.SteamId != 0 && Voice.Muted.Contains(p.SteamId);
                    string role = (p.Me ? (host ? "You host this game" : "You") : p.Host ? "Hosts this game" : "Player")
                                + (talking ? "  <color=#2E7D32>talking</color>" : muted ? "  <color=#B03030>muted</color>" : "");
                    if (p.Me)
                        Card(p.Name, p.Host ? "Host" : p.Ping >= 0 ? p.Ping + " ms" : "", role,
                             "Chat: " + ChatKey(), "Talk: " + TalkKey(), "Voice: " + VoiceModeText(),
                             host ? (Mp.Server.Password.Length > 0 ? "Password: yes" : "Password: none") : "");
                    else
                        Card(p.Name, p.Host ? "Host" : p.Ping >= 0 ? p.Ping + " ms" : "", role, "", "", "", "");
                    // the host's own card: where people who join appear (MpServer.JoinAtHost)
                    if (p.Me && host) Show(_secondary, true, MpServer.JoinAtHost ? "Joiners: next to me" : "Joiners: at the start");
                    if (!p.Me)
                    {
                        Show(_mute, true, muted ? "Unmute" : "Mute");
                        if (host) Show(_kick, !_confirm.activeSelf, "Kick");   // the game hides Delete while its Yes/No shows (in its place)
                    }
                }
                Show(_primary, true, host ? "Stop hosting" : "Leave");
            }
            else if (_addressMode)
            {
                Card("Join by address", "", "For games on your own network (LAN)", "or a host's public address.", "Enter IP:port below,", "e.g. 203.0.113.5:27070", "");
                showInput = true; placeholder = "IP address:port";
                Show(_primary, true, "Join");
                Show(_secondary, true, "Back to friends");
            }
            else if (sel2 != null)
            {
                var h = sel2.Hosts;
                bool compatible = h != null && h.Protocol == Protocol.Version && h.Version == Plugin.Version;
                Card(sel2.Name, h != null ? h.Players + "/" + h.Max : "",
                     h != null ? "Hosting a game" : "Playing, not hosting",
                     h == null ? "" : compatible ? "TLD Revamp " + h.Version : "<color=#B03030>Needs TLD Revamp " + h.Version + "</color>",
                     h == null ? "" : h.Locked ? "Password: yes" : "Password: none",
                     "", h != null && compatible ? "" : "");
                showInput = h != null && h.Locked; placeholder = "Password";
                Show(_primary, h != null, "Join");
                if (h != null) _primary.interactable = compatible;
                Show(_secondary, true, "Join by address");
            }
            else
            {
                Card(Session.MyName(), "", world ? "Host this game for your friends." : "Start or load a game first,",
                     world ? "Steam friends then see it here" : "then host it from this menu.",
                     world ? "and join from their Multiplayer menu." : "", "Friends who play now are listed", "on the left: pick one to join.");
                showInput = world; placeholder = "Password (optional)";
                Show(_primary, world, "Host");
                Show(_secondary, true, "Join by address");
            }
            if (!_primary.gameObject.activeSelf || connecting || inGame || _addressMode || sel2 == null) _primary.interactable = true;
            if (!inGame && !connecting) Show(_mute, true, "Refresh");
            if (_input.gameObject.activeSelf != showInput) _input.gameObject.SetActive(showInput);
            if (_inputPlaceholder != null && _inputPlaceholder.text != placeholder) _inputPlaceholder.text = placeholder;
            _input.contentType = placeholder.StartsWith("Password") ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            if (_confirm.activeSelf && !(inGame && Mp.Server != null && Selected()?.P != null && !Selected().P.Me)) _confirm.SetActive(false);   // the player left meanwhile
            _msg.gameObject.SetActive(msg.Length > 0);
            if (msg.Length > 0) _msg.text = msg;
        }

        private static readonly Vector3[] _corners = new Vector3[4];
        private static void Card(string name, string right, params string[] lines)
        {
            _cardName.text = Esc(name); _cardRight.text = right;
            // the name stops short of the right-hand text (the ping): it shrinks, then ends in "…"
            float m = 0f;
            if (!string.IsNullOrEmpty(right))
            {
                var nr = (RectTransform)_cardName.transform; var rr = (RectTransform)_cardRight.transform;
                rr.GetWorldCorners(_corners);
                float textLeft = nr.InverseTransformPoint(_corners[2]).x - _cardRight.GetPreferredValues(right).x;   // right-aligned
                m = Mathf.Max(0f, nr.rect.xMax - textLeft + 30f);
            }
            _cardName.margin = new Vector4(0f, 0f, m, 0f);
            for (int i = 0; i < _cardLines.Count; i++) _cardLines[i].text = i < lines.Length ? lines[i] : "";
        }

        private static string Esc(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");
        private static string ChatKey() => Keys.Bound(inputscript.IN.multiplayerchat) ? "Enter / " + Keys.Name(inputscript.IN.multiplayerchat) : "Enter";
        private static string TalkKey() => Voice.Setting == Voice.Mode.OpenMic ? "open mic" : Voice.Setting == Voice.Mode.Off ? "off" : "hold " + (Keys.Bound(inputscript.IN.voicechat) ? Keys.Name(inputscript.IN.voicechat) : "V");
        private static string VoiceModeText() => Voice.Setting == Voice.Mode.PushToTalk ? "push to talk" : Voice.Setting == Voice.Mode.OpenMic ? "open mic" : "off (Settings → Revamp)";

        // ---- actions
        private static void Primary()
        {
            _message = "";
            try
            {
                var c = Mp.Client;
                if (c != null && !c.Ready) { Mp.Stop(); }
                else if (Session.InGame) { Session.Leave(); if (DataFromMenuScript.s != null && DataFromMenuScript.s.mainmenu) Close(); }
                else if (_addressMode)
                {
                    string addr = _input.text.Trim();
                    if (addr.Length == 0) { _message = "Enter an address first."; }
                    else { if (addr.IndexOf(':') < 0) addr += ":27070"; Session.LastJoinAddress = addr; Session.LastJoinSteamId = 0; Mp.Join(addr); }
                }
                else
                {
                    var e = Selected();
                    if (e != null && e.Hosts != null) { Session.LastJoinSteamId = e.SteamId; Session.LastJoinAddress = null; Mp.JoinFriend(e.SteamId, _input.text); }
                    else if (e == null) { Mp.Host(27070, _input.text.Trim()); _selAny = false; }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("MpScreen: " + ex); _message = ex.Message; }
            _input.SetTextWithoutNotify("");
            Refresh();
        }

        private static void Secondary()
        {
            if (Session.InGame && Mp.Server != null) { MpServer.JoinAtHost = !MpServer.JoinAtHost; Refresh(); return; }   // the host's own card
            _addressMode = !_addressMode; _selAny = false; _message = "";
            _input.SetTextWithoutNotify("");
            Refresh();
            if (_addressMode) _input.ActivateInputField();
        }

        private static void MuteOrRefresh()
        {
            if (!Session.InGame) { _friendsAt = -10f; Refresh(); return; }
            var e = Selected();
            if (e == null || e.P == null || e.P.Me || e.SteamId == 0) return;
            if (!Voice.Muted.Remove(e.SteamId)) Voice.Muted.Add(e.SteamId);
            Refresh();
        }

        private static void AskKick()
        {
            var e = Selected();
            if (e == null || e.P == null || e.P.Me || Mp.Server == null) return;
            _confirmText.text = "Kick " + Esc(e.Name) + "?";
            _confirm.SetActive(true);
            _kick.gameObject.SetActive(false);
        }

        private static void DoKick()
        {
            _confirm.SetActive(false);
            var e = Selected();
            if (e == null || e.P == null || Mp.Server == null) return;
            if (Mp.Server.Kick(e.PlayerId)) { _message = e.Name + " was kicked."; _selAny = false; }
            Refresh();
        }

        /// Diagnostics / test hooks (bridge `mp screen [open|close|tile i|primary|secondary|kick|yes|mute|text <s>]`).
        public static string Test(string arg)
        {
            var a = arg.Split(new[] { ' ' }, 2);
            switch (a[0])
            {
                case "open": Toggle(); if (!IsOpen) Open(); break;
                case "close": Close(); break;
                case "tile": SelectTile(int.Parse(a[1])); break;
                case "primary": Primary(); break;
                case "secondary": Secondary(); break;
                case "kick": AskKick(); break;
                case "yes": DoKick(); break;
                case "mute": MuteOrRefresh(); break;
                case "text": _input.SetTextWithoutNotify(a.Length > 1 ? a[1] : ""); break;
            }
            if (IsOpen) Refresh();
            var tiles = new List<string>();
            foreach (var tl in _tiles) if (tl.Go.activeSelf) tiles.Add(Json.Str(tl.Name.text));
            var lines = new List<string>();
            foreach (var l in _cardLines) lines.Add(Json.Str(l.text));
            return "{\"open\":" + (IsOpen ? "true" : "false") + ",\"title\":" + Json.Str(_title?.text) + ",\"count\":" + Json.Str(_count?.text) +
                   ",\"tiles\":[" + string.Join(",", tiles) + "],\"card\":" + Json.Str(_cardName?.text) + ",\"cardRight\":" + Json.Str(_cardRight?.text) +
                   ",\"lines\":[" + string.Join(",", lines) + "],\"primary\":" + Json.Str(_primary != null && _primary.gameObject.activeSelf ? _primary.GetComponentInChildren<TextMeshProUGUI>().text : null) +
                   ",\"secondary\":" + Json.Str(_secondary != null && _secondary.gameObject.activeSelf ? _secondary.GetComponentInChildren<TextMeshProUGUI>().text : null) +
                   ",\"kick\":" + (_kick != null && _kick.gameObject.activeSelf ? "true" : "false") +
                   ",\"mute\":" + Json.Str(_mute != null && _mute.gameObject.activeSelf ? _mute.GetComponentInChildren<TextMeshProUGUI>().text : null) +
                   ",\"confirm\":" + Json.Str(_confirm != null && _confirm.activeSelf ? _confirmText.text : null) +
                   ",\"input\":" + (_input != null && _input.gameObject.activeSelf ? Json.Str(_inputPlaceholder != null ? _inputPlaceholder.text : "") : "null") +
                   ",\"message\":" + Json.Str(_msg != null && _msg.gameObject.activeSelf ? _msg.text : null) + "}";
        }
    }

    /// Steam profile pictures as sprites (cached; Steam loads a picture it doesn't have yet in the background).
    public static class Avatars
    {
        private static readonly Dictionary<ulong, Sprite> _cache = new Dictionary<ulong, Sprite>();
        private static readonly Dictionary<ulong, float> _asked = new Dictionary<ulong, float>();

        public static Sprite Get(ulong steamId)
        {
            if (steamId == 0 || !SteamTransport.SteamReady) return null;
            if (_cache.TryGetValue(steamId, out var s)) return s;
            if (_asked.TryGetValue(steamId, out float at) && Time.unscaledTime - at < 1f) return null;
            _asked[steamId] = Time.unscaledTime;
            try
            {
                int h = SteamFriends.GetLargeFriendAvatar(new CSteamID(steamId));
                if (h <= 0) { if (h == 0) SteamFriends.RequestUserInformation(new CSteamID(steamId), false); return null; }   // -1: still loading
                if (!SteamUtils.GetImageSize(h, out uint w, out uint hh) || w == 0 || hh == 0) return null;
                var buf = new byte[w * hh * 4];
                if (!SteamUtils.GetImageRGBA(h, buf, buf.Length)) return null;
                // Steam's rows run top to bottom, a texture's bottom to top
                var flipped = new byte[buf.Length]; int row = (int)w * 4;
                for (int y = 0; y < hh; y++) Buffer.BlockCopy(buf, y * row, flipped, ((int)hh - 1 - y) * row, row);
                var tex = new Texture2D((int)w, (int)hh, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(flipped); tex.Apply(false, true);
                s = Sprite.Create(tex, new Rect(0, 0, w, hh), new Vector2(0.5f, 0.5f));
                _cache[steamId] = s;
                return s;
            }
            catch (Exception e) { Plugin.Log.LogWarning("avatar: " + e.Message); return null; }
        }
    }
}
