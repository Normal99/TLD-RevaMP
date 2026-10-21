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

        /// The rig's mirror of the game's far camera. With distant terrain on (a draw distance above 500 m) the
        /// game renders in two layers: player.farCamera (200 m to the horizon, clears to the sky) under the
        /// gameplay camera, which then clears only depth. A rig of one camera copied the depth-only clear, so
        /// wherever it drew nothing (the sky, everything past 500 m) the player's own view showed through. The
        /// mirror draws the far layer from the cinematic viewpoint; it exists only while the game's far camera is on.
        private GameObject _farGo;
        private Camera _far;
        private bool _farWanted, _enabled;

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
        public bool FarMirrored => _farWanted && _far != null;

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
            cam.nearClipPlane = NearClipPlane;
            cam.farClipPlane = FarClipPlane;
        }

        /// Copies the game's camera stack: the gameplay camera's setup, and the far camera's when it is on. Called
        /// every frame, so a draw-distance change while filming is followed. This reads the game's cameras; it never
        /// writes to them.
        public void MirrorStack(Camera gameplay, Camera farLayer)
        {
            ApplyGameplayMasks(gameplay);
            _farWanted = farLayer != null && farLayer.enabled;
            if (!_farWanted)
            {
                if (_far != null && _far.enabled) _far.enabled = false;
                return;
            }
            var mirror = FarCamera();
            mirror.cullingMask = farLayer.cullingMask;
            mirror.clearFlags = farLayer.clearFlags;
            mirror.nearClipPlane = farLayer.nearClipPlane;
            mirror.farClipPlane = farLayer.farClipPlane;
            if (mirror.enabled != _enabled) mirror.enabled = _enabled;
        }

        private Camera FarCamera()
        {
            if (_far != null) return _far;
            if (_farGo != null) UnityEngine.Object.Destroy(_farGo);
            _farGo = new GameObject("TLDRevampCinematicFarCamera");
            _farGo.hideFlags = HideFlags.HideAndDontSave;
            _farGo.transform.SetParent(null, false);   // never a child of the player rig
            _far = _farGo.AddComponent<Camera>();
            _far.depth = 49f;   // under the rig's own camera, over every game camera
            _far.enabled = false;
            return _far;
        }

        /// Puts the rig where it should be. The rig's transform is its own; nothing parents it to the player.
        public void Apply()
        {
            var cam = Camera();
            if (cam == null) return;
            cam.transform.SetPositionAndRotation(Position, Rotation);
            cam.fieldOfView = Fov;
            if (_far != null)
            {
                _far.transform.SetPositionAndRotation(Position, Rotation);
                _far.fieldOfView = Fov;
            }
        }

        public void SetEnabled(bool on)
        {
            _enabled = on;
            var cam = Camera();
            if (cam != null) cam.enabled = on;
            if (_far != null) _far.enabled = on && _farWanted;
        }

        /// Destroys the rig. The game's cameras and the player are untouched: there is nothing to restore here,
        /// because nothing of the game's was ever changed.
        public void Dispose()
        {
            if (_camera != null) UnityEngine.Object.Destroy(_camera);
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_far != null) UnityEngine.Object.Destroy(_far);
            if (_farGo != null) UnityEngine.Object.Destroy(_farGo);
            _camera = null;
            _go = null;
            _far = null;
            _farGo = null;
            _farWanted = false;
            _enabled = false;
        }
    }
}
