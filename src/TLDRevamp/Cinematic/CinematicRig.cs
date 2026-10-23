using System;
using UnityEngine;

namespace TLDRevamp.Cinematic
{
    /// The cinematic camera's own rig: a GameObject of its own with a Camera component of its own.
    ///
    /// The rule this whole feature rests on: the game's cameras are never moved, reparented, repointed or
    /// re-FOVed. The rig is a sibling of the player's rig, not a child of it, so the game's per-frame
    /// "lerp the camera parent toward the head" cannot drag it back. The gameplay camera stays enabled and in
    /// place, so every gameplay reader of it (hand-over placement, aim checks, the LOD culler) keeps seeing
    /// exactly what it saw before.
    ///
    /// The rig carries no AudioListener: the game already has one (menuhandler.s.listener) and a second one
    /// would silence it.
    public sealed class CinematicRig
    {
        private GameObject _go;
        private Camera _camera;

        /// A camera of the rig's own that draws one of the game's lower layers from the cinematic viewpoint. The game
        /// renders in up to three layers: player.skyCamera (the sky dome, clouds, sun and moon, with "sky objects" on),
        /// then player.farCamera (200 m to the horizon, with distant terrain on), then the gameplay camera. Each layer
        /// over another clears only depth. A rig of one camera copied that depth-only clear, so wherever it drew nothing
        /// (the sky, everything past 500 m) the player's own view showed through: in a replay, where the player stands
        /// still, the sky showed a frozen picture of where the player stood. Each mirror exists only while the game's
        /// camera it copies is on.
        private sealed class Mirror
        {
            private readonly string _name;
            private readonly float _depth;
            private readonly bool _fromSource;   // looks from where the game's camera is (the sky, centred on its camera)
            private GameObject _go;
            private Camera _cam;
            private Vector3 _sourceAt;
            public bool Wanted;

            public Mirror(string name, float depth, bool fromSource) { _name = name; _depth = depth; _fromSource = fromSource; }

            public bool Built => _cam != null;
            public Camera Cam => _cam;

            /// Copies the game's camera (reads only). Off when that camera is missing or off.
            public void Follow(Camera source, bool rigEnabled)
            {
                Wanted = source != null && source.isActiveAndEnabled;   // the game also switches the sky camera's whole object off
                if (!Wanted)
                {
                    if (_cam != null && _cam.enabled) _cam.enabled = false;
                    return;
                }
                var mirror = Camera();
                mirror.cullingMask = source.cullingMask;
                mirror.clearFlags = source.clearFlags;
                mirror.backgroundColor = source.backgroundColor;
                mirror.nearClipPlane = source.nearClipPlane;
                mirror.farClipPlane = source.farClipPlane;
                _sourceAt = source.transform.position;
                if (mirror.enabled != rigEnabled) mirror.enabled = rigEnabled;
            }

            private Camera Camera()
            {
                if (_cam != null) return _cam;
                if (_go != null) UnityEngine.Object.Destroy(_go);
                _go = new GameObject(_name);
                _go.hideFlags = HideFlags.HideAndDontSave;
                _go.transform.SetParent(null, false);   // never a child of the player rig
                _cam = _go.AddComponent<Camera>();
                _cam.depth = _depth;   // under the rig's own camera, over every game camera
                _cam.enabled = false;
                return _cam;
            }

            public void Apply(Vector3 position, Quaternion rotation, float fov)
            {
                if (_cam == null) return;
                _cam.transform.SetPositionAndRotation(_fromSource ? _sourceAt : position, rotation);
                _cam.fieldOfView = fov;
            }

            public void SetEnabled(bool on)
            {
                if (_cam != null) _cam.enabled = on && Wanted;
            }

            public void Dispose()
            {
                if (_cam != null) UnityEngine.Object.Destroy(_cam);
                if (_go != null) UnityEngine.Object.Destroy(_go);
                _cam = null;
                _go = null;
                Wanted = false;
            }
        }

        private readonly Mirror _far = new Mirror("TLDRevampCinematicFarCamera", 49f, false);
        private readonly Mirror _sky = new Mirror("TLDRevampCinematicSkyCamera", 48f, true);
        private bool _enabled;

