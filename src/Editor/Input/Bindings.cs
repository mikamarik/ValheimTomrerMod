using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Placement;
using ValheimTomrer.Editor.Ui;
using ValheimTomrer.Editor.View;

namespace ValheimTomrer.Editor.Input
{
    /// <summary>What was held down with a key.</summary>
    [Flags]
    internal enum KeyMods
    {
        None = 0,
        Shift = 1,
        Ctrl = 2,
        Cmd = 4,
        Alt = 8,
    }

    /// <summary>One line of the help table.</summary>
    internal sealed class HelpRow
    {
        public readonly string Keys;
        public readonly string What;

        public HelpRow(string keys, string what)
        {
            Keys = keys;
            What = what;
        }
    }

    /// <summary>
    /// The one place that says what a key, the wheel and the fly keys do. The same map as the
    /// Tomrer editor's view/Editor.ts, with the help table next to it so the two cannot drift.
    ///
    /// <see cref="Press"/> is the whole dispatcher: it takes a key and what was held with it and
    /// returns whether the editor used it. <see cref="Tick"/> only reads the real keyboard and
    /// calls it, which is what lets the autotest drive every binding with no keyboard at all.
    /// </summary>
    internal static class Bindings
    {
        /// <summary>How far one arrow-key press moves the selection, and with Alt.</summary>
        public const float NudgeStep = 0.5f;

        public const float NudgeFine = 0.1f;

        /// <summary>Keys the editor watches. Everything else goes to the game as usual.</summary>
        private static readonly KeyCode[] Watched =
        {
            KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.Space,
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.Z, KeyCode.Y, KeyCode.G, KeyCode.R, KeyCode.Q, KeyCode.E,
            KeyCode.F, KeyCode.B, KeyCode.H, KeyCode.Slash, KeyCode.Question,
            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
            KeyCode.PageUp, KeyCode.PageDown, KeyCode.Delete, KeyCode.Backspace,
        };

        private static readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
        private static readonly List<KeyCode> LetGo = new List<KeyCode>();
        private static KeyCode[] _readable;

        /// <summary>What was held with the last key, or what the last <see cref="Tick"/> read.</summary>
        public static KeyMods Mods { get; private set; }

        public static bool Shift => (Mods & KeyMods.Shift) != 0;

        /// <summary>Ctrl or Cmd: the shortcut modifier, whichever the keyboard has.</summary>
        public static bool Shortcut => (Mods & (KeyMods.Ctrl | KeyMods.Cmd)) != 0;

        /// <summary>The fly keys held right now: right, up, forward.</summary>
        public static Vector3 Wish
        {
            get
            {
                var wish = Vector3.zero;
                wish.z += Down(KeyCode.W) - Down(KeyCode.S);
                wish.x += Down(KeyCode.D) - Down(KeyCode.A);
                wish.y += Down(KeyCode.Space)
                    - Mathf.Max(Down(KeyCode.LeftControl), Down(KeyCode.RightControl));
                return wish;
            }
        }

        /// <summary>Everything the editor does with the keyboard, the mouse wheel and the pad, once a frame.</summary>
        public static void Tick()
        {
            var dt = Mathf.Min(EditorInput.Dt, 0.1f);
            SetMods(ReadMods());

            if (ModUi.Typing || Dialogs.IsOpen)
            {
                // A text box or a dialog owns the keyboard: stop flying and read nothing.
                Held.Clear();
                return;
            }

            // macOS sends no key-up while Cmd is down, so a fly key held into a shortcut would
            // fly on for ever. Cmd going down drops everything, as the browser editor does.
            if (ZInput.GetKeyDown(KeyCode.LeftCommand, false) || ZInput.GetKeyDown(KeyCode.RightCommand, false))
            {
                Held.Clear();
            }

            foreach (var key in Readable())
            {
                if (ZInput.GetKeyDown(key, false))
                {
                    Press(key, Mods);
                }
            }

            LetGo.Clear();
            foreach (var key in Held)
            {
                if (!ZInput.GetKey(key, false))
                {
                    LetGo.Add(key);
                }
            }

            foreach (var key in LetGo)
            {
                Held.Remove(key);
            }

            Fly(dt);
        }

        /// <summary>
        /// One key press. True when the editor used it. Everything the help table promises runs
        /// through here, so the test can press a key without a keyboard.
        /// </summary>
        public static bool Press(KeyCode key, KeyMods mods)
        {
            SetMods(mods);

            // A text box has the keyboard, or a dialog is up: the key is not ours.
            if (ModUi.Typing || Dialogs.IsOpen)
            {
                return false;
            }

            // Ctrl also flies down, so a shortcut it can reach has to win before the fly keys.
            if (Shortcut && Shortcuts(key))
            {
                return true;
            }

            // Cmd is never a fly modifier: Cmd + a fly key is a shortcut that missed.
            if ((mods & KeyMods.Cmd) == 0 && IsFlyKey(key))
            {
                Held.Add(key);
                return true;
            }

            if (Shortcut)
            {
                return false;
            }

            return Plain(key);
        }

