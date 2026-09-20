using ValheimTomrer.Blueprints;
using HarmonyLib;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// The camera zooms on the mouse wheel in repair mode. In blueprint mode the wheel rotates
    /// the blueprint, so never report repair mode then (only the camera reads this).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.InRepairMode))]
    internal static class PlayerInRepairModePatch
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (BlueprintMode.Active && __instance == Player.m_localPlayer)
            {
                __result = false;
            }
        }
    }
}
