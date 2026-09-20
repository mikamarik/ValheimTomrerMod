using ValheimTomrer.Blueprints;
using HarmonyLib;

namespace ValheimTomrer.Patches
{
    /// <summary>Blueprints always rotate, so the camera must not zoom on the wheel (only the camera reads this).</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.CanRotatePiece))]
    internal static class PlayerCanRotatePiecePatch
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (BlueprintMode.Active && __instance == Player.m_localPlayer)
            {
                __result = true;
            }
        }
    }
}
