using System.IO;
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
    ///
    /// Closing keeps everything: the blueprint with its unsaved changes and its undo, the
    /// selection, what is in hand, the camera, the tabs and filters, the piece menu, a dialog that
    /// was up and the panel walk. The next open comes back to all of it. Only
    /// <see cref="Forget"/> starts over.
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
                WorldCapture.Cancel();
                if (ModUi.Open)
                {
                    Close();
                }

                return;
            }

            EditorInput.Poll();

            if (!ModUi.Open)
            {
                // The window is closed, so the capture can have the world: it aims with the
                // player's own view and takes no input away from the game.
                if (!CanOpen())
                {
                    WorldCapture.Cancel();
                    return;
                }

                if (ZInput.GetKeyDown(EditorConfig.Key.Value))
                {
                    // With a blueprint in hand the key edits that one, not the one left open. The
                    // build tool lets go of it: the Build this button puts it back.
                    var inHand = BlueprintMode.Current;
                    Open(inHand);
                    if (inHand != null && ModUi.Open)
                    {
                        BlueprintMode.Exit();
                        ValheimTomrerPlugin.Log.LogInfo($"editor took '{inHand.Name}' out of the build tool's hand");
                    }

                    return;
                }

                WorldCapture.Tick();
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
                // The one exception, and the pad's only way out of a box: Esc or circle in a
                // dialog hands the keyboard back, so the rest of the dialog can be reached. The
                // press is used up here, or the same one would close the dialog as well.
                if (Dialogs.IsOpen && EditorInput.Cancel)
                {
                    FocusNav.StopTyping();
                }

                return;
            }

            // Esc walks back one step at a time: a dialog, then what is in hand, then the mouse
            // the pane took, then the panel walk, then the selection. Nothing left to step back
            // from and it closes.
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

        /// <summary>True while a blueprint is kept from the last time, so the next open comes back to it.</summary>
        public static bool Kept => !ModUi.Open && EditorState.Document != null;

        /// <summary>
        /// Opens the window as it was left. The first time, or after <see cref="Forget"/>, it starts
        /// on <paramref name="inHand"/>, else on the first kit it can build.
        ///
        /// A blueprint in the build tool's hand that is not the one left open takes its place, and
        /// the window asks first when the one left open has unsaved changes.
        /// </summary>
        public static void Open(ResolvedBlueprint inHand = null)
        {
            if (ModUi.Open)
            {
                return;
            }

            if (EditorState.Document == null)
            {
                var blueprint = inHand ?? FirstKit();
                var document = blueprint != null
                    ? BlueprintDocument.FromBlueprint(blueprint.Blueprint)
                    : BlueprintDocument.New();
                Begin(document, blueprint);
                return;
            }

            Begin(null, null);
            if (ModUi.Open && inHand != null && !Holds(EditorState.Document, inHand.Blueprint))
            {
                EditorCommands.Take(BlueprintDocument.FromBlueprint(inHand.Blueprint), inHand);
            }
        }

        /// <summary>
        /// Opens the window on a document that is already read, whatever is in it. The pane builds
        /// the pieces itself, so a blueprint holding a piece this game does not have still opens.
        /// A blueprint left open with unsaved changes is asked about first.
        /// </summary>
        public static void OpenDocument(BlueprintDocument document)
        {
            if (ModUi.Open || document == null)
            {
                return;
            }

            if (EditorState.Document == null)
            {
                Begin(document, null);
                return;
            }

            Begin(null, null);
            if (ModUi.Open)
            {
                EditorCommands.Take(document, null);
            }
        }

        /// <summary>
        /// Swaps the open blueprint for another one without closing the window. New and Open both
        /// come through here: the pane starts empty and fills itself from the document. With
        /// <paramref name="blueprint"/> it is built a few pieces a frame instead.
        /// </summary>
        public static void Replace(BlueprintDocument document, ResolvedBlueprint blueprint = null)
        {
            if (!ModUi.Open || document == null)
            {
                return;
            }

            EditorState.Open(document);
            PiecePicker.Close();
            ViewportHost.Show(blueprint);
            Palette.Selected = null;
            PieceListPanel.Show(document);
            BlueprintPanel.Show(document);
            SelectionPanel.Show();
            ChecksPanel.Show(document);
            _syncedSelection = -1;
            ValheimTomrerPlugin.Log.LogInfo($"editor now on '{document.Name}' ({document.Pieces.Count} pieces)");
        }

        /// <summary>Shows the window. A null document comes back to everything that was kept.</summary>
        private static void Begin(BlueprintDocument document, ResolvedBlueprint blueprint)
        {
            // The window is about to cover the world, so a half-picked capture box goes.
            WorldCapture.Cancel();
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
            ViewportHost.Ensure(EditorWindow.ViewportHost);
            if (document != null)
            {
                EditorState.Open(document);
                ViewportHost.Show(blueprint);
            }
            else
            {
                EditorState.Rebind();
                ViewportHost.Wake();
            }

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
            FocusNav.Ensure(EditorWindow.Root);
            _syncedSelection = -1;
            ModUi.Open = true;
            EditorInput.Reset();
            if (document == null)
            {
                FocusNav.Resume();
            }

            ValheimTomrerPlugin.Log.LogInfo(document != null
                ? "editor opened"
                : $"editor opened again on '{Document.Name}' ({Document.Pieces.Count} pieces"
                    + (Document.Dirty ? ", unsaved changes" : "") + ")");
        }

        /// <summary>
        /// Hides the window and gives the game its input back. Everything the editor holds stays
        /// for the next open, see the class comment.
        /// </summary>
        public static void Close()
        {
            if (!ModUi.Open)
            {
                return;
            }

            // A box that has the keyboard lets go of it. The walk itself stays where it is.
            FocusNav.StopTyping();
            ViewportHost.Sleep();
            PiecePicker.PieceChosen = null;
            Palette.PieceChosen = null;
            Palette.Selected = null;
            Palette.Close();
            PieceListPanel.PieceClicked = null;
            PieceListPanel.Close();
            BlueprintPanel.Close();
            SelectionPanel.Close();
            ChecksPanel.Close();
            Toasts.Clear();
            EditorCommands.Reset();
            Bindings.Reset();
            PadBindings.Reset();
            EditorWindow.Show(false);
            ModUi.MarkClosed();
            UITooltip.HideTooltip();
            PlayerController.SetTakeInputDelay(0.2f);
            ZInput.ResetAllButtonStates();
            EditorInput.Reset();
            ValheimTomrerPlugin.Log.LogInfo("editor closed, its state kept");
        }

        /// <summary>
        /// Closes and drops everything that was kept: the blueprint, the pane, a dialog, the piece
        /// menu and the walk. The next open starts on the blueprint in hand or the first kit. The
        /// autotest calls it between scenarios.
        /// </summary>
        public static void Forget()
        {
            Close();
            Dialogs.Close();
            PiecePicker.Close();
            FocusNav.Leave();
            ViewportHost.Close();
            EditorState.Close();
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
            Forget();
            WorldCapture.Shutdown();
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

        /// <summary>True when a blueprint is the one the document came from: the same file, or the same kit.</summary>
        private static bool Holds(BlueprintDocument document, Blueprint blueprint)
        {
            if (string.IsNullOrEmpty(document.SourcePath) || string.IsNullOrEmpty(blueprint.SourcePath))
            {
                // Kits have no file. A new blueprint has none either, but it is not read only.
                return string.IsNullOrEmpty(document.SourcePath) && string.IsNullOrEmpty(blueprint.SourcePath)
                    && document.ReadOnly && blueprint.ReadOnly && document.Name == blueprint.Name;
            }

            return string.Equals(
                Path.GetFullPath(document.SourcePath), Path.GetFullPath(blueprint.SourcePath), System.StringComparison.Ordinal);
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
