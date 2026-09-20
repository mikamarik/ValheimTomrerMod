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

        public static void Bind(ConfigFile config)
        {
            Key = config.Bind(
                "Editor",
                "Key",
                KeyCode.F7,
                "Opens the blueprint editor. Esc, the same key, or the pad's B/circle closes it.");
        }
    }
}
