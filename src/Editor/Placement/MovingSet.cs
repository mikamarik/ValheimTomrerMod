using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;

namespace ValheimTomrer.Editor.Placement
{
    /// <summary>One piece being placed, laid out against the set's pivot.</summary>
    internal sealed class MovingPiece
    {
        /// <summary>The document's own id, or -1 for a piece that is not in the document yet.</summary>
        public int Id = -1;

        public string Prefab;
        public Vector3 Pos;
        public Quaternion Rot = Quaternion.identity;

        /// <summary>The catalog entry, or null for a piece this game does not have.</summary>
        public PieceEntry Entry;
    }

    /// <summary>One snap point of the moving set, in pivot space.</summary>
    internal struct MovingSnap
    {
        public Vector3 Local;
        public string Name;
    }

    /// <summary>Where one piece of the set ends up.</summary>
    internal struct PlacedPiece
    {
        public int Id;
        public string Prefab;
        public Vector3 Pos;
        public Quaternion Rot;
        public PieceEntry Entry;
    }

    /// <summary>
    /// What is being placed: one piece or a whole group, laid out around a pivot, before the
    /// placing rule turns and moves it.
    ///
    /// One piece pivots on itself, so the numbers match the game's ghost exactly. A group pivots on
    /// the bottom centre of its union box, which is where a person expects to carry a building from.
    /// </summary>
    internal sealed class MovingSet
    {
        private readonly List<MovingPiece> _pieces = new List<MovingPiece>();
        private readonly List<MovingSnap> _snaps = new List<MovingSnap>();
        private readonly List<TouchCollider> _touch = new List<TouchCollider>();

        private MovingSet(IEnumerable<MovingPiece> pieces, Vector3 pivot)
        {
            Pivot = pivot;
            foreach (var piece in pieces)
            {
                _pieces.Add(piece);
                var entry = piece.Entry;
                if (entry == null)
                {
                    continue;
                }

                for (var i = 0; i < entry.SnapPoints.Length; i++)
                {
                    _snaps.Add(new MovingSnap
                    {
                        Local = piece.Pos + (piece.Rot * entry.SnapPoints[i]),
                        Name = i < entry.SnapNames.Length && !string.IsNullOrEmpty(entry.SnapNames[i])
                            ? entry.SnapNames[i]
                            : "snap " + (i + 1),
                    });
                }

                foreach (var collider in entry.TouchColliders)
                {
                    _touch.Add(new TouchCollider { Collider = collider, Pos = piece.Pos, Rot = piece.Rot });
                }
            }

            CanRotate = true;
            foreach (var piece in _pieces)
            {
                if (piece.Entry != null && !piece.Entry.CanRotate)
                {
                    CanRotate = false;
                }
            }

            Single = _pieces.Count == 1 ? _pieces[0].Entry : null;

            var shapes = Shapes(Vector3.zero, Quaternion.identity);
            foreach (var snap in _snaps)
            {
                Reach = Mathf.Max(Reach, snap.Local.magnitude);
                foreach (var shape in shapes)
                {
                    Reach = Mathf.Max(
                        Reach,
                        Vector3.Distance(snap.Local, shape.Center) + shape.Half.magnitude + shape.Radius);
                }
            }
        }

        /// <summary>The pieces, with their positions relative to the pivot.</summary>
        public IReadOnlyList<MovingPiece> Pieces => _pieces;

        /// <summary>Every piece's snap points together, in pivot space.</summary>
        public IReadOnlyList<MovingSnap> Snaps => _snaps;

        /// <summary>The single piece's catalog entry. Null for a group, which has no flags of its own.</summary>
        public PieceEntry Single { get; }

        /// <summary>False when any piece refuses to be turned (Piece.m_canRotate).</summary>
        public bool CanRotate { get; }

        /// <summary>How far the colliders reach from any of the snap points. The assist step needs it.</summary>
        public float Reach { get; }

        /// <summary>Where the pivot sat in the world the set was taken from.</summary>
        public Vector3 Pivot { get; }