        /// <summary>
        /// The watched keys the game's input can actually read. ZInput turns a KeyCode into a
        /// new-input-system key and throws on the ones it has no name for, so each is tried once.
        /// </summary>
        private static KeyCode[] Readable()
        {
            if (_readable != null)
            {
                return _readable;
            }

            var works = new List<KeyCode>(Watched.Length);
            foreach (var key in Watched)
            {
                try
                {
                    ZInput.GetKeyDown(key, false);
                    works.Add(key);
                }
                catch (Exception e)
                {
                    ValheimTomrerPlugin.Log.LogInfo($"the game's input cannot read {key} ({e.GetType().Name}), skipping it");
                }
            }

            _readable = works.ToArray();
            return _readable;
        }

        /// <summary>A key let go. Only the fly keys care.</summary>
        public static void Release(KeyCode key)
        {
            Held.Remove(key);
        }

        /// <summary>
        /// What is held down with the keys. Shift turns snapping off while placing, the way the
        /// game does, so it has to be known even when no key is pressed this frame.
        /// </summary>
        public static void SetMods(KeyMods mods)
        {
            Mods = mods;
            EditorState.Snapping = (mods & KeyMods.Shift) == 0;
        }

        /// <summary>Moves the camera from the held fly keys. Shift flies three times faster.</summary>
        public static void Fly(float dt)
        {
            var camera = ViewportHost.Camera;
            if (camera == null)
            {
                return;
            }

            camera.FlyKeys(Wish, Shift, dt);
        }

        /// <summary>
        /// The wheel over the pane: it turns what is in hand, like the game's build wheel, and
        /// zooms toward the cursor otherwise. One notch is one step of 22.5 degrees.
        /// </summary>
        public static void Wheel(float notches, Vector2 viewport)
        {
            var camera = ViewportHost.Camera;
            if (camera == null)
            {
                return;
            }

            if (EditorState.Mode == EditMode.Place)
            {
                var steps = Mathf.RoundToInt(Mathf.Sign(notches) * Mathf.Ceil(Mathf.Abs(notches)));
                if (steps != 0)
                {
                    EditorState.SetPlaceSteps(EditorState.Steps + steps);
                }

                return;
            }

            // The UI module reports one notch as 1; the zoom curve is written for the browser's 100.
            camera.Zoom(-notches * 100f, viewport);
        }

        /// <summary>
        /// Esc, one step back at a time: a dialog, then what is in hand, then the free camera,
        /// then the selection. False means there was nothing left, so the window closes.
        /// </summary>
        public static bool Cancel()
        {
            if (Dialogs.Close())
            {
                return true;
            }

            if (EditorState.Mode != EditMode.Idle)
            {
                EditorState.CancelMode();
                return true;
            }

            if (ViewportHost.LeaveFreeLook())
            {
                return true;
            }

            if (EditorState.SelectionCount > 0)
            {
                EditorState.Select(Array.Empty<int>());
                return true;
            }

            return false;
        }

        /// <summary>Forget the held keys, so a key held while the window opened does not fly.</summary>
        public static void Reset()
        {
            Held.Clear();
            Mods = KeyMods.None;
        }

        // ---------- the map ----------

