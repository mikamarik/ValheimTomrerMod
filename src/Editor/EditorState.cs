using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;
using ValheimTomrer.Editor.Placement;

namespace ValheimTomrer.Editor
{
    /// <summary>What a click on the pane does.</summary>
    internal enum EditMode
    {
        /// <summary>Nothing in hand: a click selects.</summary>
        Idle,

        /// <summary>Something is in hand: a click drops it.</summary>
        Place,
    }

    /// <summary>Why something is in hand.</summary>
    internal enum PlaceAction
    {
        /// <summary>A new piece from the palette. Stays in hand after dropping.</summary>
        Add,

        /// <summary>The selected pieces, taken out of the scene. Back to idle after dropping.</summary>
        Move,

        /// <summary>Copies of the selected pieces. Stays in hand after dropping.</summary>
        Duplicate,
    }

    /// <summary>How a new selection meets the old one.</summary>
    internal enum SelectHow
    {
        Set,
        Add,
        Toggle,
    }

    /// <summary>
    /// What the editor is doing to the open blueprint: what is selected, what is in hand, and every
    /// action that changes either. The same list as the Tomrer editor's store (src/app/store.ts),
    /// so the two behave the same.
    ///
    /// Nothing here touches the UI or the 3D pane. The pane reads it once a frame and draws it,
    /// which is what lets the autotest drive a whole editing session with no window open.
    /// </summary>
    internal static class EditorState
    {
        /// <summary>Draw the pieces as boxes instead of models. A top bar switch.</summary>
        public static bool PieceBoxesOn { get; set; }

        /// <summary>Show the snap points while placing. Snapping itself is always on.</summary>
        public static bool SnapDotsOn { get; set; } = true;

        /// <summary>A group bigger than this snaps against every piece, not only the ones within 10 m.</summary>
        private const int SearchAllFrom = 8;

        private static readonly HashSet<int> Selected = new HashSet<int>();
        private static readonly List<int> MovingIds = new List<int>();

        private static SceneIndex _index;
        private static int _indexRevision = -1;
        private static int _indexMoving = -1;

        /// <summary>The blueprint being edited, or null while the editor is closed.</summary>
        public static BlueprintDocument Document { get; private set; }

        /// <summary>The ids of the selected pieces.</summary>
        public static IReadOnlyCollection<int> Selection => Selected;

        public static int SelectionCount => Selected.Count;

        /// <summary>The ids of the pieces in hand while moving. Empty otherwise.</summary>
        public static IReadOnlyList<int> Carrying =>
            Mode == EditMode.Place && Action == PlaceAction.Move ? MovingIds : (IReadOnlyList<int>)EmptyIds;

        private static readonly int[] EmptyIds = new int[0];

        public static EditMode Mode { get; private set; } = EditMode.Idle;

        public static PlaceAction Action { get; private set; }

        /// <summary>What is in hand, laid out around its pivot. Null while idle.</summary>
        public static MovingSet Moving { get; private set; }

        /// <summary>The catalog entry in hand while adding. Null for move and duplicate.</summary>
        public static PieceEntry Held { get; private set; }

        /// <summary>Wheel steps of 22.5 degrees for what is in hand.</summary>
        public static int Steps { get; private set; }

        /// <summary>The turn new pieces keep between placements, the way the game does.</summary>
        public static int PlaceSteps { get; private set; }

        /// <summary>The chosen snap point, -1 for automatic.</summary>
        public static int Manual { get; private set; } = -1;

        /// <summary>False while the no-snap modifier is held.</summary>
        public static bool Snapping { get; set; } = true;

        /// <summary>Where what is in hand would land, from the last <see cref="Aim"/>. Null when nothing was hit.</summary>
        public static PlaceResult Aimed { get; private set; }

        /// <summary>The last thing the editor wants to tell the player, and when it was said.</summary>
        public static string Message { get; private set; }

        public static float MessageAt { get; private set; }

        /// <summary>Goes up on every change of selection or mode, so the pane can notice.</summary>
        public static int Version { get; private set; }

