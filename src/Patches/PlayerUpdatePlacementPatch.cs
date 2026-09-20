using ValheimTomrer.Blueprints;
using HarmonyLib;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Build-mode input. Listens for the blueprint key, and while a blueprint is active it
    /// takes over the click and the mouse wheel. The build menu keeps working.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
    internal static class PlayerUpdatePlacementPatch
    {
        private static bool Prefix(Player __instance, bool takeInput)
        {
            if (__instance != Player.m_localPlayer)
            {
                return true;
            }

            if (!ValheimTomrerPlugin.ModEnabled.Value || !__instance.InPlaceMode() || __instance.IsDead())
            {
                BlueprintMode.Exit();
                return true;
            }

            if (takeInput && !Hud.IsPieceSelectionVisible()
                && ZInput.GetKeyDown(ValheimTomrerPlugin.BlueprintKey.Value, false))
            {
                BlueprintMode.Cycle(__instance);
            }

            if (!BlueprintMode.Active)
            {
                return true;
            }

            // Vanilla sets this every frame; left as is, the HUD keeps showing an old piece's health.
            __instance.m_hoveringPiece = null;

            if (!takeInput)
            {
                return false;
            }

            // Right click still opens the build menu; picking a piece there ends blueprint mode.
            __instance.UpdateBuildGuiInput();
            if (!Hud.IsPieceSelectionVisible())
            {
                BlueprintMode.HandleInput(__instance);
            }

            return false;
        }
    }
}
