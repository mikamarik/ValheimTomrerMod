using BepInEx.Configuration;
using UnityEngine;

namespace ValheimTomrer.Editor
{
    /// <summary>
    /// Config for the in-game blueprint editor. Bound from the plugin so every editor
    /// setting sits in one place instead of growing the plugin file.
    /// </summary>
    internal static class EditorConfig
    {
        /// <summary>
        /// The layer the editor draws its own copies on. Free in Valheim 1.0 (Phase 0 measured
        /// 3, 6, 7 and 30 free). The main camera drops it in GameCameraAwakePatch, and every
        /// physics query the editor makes is masked to it, so the two worlds never mix.
        /// </summary>
        public const int Layer = 30;

        public static ConfigEntry<KeyCode> Key;

        /// <summary>Puts down the rectangle that turns a standing building into a blueprint, and captures it.</summary>
        public static ConfigEntry<KeyCode> CaptureKey;

        /// <summary>Off: the palette only lists what this character has unlocked.</summary>
        public static ConfigEntry<bool> ShowAllPieces;

        /// <summary>Snap dots while placing. The top bar's Dots button writes this.</summary>
        public static ConfigEntry<bool> SnapDots;

        /// <summary>Pieces as wire boxes instead of models. The top bar's Boxes button writes this.</summary>
        public static ConfigEntry<bool> Boxes;

        public static ConfigEntry<float> LookSensitivity;

        public static ConfigEntry<float> PadLookSensitivity;

        public static void Bind(ConfigFile config)
        {
            Key = config.Bind(
                "Editor",
                "Key",
                KeyCode.F7,
                "Opens and closes the blueprint editor. Esc and the pad's circle close it too.");

            CaptureKey = config.Bind(
                "Editor",
                "CaptureKey",
                KeyCode.F8,
                "With the editor closed: puts a rectangle on the ground where you aim. The wheel turns it, "
                + "Shift + wheel and Alt + wheel change its sides. Press again and what stands inside opens "
                + "in the editor as a new blueprint. Esc stops it.");

            ShowAllPieces = config.Bind(
                "Editor",
                "ShowAllPieces",
                false,
                "Show every building piece in the editor. Off means only the ones this character has unlocked.");

            SnapDots = config.Bind(
                "Editor",
                "SnapDots",
                true,
                "Show the snap dots while placing a piece. Snapping itself is always on.");

            Boxes = config.Bind(
                "Editor",
                "Boxes",
                false,
                "Draw pieces as plain boxes instead of models. Easier to see through a full blueprint.");

            LookSensitivity = config.Bind(
                "Editor",
                "LookSensitivity",
                1f,
                new ConfigDescription(
                    "Mouse look speed in the 3D view. 2 is twice as fast.",
                    new AcceptableValueRange<float>(0.1f, 5f)));

            PadLookSensitivity = config.Bind(
                "Editor",
                "PadLookSensitivity",
                1f,
                new ConfigDescription(
                    "Right stick look speed on a controller. 2 is twice as fast.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
        }
    }
}