        /// <summary>The pieces that stay put: the document, minus whatever is being moved.</summary>
        public static SceneIndex Index
        {
            get
            {
                var moving = Action == PlaceAction.Move && Mode == EditMode.Place ? MovingIds.Count : 0;
                if (_index != null && Document != null && _indexRevision == Document.Revision && _indexMoving == moving)
                {
                    return _index;
                }

                _indexRevision = Document != null ? Document.Revision : -1;
                _indexMoving = moving;
                _index = new SceneIndex(StandingPieces());
                return _index;
            }
        }

        public static void Open(BlueprintDocument document)
        {
            if (document != null)
            {
                PieceBoxesOn = EditorConfig.Boxes != null && EditorConfig.Boxes.Value;
                SnapDotsOn = EditorConfig.SnapDots == null || EditorConfig.SnapDots.Value;
            }

            Document = document;
            Selected.Clear();
            MovingIds.Clear();
            Mode = EditMode.Idle;
            Moving = null;
            Held = null;
            Steps = 0;
            PlaceSteps = 0;
            Manual = -1;
            Snapping = true;
            Aimed = null;
            Message = null;
            _index = null;
            _indexRevision = -1;
            Version++;
        }

        public static void Close()
        {
            Open(null);
        }

        // ---------- selection ----------

        public static void Select(IEnumerable<int> ids, SelectHow how = SelectHow.Set)
        {
            if (how == SelectHow.Set)
            {
                Selected.Clear();
            }

            if (ids != null)
            {
                foreach (var id in ids)
                {
                    if (how == SelectHow.Toggle && Selected.Contains(id))
                    {
                        Selected.Remove(id);
                    }
                    else
                    {
                        Selected.Add(id);
                    }
                }
            }

            Version++;
        }

        public static bool IsSelected(int id)
        {
            return Selected.Contains(id);
        }

        public static void Select(int id, SelectHow how = SelectHow.Set)
        {
            Select(new[] { id }, how);
        }

        public static void SelectAll()
        {
            Selected.Clear();
            if (Document != null)
            {
                foreach (var piece in Document.Pieces)
                {
                    Selected.Add(piece.Id);
                }
            }

            Version++;
        }

        public static void DeleteSelection()
        {
            if (Document == null || Selected.Count == 0)
            {
                return;
            }

            var count = Selected.Count;
            Document.RemovePieces(new List<int>(Selected));
            Selected.Clear();
            CancelMode();
            Say($"Deleted {Count(count)}. Ctrl+Z brings {(count == 1 ? "it" : "them")} back.");
            Version++;
        }

        /// <summary>The selected pieces, in the document's own order.</summary>
        public static List<DocPiece> SelectedPieces()
        {
            var pieces = new List<DocPiece>(Selected.Count);
            if (Document == null)
            {
                return pieces;
            }

            foreach (var piece in Document.Pieces)
            {
                if (Selected.Contains(piece.Id))
                {
                    pieces.Add(piece);
                }
            }

            return pieces;
        }

        // ---------- what is in hand ----------

        /// <summary>A piece from the palette goes in hand, and stays there after each drop.</summary>
        public static bool StartAdd(PieceEntry entry)
        {
            if (Document == null || entry == null)
            {
                return false;
            }

            Held = entry;
            Moving = MovingSet.One(entry);
            Action = PlaceAction.Add;
            Mode = EditMode.Place;
            Steps = PlaceSteps;
            Manual = -1;
            Aimed = null;
            Version++;
            return true;
        }

        /// <summary>The selection leaves the scene and follows the aim until it is dropped.</summary>
        public static bool StartMove()
        {
            var pieces = SelectedPieces();
            if (pieces.Count == 0)
            {
                return false;
            }

            MovingIds.Clear();
            foreach (var piece in pieces)
            {
                MovingIds.Add(piece.Id);
            }

            Held = null;
            Moving = MovingSet.Of(MovingPieces(pieces));
            Action = PlaceAction.Move;
            Mode = EditMode.Place;
            Steps = 0;
            Manual = -1;
            Aimed = null;
            _index = null;
            Version++;
            return true;
        }

