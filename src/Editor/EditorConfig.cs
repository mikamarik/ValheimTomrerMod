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
