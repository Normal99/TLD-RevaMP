// A synthetic TLDLoader mod: a real Loader mod (it derives from TLDLoader.Mod), so the production scanner finds
// its lifecycle, its component, its world-wide value and its input handler. It references the game's and
// Revamp's own types by name only, so it can be built and scanned without the game present.
using System;
using TLDLoader;
using UnityEngine;

namespace FixtureMod
{
    /// <summary>The mod itself. The scanner reads ID, Name, Version and the lifecycle overrides from here.</summary>
    public sealed class FixtureMod : Mod
    {
        public override string ID => "fixture.mod";
        public override string Name => "Fixture Mod";
        public override string Version => "1.0.0";
        public override string Author => "TLD RevaMP evidence lane";

        public static bool Started;

        public override void OnLoad()
        {
            Started = true;
            Time.timeScale = 1.0f;          // a world-wide value: the scanner must report it
        }

        public override void OnUnload() => Started = false;

        public override void Update()
        {
            if (Input.GetKeyDown(KeyCode.F5)) Debug.Log("fixture");
        }

        public override void OnGUI() => GUI.Label(new Rect(0, 0, 10, 10), "fixture");
    }
}
