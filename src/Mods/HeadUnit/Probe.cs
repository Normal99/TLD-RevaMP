using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HeadUnit
{
    /// Checks for tools/headunit.py, called through TLD Revamp's debug bridge (`call HeadUnit.Probe.<name> ...`):
    /// a unit into the nearest car's radio slot as a player would, a button pressed along a ray at it as the crosshair
    /// does, and a picture of the screen.
    public static class Probe
    {
        private static UnitBehaviour Nearest(bool mounted)
        {
            var pl = mainscript.s.player.transform.position;
            return UnitBehaviour.All.Where(u => u != null && Attached(u) == mounted)
                .OrderBy(u => (u.transform.position - pl).sqrMagnitude).FirstOrDefault();
        }

        private static bool Attached(UnitBehaviour u) { var a = u.GetComponent<attachablescript>(); return a != null && a.attached && u.Car != null; }

        /// Every unit: attached or loose, which car, where its screen faces in the car's space.
        public static string List()
        {
            var rows = new List<string>();
            foreach (var u in UnitBehaviour.All)
            {
                if (u == null) continue;
                var a = u.GetComponent<attachablescript>();
                string f = "";
                if (u.Car != null && u.Touch != null)
                {
                    var ct = u.Car.transform;
                    f = $" at {ct.InverseTransformPoint(u.Touch.Root.position)} faces {ct.InverseTransformDirection(-u.Touch.Root.forward)}";
                }
                rows.Add($"{u.name}: {(a != null && a.attached ? "attached to " + (a.slot != null ? a.slot.name : "?") : "loose")}, car {(u.Car != null ? u.Car.name : "none")}{f}");
            }
            return string.Join(" | ", rows);
        }

        private static carscript NearestCar()
        {
            var pl = mainscript.s.player.transform.position;
            return UnityEngine.Object.FindObjectsOfType<carscript>().OrderBy(c => (c.transform.position - pl).sqrMagnitude).FirstOrDefault();
        }

        /// The nearest loose unit stuck where the player looks, as the game's glue key does (attachablescript.Attach on
        /// the look ray's hit): stuck or not, the body shown or not, the screen's back to the spot, facing the player.
        public static string Stick()
        {
            var u = UnitBehaviour.All.Where(x => x != null && (x.Attachable == null || !x.Attachable.attached))
                .OrderBy(x => (x.transform.position - mainscript.s.player.transform.position).sqrMagnitude).FirstOrDefault();
            var cam = mainscript.s.player.Cam;
            if (u == null || cam == null) return "no loose unit";
            RaycastHit hit = default; bool got = false;
            foreach (var h in Physics.RaycastAll(cam.transform.position, cam.transform.forward, 3f).OrderBy(h => h.distance))
                if (!h.collider.isTrigger && !h.collider.transform.IsChildOf(u.transform) && !h.collider.transform.IsChildOf(mainscript.s.player.transform)) { hit = h; got = true; break; }
            if (!got) return "nothing within 3 m where the player looks";
            u.Attachable.Attach(hit);
            return StuckState(u, hit.point, hit.collider.name);
        }

        public static string StuckState(UnitBehaviour u, Vector3 spot, string on)
        {
            var bezel = u.transform.Find(Look.BezelName);
            var back = bezel != null ? bezel.position + bezel.forward * (bezel.lossyScale.z * 0.5f) : Vector3.zero;
            var cam = mainscript.s.player.Cam.transform;
            float faces = u.Touch != null ? Vector3.Dot(-u.Touch.Root.forward, (cam.position - u.Touch.Root.position).normalized) : -9f;
            int shown = u.GetComponentsInChildren<Renderer>().Count(r => r.enabled), solid = u.GetComponentsInChildren<Collider>().Count(c => c.enabled && !c.isTrigger);
            return $"stuck {Glue.Stuck(u)} on {on}, car {(u.GetComponentInParent<carscript>() != null ? u.GetComponentInParent<carscript>().name : "none")}; body shown {u.BodyShown}; "
                + $"renderers on {shown}, solid colliders {solid}; screen back {Vector3.Distance(back, spot):F3} m from the spot; faces the player {faces:F2}";
        }

        /// The nearest loose unit into the nearest car's radio slot (its own radio out first, dropped beside the car).
        public static string Mount()
        {
            var u = Nearest(false); var car = NearestCar();
            if (u == null || car == null) return "no loose unit or no car";
            var att = u.GetComponent<attachablescript>();
            var slots = car.GetComponentsInChildren<partslotscript>(true).Where(s => s.tipus != null && Units.TakesRadio(s.tipus)).ToList();
            if (slots.Count == 0) return "car " + car.name + ": no radio slot";
            var slot = slots[0];
            string was = "empty";
            if (slot.hasPart())
            {
                var old = slot.part();
                was = old.name;
                if (!slot.UnCraft()) return "couldn't take out " + old.name;
                // out of the way for the test (the car's passenger area stores it in front of the dashboard otherwise)
                UnityEngine.Object.Destroy(old.tosave != null && old.tosave.P != null ? old.tosave.P.gameObject : old.gameObject);
            }
            bool fits = slot.CanCraft(att);
            if (!fits) return $"car {car.name}: slot {slot.name} (was {was}) CanCraft false; slot tipus {string.Join(",", slot.tipus)}";
            slot.Craft(att);
            return $"car {car.name}: slot {slot.name} (was {was}) CanCraft true, mounted {u.name}";
        }

        /// The player put in front of the mounted unit, looking at its screen (for a real screenshot: bridge `shot`,
        /// with the game's own image effects).
        public static string Aim(float dist) => AimBeside(0f, 0f);

        /// The look turned at the mounted unit's screen, moved `right` and `up` metres in the car's frame (beside it:
        /// the dashboard, for Stick).
        public static string AimBeside(float right, float up)
        {
            var u = Nearest(true);
            if (u == null || u.Touch == null) return "no mounted unit";
            var p = mainscript.s.player; var t = u.Touch.Root;
            var cam = p.Cam != null ? p.Cam.transform : p.transform;
            var ct = u.Car != null ? u.Car.transform : t;
            var d = t.position + ct.right * right + ct.up * up - cam.position;
            var flat = new Vector3(d.x, 0f, d.z);
            if (flat.sqrMagnitude > 1e-4f) p.BodyRot.rotation = Quaternion.LookRotation(flat);
            p.FyRot = -Mathf.Atan2(d.y, flat.magnitude) * Mathf.Rad2Deg;
            p.Th.localEulerAngles = new Vector3(p.FyRot, 0f, 0f);
            return $"eye {d.magnitude:F2} m from the spot";
        }

        /// The bezel shown or hidden (A/B for the see-through look).
        public static string Bezel(bool on)
        {
            var u = Nearest(true);
            var b = u != null ? u.transform.Find("HeadUnitBezel") : null;
            if (b == null) return "no bezel";
            b.GetComponent<Renderer>().enabled = on;
            return "bezel " + (on ? "on" : "off");
        }

        /// Where the mounted unit's screen is and which way it faces (towards the driver's seat is right).
        /// The game's station list (its own names and frequencies) and the names the unit shows for them.
        public static string Stations()
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (bool am in new[] { false, true })
                foreach (var ch in HeadUnit.Stations.Band(am))
                    parts.Add((am ? "AM " : "FM ") + ch.FR + " '" + ch.name + "'" + (ch.custom ? " custom" : "") + " -> " + HeadUnit.Stations.Name(am, ch, true));
            return string.Join("; ", parts);
        }

        public static string Where()
        {
            var u = Nearest(true);
            if (u == null || u.Touch == null) return "no mounted unit";
            var t = u.Touch.Root;
            var seat = u.Car.GetComponentsInChildren<seatscript>(true).FirstOrDefault();
            var toSeat = seat != null ? (seat.transform.position - t.position).normalized : Vector3.zero;
            float facing = Vector3.Dot(-t.forward, toSeat);
            var buttons = string.Join(" ", u.Touch.ButtonIds());
            return $"car {u.Car.name}; screen faces the seat {facing:F2} (1 = straight at it); radio {(u.Radio != null ? (u.Radio.ON ? "on" : "off") : "MISSING")}; buttons {buttons}";
        }

        /// A press on button `id` along a ray from in front of the screen; the radio's state before and after.
        public static string Press(string id)
        {
            var u = Nearest(true);
            if (u == null || u.Touch == null) return "no mounted unit";
            if (!u.Touch.Where(id, out var p, out var outward)) return "no button " + id + " (has " + string.Join(" ", u.Touch.ButtonIds()) + ")";
            string before = State(u);
            Touch.Find(new Ray(p + outward * 0.6f, -outward));
            if (Touch.LookedButton == null) return "the ray found no button (" + id + ")";
            if (Touch.LookedButton.Id != id) return "the ray found " + Touch.LookedButton.Id + ", not " + id;
            Touch.LookedButton.Press?.Invoke();
            return $"pressed {id}: {before} -> {State(u)}";
        }

        /// Every renderer of the mounted unit and near it, in the screen's own space (z < 0: in front of the screen's
        /// plane, towards the player): name, its z range, x/y range.
        public static string Geo()
        {
            var u = Nearest(true);
            if (u == null || u.Touch == null) return "no mounted unit";
            var t = u.Touch.Root;
            var rows = new List<string>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                var b = r.bounds;
                if ((b.center - t.position).sqrMagnitude > 0.5f * 0.5f) continue;
                float zmin = float.MaxValue, zmax = float.MinValue, xmin = zmin, xmax = zmax, ymin = zmin, ymax = zmax;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var l = t.InverseTransformPoint(c);
                    zmin = Mathf.Min(zmin, l.z); zmax = Mathf.Max(zmax, l.z); xmin = Mathf.Min(xmin, l.x); xmax = Mathf.Max(xmax, l.x); ymin = Mathf.Min(ymin, l.y); ymax = Mathf.Max(ymax, l.y);
                }
                if (xmax < -u.Touch.W / 2 || xmin > u.Touch.W / 2 || ymax < -u.Touch.H / 2 || ymin > u.Touch.H / 2) continue;
                if (r.transform.IsChildOf(u.transform)) continue;
                string path = r.name; for (var q = r.transform.parent; q != null; q = q.parent) path = q.name + "/" + path;
                rows.Add($"{path}: z {zmin:F3}..{zmax:F3} x {xmin:F2}..{xmax:F2} y {ymin:F2}..{ymax:F2}");
            }
            rows.Sort();
            return string.Join(" | ", rows);
        }

        /// A YouTube video into the mounted unit's queue, as + LINK does after yt-dlp found it.
        public static string Add(string id)
        {
            var u = Nearest(true);
            if (u == null) return "no mounted unit";
            bool ok = u.Edit(w => { w.Byte((byte)Op.Add); w.VarUInt(1); w.String(id); w.String("probe " + id); w.Float(0f); });
            return ok ? "added " + id : "edit refused";
        }

        /// A link (YouTube or Spotify) into the mounted unit's queue, as + LINK's ADD does.
        public static string Link(string link)
        {
            var u = Nearest(true);
            if (u == null) return "no mounted unit";
            Controls.AddLink(u, link);
            return "adding " + link + "; ffmpeg " + (Ytdlp.HasFfmpeg ? "present" : "missing");
        }

        /// A saved Spotify embed page (in the cache folder) as the mod reads it.
        public static string SpotifyParse(string file) => Spotify.ParseCheck(System.IO.File.ReadAllText(System.IO.Path.Combine(Ytdlp.CacheDir, file)));

        public static string Mp4(string file) => HeadUnit.Video.Mp4Seconds(System.IO.Path.Combine(Ytdlp.CacheDir, file)).ToString("F2");
        private static UnityEngine.Video.VideoPlayer _fileProbe; private static string _fileProbeErr = "";
        /// A cache file played on its own player (never destroyed: Proton), to see what this machine decodes.
        public static string PlayFile(string file)
        {
            if (_fileProbe == null)
            {
                var go = new GameObject("HeadUnitProbeVideo"); UnityEngine.Object.DontDestroyOnLoad(go);
                _fileProbe = go.AddComponent<UnityEngine.Video.VideoPlayer>();
                _fileProbe.playOnAwake = false; _fileProbe.renderMode = UnityEngine.Video.VideoRenderMode.RenderTexture;
                _fileProbe.targetTexture = new RenderTexture(640, 360, 0);
                _fileProbe.audioOutputMode = UnityEngine.Video.VideoAudioOutputMode.None;
                _fileProbe.errorReceived += (vp, m) => _fileProbeErr = m;
            }
            _fileProbeErr = "";
            _fileProbe.url = "file:///" + System.IO.Path.Combine(Ytdlp.CacheDir, file).Replace('\\', '/');
            _fileProbe.Play();
            return "playing " + _fileProbe.url;
        }
        public static string FileState() => _fileProbe == null ? "none" : $"prepared {_fileProbe.isPrepared} playing {_fileProbe.isPlaying} time {_fileProbe.time:F1} frame {_fileProbe.frame}/{_fileProbe.frameCount} length {_fileProbe.length:F1} {_fileProbe.width}x{_fileProbe.height} err '{_fileProbeErr}'";

        /// This machine's video volume (its own: not shared), the unit's sound now (its loudest sample of the last
        /// 1024), and whether this machine converts videos.
        public static string Sound()
        {
            var u = Nearest(true);
            var a = u?.VideoAudio;
            float peak = 0f;
            if (a != null) { var d = new float[1024]; a.GetOutputData(d, 0); foreach (var x in d) peak = Mathf.Max(peak, Mathf.Abs(x)); }
            return $"volume {Plugin.VideoVolume.Value:F1} source {(a != null ? a.volume.ToString("F2") : "none")} peak {peak:F3} convert {Plugin.Convert.Value}";
        }
        /// The mounted unit's NAV map as drawn (TheLongDrive_Data/headunit_map_<name>.png), and its pixels by colour:
        /// the main road's, the other roads'.
        public static string MapPng(string name)
        {
            var t = Nearest(true)?.MapTexture;
            if (t == null) return "no map (NAV not opened)";
            var px = t.GetPixels32();
            int main = 0, road = 0;
            foreach (var c in px) { if (c.r == 240 && c.g == 222 && c.b == 120) main++; else if (c.r == 150 && c.g == 155 && c.b == 165) road++; }
            string path = System.IO.Path.Combine(Application.dataPath, $"headunit_map_{name}.png");
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(t));
            return $"main road {main} px, other roads {road} px: {path}";
        }
        /// The mounted unit's + ADD dialog: `text` (a link, or words to search) submitted; then SearchResults.
        public static string Search(string text) { var u = Nearest(true); if (u == null) return "no mounted unit"; u.TestSubmit(text.Replace('_', ' ')); return "submitted " + text; }
        public static string SearchResults() => Nearest(true)?.TestResults() ?? "no mounted unit";
        public static string ScreenText()
        {
            var t = Nearest(true)?.Touch?.Text;
            return t == null ? "no text" : $"'{t.text}' enabled {t.enabled} active {t.gameObject.activeInHierarchy} at {t.transform.localPosition} size {t.rectTransform.sizeDelta} font {t.fontSize:F3} color {t.color}";
        }
        public static string Missing(string s) => Nearest(true)?.Touch?.Missing(s.Replace('_', ' ')) ?? "no mounted unit";
        public static string SetConvert(bool on) { Plugin.Convert.Value = on; return "convert " + on; }
        public static string Player() { var u = Nearest(true); return u == null ? "no mounted unit" : u.VideoDescribe; }

        /// The tools' downloads, as the dialog's DOWNLOAD does.
        public static string GetTools() { Ytdlp.Download(); return Ytdlp.Status; }

        public static string Tools() => $"yt-dlp {Ytdlp.Present}, ffmpeg {Ytdlp.HasFfmpeg}, Spotify busy {Spotify.Busy}, downloading {Ytdlp.Downloading}: {Ytdlp.Status}; flash {Panel.Flashing}";

        /// The mounted unit's video: ready, playing, where, why not.
        public static string Video()
        {
            var u = Nearest(true);
            if (u == null) return "no mounted unit";
            var s = u.State;
            return $"tab {u.Tab}; index {(s != null ? s.Index : -1)} of {(s != null ? s.Queue.Count : 0)}, {(s != null && s.Playing ? "playing" : "paused")} at {(s != null ? s.Position(TLDRevamp.Multiplayer.MpApi.SessionTime) : 0f):F1}; "
                + $"video {(u.VideoReady ? "ready" : "not ready")} {(u.VideoPlaying ? "PLAYING" : "")} problem {u.VideoProblem ?? "none"}";
        }

        public static string State(UnitBehaviour u)
        {
            var r = u.Radio; var s = u.State;
            return $"mode {u.Tab}, queue {(s != null ? s.Queue.Count : -1)} {(s != null && s.Playing ? "playing" : "stopped")} video {(u.VideoReady ? "ready" : "not ready")}, radio {(r == null ? "MISSING" : (r.ON ? "on" : "off") + $" vol {(r.Bvolume != null ? r.Bvolume.tunedLerp : -1f):F2} fr {r.FR:F1} {(r.amfm ? "AM" : "FM")}")}";
        }

        /// A picture of the mounted unit from in front of its screen (TheLongDrive_Data/headunit_<name>.png).
        public static string Shot(string name)
        {
            var u = Nearest(true);
            if (u == null || u.Touch == null) return "no mounted unit";
            var t = u.Touch.Root;
            var go = new GameObject("HeadUnitProbeCam");
            try
            {
                var cam = go.AddComponent<Camera>();
                var src = mainscript.s.player.Cam;
                if (src != null)
                {
                    cam.CopyFrom(src);
                    // the game's own image effects too (heat haze reads the depth texture)
                    foreach (var pp in src.GetComponents<postprocessingscript>())
                    {
                        if (!pp.enabled) continue;
                        var c2 = go.AddComponent<postprocessingscript>();
                        c2.M = pp.M; c2.depthText = pp.depthText; c2.C = cam;
                        c2.FStart();
                    }
                }
                cam.fieldOfView = 50f; cam.nearClipPlane = 0.02f;
                go.transform.position = t.position - t.forward * 0.45f + u.Car.transform.up * 0.12f;
                go.transform.rotation = Quaternion.LookRotation(t.position - go.transform.position, u.Car.transform.up);
                var rt = new RenderTexture(960, 540, 24);
                cam.targetTexture = rt; cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); tex.Apply();
                RenderTexture.active = null; cam.targetTexture = null;
                var path = Path.Combine(Application.dataPath, "headunit_" + name + ".png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex); rt.Release(); UnityEngine.Object.Destroy(rt);
                return path;
            }
            finally { UnityEngine.Object.Destroy(go); }
        }
    }
}
