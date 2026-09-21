using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;

namespace ValheimTomrer.Editor.Placement
{
    /// <summary>One piece that stays put while something is placed.</summary>
    internal sealed class ScenePiece
    {
        /// <summary>The document's own id, handed back so the caller can find the piece again.</summary>
        public int Id;

        public string Prefab;
        public Vector3 Pos;
        public Quaternion Rot;

        /// <summary>The catalog entry, or null for a piece this game does not have.</summary>
        public PieceEntry Entry;
    }

    /// <summary>A piece with its colliders and snap points already in world space.</summary>
    internal sealed class IndexedPiece
    {
        public int Id;
        public string Prefab;
        public Vector3 Pos;
        public Quaternion Rot;
        public PieceEntry Entry;

        /// <summary>Colliders the aiming ray can hit.</summary>
        public PlaceShape[] Ray;

        /// <summary>Colliders that make this piece count as near enough to snap to.</summary>
        public PlaceShape[] Search;

        public Vector3[] Snaps;

        /// <summary>Radius around the pivot that holds every search collider.</summary>
        public float Reach;
    }

    /// <summary>What the aiming ray met.</summary>
    internal struct SceneHit
    {
        public float T;
        public Vector3 Point;
        public Vector3 Normal;

        /// <summary>The ground, not a piece. The game treats a heightmap hit this way.</summary>
        public bool Terrain;

        /// <summary>Id of the piece that was hit, or -1 for the ground.</summary>
        public int Piece;
    }

    /// <summary>One snap point of one piece, with its distance to whatever was asked for.</summary>
    internal struct SnapRef
    {
        public IndexedPiece Piece;
        public Vector3 Point;
        public float Distance;
    }

    /// <summary>
    /// The pieces that stay put while something is placed, with their colliders and snap points in
    /// world space, plus a 0.5 m spatial hash over the snap points and the pivots so the searches
    /// the placing rule runs every frame do not walk the whole scene.
    ///
    /// The index is built once and never changes. Move a piece and build a new one.
    /// </summary>
    internal sealed class SceneIndex
    {
        private readonly List<IndexedPiece> _pieces = new List<IndexedPiece>();
        private readonly Dictionary<Cell, List<SnapRef>> _cells = new Dictionary<Cell, List<SnapRef>>();
        private readonly Dictionary<Cell, List<IndexedPiece>> _pivots = new Dictionary<Cell, List<IndexedPiece>>();

        public SceneIndex(IEnumerable<ScenePiece> pieces)
        {
            foreach (var piece in pieces)
            {
                _pieces.Add(Index(piece));
            }

            foreach (var piece in _pieces)
            {
                foreach (var point in piece.Snaps)
                {
                    Add(_cells, CellOf(point), new SnapRef { Piece = piece, Point = point });
                }

                Add(_pivots, CellOf(piece.Pos), piece);
            }
        }

        public IReadOnlyList<IndexedPiece> Pieces => _pieces;

        /// <summary>
        /// The editor's floor at y = 0 counts as terrain. Switch it off for a scene that has none.
        /// </summary>
        public bool HasGround { get; set; } = true;

