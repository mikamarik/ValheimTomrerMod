using ValheimTomrer.Blueprints;
using HarmonyLib;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Shows the active blueprint in the vanilla build card instead of the selected piece, with its
    /// materials list. Anything else on the card (a normal piece, the build menu's hovered piece)
    /// hides the list and keeps the game's own slots.
    /// </summary>
    [HarmonyPatch(typeof(Hud), nameof(Hud.SetupPieceInfo))]
    internal static class HudSetupPieceInfoPatch
    {
        private static void Postfix(Hud __instance)
        {
            if (!ValheimTomrerPlugin.ModEnabled.Value || !BlueprintMode.Active || Hud.IsPieceSelectionVisible()
                || Player.m_localPlayer == null)
            {
                BlueprintInfoCard.Hide();
                return;
            }

            BlueprintInfoCard.Show(__instance, Player.m_localPlayer, BlueprintMode.Current);
        }
    }
}
