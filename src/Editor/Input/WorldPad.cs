using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Editor.Input
{
    /// <summary>
    /// The controller in the world, with the editor window closed: the hammer's next blueprint, the
    /// editor's open combo and the capture. It reads the game's own named buttons (ZInput), so the
    /// game's layout decides which button is which: "the modifier" is the game's JoyAltKeys, L2 in
    /// the default layout and L1 in the alternative one.
    ///
    /// It also holds a few buttons back from the game (<c>ZInputTryGetButtonStatePatch</c>), so a
    /// press the mod uses does not do something else as well:
    /// - the D-pad and circle while a capture is up: the hotbar, the forsaken power, the camera and
    ///   minimap zoom and the jump stay quiet;
    /// - square and triangle while the modifier is held: modifier + triangle must not also open the
    ///   inventory, modifier + square must not use or sit.
    /// A button held back stays held back until it is let go, so the rest of that press never
    /// reaches the game either. Nothing is held back while a game menu (inventory, build menu, map,
    /// radial menu, trader) or the editor window is up: those have every button, as usual.
    ///
    /// The mod's own reads go to the game's button objects directly, never through the held-back
    /// path, so they always see the press.
    /// </summary>
    internal static class WorldPad
    {
        /// <summary>The game's modifier: L2 in the default layout, L1 in the alternative one.</summary>
        public const string Modifier = "JoyAltKeys";

        public const string Square = "JoyButtonX";
        public const string Triangle = "JoyButtonY";
        public const string Circle = "JoyButtonB";
        public const string DpadLeft = "JoyDPadLeft";
        public const string DpadRight = "JoyDPadRight";
        public const string DpadUp = "JoyDPadUp";
        public const string DpadDown = "JoyDPadDown";

        [System.Flags]
        private enum Group
        {
            None = 0,
            Dpad = 1,
            Circle = 2,
            Square = 4,
            Triangle = 8,
        }

        /// <summary>Every game button bound to one of the physical buttons above, whatever its name.</summary>
        private static readonly Dictionary<string, Group> Groups = new Dictionary<string, Group>();

        private static readonly Dictionary<string, PadButton> Buttons = new Dictionary<string, PadButton>
        {
            { "<Gamepad>/buttonSouth", PadButton.Cross },
            { "<Gamepad>/buttonEast", PadButton.Circle },
            { "<Gamepad>/buttonWest", PadButton.Square },
            { "<Gamepad>/buttonNorth", PadButton.Triangle },
            { "<Gamepad>/leftShoulder", PadButton.L1 },
            { "<Gamepad>/rightShoulder", PadButton.R1 },
            { "<Gamepad>/leftTrigger", PadButton.L2 },
            { "<Gamepad>/rightTrigger", PadButton.R2 },
            { "<Gamepad>/start", PadButton.Options },
            { "<Gamepad>/leftStickPress", PadButton.L3 },
            { "<Gamepad>/rightStickPress", PadButton.R3 },
            { "<Gamepad>/dpad/up", PadButton.Up },
            { "<Gamepad>/dpad/down", PadButton.Down },
            { "<Gamepad>/dpad/left", PadButton.Left },
            { "<Gamepad>/dpad/right", PadButton.Right },
        };

        private static ZInput _builtFor;
        private static InputLayout _builtLayout;
        private static int _builtCount = -1;

        private static int _liveFrame = -1;
        private static bool _live;
        private static Group _latched;

        /// <summary>
        /// True where the world combos work: the mod on, the editor window closed, the player free to
        /// act, and no game menu up. Worked out once a frame.
        /// </summary>
        public static bool Live
        {
            get
            {
                if (_liveFrame != Time.frameCount)
                {
                    _liveFrame = Time.frameCount;
                    _live = WorkOutLive();
                }

                return _live;
            }
        }

        /// <summary>The game's modifier is held.</summary>
        public static bool ModifierHeld => Held(Modifier);

        /// <summary>Square alone: the hammer's next blueprint, like B. The caller checks build mode.</summary>
        public static bool NextBlueprint => Pressed(Square) && !ModifierHeld && !Hud.InRadial();

        /// <summary>Modifier + square with the window closed: opens the editor, like F7.</summary>
        public static bool OpenEditor => Live && ModifierHeld && Pressed(Square);

        /// <summary>Modifier + triangle: starts a capture, or takes it, like F8.</summary>
        public static bool Capture => Live && ModifierHeld && Pressed(Triangle);

        /// <summary>Circle: stops a capture, like Esc. The caller checks that one is up.</summary>
        public static bool CancelCapture => Live && Pressed(Circle);

        /// <summary>
        /// The modifier as the editor's own pad reader names it (<see cref="PadReader"/> reads the pad
        /// directly), so the same two buttons open and close the window.
        /// </summary>
        public static PadButton ModifierButton => ButtonOf(Modifier) ?? PadButton.L2;

        /// <summary>The game button went down this frame (the game's own repeat for the D-pad included).</summary>
        public static bool Pressed(string name)
        {
            var def = Def(name);
            return def != null && def.Pressed;
        }

        public static bool Held(string name)
        {
            var def = Def(name);
            return def != null && def.Held;
        }

        /// <summary>
        /// "L2", "LT" or "L1": what the pad in hand calls the button the game binds to a name, in the
        /// <see cref="Glyphs"/> wording. The name itself when nothing is bound.
        /// </summary>
        public static string NameOf(string gameButton, Glyphs glyphs)
        {
            var button = ButtonOf(gameButton);
            return button.HasValue ? glyphs.Of(button.Value) : gameButton;
        }

        /// <summary>The pad button the game binds to a name, or null.</summary>
        public static PadButton? ButtonOf(string gameButton)
        {
            var path = PathOf(Def(gameButton));
            return path != null && Buttons.TryGetValue(path, out var button) ? button : (PadButton?)null;
        }

        /// <summary>
        /// Once a frame, before anything reads the combos: the button list (again after a layout
        /// change), and which held-back buttons are still down.
        /// </summary>
        public static void Tick()
        {
            BuildGroups();
            if (!ValheimTomrerPlugin.ModEnabled.Value)
            {
                _latched = Group.None;
                return;
            }

            _latched = Wanted() | (_latched & Down());
        }

        /// <summary>
        /// True when the game must not see this button now. Called for every button the game reads,
        /// many times a frame, so the common case is one dictionary miss.
        /// </summary>
        public static bool HeldBack(string name)
        {
            if (name == null || !Groups.TryGetValue(name, out var group))
            {
                return false;
            }

            return ((Wanted() | _latched) & group) != Group.None;
        }

        /// <summary>What the combos hold back right now.</summary>
        private static Group Wanted()
        {
            if (!Live)
            {
                return Group.None;
            }

            var wanted = Group.None;
            if (WorldCapture.Active)
            {
                wanted |= Group.Dpad | Group.Circle;
            }

            if (ModifierHeld)
            {
                wanted |= Group.Square | Group.Triangle;
            }

            return wanted;
        }

        /// <summary>The held-back buttons that are physically down, whatever the game's own state says.</summary>
        private static Group Down()
        {
            var down = Group.None;
            if (IsDown(DpadLeft) || IsDown(DpadRight) || IsDown(DpadUp) || IsDown(DpadDown))
            {
                down |= Group.Dpad;
            }

            if (IsDown(Circle))
            {
                down |= Group.Circle;
            }

            if (IsDown(Square))
            {
                down |= Group.Square;
            }

            if (IsDown(Triangle))
            {
                down |= Group.Triangle;
            }

            return down;
        }

        private static bool IsDown(string name)
        {
            var def = Def(name);
            return def != null && def.ButtonAction != null && def.ButtonAction.enabled && def.ButtonAction.IsPressed();
        }

        private static bool WorkOutLive()
        {
            var enabled = ValheimTomrerPlugin.ModEnabled;
            if (enabled == null || !enabled.Value || ModUi.Open || !EditorSession.CanOpen())
            {
                return false;
            }

            return !InventoryGui.IsVisible() && !Hud.IsPieceSelectionVisible() && !Hud.InRadial()
                && !Minimap.IsOpen() && !StoreGui.IsVisible();
        }

        private static ZInput.ButtonDef Def(string name)
        {
            var input = ZInput.instance;
            return input != null ? input.GetButtonDef(name) : null;
        }

        private static string PathOf(ZInput.ButtonDef def)
        {
            if (def == null || def.ButtonAction == null || def.ButtonAction.bindings.Count == 0)
            {
                return null;
            }

            return def.GetActionPath();
        }

        /// <summary>
        /// Which game buttons sit on the D-pad, circle, square and triangle, by their binding, so
        /// every name the game reads them by is held back: JoyHotbarUse, JoyGP, JoyCamZoomIn and
        /// JoySit on the D-pad, JoyJump, JoyBuildMenu and JoyDodge on circle, and so on. Built again
        /// when the game's layout or its button list changes.
        /// </summary>
        private static void BuildGroups()
        {
            var input = ZInput.instance;
            var count = input != null && input.m_buttons != null ? input.m_buttons.Count : -1;
            if (input == _builtFor && ZInput.InputLayout == _builtLayout && count == _builtCount)
            {
                return;
            }

            _builtFor = input;
            _builtLayout = ZInput.InputLayout;
            _builtCount = count;
            Groups.Clear();
            if (input == null || input.m_buttons == null)
            {
                return;
            }

            foreach (var pair in input.m_buttons)
            {
                var def = pair.Value;
                if (def == null || def.Source != ZInput.InputSource.Gamepad)
                {
                    continue;
                }

                var path = PathOf(def);
                if (path == null || !Buttons.TryGetValue(path, out var button))
                {
                    continue;
                }

                var group = GroupOf(button);
                if (group != Group.None)
                {
                    Groups[pair.Key] = group;
                }
            }
        }

        private static Group GroupOf(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up:
                case PadButton.Down:
                case PadButton.Left:
                case PadButton.Right:
                    return Group.Dpad;
                case PadButton.Circle:
                    return Group.Circle;
                case PadButton.Square:
                    return Group.Square;
                case PadButton.Triangle:
                    return Group.Triangle;
                default:
                    return Group.None;
            }
        }
    }
}
