using HarmonyLib;
using ValheimTomrer.Editor.Catalog;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// The game rebuilt the hammer's unlocked list, so the palette's copy is out of date. Only a
    /// flag is set here: the catalog reads the list again on the next editor tick, not inside the
    /// game's own call.
    /// </summary>
    [HarmonyPatch(typeof(Player), "UpdateAvailablePiecesList")]
    internal static class PlayerUpdateAvailablePiecesListPatch
    {
        private static void Postfix(Player __instance)
        {
            if (!ValheimTomrerPlugin.ModEnabled.Value || __instance != Player.m_localPlayer)
            {
                return;
            }

            PieceCatalog.Invalidate();
        }
    }
}
