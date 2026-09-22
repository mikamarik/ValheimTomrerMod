using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ValheimTomrer.Editor.Input
{
    /// <summary>Buttons named the way the controller shows them, PlayStation first.</summary>
    internal enum PadButton
    {
        Cross,
        Circle,
        Square,
        Triangle,
        L1,
        R1,
        L2,
        R2,
        Options,
        L3,
        R3,
        Up,
        Down,
        Left,
        Right,
    }

    /// <summary>
    /// A made-up pad, for the autotest. <see cref="PadReader.Fake"/> takes one instead of a real
    /// device, and the reader treats it exactly like one: same dead zone, same press edges.
    /// </summary>
    internal sealed class PadState
    {
        public bool Ps = true;
        public Vector2 Ls;
        public Vector2 Rs;

        public readonly HashSet<PadButton> Down = new HashSet<PadButton>();

        public void Set(PadButton button, bool down)
        {
            if (down)
            {
                Down.Add(button);
            }
            else
            {
                Down.Remove(button);
            }
        }

        public void Clear()
        {
            Down.Clear();
            Ls = Vector2.zero;
            Rs = Vector2.zero;
        }
    }

    /// <summary>One frame of the pad. Sticks are already dead-zoned, up is positive.</summary>
    internal sealed class PadFrame
    {
        public bool Ps;          // a PlayStation controller
        public Vector2 Ls;
        public Vector2 Rs;
        public bool Woke;        // just picked up: a button went down, or a stick left the middle

        internal HashSet<PadButton> Down = new HashSet<PadButton>();
        internal HashSet<PadButton> Before = new HashSet<PadButton>();

        public bool Held(PadButton button) => Down.Contains(button);

        public bool Pressed(PadButton button) => Down.Contains(button) && !Before.Contains(button);
    }

    /// <summary>
    /// Reads the first connected pad. The sticks read what the game's own ZInput reads (the raw
    /// stick, radial dead zone 0.2 rescaled to 0..1), so the look feels like the game's. Triggers
    /// are down above 0.5 and up again below 0.3, PlayStation is detected by name.
    ///
    /// Unity already reports stick up as positive, so nothing is inverted here.
    /// </summary>
    internal sealed class PadReader
    {
        private const float DeadZone = 0.2f;   // ZInput.m_stickDeadZone

        private static readonly PadButton[] All = (PadButton[])System.Enum.GetValues(typeof(PadButton));

        private HashSet<PadButton> _prev = new HashSet<PadButton>();
        private bool _still = true;   // both sticks in the middle last frame
        private bool _fresh = true;

        /// <summary>
        /// A made-up pad the reader hands out instead of a real device. Only the autotest sets
        /// it, so every button can be driven with no controller plugged in.
        /// </summary>
        public static PadState Fake { get; set; }

        /// <summary>Buttons held right now count as already held: the next read reports no presses.</summary>
        public void Reset()
        {
            _fresh = true;
        }

        /// <summary>The first connected pad, or null. Nothing while the game window is not in front.</summary>
        public PadFrame Read()
        {
            var fake = Fake;
            var pad = Gamepad.current;
            if (fake == null && (pad == null || !Application.isFocused))
            {
                Reset();
                return null;
            }

            var down = new HashSet<PadButton>();
            if (fake != null)
            {
                down.UnionWith(fake.Down);
            }
            else
            {
                foreach (var button in All)
                {
                    var control = Control(pad, button);
                    if (control == null)
                    {
                        continue;
                    }

                    // Triggers are analog: down past half way, up again below a third.
                    var isDown = button == PadButton.L2 || button == PadButton.R2
                        ? control.ReadValue() > (_prev.Contains(button) ? 0.3f : 0.5f)
                        : control.isPressed;
                    if (isDown)
                    {
                        down.Add(button);
                    }
                }
            }

            var before = _fresh ? down : _prev;
            // Unprocessed, like ZInput.ReadValueDef: ReadValue adds Unity's own stick dead zone
            // (0.125 to 0.925) under ours, a bigger dead zone and a steeper ramp than the game's.
            var ls = Stick(fake != null ? fake.Ls : pad.leftStick.ReadUnprocessedValue());
            var rs = Stick(fake != null ? fake.Rs : pad.rightStick.ReadUnprocessedValue());
            var still = ls == Vector2.zero && rs == Vector2.zero;

            var woke = false;
            foreach (var button in down)
            {
                if (!before.Contains(button))
                {
                    woke = true;
                    break;
                }
            }

            // A drifting stick that never comes back to the middle does not count:
            // the mouse can still take over.
            woke = woke || (!still && _still && !_fresh);

            var frame = new PadFrame
            {
                Ps = fake != null ? fake.Ps : IsPlayStation(pad),
                Ls = ls,
                Rs = rs,
                Woke = woke,
                Down = down,
                Before = before,
            };

            _prev = down;
            _still = still;
            _fresh = false;
            return frame;
        }

        /// <summary>Radial dead zone, then 0..1 again beyond it: ZInput.ApplyDeadzoneVector.</summary>
        private static Vector2 Stick(Vector2 raw)
        {
            var length = raw.magnitude;
            if (length <= DeadZone)
            {
                return Vector2.zero;
            }

            return raw * (Mathf.Min(1f, (length - DeadZone) / (1f - DeadZone)) / length);
        }

        // Sony's USB vendor id is 054c. Not "Wireless Controller": the Xbox pad is called that too.
        private static bool IsPlayStation(Gamepad pad)
        {
            return Mentions(pad.name) || Mentions(pad.layout) || Mentions(pad.displayName);
        }

        private static bool Mentions(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            text = text.ToLowerInvariant();
            return text.Contains("dualsense") || text.Contains("dualshock")
                || text.Contains("playstation") || text.Contains("054c");
        }

        private static ButtonControl Control(Gamepad pad, PadButton button)
        {
            switch (button)
            {
                case PadButton.Cross: return pad.buttonSouth;
                case PadButton.Circle: return pad.buttonEast;
                case PadButton.Square: return pad.buttonWest;
                case PadButton.Triangle: return pad.buttonNorth;
                case PadButton.L1: return pad.leftShoulder;
                case PadButton.R1: return pad.rightShoulder;
                case PadButton.L2: return pad.leftTrigger;
                case PadButton.R2: return pad.rightTrigger;
                case PadButton.Options: return pad.startButton;
                case PadButton.L3: return pad.leftStickButton;
                case PadButton.R3: return pad.rightStickButton;
                case PadButton.Up: return pad.dpad.up;
                case PadButton.Down: return pad.dpad.down;
                case PadButton.Left: return pad.dpad.left;
                case PadButton.Right: return pad.dpad.right;
                default: return null;
            }
        }
    }
}
