using System.Reflection;
using ValheimTomrer.Blueprints;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimTomrer
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class ValheimTomrerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mikamarik.valheimtomrer";
        public const string PluginName = "ValheimTomrer";
        public const string PluginVersion = "0.1.0";

        internal static ValheimTomrerPlugin Instance;
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> ModEnabled;
        internal static ConfigEntry<KeyCode> BlueprintKey;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ModEnabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "Master switch. Turn off to neutralise ValheimTomrer without uninstalling it.");

            BlueprintKey = Config.Bind(
                "Blueprints",
                "Key",
                KeyCode.B,
                "With a hammer in hand: selects the next blueprint. After the last one, back to normal building.");

#if DEBUG
            Dev.AutoTest.Init();
#endif

            BlueprintLibrary.Reload();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        /// <summary>
        /// Harmony patches outlive the plugin object, so an unpatch here keeps
        /// ScriptEngine hot-reloads from stacking duplicates.
        /// </summary>
        private void OnDestroy()
        {
            BlueprintMode.Exit();
            _harmony?.UnpatchSelf();
            Log?.LogInfo($"{PluginName} unloaded.");
        }
    }
}