        /// <summary>The nearest surface along the ray: a piece's collider, or the ground.</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, out SceneHit hit)
        {
            hit = default(SceneHit);
            var found = false;

            if (HasGround && origin.y > 0f && dir.y < -1e-9f)
            {
                var t = -origin.y / dir.y;
                var point = origin + (dir * t);
                point.y = 0f;
                hit = new SceneHit { T = t, Point = point, Normal = Vector3.up, Terrain = true, Piece = -1 };
                found = true;
            }

            foreach (var piece in _pieces)
            {
                foreach (var shape in piece.Ray)
                {
                    if (!shape.RayHit(origin, dir, out var shapeHit) || (found && shapeHit.T >= hit.T))
                    {
                        continue;
                    }

                    hit = new SceneHit
                    {
                        T = shapeHit.T,
                        Point = shapeHit.Point,
                        Normal = shapeHit.Normal,
                        Terrain = false,
                        Piece = piece.Id,
                    };
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// Pieces with a collider within the radius of the point. This is what the game's
        /// Physics.OverlapSphere on the piece layers finds.
        /// </summary>
        public List<IndexedPiece> Near(Vector3 point, float radius)
        {
            var found = new List<IndexedPiece>();
            foreach (var piece in _pieces)
            {
                if (Vector3.Distance(piece.Pos, point) > radius + piece.Reach)
                {
                    continue;
                }

                foreach (var shape in piece.Search)
                {
                    if (Vector3.Distance(shape.ClosestPoint(point), point) <= radius)
                    {
                        found.Add(piece);
                        break;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Snap points within max of the point, from the allowed pieces (all of them when allowed
        /// is null). Max must not be more than the 0.5 m cell, or the hash would miss some.
        /// </summary>
        public List<SnapRef> SnapsNear(Vector3 point, float max, HashSet<int> allowed)
        {
            var found = new List<SnapRef>();
            var cell = CellOf(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!_cells.TryGetValue(new Cell(cell.X + dx, cell.Y + dy, cell.Z + dz), out var list))
                        {
                            continue;
                        }

                        foreach (var snap in list)
                        {
                            if (allowed != null && !allowed.Contains(snap.Piece.Id))
                            {
                                continue;
                            }

                            var distance = Vector3.Distance(snap.Point, point);
                            if (distance <= max)
                            {
                                found.Add(new SnapRef { Piece = snap.Piece, Point = snap.Point, Distance = distance });
                            }
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>Pieces whose pivot is within max (at most 0.5 m) of the point.</summary>
        public List<IndexedPiece> PiecesAt(Vector3 point, float max)
        {
            var found = new List<IndexedPiece>();
            var cell = CellOf(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (!_pivots.TryGetValue(new Cell(cell.X + dx, cell.Y + dy, cell.Z + dz), out var list))
                        {
                            continue;
                        }

                        foreach (var piece in list)
                        {
                            if (Vector3.Distance(piece.Pos, point) <= max)
                            {
                                found.Add(piece);
                            }
                        }
                    }
                }
            }

            return found;
        }

        private static IndexedPiece Index(ScenePiece piece)
        {
            var entry = piece.Entry;
            var indexed = new IndexedPiece
            {
                Id = piece.Id,
                Prefab = piece.Prefab,
                Pos = piece.Pos,
                Rot = piece.Rot,
                Entry = entry,
                Ray = Shapes(entry != null ? entry.RayColliders : null, piece.Pos, piece.Rot),
                Search = Shapes(entry != null ? entry.SnapSearchColliders : null, piece.Pos, piece.Rot),
                Snaps = Points(entry, piece.Pos, piece.Rot),
            };

            foreach (var shape in indexed.Search)
            {
                var reach = Vector3.Distance(piece.Pos, shape.Center) + shape.Half.magnitude + shape.Radius;
                if (reach > indexed.Reach)
                {
                    indexed.Reach = reach;
                }
            }

            return indexed;
        }

        private static PlaceShape[] Shapes(PieceCollider[] colliders, Vector3 pos, Quaternion rot)
        {
            if (colliders == null || colliders.Length == 0)
            {
                return Array.Empty<PlaceShape>();
            }

            var shapes = new PlaceShape[colliders.Length];
            for (var i = 0; i < colliders.Length; i++)
            {
                shapes[i] = PlaceShape.Of(colliders[i], pos, rot);
            }

            return shapes;
        }

        private static Vector3[] Points(PieceEntry entry, Vector3 pos, Quaternion rot)
        {
            if (entry == null || entry.SnapPoints == null || entry.SnapPoints.Length == 0)
            {
                return Array.Empty<Vector3>();
            }

            var points = new Vector3[entry.SnapPoints.Length];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = pos + (rot * entry.SnapPoints[i]);
            }

            return points;
        }

        private static void Add<T>(Dictionary<Cell, List<T>> map, Cell cell, T item)
        {
            if (map.TryGetValue(cell, out var list))
            {
                list.Add(item);
            }
            else
            {
                map[cell] = new List<T> { item };
            }
        }

        private static Cell CellOf(Vector3 p)
        {
            return new Cell(
                Mathf.FloorToInt(p.x / Placer.SnapDistance),
                Mathf.FloorToInt(p.y / Placer.SnapDistance),
                Mathf.FloorToInt(p.z / Placer.SnapDistance));
        }

        /// <summary>One 0.5 m box of the spatial hash.</summary>
        private struct Cell : IEquatable<Cell>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public Cell(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(Cell other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is Cell other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((X * 397) ^ Y) * 397 ^ Z;
                }
            }
        }
    }
}
