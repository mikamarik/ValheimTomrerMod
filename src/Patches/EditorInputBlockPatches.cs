using HarmonyLib;
using UnityEngine;
using ValheimTomrer.Blueprints.Sites;
using ValheimTomrer.Editor;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// The whole input takeover, on purpose in one file instead of one file per target: after a
    /// game update these nine methods have to be re-checked together, and a missing one shows up
    /// as "the game still reacts while the editor is open".
    ///
    /// Every patch is gated on ModUi.Blocking, which stays true for one extra frame after the
    /// window closes so the closing Esc does not also open the pause menu. Two also look at the
    /// world capture, which runs with the window closed: the wheel and the pause menu's Esc.
    /// Measured against Valheim 1.0.15.
    /// </summary>
    internal static class EditorInputBlockPatches
    {
        /// <summary>Attack, use, hotbar keys, building.</summary>
        [HarmonyPatch(typeof(Player), "TakeInput")]
        private static class PlayerTakeInput
        {
            private static void Postfix(ref bool __result)
            {
                if (ModUi.Blocking)
                {
                    __result = false;
                }
            }
        }

        /// <summary>Walking and looking.</summary>
        [HarmonyPatch(typeof(PlayerController), "TakeInput", new[] { typeof(bool) })]
        private static class PlayerControllerTakeInput
        {
            private static void Postfix(ref bool __result)
            {
                if (ModUi.Blocking)
                {
                    __result = false;
                }
            }
        }

        /// <summary>
        /// The check most of the game already honours: it blocks the pause menu, the map key,
        /// chat, zoom and the crosshair in one go.
        /// </summary>
        [HarmonyPatch(typeof(TextInput), nameof(TextInput.IsVisible))]
        private static class TextInputIsVisible
        {
            private static void Postfix(ref bool __result)
            {
                if (ModUi.Blocking)
                {
                    __result = true;
                }
            }
        }

        /// <summary>Tab. InventoryGui does not look at TextInput.</summary>
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show), new[] { typeof(Container), typeof(int) })]
        private static class InventoryGuiShow
        {
            private static bool Prefix() => !ModUi.Blocking;
        }

        /// <summary>Gamepad D-pad up uses a hotbar item.</summary>
        [HarmonyPatch(typeof(HotkeyBar), "Update")]
        private static class HotkeyBarUpdate
        {
            private static bool Prefix() => !ModUi.Blocking;
        }

        /// <summary>
        /// Esc and the pad's menu button open the pause menu. Also held back on the one frame
        /// the world capture uses Esc to stop, and the one the remove window closes on, or the
        /// same press would pause the game too.
        /// </summary>
        [HarmonyPatch(typeof(Menu), "Update")]
        private static class MenuUpdate
        {
            private static bool Prefix() => !ModUi.Blocking && !WorldCapture.TakesEscape && !SiteRemovePopup.TakesEscape;
        }

        /// <summary>The map key.</summary>
        [HarmonyPatch(typeof(Minimap), "Update")]
        private static class MinimapUpdate
        {
            private static bool Prefix() => !ModUi.Blocking;
        }

        /// <summary>
        /// The cursor is locked again every LateUpdate, so freeing it once does not hold.
        /// This runs after the game decided, and sets what the editor wants: free to point at the
        /// window, held and hidden once the 3D pane has taken the mouse.
        /// </summary>
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
        private static class GameCameraUpdateMouseCapture
        {
            private static void Postfix()
            {
                if (!ModUi.Blocking)
                {
                    return;
                }

                if (ModUi.LockCursor)
                {
                    ZCursor.LockState = CursorLockMode.Locked;
                    ZCursor.Hide();
                    return;
                }

                ZCursor.LockState = CursorLockMode.None;
                ZCursor.Show();
            }
        }

        /// <summary>
        /// Camera zoom. The private method, because the public static wrapper is small enough
        /// to be inlined. Our own scroll views read the wheel through the EventSystem, so they
        /// keep scrolling. Also 0 while the world capture is up: its wheel turns and resizes the
        /// rectangle, and the capture reads the mouse itself.
        /// </summary>
        [HarmonyPatch(typeof(ZInput), "Internal_GetMouseScrollWheel")]
        private static class ZInputMouseScrollWheel
        {
            private static void Postfix(ref float __result)
            {
                if (ModUi.Blocking || WorldCapture.Active)
                {
                    __result = 0f;
                }
            }
        }
    }
}
