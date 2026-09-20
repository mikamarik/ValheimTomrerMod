using UnityEngine;
using ValheimTomrer.Editor.Input;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Editor
{
    /// <summary>
    /// Opens and closes the editor, and owns the one-per-frame tick. The key is read in the
    /// plugin's Update, not in a build patch, so the editor opens without a hammer in hand.
    ///
    /// Close follows the vanilla pattern (StoreGui.Update): hide, let the one-frame grace run
    /// out, then hand input back with a short delay and reset the buttons, so the key that
    /// closed the window is not used again by the game in the same breath.
    /// </summary>
    internal static class EditorSession
    {
        public static bool IsOpen => ModUi.Open;

        public static void Tick()
        {
            if (!ValheimTomrerPlugin.ModEnabled.Value)
            {
                if (ModUi.Open)
                {
                    Close();
                }

                return;
            }

            EditorInput.Poll();

            if (!ModUi.Open)
            {
                if (CanOpen() && ZInput.GetKeyDown(EditorConfig.Key.Value))
                {
                    Open();
                }

                return;
            }

            var player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.InCutscene() || player.IsTeleporting())
            {
                Close();
                return;
            }

            // The console, chat and popups handle their own Esc. Let them have it first.
            if (Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || UnifiedPopup.IsVisible())
            {
                return;
            }

            if (ZInput.GetKeyDown(EditorConfig.Key.Value) || EditorInput.Cancel)
            {
                Close();
            }
        }

        public static void Open()
        {
            if (ModUi.Open)
            {
                return;
            }

            if (!EditorWindow.Ensure())
            {
                ValheimTomrerPlugin.Log.LogWarning("editor could not open: the HUD is not ready yet.");
                return;
            }

            Hud.HidePieceSelection();
            if (InventoryGui.IsVisible())
            {
                InventoryGui.instance.Hide();
            }

            if (StoreGui.IsVisible())
            {
                StoreGui.instance.Hide();
            }

            var map = Minimap.instance;
            if (map != null && map.m_mode == Minimap.MapMode.Large)
            {
                map.SetMapMode(Minimap.MapMode.Small);
            }

            UITooltip.HideTooltip();
            EditorWindow.Show(true);
            ModUi.Open = true;
            EditorInput.Reset();
            ValheimTomrerPlugin.Log.LogInfo("editor opened");
        }

        public static void Close()
        {
            if (!ModUi.Open)
            {
                return;
            }

            EditorWindow.Show(false);
            ModUi.MarkClosed();
            UITooltip.HideTooltip();
            PlayerController.SetTakeInputDelay(0.2f);
            ZInput.ResetAllButtonStates();
            EditorInput.Reset();
            ValheimTomrerPlugin.Log.LogInfo("editor closed");
        }

        /// <summary>Plugin OnDestroy: drop the canvas and the cached sprites for a hot reload.</summary>
        public static void Shutdown()
        {
            Close();
            EditorWindow.Destroy();
            UiTheme.Clear();
        }

        private static bool CanOpen()
        {
            var player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.InCutscene() || player.IsTeleporting())
            {
                return false;
            }

            if (Hud.instance == null)
            {
                return false;
            }

            if (Menu.IsVisible() || Console.IsVisible() || TextInput.IsVisible() || UnifiedPopup.IsVisible())
            {
                return false;
            }

            return Chat.instance == null || !Chat.instance.HasFocus();
        }
    }
}
