// The native VR controls without a game: which button means what, which hand the interaction ray comes from, and
// that a press keeps its meaning until it is released. No Unity, no headset.
using System;
using TLDRevamp.VR;

static class VrControlsTest
{
    private static int _fails;
    private static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) _fails++; }

    const int L = VrControlMap.Left, R = VrControlMap.Right, N = VrControlMap.None;

    private sealed class Rig
    {
        public readonly VrHandButtons Left = new VrHandButtons(), Right = new VrHandButtons();
        public readonly VrControlMap Map = new VrControlMap();
        public VrGameInput Frame(bool lt = false, bool lg = false, bool lp = false, bool rt = false, bool rg = false, bool rp = false, int holder = N, bool menu = false)
        {
            Left.Feed(lt, lg, lp);
            Right.Feed(rt, rg, rp);
            return Map.Map(Left, Right, holder, menu);
        }
    }

    private static int Main()
    {
        Buttons();
        Use();
        HeldItem();
        PressKeepsItsMeaning();
        Mount();
        Grabs();
        PointerHand();
        Menu();
        Throw();
        return _fails == 0 ? 0 : 1;
    }

    private static void Buttons()
    {
        var b = new VrButton();
        b.Feed(true);
        Check("a press is a Down and a Hold", b.Down && b.Hold && !b.Up);
        b.Feed(true);
        Check("a held press does not repeat its Down", !b.Down && b.Hold);
        b.Feed(false);
        Check("a release is an Up", b.Up && !b.Hold && !b.Down);
        b.Feed(false);
        Check("the Up is one frame", !b.Up);
        b.Feed(true); b.Clear(); b.Feed(false);
        Check("a cleared button releases without an Up", !b.Up && !b.Hold);
    }

    private static void Use()
    {
        var r = new Rig();
        var f = r.Frame(rt: true);
        Check("the right trigger is the game's use", f.Use && f.UseDown && !f.Lmb);
        f = r.Frame(rt: true);
        Check("a held trigger holds use, one Down", f.Use && !f.UseDown);
        f = r.Frame();
        Check("letting go is the use Up", f.UseUp && !f.Use);
        f = r.Frame(lt: true);
        Check("the left trigger uses too", f.Use && f.UseDown);
        Check("and the ray comes from the left hand", f.Pointer == L);
    }

    private static void HeldItem()
    {
        var r = new Rig();
        var f = r.Frame(rt: true, holder: R);
        Check("the holding hand's trigger uses the item (lmb)", f.Lmb && f.LmbDown && !f.Use);
        r.Frame(holder: R);
        f = r.Frame(lt: true, holder: R);
        Check("the free hand's trigger still uses the world", f.Use && f.UseDown && !f.Lmb);
    }

    private static void PressKeepsItsMeaning()
    {
        var r = new Rig();
        r.Frame(rt: true, rg: true, holder: R);           // pressing the trigger while holding: use the item
        var f = r.Frame(rt: true, rg: false, holder: N);  // the grip let go, the trigger is still down
        Check("a trigger pressed on the item stays the item's (no use of what is behind it)", !f.Use && f.Lmb);
        f = r.Frame(holder: N);
        Check("and comes up as the item's", f.LmbUp && !f.UseUp);
        f = r.Frame(rt: true);
        Check("the next press on the empty hand is a use", f.UseDown && !f.LmbDown);
    }

    private static void Mount()
    {
        var r = new Rig();
        var f = r.Frame(rp: true);
        Check("A is the game's mount", f.MountDown && f.Mount && !f.Use);
        f = r.Frame();
        Check("A comes up as the mount Up", f.MountUp);
        f = r.Frame(lp: true);
        Check("X mounts too, from the left hand", f.MountDown && f.Pointer == L);
    }

    private static void Grabs()
    {
        var r = new Rig();
        var f = r.Frame(rg: true);
        Check("the right grip grabs with the right hand", f.GrabRight == 1 && f.GrabLeft == 0);
        f = r.Frame(rg: true, holder: R);
        Check("a held grip grabs once", f.GrabRight == 0 && !f.Release);
        f = r.Frame(holder: R);
        Check("letting go of the holding grip releases", f.Release);
        f = r.Frame(lg: true, holder: R);
        Check("the other grip grabs with the other hand (a hand-over)", f.GrabLeft == 1 && !f.Release);
        r.Frame(lg: true, holder: L);
        r.Frame(lg: true, rg: true, holder: L);
        f = r.Frame(lg: true, holder: L);
        Check("letting go of the other grip does not release the held item", !f.Release);
        f = r.Frame(holder: L);
        Check("letting go of the new holder's grip releases it", f.Release);
    }

    private static void PointerHand()
    {
        var r = new Rig();
        Check("the ray starts on the right hand", r.Frame().Pointer == R);
        Check("a left press moves it left", r.Frame(lt: true).Pointer == L);
        Check("it stays left while nothing else is pressed", r.Frame().Pointer == L);
        Check("the right hand pressing takes it back", r.Frame(rg: true).Pointer == R);
        r.Frame();
        Check("with nothing pressed, the holding hand points", r.Frame(holder: L).Pointer == L);
        Check("a tie goes to the right hand", r.Frame(lt: true, rt: true).Pointer == R);
    }

    private static void Menu()
    {
        var r = new Rig();
        var f = r.Frame(rt: true, rp: true, rg: true, menu: true);
        Check("an open menu sends nothing to the world", !f.Use && !f.UseDown && !f.MountDown && f.GrabRight == 0);
        f = r.Frame(rt: true, menu: false);
        Check("a trigger held through the menu does not become a use", !f.Use && !f.UseDown);
    }

    private static void Throw()
    {
        Check("a slow let-go is a drop", VrThrow.Speed(0.5f) == 0f);
        Check("a swing throws a little harder than the hand", VrThrow.Speed(4f) > 4f);
        Check("a tracking glitch is capped", VrThrow.Speed(500f) == VrThrow.MaxSpeed);
        Check("NaN is a drop", VrThrow.Speed(float.NaN) == 0f);
    }
}
