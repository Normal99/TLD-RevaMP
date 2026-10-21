using System;

namespace TLDRevamp.VR
{
    /// The state of a VR UI ray (one per hand).
    public enum VrUiRayState
    {
        Idle,      // the ray points at nothing
        Hovering,  // the ray points at a control
        Pressed,   // the control is pressed (this frame)
        Activated, // the control was activated (this frame)
        Cancelled   // the press was cancelled (this frame)
    }

    /// One event the ray reports. The UI code reacts to these; nothing else may act on a raw button.
    public enum VrUiEvent
    {
        None,
        HoverChanged,
        Pressed,
        Activated,
        Cancelled,
        Released
    }

    /// The UI ray contract: hit, focus, one activation, cancel.
    ///
    /// The game has no VR UI ray of its own: the hands carry a laser (laserScript) and the menu is a world-space
    /// canvas (mainScript2.UpdVRCanv). The buttons are ordinary Unity UI buttons, so a VR press has to arrive as one
    /// pointer press and one release, and a press that is cancelled must not leave a button stuck down.
    ///
    /// The contract, in order:
    ///   - a hit names the control under the ray; a different control ends the old focus and starts a new one
    ///   - a press on the focused control opens the press
    ///   - the press opens exactly one activation: a held press does not activate twice
    ///   - a cancel closes the press without an activation, and the control is released
    ///   - a release closes the press; a release without a press does nothing
    public sealed class VrUiRayMachine
    {
        private readonly string _name;
        private VrUiRayState _state = VrUiRayState.Idle;
        private string _focus;      // the control under the ray
        private string _pressed;    // the control the press is open on
        private bool _activated;    // the open press already activated
        private int _activations, _cancellations, _presses;

        public VrUiRayMachine(string name)
        {
            _name = name ?? "";
        }

        public string Name { get { return _name; } }
        public VrUiRayState State { get { return _state; } }
        public string Focus { get { return _focus; } }
        public string Pressed { get { return _pressed; } }
        public int Activations { get { return _activations; } }
        public int Cancellations { get { return _cancellations; } }
        public int Presses { get { return _presses; } }

        /// The ray hit a control (or nothing, with a null name). A focus change is an event of its own.
        public VrUiEvent Hit(string control)
        {
            if (string.Equals(control, _focus, StringComparison.Ordinal))
                return VrUiEvent.None;
            _focus = control;
            return VrUiEvent.HoverChanged;
        }

        /// The trigger went down. Only a control under the ray may be pressed.
        public VrUiEvent Press()
        {
            if (_focus == null) return VrUiEvent.None;
            if (_pressed != null) return VrUiEvent.None;   // a second press while one is open is ignored
            _pressed = _focus;
            _activated = false;
            _state = VrUiRayState.Pressed;
            _presses++;
            return VrUiEvent.Pressed;
        }

        /// The trigger came up. A press that never activated is released; one that already did is just closed.
        public VrUiEvent Release()
        {
            if (_pressed == null) return VrUiEvent.None;
            var hadActivated = _activated;
            _pressed = null;
            _activated = false;
            _state = _focus != null ? VrUiRayState.Hovering : VrUiRayState.Idle;
            return hadActivated ? VrUiEvent.None : VrUiEvent.Released;
        }

        /// The press opens its one activation. A held press does not activate again: the second call is nothing.
        public VrUiEvent Activate()
        {
            if (_pressed == null) return VrUiEvent.None;
            if (_activated) return VrUiEvent.None;
            _activated = true;
            _state = VrUiRayState.Activated;
            _activations++;
            return VrUiEvent.Activated;
        }

        /// The press was cancelled: the device went away, the session failed, the ray lost the control. The control
        /// is released and no activation happens.
        public VrUiEvent Cancel()
        {
            if (_pressed == null && _state != VrUiRayState.Activated) return VrUiEvent.None;
            _pressed = null;
            _activated = false;
            _state = _focus != null ? VrUiRayState.Hovering : VrUiRayState.Idle;
            _cancellations++;
            return VrUiEvent.Cancelled;
        }

        /// The ray lost the control under it while a press was open: the press is cancelled, not left hanging.
        public VrUiEvent FocusLost()
        {
            var wasFocused = _focus != null;
            _focus = null;
            if (wasFocused && _pressed != null) return Cancel();
            _state = VrUiRayState.Idle;
            return VrUiEvent.HoverChanged;
        }

        /// A new frame: the one-frame events are gone, the focus stays.
        public void EndFrame()
        {
            if (_state == VrUiRayState.Pressed) _state = _focus != null ? VrUiRayState.Hovering : VrUiRayState.Idle;
            else if (_state == VrUiRayState.Activated) _state = _focus != null ? VrUiRayState.Hovering : VrUiRayState.Idle;
            else if (_state == VrUiRayState.Cancelled) _state = _focus != null ? VrUiRayState.Hovering : VrUiRayState.Idle;
        }

        public string Describe()
        {
            return _name + " ui=" + _state.ToString().ToLowerInvariant() + " focus=" + (_focus ?? "-") +
                   " pressed=" + (_pressed ?? "-") + " activations=" + _activations + " cancels=" + _cancellations;
        }
    }
}