        public int Count => _pieces.Count;

        /// <summary>How many colliders the touch rule has to work with. Zero means it is skipped.</summary>
        public int TouchCount => _touch.Count;

        /// <summary>One piece, pivoting on itself.</summary>
        public static MovingSet One(PieceEntry entry, Quaternion rot)
        {
            var piece = new MovingPiece
            {
                Prefab = entry != null ? entry.PrefabName : null,
                Entry = entry,
                Pos = Vector3.zero,
                Rot = rot,
            };
            return new MovingSet(new[] { piece }, Vector3.zero);
        }

        public static MovingSet One(PieceEntry entry)
        {
            return One(entry, Quaternion.identity);
        }

        /// <summary>
        /// A set taken from world positions. One piece keeps its own pivot; a group is rebased on
        /// the bottom centre of its union box, measured the same way the blueprint preview measures
        /// a building, so the two agree.
        /// </summary>
        public static MovingSet Of(IList<MovingPiece> pieces)
        {
            if (pieces == null || pieces.Count == 0)
            {
                return new MovingSet(Array.Empty<MovingPiece>(), Vector3.zero);
            }

            var pivot = pieces.Count == 1 ? pieces[0].Pos : BottomCentre(pieces);
            var moved = new List<MovingPiece>(pieces.Count);
            foreach (var piece in pieces)
            {
                moved.Add(new MovingPiece
                {
                    Id = piece.Id,
                    Prefab = piece.Prefab,
                    Entry = piece.Entry,
                    Pos = piece.Pos - pivot,
                    Rot = piece.Rot,
                });
            }

            return new MovingSet(moved, pivot);
        }

        /// <summary>World position and rotation of every piece, for one pivot transform.</summary>
        public PlacedPiece[] World(Vector3 pos, Quaternion rot)
        {
            var placed = new PlacedPiece[_pieces.Count];
            for (var i = 0; i < _pieces.Count; i++)
            {
                var piece = _pieces[i];
                placed[i] = new PlacedPiece
                {
                    Id = piece.Id,
                    Prefab = piece.Prefab,
                    Entry = piece.Entry,
                    Pos = pos + (rot * piece.Pos),
                    Rot = rot * piece.Rot,
                };
            }

            return placed;
        }

        /// <summary>The colliders the touch rule uses, in world space for one pivot transform.</summary>
        public PlaceShape[] Shapes(Vector3 pos, Quaternion rot)
        {
            var shapes = new PlaceShape[_touch.Count];
            for (var i = 0; i < _touch.Count; i++)
            {
                var touch = _touch[i];
                shapes[i] = PlaceShape.Of(touch.Collider, pos + (rot * touch.Pos), rot * touch.Rot);
            }

            return shapes;
        }

        /// <summary>Bottom centre of the box around every piece, in the world the set came from.</summary>
        private static Vector3 BottomCentre(IList<MovingPiece> pieces)
        {
            var box = new Bounds(pieces[0].Pos, Vector3.zero);
            foreach (var piece in pieces)
            {
                var entry = piece.Entry;
                if (entry == null || entry.Bounds.size == Vector3.zero)
                {
                    box.Encapsulate(piece.Pos);
                    continue;
                }

                var centre = piece.Pos + (piece.Rot * entry.Bounds.center);
                var e = entry.Bounds.extents;
                var extent =
                    Abs(piece.Rot * new Vector3(e.x, 0f, 0f))
                    + Abs(piece.Rot * new Vector3(0f, e.y, 0f))
                    + Abs(piece.Rot * new Vector3(0f, 0f, e.z));
                box.Encapsulate(centre - extent);
                box.Encapsulate(centre + extent);
            }

            return new Vector3(box.center.x, box.min.y, box.center.z);
        }

        private static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }

        private struct TouchCollider
        {
            public PieceCollider Collider;
            public Vector3 Pos;
            public Quaternion Rot;
        }
    }
}
