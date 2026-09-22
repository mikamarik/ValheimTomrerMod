using HarmonyLib;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// The game fires "input layout changed" every time the player goes from the keyboard or mouse
    /// to the pad and back, and the build menu, alive behind the editor, answers by clearing the
    /// UI's selection. In the editor that selection is a text box that is typing: it lost the
    /// keyboard on the first pad press, and the next circle closed the whole dialog. While the
    /// editor window is up the selection is left alone; the menu's favourites list still closes.
    /// </summary>
    [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OnLayoutChanged))]
    internal static class BuildUiOnLayoutChangedPatch
    {
        private static bool Prefix(BuildUi __instance)
        {
            if (!ModUi.Open)
            {
                return true;
            }

            if (__instance.m_favoritesDropdown != null)
            {
                __instance.m_favoritesDropdown.Close();
            }

            return false;
        }
    }
}
