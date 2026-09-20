using System.IO;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Editor
{
    /// <summary>
    /// The verbs behind the top bar, the dialogs and the shortcuts: new, open, save, save as,
    /// build it in the world, centre the origin and the two view switches. Every one of them
    /// reports what happened through a toast, so a button, a key and the test all take the same path.
    /// </summary>
    internal static class EditorCommands
    {
        /// <summary>A busy chip shows for at least this long, or it would flash by unread.</summary>
        private const float MinBusySeconds = 0.35f;

        private static bool _working;
        private static float _busyUntil;
        private static bool _discardOk;

        /// <summary>What the editor is doing right now, or null.</summary>
        public static string Busy { get; private set; }

        /// <summary>Errors in the open blueprint, from the panel that already worked them out.</summary>
        public static int Errors
        {
            get
            {
                var errors = 0;
                var rows = ChecksPanel.Rows;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Level == CheckLevel.Error)
                    {
                        errors++;
                    }
                }

                return errors;
            }
        }

        /// <summary>Starts an empty blueprint, asking first when the open one has changes.</summary>
        public static void NewBlueprint()
        {
            if (AskFirst("Start a new blueprint?", NewBlueprint))
            {
                return;
            }

            EditorSession.Replace(DocumentStore.New());
            Toasts.Info("New blueprint.");
        }

        /// <summary>Shows the list of kits and files.</summary>
        public static void OpenDialog()
        {
            Dialogs.Open();
        }

        /// <summary>Opens one row of that list, asking first when the open blueprint has changes.</summary>
        public static void OpenEntry(BlueprintEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (AskFirst($"Open {entry.Name}?", () => OpenEntry(entry)))
            {
                return;
            }

            Begin("Opening");
            BlueprintDocument document;
            string error;
            var ok = entry.IsKit
                ? DocumentStore.OpenKit(entry, out document, out error)
                : DocumentStore.Open(entry.Path, out document, out error);
            Done();
            if (!ok)
            {
                Toasts.Error(error);
                return;
            }

            Dialogs.Close();
            EditorSession.Replace(document);
            Toasts.Ok($"Opened {document.Name}, {document.Pieces.Count} pieces.");
        }

        /// <summary>
        /// Writes the blueprint back over its own file. A kit, a read-only file or a blueprint
        /// that was never saved goes to Save as instead.
        /// </summary>
        public static bool Save()
        {
            var document = EditorState.Document;
            if (document == null)
            {
                return false;
            }

            if (document.ReadOnly || string.IsNullOrEmpty(document.SourcePath))
            {
                Dialogs.SaveAs(document.Name);
                return false;
            }

            Begin("Saving");
            var ok = DocumentStore.Save(document, out var error);
            Done();
            if (!ok)
            {
                Toasts.Error(error);
                return false;
            }

            Toasts.Ok($"Saved {Path.GetFileName(document.SourcePath)}.");
            return true;
        }

        /// <summary>Writes the blueprint under a name of its own, in the player's folder.</summary>
        public static bool SaveAs(string name, bool overwrite)
        {
            var document = EditorState.Document;
            if (document == null)
            {
                return false;
            }

            Begin("Saving");
            var ok = DocumentStore.SaveAs(document, name, overwrite, out var error);
            Done();
            if (!ok)
            {
                Toasts.Error(error);
                return false;
            }

            Dialogs.Close();
            Toasts.Ok($"Saved {Path.GetFileName(document.SourcePath)}.");
            return true;
        }

        /// <summary>
        /// Hands the open blueprint to the build tool: saves it when it has changes, closes the
        /// window, and leaves the vanilla preview in hand, ready for a click. It equips nothing,
        /// so the build tool has to be out already.
        /// </summary>
        public static bool BuildThis()
        {
            var document = EditorState.Document;
            if (document == null)
            {
                return false;
            }

            if (document.Pieces.Count == 0)
            {
                Toasts.Error("A blueprint needs at least one piece.");
                return false;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                Toasts.Error("No player to build with.");
                return false;
            }

            // No hammer out means no build tool to put the preview in. Say so, do not equip one.
            if (!player.InPlaceMode())
            {
                Toasts.Error("Take the hammer out first, then press Build this again.");
                return false;
            }

            // Save first, so what stands in the world is what the file holds. A blueprint with no
            // file of its own opens the Save as dialog instead, and the next press goes through.
            if (document.Dirty && !Save())
            {
                return false;
            }

            if (!ResolvedBlueprint.TryResolve(document.ToBlueprint(), out var resolved, out var error))
            {
                Toasts.Error(error);
                return false;
            }

            var locked = BlueprintRules.UnavailablePieces(player, resolved);
            if (locked.Count > 0)
            {
                Toasts.Error("Not unlocked yet: "
                    + string.Join(", ", locked.Select(p => Localization.instance.Localize(p.m_name))));
                return false;
            }

            EditorSession.Close();
            BlueprintMode.Select(player, resolved);
            player.Message(MessageHud.MessageType.Center, $"{resolved.Name}: click to build");
            ValheimTomrerPlugin.Log.LogInfo(
                $"editor handed '{resolved.Name}' ({resolved.Parts.Count} pieces) to the build tool");
            return true;
        }

        /// <summary>Moves the origin to the bottom centre of the blueprint.</summary>
        public static void CenterOrigin()
        {
            var document = EditorState.Document;
            if (document == null)
            {
                return;
            }

            if (document.HasSections)
            {
                Toasts.Error("This file has snap point or terrain sections that would have to move too.");
                return;
            }

            if (!EditorState.CenterOrigin())
            {
                Toasts.Info("The origin is already at the bottom centre.");
                return;
            }

            Toasts.Ok("The origin is now the bottom centre.");
        }

        /// <summary>Draws the pieces as boxes instead of models, which is faster on a big blueprint.</summary>
        public static void ToggleBoxes()
        {
            EditorState.PieceBoxesOn = !EditorState.PieceBoxesOn;
            Toasts.Info(EditorState.PieceBoxesOn ? "Pieces as boxes." : "Pieces as models.");
        }

        /// <summary>Snapping stays on either way: this only shows where the snap points are.</summary>
        public static void ToggleSnapDots()
        {
            EditorState.SnapDotsOn = !EditorState.SnapDotsOn;
            Toasts.Info(EditorState.SnapDotsOn ? "Snap dots on." : "Snap dots off.");
        }

        public static void Help()
        {
            Dialogs.Help();
        }

        public static void Settings()
        {
            Dialogs.Settings();
        }

        /// <summary>Show all pieces, or only the ones this character has unlocked.</summary>
        public static void ToggleShowAllPieces()
        {
            if (EditorConfig.ShowAllPieces == null)
            {
                return;
            }

            EditorConfig.ShowAllPieces.Value = !EditorConfig.ShowAllPieces.Value;
            PieceCatalog.Invalidate();
        }

        /// <summary>Lets the busy chip fade after its shortest showing.</summary>
        public static void Tick()
        {
            if (!_working && Busy != null && Time.unscaledTime >= _busyUntil)
            {
                Busy = null;
            }
        }

        /// <summary>Forgets a half-answered question when the editor closes.</summary>
        public static void Reset()
        {
            _working = false;
            _discardOk = false;
            Busy = null;
        }

        /// <summary>
        /// True when the question was asked instead of doing the thing. Answering it runs the
        /// same command again, and the second run goes straight through.
        /// </summary>
        private static bool AskFirst(string title, System.Action again)
        {
            if (_discardOk)
            {
                _discardOk = false;
                return false;
            }

            var document = EditorState.Document;
            if (document == null || !document.Dirty)
            {
                return false;
            }

            var name = string.IsNullOrEmpty(document.Name) ? "This blueprint" : document.Name;
            Dialogs.Confirm(
                title,
                $"{name} has changes that are not saved. They will be lost.",
                "Discard",
                () =>
                {
                    _discardOk = true;
                    again();
                    _discardOk = false;
                });
            return true;
        }

        private static void Begin(string what)
        {
            _working = true;
            Busy = what;
            _busyUntil = Time.unscaledTime + MinBusySeconds;
        }

        private static void Done()
        {
            _working = false;
        }
    }
}
