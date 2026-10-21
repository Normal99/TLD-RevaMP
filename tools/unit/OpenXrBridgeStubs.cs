// Test-only stubs of the Unity and OpenXR API surface that src/TLDRevamp/Fixes/OpenXrBridge.cs uses.
//
// They are deliberately FAITHFUL to the shipped assemblies (lib/Unity.XR.Management.dll,
// lib/UnityEngine.XRModule.dll, lib/Unity.XR.OpenXR.dll), because the test drives the PRODUCTION bridge
// against them: the manager really warns when it is stopped without being initialized, really keeps its
// activeLoader until DeinitializeLoader, and the loader's subsystems really only run while the loader is
// started. A stub that was more forgiving than the real thing would make the test prove nothing.
//
// Everything that COUNTS a call is recorded, so a test can assert what the production code really did:
// which manager calls were made, in which order, and how often.
using System;
using System.Collections.Generic;

// ---------------------------------------------------------------- the game's own types
public class mainScript2
{
    public static mainScript2 s;
    /// The game's own rig recenter. The stub counts it, so the test can prove the bridge does not recenter
    /// twice (the rig once and the runtime's tracking origin once).
    public static int RigRecenters;
    public void VRRecenter() { RigRecenters++; }
}

/// The game's own VR entry. The privacy scan rejects a file that DEFINES the game's own classes (the
/// mod patches them, it never defines them), so the stub is an abstract class with the one member the
/// bridge touches. The real type is a plain MonoBehaviour; nothing in the bridge depends on that.
public abstract class mainscript
{
    public static bool SetVREnabled;
    public void SetVR(bool enable) { SetVREnabled = enable; }
}

// ---------------------------------------------------------------- UnityEngine
namespace UnityEngine
{
    public static class Time
    {
        public static float unscaledTime = 1000f;
        public static float realtimeSinceStartup = 1000f;
    }

    public static class Resources
    {
        public static T[] FindObjectsOfTypeAll<T>() where T : class { return new T[0]; }
    }

    public static class Debug
    {
        public static readonly List<string> Warnings = new List<string>();
        public static void LogWarning(object message) { Warnings.Add(message == null ? "" : message.ToString()); }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(0, 0, 0); } }
    }

    public struct Quaternion { public float x, y, z, w; }

    public interface ISubsystem
    {
        bool running { get; }
        void Start();
        void Stop();
    }

    /// The real values (lib/UnityEngine.XRModule.dll): Unknown=0, Device=1, Floor=2, TrackingReference=4,
    /// Unbounded=8. The stub mirrors them so a mode name the bridge prints is the real mode.
    public enum TrackingOriginModeFlags
    {
        Unknown = 0,
        Device = 1,
        Floor = 2,
        TrackingReference = 4,
        Unbounded = 8,
    }
}

namespace UnityEngine.XR
{
    /// The real one is a flags enum (lib/UnityEngine.XRModule.dll), so the bridge's
    /// `d.characteristics & InputDeviceCharacteristics.Left` really is an enum AND.
    [Flags]
    public enum InputDeviceCharacteristics
    {
        None = 0,
        HeadMounted = 1,
        Camera = 2,
        HeldInHand = 4,
        HandTracking = 8,
        EyeTracking = 16,
        TrackedDevice = 32,
        Controller = 64,
        TrackingReference = 128,
        Left = 256,
        Right = 512,
    }

    public class InputDevice
    {
        public string name = "device";
        public bool isValid = true;
        public InputDeviceCharacteristics characteristics;
    }

    public static class InputDevices
    {
        /// The devices the stub reports. The test sets them, so the bridge's controller list can be checked.
        public static readonly List<InputDevice> Devices = new List<InputDevice>();
        public static void GetDevicesWithCharacteristics(InputDeviceCharacteristics c, List<InputDevice> into)
        {
            into.Clear();
            into.AddRange(Devices);
        }
    }

    public class XRDisplaySubsystem : ISubsystem
    {
        public bool running { get; private set; }
        public void Start() { running = true; }
        public void Stop() { running = false; }
    }