        /// <summary>Copies of the selection go in hand, and stay there so more can be dropped.</summary>
        public static bool StartDuplicate()
        {
            var pieces = SelectedPieces();
            if (pieces.Count == 0)
            {
                return false;
            }

            MovingIds.Clear();
            Held = null;
            Moving = MovingSet.Of(MovingPieces(pieces));
            Action = PlaceAction.Duplicate;
            Mode = EditMode.Place;
            Steps = 0;
            Manual = -1;
            Aimed = null;
            Version++;
            return true;
        }

        public static void CancelMode()
        {
            if (Mode == EditMode.Idle)
            {
                return;
            }

            Mode = EditMode.Idle;
            Moving = null;
            Held = null;
            MovingIds.Clear();
            Aimed = null;
            _index = null;
            Version++;
        }

        public static void SetPlaceSteps(int steps)
        {
            if (Mode != EditMode.Place)
            {
                return;
            }

            Steps = steps;
            if (Action == PlaceAction.Add)
            {
                PlaceSteps = steps;
            }

            Version++;
        }

        /// <summary>Walks the chosen snap point, the way Q and E do in the game.</summary>
        public static void SetManualSnap(int manual)
        {
            if (Mode != EditMode.Place || Moving == null)
            {
                return;
            }

            Manual = Placer.WrapManual(manual, Moving.Snaps.Count);
            Version++;
        }

        /// <summary>The name of the chosen snap point, or null while it is automatic.</summary>
        public static string ManualName()
        {
            if (Moving == null || Manual < 0 || Manual >= Moving.Snaps.Count)
            {
                return null;
            }

            return Moving.Snaps[Manual].Name;
        }

        /// <summary>
        /// Runs the placing rule for one ray, in the editor scene's own space, and remembers the
        /// answer for the ghost and the HUD. False when the ray met nothing at all.
        /// </summary>
        public static bool Aim(Vector3 origin, Vector3 dir, out PlaceResult result)
        {
            result = null;
            if (Mode != EditMode.Place || Moving == null || Moving.Count == 0)
            {
                Aimed = null;
                return false;
            }

            var options = new PlaceOptions
            {
                Steps = Steps,
                Manual = Manual,
                Snapping = Snapping,
                SearchAll = Moving.Count > SearchAllFrom,
                Assist = true,
            };

            var hit = Placer.Place(Index, Moving, origin, dir.normalized, options, out result);
            Aimed = hit ? result : null;
            return hit;
        }

        /// <summary>Nothing is in hand, so there is nowhere for it to land.</summary>
        public static void ClearAim()
        {
            Aimed = null;
        }

        /// <summary>A click while something is in hand: add it, drop the moved pieces, or drop the copies.</summary>
        public static bool CommitPlacement(PlaceResult result)
        {
            if (Document == null || Mode != EditMode.Place || Moving == null || result == null || result.Duplicate)
            {
                return false;
            }

            var world = result.World;
            switch (Action)
            {
                case PlaceAction.Add:
                {
                    var id = Document.AddPiece(world[0].Prefab, world[0].Pos, Clean(world[0].Rot));
                    Select(id);
                    break;
                }

                case PlaceAction.Move:
                {
                    var moves = new List<PieceMove>(world.Length);
                    foreach (var placed in world)
                    {
                        moves.Add(new PieceMove { Id = placed.Id, Position = placed.Pos, Rotation = Clean(placed.Rot) });
                    }

                    Document.SetPieces(moves);
                    CancelMode();
                    break;
                }

                default:
                {
                    var copies = new List<NewPiece>(world.Length);
                    foreach (var placed in world)
                    {
                        copies.Add(new NewPiece
                        {
                            PrefabName = placed.Prefab,
                            Position = placed.Pos,
                            Rotation = Clean(placed.Rot),
                        });
                    }

                    Select(Document.AddPieces(copies));
                    break;
                }
            }

            Aimed = null;
            Version++;
            return true;
        }

