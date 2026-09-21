using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Editor.Input
{
    /// <summary>
    /// Everything the controller does, in the order the Tomrer editor does it (view/Editor.ts,
    /// onPad and padPicker). The buttons follow the game's building layout where the game has one:
    /// R2 places, L2 and the right stick turn, L1 is "no snapping", L3 and R3 walk the snap point,
    /// R1 removes, cross opens the piece menu.
    ///
    /// The order matters: a dialog eats everything, then the piece menu, then the editor.
    /// <see cref="Tick"/> is the only dispatcher, so the autotest can drive every row of the help
    /// table through a made-up pad (<see cref="PadReader.Fake"/>) with no controller at all.
    ///
    /// Circle is not read here. It goes through the one Esc ladder in <see cref="Bindings.Cancel"/>,
    /// which already steps back the menu, the dialog, what is in hand, the mouse the pane took and
    /// the selection, so the pad and Esc can never do two different things.
    /// </summary>
    internal static class PadBindings
    {
        /// <summary>Holding the turn: the game's Player.UpdatePlacement numbers.</summary>
        public const float TurnDelay = 0.25f;

        public const float TurnEvery = 0.08f;

        /// <summary>Holding a direction in the piece menu: the game's menu numbers.</summary>
        public const float NavDelay = 0.3f;

        public const float NavEvery = 0.1f;

        /// <summary>How far the right stick has to go before it turns the piece instead of the camera.</summary>
        private const float TurnStick = 0.4f;

        /// <summary>How far a stick has to go to count as a direction in the piece menu.</summary>
        private const float NavStick = 0.5f;

        private static readonly Repeater Turn = new Repeater(TurnDelay, TurnEvery);
        private static readonly Repeater NavX = new Repeater(NavDelay, NavEvery);
        private static readonly Repeater NavY = new Repeater(NavDelay, NavEvery);

        /// <summary>The controller, once a frame. Called from the pane's tick, after the keys.</summary>
        public static void Tick()
        {
            var pad = EditorInput.Pad;
            var dt = Mathf.Min(EditorInput.Dt, 0.1f);
            if (pad == null)
            {
                Reset();
                return;
            }

            // Read before the wake below clears it: a button that was selected when the press
            // happened gets that press from the game's own UI, so cross has to stand back.
            var selected = ModUi.HasSelection;

            // 1. The pad woke: the crosshair aims, the hints switch to its own button names, and
            //    whatever the UI had selected lets go, so the next cross is the editor's.
            if (pad.Woke && !ModUi.Typing)
            {
                ViewportHost.TakeAim();
            }

            if (ModUi.Typing)
            {
                return;
            }

            // A held direction repeats only where it is used; elsewhere it starts fresh.
            if (Dialogs.IsOpen || PiecePicker.IsOpen || FocusNav.Active)
            {
                Turn.Step(0, dt);
            }

            if (!PiecePicker.IsOpen && !FocusNav.Active)
            {
                NavX.Step(0, dt);
                NavY.Step(0, dt);
            }

            // 2. A dialog is up: the walk through it (the D-pad moves, cross presses), Options
            //    (it closes the help) and circle (the Esc ladder). Nothing else.
            if (Dialogs.IsOpen)
            {
                if (pad.Pressed(PadButton.Options) && Dialogs.Kind == "help")
                {
                    Dialogs.Close();
                    return;
                }

                if (!FocusNav.InDialog)
                {
                    FocusNav.EnterDialog();
                }

                Focus(pad, dt);
                return;
            }

            // 3. Options opens the help.
            if (pad.Pressed(PadButton.Options))
            {
                Dialogs.Help();
                return;
            }

            // 4. The piece menu has the pad while it is up.
            if (PiecePicker.IsOpen)
            {
                Picker(pad, dt);
                return;
            }

            // 5. The panel walk has the pad while it is on. Circle is not read here: it falls
            //    through to the Esc ladder, which leaves the walk.
            if (FocusNav.Active)
            {
                Focus(pad, dt);
                return;
            }

            // 6. L1 held: no snapping, like Shift on the keyboard. The keys put it back.
            var alt = pad.Held(PadButton.L1);
            if (alt)
            {
                EditorState.Snapping = false;
            }

            var placing = EditorState.Mode == EditMode.Place;
            var moving = placing && EditorState.Action == PlaceAction.Move;
            var l2 = pad.Held(PadButton.L2);

            // 7. L2 and the right stick turn 22.5 degrees; on its own the stick turns the camera.
            var turn = l2 && Mathf.Abs(pad.Rs.x) > TurnStick ? (pad.Rs.x < 0f ? 1 : -1) : 0;
            if (Turn.Step(turn, dt))
            {
                if (placing)
                {
                    EditorState.SetPlaceSteps(EditorState.Steps + turn);
                }
                else
                {
                    EditorState.RotateSelection(turn > 0 ? 1 : -1);
                }
            }

            var camera = ViewportHost.Camera;
            if (!l2 && camera != null)
            {
                camera.TurnPad(pad.Rs, dt);
            }

            // 8. R2: copy the aimed kind, drop what is in hand, or take the aimed piece.
            if (pad.Pressed(PadButton.R2))
            {
                if (l2)
                {
                    CopyAimed();
                }
                else if (placing)
                {
                    EditorState.CommitPlacement(EditorState.Aimed);
                }
                else
                {
                    SelectAimed(alt);
                }
            }

            // 9. Cross opens the piece menu. Not while moving: those pieces are out of the
            //    blueprint until they are dropped. Not while a button is selected either, or the
            //    UI would press that button with the same press.
            if (pad.Pressed(PadButton.Cross) && !moving && !selected)
            {
                PiecePicker.Open();
            }

            // 10. Circle: the Esc ladder, in EditorSession.

            // 11. Square moves what the crosshair is on, triangle copies it, on their own.
            if (pad.Pressed(PadButton.Square) && !placing)
            {
                MoveAimed();
            }

            if (pad.Pressed(PadButton.Triangle) && !placing)
            {
                CloneAimed();
            }

            // 12. R1 removes it.
            if (pad.Pressed(PadButton.R1) && !moving)
            {
                DeleteAimed();
            }

            // 13. L3 and R3: the snap point while placing, else R3 frames the selection.
            var back = pad.Pressed(PadButton.L3);
            var next = pad.Pressed(PadButton.R3);
            if (back || next)
            {
                if (placing)
                {
                    EditorState.SetManualSnap(EditorState.Manual + (back ? -1 : 1));
                }
                else if (next)
                {
                    ViewportHost.Frame();
                }
                else
                {
                    // L3 with an empty hand opens the panel walk.
                    FocusNav.Enter();
                }
            }

            // 14. The D-pad left and right: undo and redo.
            if (pad.Pressed(PadButton.Left))
            {
                EditorState.Undo();
            }

            if (pad.Pressed(PadButton.Right))
            {
                EditorState.Redo();
            }

            // 15. The left stick flies, L1 three times faster, the D-pad up and down.
            Fly(pad, dt);
        }

        /// <summary>Forget the held repeats, so a button held while the window opened does nothing.</summary>
        public static void Reset()
        {
            Turn.Reset();
            NavX.Reset();
            NavY.Reset();
        }

        /// <summary>
        /// In the panel walk: the D-pad and the left stick step up, down, left and right by what
        /// is on screen, L1 and R1 change panel, cross presses. Everything else does nothing, the
        /// way the piece menu holds the pad.
        /// </summary>
        private static void Focus(PadFrame pad, float dt)
        {
            // The stick counts on the axis it is pushed along most, so a slanted push is one step.
            var ls = pad.Ls;
            var sideways = Mathf.Abs(ls.x) >= Mathf.Abs(ls.y);
            var right = Dir(pad.Held(PadButton.Left) || (sideways && ls.x < -NavStick),
                pad.Held(PadButton.Right) || (sideways && ls.x > NavStick));
            var up = Dir(pad.Held(PadButton.Down) || (!sideways && ls.y < -NavStick),
                pad.Held(PadButton.Up) || (!sideways && ls.y > NavStick));
            if (NavY.Step(up, dt))
            {
                FocusNav.Step(0, up);
            }

            if (NavX.Step(right, dt))
            {
                FocusNav.Step(right, 0);
            }

            if (pad.Pressed(PadButton.L1))
            {
                FocusNav.NextRegion(-1);
            }

            if (pad.Pressed(PadButton.R1))
            {
                FocusNav.NextRegion(1);
            }

            if (pad.Pressed(PadButton.Cross))
            {
                FocusNav.Press();
            }
        }

        /// <summary>In the piece menu: the D-pad or the left stick moves, L1 and R1 change the tab.</summary>
        private static void Picker(PadFrame pad, float dt)
        {
            var dx = Dir(pad.Held(PadButton.Left) || pad.Ls.x < -NavStick,
                pad.Held(PadButton.Right) || pad.Ls.x > NavStick);
            var dy = Dir(pad.Held(PadButton.Up) || pad.Ls.y > NavStick,
                pad.Held(PadButton.Down) || pad.Ls.y < -NavStick);
            if (NavX.Step(dx, dt))
            {
                PiecePicker.Move(dx, 0);
            }

            if (NavY.Step(dy, dt))
            {
                PiecePicker.Move(0, dy);
            }

            if (pad.Pressed(PadButton.L1))
            {
                PiecePicker.NextTab(-1);
            }

            if (pad.Pressed(PadButton.R1))
            {
                PiecePicker.NextTab(1);
            }

            if (pad.Pressed(PadButton.Cross))
            {
                PiecePicker.Pick();
            }
        }

        private static int Dir(bool negative, bool positive)
        {
            return (positive ? 1 : 0) - (negative ? 1 : 0);
        }

        /// <summary>The left stick and the D-pad. The stick's tilt counts twice: fine moves near the middle.</summary>
        private static void Fly(PadFrame pad, float dt)
        {
            var camera = ViewportHost.Camera;
            if (camera == null)
            {
                return;
            }

            var stick = pad.Ls * pad.Ls.magnitude;
            var up = (pad.Held(PadButton.Up) ? 1f : 0f) - (pad.Held(PadButton.Down) ? 1f : 0f);
            camera.FlyPad(new Vector3(stick.x, up, stick.y), pad.Held(PadButton.L1), dt);
        }

        // ---------- what the crosshair is on ----------

        /// <summary>The piece in the middle of the view, or null.</summary>
        private static DocPiece Aimed()
        {
            var document = EditorState.Document;
            var id = ViewportHost.AimPiece;
            return document != null && id >= 0 ? document.Find(id) : null;
        }

        private static void SelectAimed(bool toggle)
        {
            var piece = Aimed();
            if (piece != null)
            {
                EditorState.Select(piece.Id, toggle ? SelectHow.Toggle : SelectHow.Set);
            }
            else if (!toggle)
            {
                EditorState.Select(Array.Empty<int>());
            }
        }

        /// <summary>L2 + R2, like the game's copy: another piece of the aimed kind goes in hand.</summary>
        private static void CopyAimed()
        {
            var piece = Aimed();
            var entry = piece != null ? PieceCatalog.Find(piece.PrefabName) : null;
            if (entry != null)
            {
                EditorSession.StartAdd(entry);
            }
        }

        /// <summary>R1, like the game's remove: the aimed piece, or the selection when it is in it.</summary>
        /// <summary>
        /// What square, triangle and R1 act on. The pad works off the crosshair, so a piece in the
        /// middle of the view is taken even when nothing was selected, and the whole selection is
        /// taken when that piece is already part of it. Aiming at nothing keeps the selection that
        /// is there, so the three buttons still work after picking several pieces with R2.
        /// False means there is nothing to act on.
        /// </summary>
        private static bool TakeAimed()
        {
            var piece = Aimed();
            if (piece != null && !EditorState.IsSelected(piece.Id))
            {
                EditorState.Select(piece.Id);
            }

            return EditorState.SelectionCount > 0;
        }

        private static void DeleteAimed()
        {
            if (TakeAimed())
            {
                EditorState.DeleteSelection();
            }
        }

        private static void MoveAimed()
        {
            if (TakeAimed())
            {
                EditorState.StartMove();
            }
        }

        /// <summary>L2 + R1: copies of the aimed piece, turned the same way, keep coming.</summary>
        private static void CloneAimed()
        {
            if (TakeAimed())
            {
                EditorState.StartDuplicate();
            }
        }

        // ---------- the help table ----------

        /// <summary>
        /// The controller half of the help, in the pad's own button names and the same order as
        /// the Tomrer editor's PAD table (src/ui/Dialogs.tsx).
        /// </summary>
        public static HelpRow[] Help(Glyphs g)
        {
            var cross = g.Of(PadButton.Cross);
            var circle = g.Of(PadButton.Circle);
            var l1 = g.Of(PadButton.L1);
            var l2 = g.Of(PadButton.L2);
            var r1 = g.Of(PadButton.R1);
            var r2 = g.Of(PadButton.R2);
            var l3 = g.Of(PadButton.L3);
            var r3 = g.Of(PadButton.R3);

            var rows = new List<HelpRow>
            {
                new HelpRow(g.Ls, "Fly forward (where the camera looks), back, left, right"),
                new HelpRow($"{l1} + {g.Ls}", "Fly 3 times faster, like Shift on the keyboard"),
                new HelpRow(g.Rs, "Look around"),
                new HelpRow(g.Dpad + " up, down", "Fly up, down"),
                new HelpRow(r2, "Place (while placing), else select the piece in the middle of the view"),
                new HelpRow($"{l1} + {r2}", "Add that piece to the selection, or take it out"),
                new HelpRow($"{l2} + {g.Rs} left, right", "Turn 22.5 degrees: the piece being placed, else the selection"),
                new HelpRow($"{l1} (hold)", "No snapping while held, like in the game"),
                new HelpRow($"{l3} (in the view)",
                    $"Leave the view and walk the panels. {circle} comes back to the view."),
                new HelpRow($"{l3} (in the panels)",
                    $"{g.Dpad} or {g.Ls} moves up, down, left, right, {r1} goes left, top, right and {l1} back, "
                    + $"{cross} presses, {circle} goes back to the view"),
                new HelpRow($"{l3}, {r3} (while placing)", "Pick the snap point, like Q and E"),
                new HelpRow($"{r3} (in the view)", "Look at the selection, or at everything"),
                new HelpRow(cross,
                    $"Pieces menu: {g.Dpad} to choose, {l1} {r1} for the tab, {cross} to place, {circle} to close"),
                new HelpRow(circle,
                    "Stop placing, else leave the panels for the view, else clear the selection, "
                    + "else close the editor"),
                new HelpRow(g.Of(PadButton.Square),
                    "Move the piece in the middle of the view. It follows the crosshair, "
                    + $"{r2} drops it, {circle} puts it back."),
                new HelpRow(g.Of(PadButton.Triangle),
                    "Copy the piece in the middle of the view. Copies of it, turned the same way, "
                    + $"keep coming until {circle} stops."),
                new HelpRow(r1, "Delete the piece in the middle of the view"),
                new HelpRow("The three above",
                    "Take the whole selection when the aimed piece is part of it, and the selection "
                    + "on its own when the crosshair is on nothing"),
                new HelpRow($"{l2} + {r2}", "Place another piece of the kind in the middle of the view, like the game's copy"),
                new HelpRow(g.Dpad + " left, right", "Undo, redo"),
                new HelpRow(g.Of(PadButton.Options), "This help"),
            };

            return rows.ToArray();
        }
    }
}