        /// <summary>The keys that work with Ctrl or Cmd.</summary>
        private static bool Shortcuts(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Z:
                    if (Shift)
                    {
                        EditorState.Redo();
                    }
                    else
                    {
                        EditorState.Undo();
                    }

                    return true;
                case KeyCode.Y:
                    EditorState.Redo();
                    return true;
                case KeyCode.A:
                    EditorState.SelectAll();
                    return true;
                case KeyCode.D:
                    EditorState.StartDuplicate();
                    return true;
                case KeyCode.S:
                    EditorCommands.Save();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The keys that work on their own.</summary>
        private static bool Plain(KeyCode key)
        {
            var placing = EditorState.Mode == EditMode.Place;
            switch (key)
            {
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (!placing)
                    {
                        EditorState.DeleteSelection();
                    }

                    return true;
                case KeyCode.PageUp:
                case KeyCode.PageDown:
                    if (!placing)
                    {
                        EditorState.Nudge(new Vector3(0f, key == KeyCode.PageUp ? Step() : -Step(), 0f));
                    }

                    return true;
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                    if (!placing)
                    {
                        NudgeArrow(key);
                    }

                    return true;
                case KeyCode.G:
                    if (!placing)
                    {
                        EditorState.StartMove();
                    }

                    return true;
                case KeyCode.F:
                    ViewportHost.Frame();
                    return true;
                case KeyCode.B:
                    ViewportHost.ToggleCamera();
                    return true;
                case KeyCode.R:
                    var direction = Shift ? -1 : 1;
                    if (placing)
                    {
                        EditorState.SetPlaceSteps(EditorState.Steps + direction);
                    }
                    else
                    {
                        EditorState.RotateSelection(direction);
                    }

                    return true;
                case KeyCode.Q:
                case KeyCode.E:
                    if (!placing)
                    {
                        return false;
                    }

                    EditorState.SetManualSnap(EditorState.Manual + (key == KeyCode.Q ? -1 : 1));
                    return true;
                case KeyCode.H:
                case KeyCode.Question:
                    Dialogs.Help();
                    return true;
                case KeyCode.Slash:
                    if (!Shift)
                    {
                        return false;
                    }

                    Dialogs.Help();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>An arrow key moves along the ground axis closest to where the camera looks.</summary>
        private static void NudgeArrow(KeyCode key)
        {
            var raycast = ViewportHost.Raycast;
            var forward = Vector3.forward;
            var right = Vector3.right;
            if (raycast != null)
            {
                raycast.GroundAxes(out forward, out right);
            }

            var axis = key == KeyCode.UpArrow ? Snap(forward)
                : key == KeyCode.DownArrow ? -Snap(forward)
                : key == KeyCode.RightArrow ? Snap(right)
                : -Snap(right);
            EditorState.Nudge(axis * Step());
        }

        /// <summary>The whole ground axis a direction is closest to.</summary>
        private static Vector3 Snap(Vector3 direction)
        {
            return Mathf.Abs(direction.x) >= Mathf.Abs(direction.z)
                ? new Vector3(Mathf.Sign(direction.x), 0f, 0f)
                : new Vector3(0f, 0f, Mathf.Sign(direction.z));
        }

        private static float Step()
        {
            return (Mods & KeyMods.Alt) != 0 ? NudgeFine : NudgeStep;
        }

        private static bool IsFlyKey(KeyCode key)
        {
            return key == KeyCode.W || key == KeyCode.A || key == KeyCode.S || key == KeyCode.D
                || key == KeyCode.Space || key == KeyCode.LeftControl || key == KeyCode.RightControl;
        }

        private static float Down(KeyCode key)
        {
            return Held.Contains(key) ? 1f : 0f;
        }

        private static KeyMods ReadMods()
        {
            var mods = KeyMods.None;
            if (ZInput.GetKey(KeyCode.LeftShift, false) || ZInput.GetKey(KeyCode.RightShift, false))
            {
                mods |= KeyMods.Shift;
            }

            if (ZInput.GetKey(KeyCode.LeftControl, false) || ZInput.GetKey(KeyCode.RightControl, false))
            {
                mods |= KeyMods.Ctrl;
            }

            if (ZInput.GetKey(KeyCode.LeftCommand, false) || ZInput.GetKey(KeyCode.RightCommand, false))
            {
                mods |= KeyMods.Cmd;
            }

            if (ZInput.GetKey(KeyCode.LeftAlt, false) || ZInput.GetKey(KeyCode.RightAlt, false))
            {
                mods |= KeyMods.Alt;
            }

            return mods;
        }

        // ---------- the help table ----------

        /// <summary>The mouse and keyboard half of the help, in the same order as Tomrer's.</summary>
        public static readonly HelpRow[] Keys =
        {
            new HelpRow("B", "Orbit camera or free camera. Free looks around with the mouse, like flying in the game."),
            new HelpRow("W, A, S, D", "Fly forward (where the camera looks), back, left, right"),
            new HelpRow("Space, Ctrl", "Fly up, down. Hold Shift to fly 3 times faster."),
            new HelpRow("Right drag", "Turn around the point in front (orbit), or look around (free)"),
            new HelpRow("Middle drag, or Shift + right drag", "Pan"),
            new HelpRow("Wheel", "Zoom toward the cursor. While placing it turns the piece 22.5 degrees."),
            new HelpRow("Click", "Select a piece, or drop what is in hand. Shift+click adds or removes."),
            new HelpRow("Drag on the view", "Select everything in the box (orbit camera)"),
            new HelpRow("Ctrl+A", "Select all"),
            new HelpRow("Pieces tab, click a piece", "Place it: it follows the mouse, click to place, Esc to stop"),
            new HelpRow("G", "Move the selection: it follows the mouse, click to drop, Esc to cancel"),
            new HelpRow("Ctrl+D", "Duplicate: the copy follows the mouse, and copies keep coming until Esc"),
            new HelpRow("R, Shift+R", "Turn 22.5 degrees: the piece in hand, else the selection"),
            new HelpRow("Arrow keys", "Nudge 0.5 m along the ground axis closest to the camera (with Alt 0.1 m)"),
            new HelpRow("PageUp, PageDown", "Nudge up or down"),
            new HelpRow("Shift (hold)", "No snapping while held, like in the game"),
            new HelpRow("Q, E", "Pick the snap point that goes on the aimed spot, like in the game"),
            new HelpRow("Delete, Backspace", "Delete the selection"),
            new HelpRow("Ctrl+Z, Shift+Ctrl+Z, Ctrl+Y", "Undo, redo"),
            new HelpRow("F", "Look at the selection, or at everything"),
            new HelpRow("Ctrl+S", "Save"),
            new HelpRow("H, ?", "This help"),
            new HelpRow("Esc", "Stop placing, else clear the selection, else close the editor"),
        };

        /// <summary>The controller half. Filled in when the pad is wired up.</summary>
        public static readonly HelpRow[] Pad = new HelpRow[0];
    }
}
