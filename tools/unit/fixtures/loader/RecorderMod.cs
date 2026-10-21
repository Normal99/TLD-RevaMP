// A TLD Loader mod that only records the lifecycle calls the host makes.
// It is the fixture for the loader host test: the host must call it the way
// TLDLoader does, and the recorder must see exactly those calls.
using System.Collections.Generic;
using TLDLoader;

public class RecorderMod : Mod
{
    /// Every lifecycle call the host made, in order.
    public static readonly List<string> Calls = new List<string>();
    /// Set by the test to make a lifecycle call throw (the host must survive it).
    public static string ThrowOn = null;
    public static bool LoadInMenuOverridden = true;

    public override string ID => "recorder.mod";
    public override string Name => "Recorder Mod";
    public override string Version => "1.2.3";
    public override bool LoadInMenu => LoadInMenuOverridden;
    public override float ConfigScrollHeight => 0f;

    static void Record(string what)
    {
        Calls.Add(what);
        if (ThrowOn == what) throw new System.Exception("the " + what + " call failed on purpose");
    }

    public override void OnMenuLoad() { Record("OnMenuLoad"); }
    public override void OnLoad() { Record("OnLoad"); }
    public override void OnUnload() { Record("OnUnload"); }
    public override void Config() { Record("Config"); }
    public override void Update() { Record("Update"); }
    public override void FixedUpdate() { Record("FixedUpdate"); }
    public override void OnGUI() { Record("OnGUI"); }
}
