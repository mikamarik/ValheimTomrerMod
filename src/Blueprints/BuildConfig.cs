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

        public static void Bind(ConfigFile config)
        {
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
