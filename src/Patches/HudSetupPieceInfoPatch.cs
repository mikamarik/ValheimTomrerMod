using ValheimTomrer.Blueprints;
using HarmonyLib;

namespace ValheimTomrer.Patches
{
    /// <summary>Shows the active blueprint in the vanilla build info card instead of the selected piece.</summary>
    [HarmonyPatch(typeof(Hud), nameof(Hud.SetupPieceInfo))]
    internal static class HudSetupPieceInfoPatch
    {
        private static void Postfix(Hud __instance)
        {
            if (!BlueprintMode.Active || Hud.IsPieceSelectionVisible() || Player.m_localPlayer == null)
            {
                return;
            }

            BlueprintInfoCard.Show(__instance, Player.m_localPlayer, BlueprintMode.Current);
        }
    }
}