        // ---------- editing the selection ----------

        /// <summary>Arrow keys and PageUp/PageDown. A run of presses is one undo step.</summary>
        public static void Nudge(Vector3 delta)
        {
            var pieces = SelectedPieces();
            if (pieces.Count == 0 || delta == Vector3.zero)
            {
                return;
            }

            var moves = new List<PieceMove>(pieces.Count);
            foreach (var piece in pieces)
            {
                moves.Add(new PieceMove { Id = piece.Id, Position = piece.Position + delta, Rotation = piece.Rotation });
            }

            Document.SetPieces(moves, "nudge");
        }

        /// <summary>R: turns the selection 22.5 degrees, a group around the bottom centre of its box.</summary>
        public static void RotateSelection(int direction)
        {
            var pieces = SelectedPieces();
            if (pieces.Count == 0)
            {
                return;
            }

            foreach (var piece in pieces)
            {
                var entry = PieceCatalog.Find(piece.PrefabName);
                if (entry != null && !entry.CanRotate)
                {
                    Say("This piece can't turn in the game.");
                    return;
                }
            }

            var turn = Quaternion.Euler(0f, Placer.RotateStep * Mathf.Sign(direction), 0f);
            var centre = pieces.Count == 1 ? pieces[0].Position : BottomCentre(pieces);
            var moves = new List<PieceMove>(pieces.Count);
            foreach (var piece in pieces)
            {
                moves.Add(new PieceMove
                {
                    Id = piece.Id,
                    Position = centre + (turn * (piece.Position - centre)),
                    Rotation = Clean(turn * piece.Rotation),
                });
            }

            Document.SetPieces(moves);
        }

        /// <summary>The yaw field: turns one piece to a free angle, keeping any tilt it has.</summary>
        public static void SetYaw(int id, float degrees)
        {
            var piece = Document != null ? Document.Find(id) : null;
            if (piece == null || float.IsNaN(degrees) || float.IsInfinity(degrees))
            {
                return;
            }

            var level = Vector3.Angle(piece.Rotation * Vector3.up, Vector3.up) < 0.01f;
            var rotation = level
                ? Quaternion.Euler(0f, degrees, 0f)
                : Quaternion.Euler(0f, degrees - YawOf(piece.Rotation), 0f) * piece.Rotation;
            Document.SetPieces(
                new List<PieceMove> { new PieceMove { Id = id, Position = piece.Position, Rotation = Clean(rotation) } },
                "yaw:" + id);
        }

        public static void SetPosition(int id, Vector3 position)
        {
            var piece = Document != null ? Document.Find(id) : null;
            if (piece == null)
            {
                return;
            }

            Document.SetPieces(
                new List<PieceMove> { new PieceMove { Id = id, Position = position, Rotation = piece.Rotation } },
                "pos:" + id);
        }

        /// <summary>Moves the origin to the bottom centre of the blueprint. The pieces shift the other way.</summary>
        public static bool CenterOrigin()
        {
            if (Document == null || Document.HasSections || Document.Pieces.Count == 0)
            {
                return false;
            }

            var pieces = new List<DocPiece>(Document.Pieces);
            var shift = BottomCentre(pieces);
            if (shift.magnitude < 1e-4f)
            {
                Say("The origin is already at the bottom centre.");
                return false;
            }

            var moves = new List<PieceMove>(pieces.Count);
            foreach (var piece in pieces)
            {
                moves.Add(new PieceMove { Id = piece.Id, Position = piece.Position - shift, Rotation = piece.Rotation });
            }

            Document.SetPieces(moves);
            Say($"Moved the origin by {shift.x:0.00}, {shift.y:0.00}, {shift.z:0.00} m.");
            return true;
        }

        // ---------- history ----------

        public static bool Undo()
        {
            CancelMode();
            if (Document == null || !Document.Undo())
            {
                return false;
            }

            Prune();
            return true;
        }

