using UnityEngine;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;
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

        /// <summary>The blueprint being edited. The editing state owns it.</summary>
        public static BlueprintDocument Document => EditorState.Document;

        private static int _syncedSelection = -1;

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
                    // With a blueprint in hand the key edits that one, not the last kit. The
                    // build tool lets go of it: the Build this button puts it back.
                    var inHand = BlueprintMode.Current;
                    Open(inHand);
                    if (inHand != null && ModUi.Open)
                    {
                        BlueprintMode.Exit();
                        ValheimTomrerPlugin.Log.LogInfo($"editor took '{inHand.Name}' out of the build tool's hand");
                    }
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

            ViewportHost.Tick();
            Palette.Tick();
            PiecePicker.Tick();
            PieceListPanel.Tick();
            BlueprintPanel.Tick();
            SelectionPanel.Tick();
            ChecksPanel.Tick();
            TopBar.Tick();
            Dialogs.Tick();
            Toasts.Tick();
            SyncSelection();

            // A text box has the keyboard: Esc puts the old text back, it does not close the window.
            if (ModUi.Typing)
            {
                return;
            }

            // Esc walks back one step at a time: a dialog, then what is in hand, then the free
            // camera, then the selection. Nothing left to step back from and the window closes.
            var cancel = EditorInput.Cancel;
            if (cancel && Bindings.Cancel())
            {
                return;
            }

            if (ZInput.GetKeyDown(EditorConfig.Key.Value) || cancel)
            {
                Close();
            }
        }

        /// <summary>Opens the window. Without a blueprint it shows the first kit it can build.</summary>
        public static void Open(ResolvedBlueprint blueprint = null)
        {
            if (ModUi.Open)
            {
                return;
            }

            blueprint = blueprint ?? FirstKit();
            var document = blueprint != null
                ? BlueprintDocument.FromBlueprint(blueprint.Blueprint)
                : BlueprintDocument.New();
            Begin(document, blueprint);
        }

        /// <summary>
        /// Opens the window on a document that is already read, whatever is in it. The pane builds
        /// the pieces itself, so a blueprint holding a piece this game does not have still opens.
        /// </summary>
        public static void OpenDocument(BlueprintDocument document)
        {
            if (ModUi.Open || document == null)
            {
                return;
            }

            Begin(document, null);
        }

        /// <summary>
        /// Swaps the open blueprint for another one without closing the window. New and Open both
        /// come through here: the pane starts empty and fills itself from the document.
        /// </summary>
        public static void Replace(BlueprintDocument document)
        {
            if (!ModUi.Open || document == null)
            {
                return;
            }

            EditorState.Open(document);
            PiecePicker.Close();
            ViewportHost.Show(null);
            Palette.Selected = null;
            PieceListPanel.Show(document);
            BlueprintPanel.Show(document);
            SelectionPanel.Show();
            ChecksPanel.Show(document);
            _syncedSelection = -1;
            ValheimTomrerPlugin.Log.LogInfo($"editor now on '{document.Name}' ({document.Pieces.Count} pieces)");
        }

        private static void Begin(BlueprintDocument document, ResolvedBlueprint blueprint)
        {
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
            PieceCatalog.Ensure();
            EditorState.Open(document);
            ViewportHost.Ensure(EditorWindow.ViewportHost);
            ViewportHost.Show(blueprint);

            Palette.Ensure(EditorWindow.PalettePane);
            Palette.Show();
            Palette.PieceChosen = StartAdd;
            PiecePicker.Ensure(EditorWindow.Root);
            PiecePicker.PieceChosen = StartAdd;
            PieceListPanel.Ensure(EditorWindow.PieceListPane);
            PieceListPanel.Show(Document);
            PieceListPanel.PieceClicked = RowClicked;

            BlueprintPanel.Ensure(EditorWindow.BlueprintPane);
            BlueprintPanel.Show(Document);
            SelectionPanel.Ensure(EditorWindow.SelectionPane);
            SelectionPanel.Show();
            ChecksPanel.Ensure(EditorWindow.ChecksPane);
            ChecksPanel.Show(Document);
            TopBar.Ensure(EditorWindow.TopBar);
            Dialogs.Ensure(EditorWindow.Root);
            Toasts.Ensure(EditorWindow.Root);
            _syncedSelection = -1;
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

            ViewportHost.Close();
            PiecePicker.Close();
            PiecePicker.PieceChosen = null;
            Palette.PieceChosen = null;
            Palette.Selected = null;
            Palette.Close();
            PieceListPanel.PieceClicked = null;
            PieceListPanel.Close();
            BlueprintPanel.Close();
            SelectionPanel.Close();
            ChecksPanel.Close();
            Dialogs.Close();
            Toasts.Clear();
            EditorCommands.Reset();
            Bindings.Reset();
            PadBindings.Reset();
            EditorState.Close();
            EditorWindow.Show(false);
            ModUi.MarkClosed();
            UITooltip.HideTooltip();
            PlayerController.SetTakeInputDelay(0.2f);
            ZInput.ResetAllButtonStates();
            EditorInput.Reset();
            ValheimTomrerPlugin.Log.LogInfo("editor closed");
        }

        /// <summary>A tile in the palette or the pad's piece menu: that piece goes in hand.</summary>
        public static void StartAdd(PieceEntry entry)
        {
            if (EditorState.StartAdd(entry))
            {
                Palette.Selected = entry;
            }
        }

        /// <summary>A row in the blueprint's piece list: shift toggles, a plain click replaces.</summary>
        private static void RowClicked(int id, bool additive)
        {
            EditorState.Select(id, additive ? SelectHow.Toggle : SelectHow.Set);
        }

        /// <summary>Pushes the selection into the piece list, and the piece in hand into the palette.</summary>
        private static void SyncSelection()
        {
            if (_syncedSelection == EditorState.Version)
            {
                return;
            }

            _syncedSelection = EditorState.Version;
            PieceListPanel.SetSelection(EditorState.Selection);
            Palette.Selected = EditorState.Mode == EditMode.Place ? EditorState.Held : null;
        }

        /// <summary>Plugin OnDestroy: drop the canvas and the cached sprites for a hot reload.</summary>
        public static void Shutdown()
        {
            Close();
            EditorWindow.Destroy();
            PieceCatalog.Clear();
            UiTheme.Clear();
        }

        /// <summary>The first kit whose pieces all exist in this game. Phase 3 lets the player pick.</summary>
        private static ResolvedBlueprint FirstKit()
        {
            foreach (var blueprint in BlueprintLibrary.All)
            {
                if (ResolvedBlueprint.TryResolve(blueprint, out var resolved, out var error))
                {
                    return resolved;
                }

                ValheimTomrerPlugin.Log.LogWarning(error);
            }

            return null;
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
