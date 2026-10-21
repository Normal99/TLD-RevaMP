using System;
using System.Globalization;

namespace TLDRevamp.Cinematic
{
    /// The bridge surface of the cinematic camera.
    ///
    /// Two rules shape it:
    ///  - The status is read-only. It changes nothing, so any peer may ask for it. (DebugBridge.ReadOnlyCmd
    ///    decides this from the command name: `cinema` and `cinema status` are queries, everything else mutates
    ///    and is therefore refused when the host's developer policy does not allow it.)
    ///  - The arguments are bounded, structured and numeric. The bridge echoes the last command it ran into every
    ///    anomaly log line, so a free-text argument could put whatever the caller typed into the log and into
    ///    another tool's output. Nothing free-text is accepted here: a mode name, a number, or nothing at all.
    public static class CinematicBridge
    {
        /// The longest argument the camera accepts. Anything longer is refused rather than truncated.
        private const int MaxArgLength = 64;

        /// The commands:
        ///   cinema                       - the camera's status (read-only)
        ///   cinema status                - the same
        ///   cinema on                    - the camera opens in the default mode
        ///   cinema free                  - the camera opens in the free (fly) mode
        ///   cinema orbit                 - the camera opens in the orbit mode
        ///   cinema off                   - the camera closes
        ///   cinema toggle                - opens or closes the camera
        ///   cinema recentre              - puts the orbit target back in front of the camera
        ///   cinema speed <m/s>           - the camera's speed
        ///   cinema fov <degrees>         - the camera's field of view
        ///   cinema radius <metres>       - the orbit distance
        ///   cinema shot                  - the shot list's status
        ///   cinema shot load <text>      - loads a shot list (one directive per line, '\\n' separated)
        ///   cinema shot unload            - forgets the shot list
        public static string Command(string arg)
        {
            if (arg == null) return CinematicCamera.Status();
            arg = arg.Trim();
            if (arg.Length == 0) return CinematicCamera.Status();
            if (arg.Length > MaxArgLength * 16) return "{\"error\":\"the cinema command is too long\"}";

            var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            var rest = new string[parts.Length - 1];
            Array.Copy(parts, 1, rest, 0, rest.Length);
            string restText = rest.Length > 0 ? string.Join(" ", rest) : "";

            switch (verb)
            {
                case "status":
                    return CinematicCamera.Status();
                case "on":
                    return CinematicCamera.SetOn(CinematicCamera.DefaultMode, "bridge");
                case "free":
                    return CinematicCamera.SetOn(CinematicMode.Free, "bridge");
                case "orbit":
                    return CinematicCamera.SetOn(CinematicMode.Orbit, "bridge");
                case "off":
                    return CinematicCamera.SetOff("bridge");
                case "toggle":
                    return CinematicCamera.Toggle();
                case "recentre":
                    return CinematicCamera.Recentre();
                case "speed":
                    return SetSpeed(rest);
                case "fov":
                    return SetFov(rest);
                case "radius":
                    return SetRadius(rest);
                case "shot":
                    return Shot(rest, restText);
                case "screenshot":
                    // A screenshot of the camera's own view. It goes to a local folder of the camera's own and
                    // is never part of a report or a shared file (the repo's telemetry holds no screenshots).
                    return CinematicCamera.Screenshot();
                default:
                    return "{\"error\":\"cinema: use status | on | free | orbit | off | toggle | recentre | speed <m/s> | fov <deg> | radius <m> | shot [load|unload|write] | screenshot\"}";
            }
        }

        private static string SetSpeed(string[] rest)
        {
            if (rest.Length != 1 || !TryFloat(rest[0], out float value))
                return "{\"error\":\"cinema speed <metres per second>\"}";
            CinematicCamera.Speed = CinematicLimits.ClampSpeed(value);
            return CinematicCamera.Status();
        }

        private static string SetFov(string[] rest)
        {
            if (rest.Length != 1 || !TryFloat(rest[0], out float value))
                return "{\"error\":\"cinema fov <degrees>\"}";
            CinematicCamera.State.Fov = CinematicLimits.ClampFov(value);
            CinematicCamera.Fov = CinematicCamera.State.Fov;
            return CinematicCamera.Status();
        }

        private static string SetRadius(string[] rest)
        {
            if (rest.Length != 1 || !TryFloat(rest[0], out float value))
                return "{\"error\":\"cinema radius <metres>\"}";
            CinematicCamera.State.Radius = CinematicLimits.ClampRadius(value);
            return CinematicCamera.Status();
        }

        private static string Shot(string[] rest, string restText)
        {
            if (rest.Length == 0) return CinematicCamera.ShotStatus();
            string what = rest[0].ToLowerInvariant();
            if (what == "unload") return CinematicCamera.UnloadShot();
            if (what == "write")
            {
                // The canonical form of the loaded shot list. The mod's own codec is the only writer of this
                // format, so the file on disk and the take in the game cannot drift apart.
                var shot = CinematicCamera.CurrentShot;
                if (shot == null) return "{\"error\":\"no shot list is loaded\"}";
                return "{\"shotlist\":" + Json.Str(CinematicShotCodec.Write(shot)) + "}";
            }
            if (what == "load")
            {
                // The shot list is a directive list, not free text: it is parsed by the pure codec, which refuses
                // anything it does not understand. '\\n' is written as a literal backslash-n on the command line.
                string text = restText.Length > "load".Length ? restText.Substring("load".Length).Trim() : "";
                text = text.Replace("\\n", "\n");
                if (text.Length == 0) return "{\"error\":\"cinema shot load <directives>\"}";
                return CinematicCamera.LoadShot(text);
            }
            return "{\"error\":\"cinema shot [load <directives> | write | unload]\"}";
        }

        private static bool TryFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
