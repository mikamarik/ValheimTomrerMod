using BepInEx.Configuration;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Config for building a blueprint with the hammer: where the materials come from.
    /// Bound from the plugin, next to the editor's settings.
    /// </summary>
    internal static class BuildConfig
    {
        /// <summary>Prefab names left unchecked by default in the material-pull dialog.</summary>
        public static ConfigEntry<string> PullExcludedPrefabs;

        /// <summary>Off: the hammer pays from the inventory only.</summary>
        public static ConfigEntry<bool> UseChests;

        /// <summary>How far from the player a chest may stand and still pay, in metres.</summary>
        public static ConfigEntry<float> ChestRange;

        public static ConfigEntry<float> PlacementRange;
        public static ConfigEntry<UnityEngine.KeyCode> RaiseHeightKey;
        public static ConfigEntry<UnityEngine.KeyCode> LowerHeightKey;

        public static ConfigEntry<bool> PrepareGround;
        public static ConfigEntry<UnityEngine.KeyCode> PreviousAnchorKey;
        public static ConfigEntry<UnityEngine.KeyCode> NextAnchorKey;
        public static ConfigEntry<UnityEngine.KeyCode> PrepareGroundKey;

        public static void Bind(ConfigFile config)
        {
            RaiseHeightKey = config.Bind("Build", "RaiseHeightKey", UnityEngine.KeyCode.PageUp,
                "Raise a whole-blueprint placement by 0.25 metres, up to +5 metres. Ctrl + wheel also adjusts height.");
            LowerHeightKey = config.Bind("Build", "LowerHeightKey", UnityEngine.KeyCode.PageDown,
                "Lower a whole-blueprint placement by 0.25 metres, down to -5 metres, constrained by editable terrain.");
            PlacementRange = config.Bind("Build", "PlacementRange", 50f,
                new ConfigDescription("Maximum distance from your eyes to the whole-blueprint placement anchor, in metres. Ordinary hammer pieces keep their normal reach.",
                    new AcceptableValueRange<float>(50f, 100f)));
            PrepareGround = config.Bind("Build", "PrepareGround", false,
                "Raise and lower outdoor terrain under a new blueprint's lowest foundation when building. Toggle while aiming; terrain changes only on a successful build click.");
            PreviousAnchorKey = config.Bind("Build", "PreviousAnchorKey", UnityEngine.KeyCode.Q,
                "Select the previous foundation corner/center anchor while aiming a whole blueprint.");
            NextAnchorKey = config.Bind("Build", "NextAnchorKey", UnityEngine.KeyCode.E,
                "Select the next foundation corner/center anchor while aiming a whole blueprint.");
            PrepareGroundKey = config.Bind("Build", "PrepareGroundKey", UnityEngine.KeyCode.T,
                "Toggle raise/lower ground preparation while aiming a new blueprint.");

            PullExcludedPrefabs = config.Bind(
                "Build", "PullExcludedPrefabs",
                "Wood,RoundLog,FineWood,ElderBark,YggdrasilWood,Blackwood,Stone,Grausten",
                "Materials left unchecked in Pull materials by default (comma-separated prefab names). Individual selections last until the game restarts.");

            UseChests = config.Bind(
                "Build",
                "UseChests",
                true,
                "A blueprint build also takes materials from chests near you, after your inventory. "
                + "Carts and ship holds count too. Only chests you may open.");

            ChestRange = config.Bind(
                "Build",
                "ChestRange",
                20f,
                new ConfigDescription(
                    "How far a chest may be from you, in metres, and still give materials. Nearest first.",
                    new AcceptableValueRange<float>(0f, 100f)));
        }
    }
}
