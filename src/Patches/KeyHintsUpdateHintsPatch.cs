using HarmonyLib;
using ValheimTomrer.Blueprints;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// The game's hint row along the bottom of the screen. While blueprint mode, Continue or the
    /// capture is up, their own set shows in its place (<see cref="HintRow"/>) and the game's update
    /// is skipped, so its groups stay off. With none of them up, the game runs as always.
    /// </summary>
    [HarmonyPatch(typeof(KeyHints), nameof(KeyHints.UpdateHints))]
    internal static class KeyHintsUpdateHintsPatch
    {
        private static bool Prefix(KeyHints __instance)
        {
            return !HintRow.TakeOver(__instance);
        }
    }
}
