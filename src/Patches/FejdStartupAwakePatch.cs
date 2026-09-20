using HarmonyLib;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Scaffold smoke test. Proves the whole pipeline in one line of log output:
    /// Harmony patching works, and publicization exposed a private game member.
    ///
    /// FejdStartup.Awake runs on the main menu, so this fires a few seconds after
    /// launch without loading a world. Delete once real features land.
    /// </summary>
    [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake))]
    internal static class FejdStartupAwakePatch
    {
        private static void Postfix()
        {
            if (!ValheimTomrerPlugin.ModEnabled.Value)
            {
                return;
            }

            // FejdStartup.m_instance is PRIVATE STATIC in assembly_valheim.
            // This line only compiles because Krafs.Publicizer publicized the reference.
            var publicizerWorks = FejdStartup.m_instance != null;

            ValheimTomrerPlugin.Log.LogInfo(
                $"main menu reached | harmony=OK | publicizer={(publicizerWorks ? "OK" : "returned null")}");
        }
    }
}
