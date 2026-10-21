using System;
using System.Collections.Generic;
using System.Globalization;

namespace TLDRevamp.Cinematic
{
    /// A shot list: an ordered set of camera moves with cut points and playback speeds. This is a *sidecar* to the
    /// cinematic camera — its own file, its own version, its own parser. It never touches the game's diagnostic
    /// repro format (docs/REPRO-FORMAT.md) and never goes near the network.
    ///
    /// The text form is one directive per line:
    ///
    ///   # comment
    ///   version 1
    ///   cut 1.5             - jump to the next shot after 1.5 s
    ///   speed 2             - playback speed until the next directive
    ///   move 0 12 -3 4 60   - at t=0: x y z yaw pitch fov
    ///   target 0 1 0        - at t=0: the orbit target
    ///
    /// Times are seconds from the start of the take; every directive applies from its time until the next one.
    /// The positions are **global** - the world's own coordinates, the ones the game's map shows. They stay the
    /// same when the world shifts by two kilometres, so a take written today still frames the same place tomorrow.
    public static class CinematicShotFormat
    {
        public const int CurrentVersion = 1;

        /// The longest file the parser will read. A shot list is a hand-written or tool-written text file; a
        /// multi-megabyte one is a mistake, not a shot.
        public const int MaxBytes = 512 * 1024;

        /// The most directives in one take.
        public const int MaxDirectives = 4096;
    }

    /// One line of a shot list.
    public sealed class CinematicShotStep
    {
        public double Time;          // seconds from the start of the take
        public string Kind;          // "move", "target", "speed", "cut"
        public double[] Values = Array.Empty<double>();
        public string Raw;            // the original line, for error messages
    }

    /// A parsed shot list, plus the bookkeeping the playback needs.
    public sealed class CinematicShot
    {
        public int Version = CinematicShotFormat.CurrentVersion;
        public readonly List<CinematicShotStep> Steps = new List<CinematicShotStep>();

        /// The next cut time after the playhead, or null when the take has no more cuts.
        public double? NextCut(double now)
        {
            double? best = null;
            foreach (var s in Steps)
            {
                if (s.Kind != "cut") continue;
                if (s.Time <= now) continue;
                if (!best.HasValue || s.Time < best.Value) best = s.Time;
            }
            return best;
        }

        /// The last directive of each kind at or before `now`. A take is a list of states, not a list of events:
        /// the camera state at any moment is the last directive of each kind that has already happened.
        public CinematicShotStep LastOf(string kind, double now)
        {
            CinematicShotStep best = null;
            foreach (var s in Steps)
            {
                if (s.Kind != kind) continue;
                if (s.Time > now) continue;
                if (best == null || s.Time >= best.Time) best = s;
            }
            return best;
        }

        /// The take's duration: the time of its last directive.
        public double Duration
        {
            get
            {
                double d = 0;
                foreach (var s in Steps) if (s.Time > d) d = s.Time;
                return d;
            }
        }
    }

    /// Parses and writes the shot-list text format. Pure: no Unity, no files, no game.
    public static class CinematicShotCodec
    {
        /// Parses a shot list. Returns null and an error message when the text is not a valid shot list; the caller
        /// decides what to do about it (the bridge answers with the error, the game logs it).
        public static CinematicShot Parse(string text, out string error)
        {
            error = "";
            var shot = new CinematicShot();
            if (text == null) { error = "no shot list"; return null; }
            if (text.Length > CinematicShotFormat.MaxBytes * 4)
            {
                error = "shot list is too large";
                return null;
            }
            var lines = text.Split(new[] { '\n' }, StringSplitOptions.None);
            int count = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (++count > CinematicShotFormat.MaxDirectives) { error = "too many directives"; return null; }
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string kind = parts[0].ToLowerInvariant();
                var step = new CinematicShotStep { Kind = kind, Raw = line };
                switch (kind)
                {
                    case "version":
                        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                        { error = "line " + (i + 1) + ": version needs one number"; return null; }
                        if (v < 1 || v > CinematicShotFormat.CurrentVersion)
                        { error = "line " + (i + 1) + ": unsupported shot list version " + v; return null; }
                        shot.Version = v;
                        continue;   // the version is a header, not a directive
                    case "cut":
                    {
                        double t;
                        if (parts.Length < 2 || !TryNumber(parts[1], out t)) { error = "line " + (i + 1) + ": cut needs a time"; return null; }
                        if (t < 0) { error = "line " + (i + 1) + ": cut time cannot be negative"; return null; }
                        step.Time = t;
                        break;
                    }
                    case "speed":
                    {
                        double s;
                        if (parts.Length != 2 || !TryNumber(parts[1], out s)) { error = "line " + (i + 1) + ": speed needs one number"; return null; }
                        if (!(s > 0) || s > 64) { error = "line " + (i + 1) + ": speed must be between 0 and 64"; return null; }
                        step.Values = new[] { s };
                        step.Time = 0;
                        break;
                    }
                    case "move":
                    {
                        // x y z yaw pitch fov — six numbers, all optional after the first three? No: all six required,
                        // so a half-written move cannot silently mean something else.
                        if (parts.Length != 7) { error = "line " + (i + 1) + ": move needs x y z yaw pitch fov"; return null; }
                        var vals = new double[6];
                        for (int k = 0; k < 6; k++)
                            if (!TryNumber(parts[k + 1], out vals[k])) { error = "line " + (i + 1) + ": move needs numbers"; return null; }
                        step.Values = vals;
                        step.Time = 0;
                        break;
                    }
                    case "target":
                    {
                        if (parts.Length != 4) { error = "line " + (i + 1) + ": target needs x y z"; return null; }
                        var vals = new double[3];
                        for (int k = 0; k < 3; k++)
                            if (!TryNumber(parts[k + 1], out vals[k])) { error = "line " + (i + 1) + ": target needs numbers"; return null; }
                        step.Values = vals;
                        step.Time = 0;
                        break;
                    }
                    case "at":
                    {
                        // "at <time> <directive> ..." — the general form of the three above.
                        double t;
                        if (parts.Length < 3 || !TryNumber(parts[1], out t)) { error = "line " + (i + 1) + ": at needs a time"; return null; }
                        if (t < 0) { error = "line " + (i + 1) + ": time cannot be negative"; return null; }
                        step.Time = t;
                        step.Kind = parts[2].ToLowerInvariant();
                        step.Raw = line;
                        var inner = new string[parts.Length - 3];
                        Array.Copy(parts, 3, inner, 0, inner.Length);
                        string innerError;
                        var innerStep = ParseDirective(step.Kind, inner, i + 1, out innerError);
                        if (innerStep == null) { error = innerError; return null; }
                        innerStep.Time = t;
                        step = innerStep;
                        break;
                    }
                    default:
                        error = "line " + (i + 1) + ": unknown directive '" + parts[0] + "'";
                        return null;
                }
                shot.Steps.Add(step);
            }
            if (shot.Steps.Count == 0) { error = "the shot list is empty"; return null; }
            return shot;
        }

