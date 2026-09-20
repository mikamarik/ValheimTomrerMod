using ValheimTomrer.Blueprints;
using HarmonyLib;
using UnityEngine;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Picking a piece (build menu, gamepad grid, or copying a piece) ends blueprint mode.
    /// The Piece overload calls this one, so one patch covers every way of picking.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetSelectedPiece), typeof(Vector2Int))]
    internal static class PlayerSetSelectedPiecePatch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer)
            {
                BlueprintMode.Exit();
            }
        }
    }
}