        /// The camera's world position and rotation. Kept here so the rig can be rebuilt (a world shift, a
        /// scene reload) without losing the shot.
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;

        /// The camera's own field of view. The game's five cameras are never touched.
        public float Fov = CinematicLimits.DefaultFov;

        /// The culling mask copied from the gameplay camera, so the cinematic view shows the same world.
        public int CullingMask;
        public CameraClearFlags ClearFlags = CameraClearFlags.Skybox;
        public float NearClipPlane = 0.075f;
        public float FarClipPlane = 500f;

        /// The layer the gameplay camera excludes but the game's "main" camera draws on its own (layer 28 in this
        /// game): the rig draws it too, so a cinematic shot is not missing that layer.
        public int ExtraLayerMask;

        public bool Built => _go != null && _camera != null;

        /// Whether the far-layer mirror is in use (the game's far camera is on).
        public bool FarMirrored => _far.Wanted && _far.Built;

        /// Whether the sky-layer mirror is in use (the game's sky camera is on).
        public bool SkyMirrored => _sky.Wanted && _sky.Built;

        /// Creates the rig if it does not exist yet, and returns its camera.
        public Camera Camera()
        {
            if (_camera != null) return _camera;
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _go = new GameObject("TLDRevampCinematicCamera");
            _go.hideFlags = HideFlags.HideAndDontSave;
            _go.transform.SetParent(null, false);   // never a child of the player rig
            _camera = _go.AddComponent<Camera>();
            _camera.fieldOfView = Fov;
            _camera.nearClipPlane = NearClipPlane;
            _camera.farClipPlane = FarClipPlane;
            _camera.cullingMask = CullingMask;
            _camera.clearFlags = ClearFlags;
            _camera.depth = 50f;   // in front of the game's cameras (the world camera is at -1)
            _camera.enabled = false;
            return _camera;
        }

        /// Copies the gameplay camera's rendering setup, so the cinematic view matches what the player sees.
        /// This reads the game's camera; it never writes to it.
        public void ApplyGameplayMasks(Camera gameplay)
        {
            if (gameplay == null) return;
            CullingMask = gameplay.cullingMask | ExtraLayerMask;
            ClearFlags = gameplay.clearFlags;
            NearClipPlane = gameplay.nearClipPlane;
            FarClipPlane = gameplay.farClipPlane;
            var cam = Camera();
            cam.cullingMask = CullingMask;
            cam.clearFlags = ClearFlags;
            cam.backgroundColor = gameplay.backgroundColor;
            cam.nearClipPlane = NearClipPlane;
            cam.farClipPlane = FarClipPlane;
        }

        /// Copies the game's camera stack: the gameplay camera's setup, and the far and sky cameras' when they are
        /// on. Called every frame, so a draw-distance or sky-objects change while filming is followed. This reads the
        /// game's cameras; it never writes to them.
        public void MirrorStack(Camera gameplay, Camera farLayer, Camera skyLayer = null)
        {
            ApplyGameplayMasks(gameplay);
            _far.Follow(farLayer, _enabled);
            _sky.Follow(skyLayer, _enabled);
        }

        /// Puts the rig where it should be. The rig's transform is its own; nothing parents it to the player.
        public void Apply()
        {
            var cam = Camera();
            if (cam == null) return;
            cam.transform.SetPositionAndRotation(Position, Rotation);
            cam.fieldOfView = Fov;
            _far.Apply(Position, Rotation, Fov);
            _sky.Apply(Position, Rotation, Fov);
        }

        public void SetEnabled(bool on)
        {
            _enabled = on;
            var cam = Camera();
            if (cam != null) cam.enabled = on;
            _far.SetEnabled(on);
            _sky.SetEnabled(on);
        }

        /// Destroys the rig. The game's cameras and the player are untouched: there is nothing to restore here,
        /// because nothing of the game's was ever changed.
        public void Dispose()
        {
            if (_camera != null) UnityEngine.Object.Destroy(_camera);
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _far.Dispose();
            _sky.Dispose();
            _camera = null;
            _go = null;
            _enabled = false;
        }
    }
}