        public static bool Redo()
        {
            CancelMode();
            if (Document == null || !Document.Redo())
            {
                return false;
            }

            Prune();
            return true;
        }

        // ---------- boxes ----------

        /// <summary>The box around one piece, in the editor scene's space.</summary>
        public static Bounds BoxOf(DocPiece piece)
        {
            var entry = PieceCatalog.Find(piece.PrefabName);
            if (entry == null || entry.Bounds.size == Vector3.zero)
            {
                return new Bounds(piece.Position, Vector3.one * 0.5f);
            }

            var centre = piece.Position + (piece.Rotation * entry.Bounds.center);
            var e = entry.Bounds.extents;
            var extent =
                Abs(piece.Rotation * new Vector3(e.x, 0f, 0f))
                + Abs(piece.Rotation * new Vector3(0f, e.y, 0f))
                + Abs(piece.Rotation * new Vector3(0f, 0f, e.z));
            return new Bounds(centre, extent * 2f);
        }

        /// <summary>The box around a list of pieces, or null when it is empty.</summary>
        public static Bounds? BoxOf(IList<DocPiece> pieces)
        {
            if (pieces == null || pieces.Count == 0)
            {
                return null;
            }

            var box = BoxOf(pieces[0]);
            for (var i = 1; i < pieces.Count; i++)
            {
                box.Encapsulate(BoxOf(pieces[i]));
            }

            return box;
        }

        /// <summary>
        /// The bottom centre of a set of pieces: the middle of their box in x and z, the lowest
        /// point in y. Center origin and the world capture both put the origin here, so a captured
        /// blueprint is centred the way the button would centre it.
        /// </summary>
        public static Vector3 BottomCentre(IList<DocPiece> pieces)
        {
            var box = BoxOf(pieces) ?? new Bounds();
            return new Vector3(box.center.x, box.min.y, box.center.z);
        }

        // ---------- messages ----------

        public static void Say(string text)
        {
            Message = text;
            MessageAt = Time.unscaledTime;
            ValheimTomrerPlugin.Log.LogInfo("editor: " + text);
        }

        public static void ClearMessage()
        {
            Message = null;
        }

        // ---------- inside ----------

        /// <summary>What the placing rule aims at: every piece but the ones in hand.</summary>
        private static IEnumerable<ScenePiece> StandingPieces()
        {
            if (Document == null)
            {
                yield break;
            }

            var moving = Mode == EditMode.Place && Action == PlaceAction.Move;
            foreach (var piece in Document.Pieces)
            {
                if (moving && MovingIds.Contains(piece.Id))
                {
                    continue;
                }

                yield return new ScenePiece
                {
                    Id = piece.Id,
                    Prefab = piece.PrefabName,
                    Pos = piece.Position,
                    Rot = piece.Rotation,
                    Entry = PieceCatalog.Find(piece.PrefabName),
                };
            }
        }

        private static List<MovingPiece> MovingPieces(IList<DocPiece> pieces)
        {
            var moving = new List<MovingPiece>(pieces.Count);
            foreach (var piece in pieces)
            {
                moving.Add(new MovingPiece
                {
                    Id = piece.Id,
                    Prefab = piece.PrefabName,
                    Pos = piece.Position,
                    Rot = piece.Rotation,
                    Entry = PieceCatalog.Find(piece.PrefabName),
                });
            }

            return moving;
        }

        /// <summary>Drops from the selection the ids the document no longer has.</summary>
        private static void Prune()
        {
            var gone = new List<int>();
            foreach (var id in Selected)
            {
                if (Document.Find(id) == null)
                {
                    gone.Add(id);
                }
            }

            foreach (var id in gone)
            {
                Selected.Remove(id);
            }

            Version++;
        }

        private static float YawOf(Quaternion q)
        {
            var forward = q * Vector3.forward;
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        private static Quaternion Clean(Quaternion q)
        {
            return q.normalized;
        }

        private static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }

        private static string Count(int n)
        {
            return n == 1 ? "1 piece" : n + " pieces";
        }
    }
}
