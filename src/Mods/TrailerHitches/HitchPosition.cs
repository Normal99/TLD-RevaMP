using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TrailerHitches
{
    internal readonly struct HitchPosition
    {
        internal readonly float X, Y, Z;
        internal HitchPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
        public override string ToString() => X.ToString("R", CultureInfo.InvariantCulture) + "," +
            Y.ToString("R", CultureInfo.InvariantCulture) + "," + Z.ToString("R", CultureInfo.InvariantCulture);

        internal sealed class Model
        {
            internal string Key;
            internal int[] Items;
            internal HitchPosition Position;
        }

        // Initial measurements from the beta's fixed body/frame colliders and WheelCollider centres, not renderer bounds.
        // Nominal level drawbar: trailer target Y .4362 minus wheel centre Y .5085 = -.0723 m relative to the axle.
        // Static geometry only: tools/trailerhitches.py must verify settled suspension and pitch.
        internal static readonly Model[] Models =
        {
            new Model { Key = "car06p", Items = new[] { 243, 244 }, Position = new HitchPosition(0f, -0.0945f, -2.26f) },
            new Model { Key = "car09", Items = new[] { 371, 372, 388 }, Position = new HitchPosition(0f, -0.2891f, -2.34f) },
            new Model { Key = "car09p", Items = new[] { 390, 391 }, Position = new HitchPosition(0f, -0.2891f, -2.45f) },
            new Model { Key = "car09t", Items = new[] { 396, 397 }, Position = new HitchPosition(0f, -0.2891f, -4.01f) },
        };

        internal static Model ForItem(int id)
        {
            foreach (var model in Models) if (Array.IndexOf(model.Items, id) >= 0) return model;
            return null;
        }

        internal static bool TryParse(string raw, out HitchPosition position)
        {
            position = default;
            var pieces = raw?.Split(',');
            if (pieces == null || pieces.Length != 3) return false;
            var values = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(pieces[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                    float.IsNaN(values[i]) || float.IsInfinity(values[i]) || Math.Abs(values[i]) > 20f) return false;
            position = new HitchPosition(values[0], values[1], values[2]);
            return true;
        }

        internal static string Fingerprint(IReadOnlyDictionary<string, HitchPosition> positions)
        {
            var text = new StringBuilder();
            foreach (var model in Models) text.Append(model.Key).Append('=').Append(positions[model.Key]).Append(';');
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