        private static CinematicShotStep ParseDirective(string kind, string[] parts, int line, out string error)
        {
            error = "";
            var step = new CinematicShotStep { Kind = kind };
            switch (kind)
            {
                case "cut":
                {
                    double t;
                    if (parts.Length < 1 || !TryNumber(parts[0], out t)) { error = "line " + line + ": cut needs a time"; return null; }
                    if (t < 0) { error = "line " + line + ": cut time cannot be negative"; return null; }
                    step.Values = new[] { t };
                    return step;
                }
                case "speed":
                {
                    double s;
                    if (parts.Length != 1 || !TryNumber(parts[0], out s)) { error = "line " + line + ": speed needs one number"; return null; }
                    if (!(s > 0) || s > 64) { error = "line " + line + ": speed must be between 0 and 64"; return null; }
                    step.Values = new[] { s };
                    return step;
                }
                case "move":
                {
                    if (parts.Length != 6) { error = "line " + line + ": move needs x y z yaw pitch fov"; return null; }
                    var vals = new double[6];
                    for (int k = 0; k < 6; k++)
                        if (!TryNumber(parts[k], out vals[k])) { error = "line " + line + ": move needs numbers"; return null; }
                    step.Values = vals;
                    return step;
                }
                case "target":
                {
                    if (parts.Length != 3) { error = "line " + line + ": target needs x y z"; return null; }
                    var vals = new double[3];
                    for (int k = 0; k < 3; k++)
                        if (!TryNumber(parts[k], out vals[k])) { error = "line " + line + ": target needs numbers"; return null; }
                    step.Values = vals;
                    return step;
                }
                default:
                    error = "line " + line + ": unknown directive '" + kind + "'";
                    return null;
            }
        }

        /// Writes a shot list back out, in the canonical form. The same take always writes the same bytes, so a
        /// shot list can be checked into a repository without churn.
        public static string Write(CinematicShot shot)
        {
            if (shot == null) throw new ArgumentNullException(nameof(shot));
            var lines = new List<string> { "# TLD Revamp cinematic shot list", "version " + shot.Version };
            foreach (var s in shot.Steps)
            {
                switch (s.Kind)
                {
                    case "cut":
                        lines.Add("at " + Num(s.Time) + " cut " + Num(s.Values.Length > 0 ? s.Values[0] : s.Time));
                        break;
                    case "speed":
                        lines.Add("at " + Num(s.Time) + " speed " + Num(s.Values[0]));
                        break;
                    case "move":
                        lines.Add("at " + Num(s.Time) + " move " + Num(s.Values[0]) + " " + Num(s.Values[1]) + " " + Num(s.Values[2]) +
                                  " " + Num(s.Values[3]) + " " + Num(s.Values[4]) + " " + Num(s.Values[5]));
                        break;
                    case "target":
                        lines.Add("at " + Num(s.Time) + " target " + Num(s.Values[0]) + " " + Num(s.Values[1]) + " " + Num(s.Values[2]));
                        break;
                }
            }
            return string.Join("\n", lines) + "\n";
        }

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// The playback clock of a take: the playhead, the playback speed, and the cut points. Pure.
    public sealed class CinematicPlayback
    {
        public bool Playing;
        public double Time;        // seconds into the take
        public float Speed = 1f;   // how fast the take plays
        public bool Loop;

        /// Advances the playhead by a frame. Returns true when a cut happened this frame (the caller jumps to the
        /// next shot). The frame length is bounded so a stall cannot skip the whole take.
        public bool Advance(double deltaSeconds, CinematicShot shot, out bool ended)
        {
            ended = false;
            if (!Playing || shot == null) return false;
            double dt = deltaSeconds;
            if (double.IsNaN(dt) || dt < 0) dt = 0;
            if (dt > CinematicLimits.MaxDeltaTime) dt = CinematicLimits.MaxDeltaTime;
            Time += dt * Speed;
            double duration = shot.Duration;
            if (Time > duration)
            {
                if (Loop) { Time = 0; return false; }
                Time = duration;
                Playing = false;
                ended = true;
            }
            return false;
        }

        /// Moves the playhead to the next cut at or after `now`. Returns false when there is no further cut.
        public bool JumpToNextCut(CinematicShot shot, double now)
        {
            if (shot == null) return false;
            var next = shot.NextCut(now);
            if (!next.HasValue) return false;
            Time = next.Value;
            return true;
        }
    }
}