    public class XRInputSubsystem : ISubsystem
    {
        public bool running { get; private set; }
        /// The tracking origin mode, so the test can prove the origin really moved (or really did not).
        public TrackingOriginModeFlags OriginMode = TrackingOriginModeFlags.Device;
        /// The modes the runtime supports, as the real XRInputSubsystem reports them
        /// (GetSupportedTrackingOriginModes). The real enum is: Unknown=0, Device=1, Floor=2,
        /// TrackingReference=4, Unbounded=8.
        public TrackingOriginModeFlags SupportedOriginModes = TrackingOriginModeFlags.Device | TrackingOriginModeFlags.Floor;
        public int TryRecenterCalls;
        public int SetOriginCalls;
        public void Start() { running = true; }
        public void Stop() { running = false; }
        public bool TryRecenter() { TryRecenterCalls++; return true; }
        public bool TrySetTrackingOriginMode(TrackingOriginModeFlags flags)
        {
            SetOriginCalls++;
            OriginMode = flags;
            return true;
        }
        /// The real API: the mode the runtime is set up around. A configuration flag, never a pose.
        public TrackingOriginModeFlags GetTrackingOriginMode() { return OriginMode; }
        public TrackingOriginModeFlags GetSupportedTrackingOriginModes() { return SupportedOriginModes; }
    }
}

namespace UnityEngine.XR.Management
{
    using UnityEngine;

    public abstract class XRLoader
    {
        public string Name = "loader";
        public bool Initialized;
        public bool Started;
        public readonly XRDisplaySubsystem Display = new XRDisplaySubsystem();
        public readonly XRInputSubsystem Input = new XRInputSubsystem();
        public T GetLoadedSubsystem<T>() where T : class, ISubsystem
        {
            if (typeof(T) == typeof(XRDisplaySubsystem)) return Display as T;
            if (typeof(T) == typeof(XRInputSubsystem)) return Input as T;
            return null;
        }
        public virtual bool Initialize() { Initialized = true; return true; }
        public virtual bool Start() { Started = true; Display.Start(); Input.Start(); return true; }
        public virtual bool Stop() { Started = false; Display.Stop(); Input.Stop(); return true; }
        public virtual bool Deinitialize() { Initialized = false; return true; }
    }

    public sealed class XRManagerSettings
    {
        public readonly List<XRLoader> activeLoaders = new List<XRLoader>();
        public XRLoader activeLoader;
        private bool _initialized;

        public bool isInitializationComplete { get { return _initialized; } }

        /// Every manager call, in order, so the test can assert the production bridge's real call sequence.
        public readonly List<string> Calls = new List<string>();

        /// The real manager warns and does NOTHING when it is stopped without being initialized. The stub does
        /// the same, so a bridge that stops a manager it never initialized is caught.
        public bool WarnWhenNotInitialized = true;

        public void InitializeLoaderSync()
        {
            Calls.Add("InitializeLoaderSync");
            if (_initialized) { Debug.LogWarning("XR Management has already initialized an active loader in this scene"); return; }
            foreach (var loader in activeLoaders)
            {
                if (loader == null) continue;
                if (!loader.Initialize()) continue;
                activeLoader = loader;
                _initialized = true;
                return;
            }
        }

        public void StartSubsystems()
        {
            Calls.Add("StartSubsystems");
            if (!_initialized) { Debug.LogWarning("Call to StartSubsystems without an initialized manager."); return; }
            if (activeLoader != null) activeLoader.Start();
        }

        public void StopSubsystems()
        {
            Calls.Add("StopSubsystems");
            if (!_initialized)
            {
                if (WarnWhenNotInitialized) Debug.LogWarning("Call to StopSubsystems without an initialized manager.");
                return;
            }
            if (activeLoader != null) activeLoader.Stop();
        }

        public void DeinitializeLoader()
        {
            Calls.Add("DeinitializeLoader");
            if (!_initialized)
            {
                if (WarnWhenNotInitialized) Debug.LogWarning("Call to DeinitializeLoader without an initialized manager.");
                return;
            }
            StopSubsystems();
            if (activeLoader != null) activeLoader.Deinitialize();
            activeLoader = null;
            _initialized = false;
        }

        /// The game tore the session down itself: the manager is no longer initialized and holds no loader.
        public void SimulateGameToreItDown()
        {
            Calls.Add("game tore it down");
            if (activeLoader != null) activeLoader.Deinitialize();
            activeLoader = null;
            _initialized = false;
        }
    }

    public sealed class XRGeneralSettings
    {
        public static XRGeneralSettings Instance;
        public XRManagerSettings Manager;
    }
}

namespace UnityEngine.XR.OpenXR
{
    using UnityEngine.XR.Management;
    using UnityEngine.XR.OpenXR.Features;

    public sealed class OpenXRSettings
    {
        public static OpenXRSettings Instance;
        public readonly Dictionary<Type, OpenXRFeature> Features = new Dictionary<Type, OpenXRFeature>();
        public OpenXRFeature GetFeature(Type type)
        {
            OpenXRFeature feature;
            return Features.TryGetValue(type, out feature) ? feature : null;
        }
    }

