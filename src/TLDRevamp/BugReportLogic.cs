using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TLDRevamp
{
    /// The parts of the bug report that need no game: the text an agent reads first, the files that make up a saved
    /// world, the /bug command and the key chord. Kept free of Unity types so the unit tests compile it as it is.
    internal static class BugReportLogic
    {
        internal sealed class Facts
        {
            public string Note = "", Version = "", Commit = "", Build = "", Game = "", Role = "solo", Time = "";
            public int Protocol;
            public bool InSession, Seated, CarFile;
            public double X, Y, Z;
            public bool ReproPresent, ReproComplete = true, ReproFull;
            public int ReproRows, ReproSkipped, ReproDropped;
            public double ReproSeconds;
            /// File name only (never a path). Null: a new world that was never saved, or a client.
            public string WorldFile;
            public bool WorldStartCopied, LatestAutosaveCopied;
            public double AutosaveAgeSeconds = -1;
        }

        internal const string CarFile = "car.json", ReproFile = "repro.json", StartWorldDir = "world/start/", AutosaveDir = "world/latest-autosave/";

        /// REPORT.md: what happened, in which build and world, and what the bundle needs to be replayed. No names, no paths.
        internal static string Report(Facts f)
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            sb.Append("# Bug report\n\n");
            sb.Append("**What the player wrote:** ").Append(string.IsNullOrWhiteSpace(f.Note) ? "(no note)" : f.Note.Trim()).Append("\n\n");
            sb.Append("## Build\n\n");
            sb.Append("- TLD RevaMP ").Append(f.Version).Append(", commit ").Append(f.Commit).Append(", build ").Append(f.Build).Append('\n');
            sb.Append("- Game build ").Append(f.Game).Append(", wire protocol ").Append(f.Protocol.ToString(inv)).Append('\n');
            sb.Append("- Reported ").Append(f.Time).Append('\n');
            sb.Append("- Role: ").Append(f.Role).Append(f.InSession ? " in a multiplayer session" : ", not in a multiplayer session").Append('\n');
            if (f.InSession || f.X != 0 || f.Y != 0 || f.Z != 0)
                sb.Append("- Reporter at x ").Append(f.X.ToString("F1", inv)).Append(", y ").Append(f.Y.ToString("F1", inv)).Append(", z ").Append(f.Z.ToString("F1", inv))
                  .Append(" (global metres)").Append(f.Seated ? ", seated in a vehicle" : "").Append("\n\n");
            else sb.Append('\n');

            sb.Append("## Actions (").Append(ReproFile).Append(")\n\n");
            if (!f.ReproPresent)
                sb.Append("None. The action recorder was not running (off in Settings → Revamp → Debugging, or not in a multiplayer session), so this bundle cannot be replayed.\n\n");
            else
            {
                sb.Append("- ").Append(f.ReproRows.ToString(inv)).Append(" rows over ").Append(Minutes(f.ReproSeconds)).Append(", from the moment this session started recording\n");
                sb.Append("- ").Append(f.ReproComplete ? "complete" : "INCOMPLETE (dropped " + f.ReproDropped.ToString(inv) + " rows; the replay tool refuses it)")
                  .Append(", skipped (unresolvable) ").Append(f.ReproSkipped.ToString(inv)).Append('\n');
                if (f.ReproFull) sb.Append("- The recorder was full: it kept the beginning, so a bug late in a very long session is not in it.\n");
                sb.Append('\n');
            }

            sb.Append("## World\n\n");
            if (f.WorldStartCopied)
                sb.Append("- `").Append(StartWorldDir).Append("`: the save this session started from (").Append(f.WorldFile).Append("). The actions above replay from this state.\n");
            else if (f.Role == "solo")
                sb.Append("- Not in a multiplayer session, so no starting save was recorded. Host the world (even alone) to get a replayable bundle.\n");
            else if (f.Role == "client")
                sb.Append("- A client has no world save; the host's report has it.\n");
            else if (f.WorldFile == null)
                sb.Append("- This session started in a new world that had never been saved, so there is no starting save. Reproduce from a new world, or ask the host for a later save.\n");
            else
                sb.Append("- The starting save (").Append(f.WorldFile).Append(") could not be copied.\n");
            if (f.LatestAutosaveCopied)
                sb.Append("- `").Append(AutosaveDir).Append("`: the newest autosave, ").Append(f.AutosaveAgeSeconds >= 0 ? Minutes(f.AutosaveAgeSeconds) + " old" : "age unknown")
                  .Append(" when the report was taken. ")
                  .Append(f.AutosaveAgeSeconds > 3600 ? "That is over an hour: it is probably not this world. " : "It is nearer to the bug than the starting save. ")
                  .Append("It is not what the actions replay from.\n");
            sb.Append('\n');

            sb.Append("## Other files\n\n");
            sb.Append("- `blackbox.json`: motion traces and events around the report, with the note. `stats.json`, `errors.json`, `fixes.json`, `breakdown.json`: numbers about this machine and the mod.\n");
            if (f.CarFile)
                sb.Append("- `").Append(CarFile).Append("`: the car ").Append(f.Seated ? "the reporter sits in" : "nearest the reporter").Append(" as the physics engine had it when the report was taken: body (kinematic, constraints, mass), every wheel (torque, brake, rpm, mass), joints, colliders inside the body, the wheel lock and the mod's authority over it.\n");
            sb.Append("- `tldrevamp-settings.txt`: the player's mod settings. `sessions/`: five-second samples. No raw logs, names or paths are in this bundle, and the player records of the world are left out.\n\n");

            sb.Append("## Replaying it\n\n");
            sb.Append("```\npython tools/bugrepro.py <this zip> --validate\n```\n\n");
            sb.Append("That unpacks the zip, checks `").Append(ReproFile).Append("` and prints how to start the replay on two test copies. ")
              .Append("Load the starting save into a test copy under a neutral name first; the tool does that with `--install <saves folder>`. ")
              .Append("The replay checks that both copies end in the same state. It does not prove the bug is gone, and visual problems still need a look in the game.\n");
            return sb.ToString();
        }

        private static string Minutes(double seconds)
        {
            if (seconds < 90) return Math.Round(seconds).ToString(CultureInfo.InvariantCulture) + " s";
            return (seconds / 60).ToString("F1", CultureInfo.InvariantCulture) + " min";
        }

        /// The files that belong to one world save, next to the data file: leases and the properties file. The player records
        /// (.players) are left out on purpose: they are keyed by account. The game names them TLDsaveData_&lt;name&gt;.tlds and
        /// TLDsaveProp_&lt;name&gt;.tldsp.
        internal static string[] WorldFiles(string dataFile)
        {
            if (string.IsNullOrEmpty(dataFile)) return Array.Empty<string>();
            var list = new List<string> { dataFile, dataFile + ".leases" };
            int slash = Math.Max(dataFile.LastIndexOf('/'), dataFile.LastIndexOf('\\'));
            string dir = slash >= 0 ? dataFile.Substring(0, slash + 1) : "", name = dataFile.Substring(slash + 1);
            if (name.StartsWith("TLDsaveData_", StringComparison.Ordinal) && name.EndsWith(".tlds", StringComparison.Ordinal))
                list.Add(dir + "TLDsaveProp_" + name.Substring("TLDsaveData_".Length, name.Length - "TLDsaveData_".Length - ".tlds".Length) + ".tldsp");
            return list.ToArray();
        }

        /// "/bug" or "/bug some words": the note, possibly empty. Anything else is a chat line.
        internal static bool IsBugCommand(string text, out string note)
        {
            note = "";
            if (text == null) return false;
            text = text.Trim();
            if (text.Equals("/bug", StringComparison.OrdinalIgnoreCase)) return true;
            if (!text.StartsWith("/bug ", StringComparison.OrdinalIgnoreCase)) return false;
            note = text.Substring(5).Trim();
            return true;
        }

        // UnityEngine.KeyCode values of the modifier keys, so this file needs no Unity types.
        internal const int LeftShift = 304, RightShift = 303, LeftControl = 306, RightControl = 305, LeftAlt = 308, RightAlt = 307;
        private static int Twin(int modifier)
        {
            switch (modifier)
            {
                case LeftShift: return RightShift; case RightShift: return LeftShift;
                case LeftControl: return RightControl; case RightControl: return LeftControl;
                case LeftAlt: return RightAlt; case RightAlt: return LeftAlt;
                default: return 0;
            }
        }

        /// A shortcut is down when its main key went down this frame and every modifier is held. Either physical key
        /// of a modifier counts (BepInEx's own check wants exactly the bound one: a Ctrl+Insert binding ignored the right
        /// Ctrl). A shortcut with no modifier does not fire while a modifier is held that another bound shortcut on the
        /// same key needs, so Ctrl+Insert no longer also fires the plain Insert one.
        internal static bool ChordDown(int main, int[] modifiers, Func<int, bool> pressedThisFrame, Func<int, bool> held, IEnumerable<int[]> otherModifiersOnSameKey)
        {
            if (main == 0 || !pressedThisFrame(main)) return false;
            foreach (int m in modifiers) if (!(held(m) || held(Twin(m)))) return false;
            if (modifiers.Length == 0 && otherModifiersOnSameKey != null)
                foreach (var other in otherModifiersOnSameKey)
                {
                    if (other.Length == 0) continue;
                    bool all = true;
                    foreach (int m in other) if (!(held(m) || held(Twin(m)))) { all = false; break; }
                    if (all) return false;
                }
            return true;
        }
    }
}
