using ValheimTomrer.Blueprints;
using HarmonyLib;

namespace ValheimTomrer.Patches
{
    /// <summary>While a blueprint is active, show the blueprint preview instead of the single-piece one.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
    internal static class PlayerUpdatePlacementGhostPatch
    {
        private static bool Prefix(Player __instance)
        {
            if (!BlueprintMode.Active || __instance != Player.m_localPlayer)
            {
                return true;
            }

            BlueprintMode.UpdatePreview(__instance);
            return false;
        }
    }
}