    public static class OpenXRRuntime
    {
        public static string name = "Stub Runtime";
        public static string apiVersion = "1.0";
    }

    /// The OpenXR loader. Its Initialize() RECORDS the controller profiles that were enabled at that moment:
    /// the real OpenXR loader reads the enabled features when it initializes, so a profile switched on
    /// afterwards is never used for that session. The stub makes that rule observable, which is exactly what
    /// the "the profiles are configured BEFORE the loader is initialized" property needs.
    public class OpenXRLoader : XRLoader
    {
        public OpenXRLoader() { Name = "UnityEngine.XR.OpenXR.OpenXRLoader"; }
        /// The profiles that were enabled when the loader was initialized.
        public readonly List<string> FeaturesEnabledAtInitialize = new List<string>();

        public override bool Initialize()
        {
            FeaturesEnabledAtInitialize.Clear();
            var settings = OpenXRSettings.Instance;
            if (settings != null)
                foreach (var pair in settings.Features)
                    if (pair.Value != null && pair.Value.enabled) FeaturesEnabledAtInitialize.Add(pair.Key.FullName);
            return base.Initialize();
        }
    }

    public class OpenXRLoaderNoPreInit : OpenXRLoader
    {
        public OpenXRLoaderNoPreInit() { Name = "UnityEngine.XR.OpenXR.OpenXRLoaderNoPreInit"; }
    }
}

namespace UnityEngine.XR.OpenXR.Features
{
    public class OpenXRFeature
    {
        /// Whether the feature is enabled. The bridge switches the controller profiles on, and the loader
        /// reads the enabled features when it initializes: the stub's loader records them at Initialize().
        public bool enabled;
    }
}

namespace UnityEngine.XR.OpenXR.Features.Interactions
{
    public class MetaQuestTouchProControllerProfile : UnityEngine.XR.OpenXR.Features.OpenXRFeature { }
    public class OculusTouchControllerProfile : UnityEngine.XR.OpenXR.Features.OpenXRFeature { }
    public class ValveIndexControllerProfile : UnityEngine.XR.OpenXR.Features.OpenXRFeature { }
    public class HTCViveControllerProfile : UnityEngine.XR.OpenXR.Features.OpenXRFeature { }
    public class HPReverbG2ControllerProfile : UnityEngine.XR.OpenXR.Features.OpenXRFeature { }
    public class MicrosoftMotionControllerProfile : UnityEngine.XR.OpenXR.Features.OpenXRFeature { }
    public class KHRSimpleControllerProfile : UnityEngine.XR.OpenXR.Features.OpenXRFeature { }
}

// ---------------------------------------------------------------- the plugin's own logger
namespace TLDRevamp
{
    internal static class Plugin
    {
        internal static readonly FakeLog Log = new FakeLog();
        internal sealed class FakeLog
        {
            public readonly List<string> Warnings = new List<string>();
            public void LogWarning(object m) { Warnings.Add(m == null ? "" : m.ToString()); }
            public void LogInfo(object m) { }
            public void LogError(object m) { }
            public void LogDebug(object m) { }
        }
    }

    /// The production JSON helper (src/TLDRevamp/TelemetryData.cs). It is pure, so the real one is compiled in
    /// rather than a stub: the state the bridge prints is then the state's real JSON.
    internal static class Json
    {
        public static string Str(string s)
        {
            var b = new System.Text.StringBuilder().Append('"');
            foreach (char c in s ?? "")
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break;
                }
            return b.Append('"').ToString();
        }
    }
}

// ---------------------------------------------------------------- the VR contract bridge the patch calls
namespace TLDRevamp.Fixes
{
    /// The seam the bridge's Harmony prefix/postfix calls. The stub records the calls, so the test can assert
    /// that the bridge reports the game's own VR requests to the contracts.
    public static class VrContractBridge
    {
        public static readonly List<string> Calls = new List<string>();
        public static void OnVrRequested() { Calls.Add("OnVrRequested"); }
        public static void OnVrReleased() { Calls.Add("OnVrReleased"); }
        public static void OnVrSettled() { Calls.Add("OnVrSettled"); }
        public static void OnVrStopped() { Calls.Add("OnVrStopped"); }
    }
}

// ---------------------------------------------------------------- Harmony: the bridge only patches with it
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string methodName) { }
        public HarmonyPatch(Type type, string methodName, Type[] args) { }
    }
}
