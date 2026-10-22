using System;
using System.Collections.Generic;
using UnityEngine;

// Game/role side effects around the production SetProxy and ApplyProxyBodies methods.
public class TestHit : TestComponent { }
public class itemPlaceRemoveScript { public static itemPlaceRemoveScript s; public float itemUnFreezeDist; }

namespace UnityEngine
{
    public static class Physics
    {
        public static int Syncs;
        public static void SyncTransforms() { Syncs++; }
    }
    public static class Object
    {
        public static TestMount[] Mounts = Array.Empty<TestMount>();
        public static T[] FindObjectsOfType<T>() where T : class => (T[])(object)Mounts;
    }
}

namespace TLDRevamp.Net
{
    public static partial class Entities
    {
        static long GameFrozenReleased, RagdollLimbsRestored;
        static readonly Dictionary<TestCar, float> DamageGraceUntil = new Dictionary<TestCar, float>();
        static readonly Dictionary<object, Ent> ProxyWheelOwner = new Dictionary<object, Ent>();
        static readonly Dictionary<object, int> ProxyWheelIndex = new Dictionary<object, int>();
        static void ClearWorn(Ent e) { e.WornBy = -1; }
        static void ShowStored(Ent e, bool show) { }
        static void ShowHeld(Ent e, bool show) { e.Held = show; }
        public static bool GhostEndsOnOwn = true;
        static void EndGhostOnOwn(Ent e) { e.ReleaseGhost = false; }
        static void ShowCarriedCollisions(Ent e) { }
        static bool LetGoClaims = true, SeamlessClaims;
        static float LetGoClaimS = 3f;
        static long LetGoClaimTries;
        static void PlaceAtNewest(Ent e) { }
        static void DropPhysical(Ent e) { }
        static void SetPartColliders(Ent e, bool on) { }
        static void DetachContact(Ent e) { }
        static void AttachContact(Ent e) { }
        static Rigidbody[] HitchBodies(Ent e) => Array.Empty<Rigidbody>();
        static object[] WheelsOf(Ent e) => Array.Empty<object>();
        static void BindProxyEngine(Ent e) { }
    }
}
