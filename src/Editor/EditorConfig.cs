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

        /// <summary>Starts the two-corner box that turns a standing building into a blueprint.</summary>
        public static ConfigEntry<KeyCode> CaptureKey;

        /// <summary>Off: the palette only lists what this character has unlocked.</summary>
        public static ConfigEntry<bool> ShowAllPieces;

        /// <summary>The camera the pane starts in. The B key and the Orbit/Free buttons switch it.</summary>
        public static ConfigEntry<View.CameraMode> StartCamera;

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
                "With the editor closed: pick two corners in the world, and what stands in the box "
                + "opens in the editor as a new blueprint. Esc stops it.");

            ShowAllPieces = config.Bind(
                "Editor",
                "ShowAllPieces",
                false,
                "Show every building piece in the editor. Off means only the ones this character has unlocked.");

            StartCamera = config.Bind(
                "Editor",
                "CameraMode",
                View.CameraMode.Orbit,
                "Which camera the editor starts in. Orbit circles the blueprint and keeps the cursor. "
                + "Free flies with W A S D and looks with the mouse.");

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
                    "Mouse look speed in the free camera. 2 is twice as fast.",
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
