using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimTomrer.Editor.Placement
{
    /// <summary>What the person is doing while they aim.</summary>
    internal struct PlaceOptions
    {
        /// <summary>Wheel steps of 22.5 degrees.</summary>
        public int Steps;

        /// <summary>Chosen snap point, -1 for automatic (Q/E, L3/R3).</summary>
        public int Manual;

        /// <summary>False while the no-snap modifier is held.</summary>
        public bool Snapping;

        /// <summary>Snap against every piece, not only the ones within 10 m. For big groups.</summary>
        public bool SearchAll;

        /// <summary>Tomrer's own step 6.</summary>
        public bool Assist;

        /// <summary>Snapping on, automatic snap point, assist on: what the editor normally wants.</summary>
        public static PlaceOptions Default => new PlaceOptions { Manual = -1, Snapping = true, Assist = true };
    }

    /// <summary>Where the moving set lands, and why.</summary>
    internal sealed class PlaceResult
    {
        public SceneHit Hit;

        /// <summary>Where the pivot goes.</summary>
        public Vector3 Pos;

        public Quaternion Rot;

        /// <summary>Every piece of the set, in world space.</summary>
        public PlacedPiece[] World;

        public bool Snapped;

        /// <summary>The own snap point that snapped, before the move.</summary>
        public Vector3 SnapFrom;

        /// <summary>The snap point of the standing piece it went to.</summary>
        public Vector3 SnapTo;

        /// <summary>Id of the piece that was snapped to, -1 when nothing snapped.</summary>
        public int SnapPiece = -1;

        /// <summary>The snap comes from step 6, not from the game's rule.</summary>
        public bool Assisted;

        /// <summary>The rule found a snap, but it would have landed on a piece of the same kind.</summary>
        public bool SnapSkipped;

        /// <summary>It lands on a piece of the same kind: draw it red, a click does nothing.</summary>
        public bool Duplicate;

        /// <summary>The moving set's snap points where they end up.</summary>
        public Vector3[] MovingSnaps;

        /// <summary>Snap points of the pieces the search looked at, for drawing them.</summary>
        public Vector3[] NearbySnaps;

        /// <summary>The chosen snap point after wrapping, -1 for automatic.</summary>
        public int Manual = -1;

        public string ManualName;

        /// <summary>
        /// The game's support value of each piece where it lands, in the order of World. Filled in
        /// by the editor after the placing rule, see <see cref="Support.Evaluate"/>.
        /// </summary>
        public float[] Support;

        /// <summary>Which pieces would break the moment the game checks them.</summary>
        public bool[] Falls;

        /// <summary>At least one piece would fall down: draw it red, a click does nothing.</summary>
        public bool WouldFall;

        /// <summary>A click here does nothing: the same piece is there, or something would fall.</summary>
        public bool Blocked => Duplicate || WouldFall;
    }

    /// <summary>
    /// The game's placing rule, ported to plain C#: given a ray and a moving set it says where the
    /// piece lands and whether it snapped. No GameObject, no physics call, nothing drawn, so it runs
    /// headlessly and the editor can ask it anything without touching the world.
    ///
    /// The steps, in order (Player.UpdatePlacementGhost, FindClosestSnapPoints, IsOverlappingOtherPiece):
    ///   1. the ray hits a surface at P, facing N;
    ///   2. touch: push the piece 50 m out along N, find the point of its colliders nearest to P,
    ///      pull it back so that point lands on P. Ground and clipping pieces put the pivot on P;
    ///   3. a chosen snap point goes on P instead;
    ///   4. snap: the nearest pair of snap points within 0.5 m, own against nearby. Move, never turn;
    ///   5. no snap when the result would sit on a piece of the same kind;
    ///   6. assist (Tomrer only): when 4 and 5 find nothing, try every pair and take the nearest
    ///      spot that still touches the aimed point.
    /// </summary>
    internal static class Placer
    {
        /// <summary>Player.m_placeRotationDegrees.</summary>
        public const float RotateStep = 22.5f;

        public const float SnapDistance = 0.5f;

        public const float SnapSearchRadius = 10f;

        public const float OverlapDistance = 0.05f;

        public const float OverlapAngle = 10f;

        /// <summary>Tomrer only: how close the snapped piece must come to the aimed point.</summary>
        public const float AssistReach = 0.5f;

        /// <summary>Tomrer only: never snaps a piece deeper than this under the ground.</summary>
        public const float AssistMaxDepth = 0.25f;

        /// <summary>
        /// Wraps a chosen snap point the way the game does: below -1 goes to the last one, past the
        /// last one back to automatic.
        /// </summary>
        public static int WrapManual(int index, int count)
        {
            if (count == 0)
            {
                return -1;
            }

            if (index < -1)
            {
                return count - 1;
            }

            return index >= count ? -1 : index;
        }

        /// <summary>
        /// Runs the six steps. Returns false when the ray met nothing at all, which is the game's
        /// PlacementStatus.NoRayHits. The direction must have length 1.
        /// </summary>
        public static bool Place(
            SceneIndex index, MovingSet moving, Vector3 origin, Vector3 dir, PlaceOptions opts, out PlaceResult result)
        {
            result = null;
            if (index == null || moving == null || moving.Count == 0 || !index.Raycast(origin, dir, out var hit))
            {
                return false;
            }

            // 1. where the ray landed. The game flattens the normal on terrain.
            var point = hit.Point;
            var normal = hit.Terrain ? Vector3.up : hit.Normal;
            var rot = Quaternion.Euler(0f, RotateStep * (moving.CanRotate ? opts.Steps : 0), 0f);
            var manual = WrapManual(opts.Manual, moving.Snaps.Count);
            var manualOffset = manual >= 0 ? rot * -moving.Snaps[manual].Local : Vector3.zero;
            var single = moving.Single;

            // 2 and 3. touch, or the pivot straight on the aimed point.
            Vector3 pos;
            var onPoint = single != null
                && (((single.GroundPiece || single.ClipGround) && hit.Terrain) || single.ClipEverything);
            if (onPoint || moving.TouchCount == 0)
            {
                pos = point + manualOffset;
            }
            else
            {
                var ghost = point + (normal * 50f);
                var closest = Vector3.zero;
                var closestD = float.PositiveInfinity;
                foreach (var shape in moving.Shapes(ghost, rot))
                {
                    var candidate = shape.ClosestPoint(point);
                    var d = Vector3.Distance(candidate, point);
                    if (d < closestD)
                    {
                        closestD = d;
                        closest = candidate;
                    }
                }

                var offset = ghost - closest;
                if (single != null && single.WaterPiece)
                {
                    offset.y = 3f;
                }

                pos = point + (manual < 0 ? offset : manualOffset);
            }

            // 4. the nearest pair of snap points, own against the pieces around the ghost.
            var nearby = opts.SearchAll ? new List<IndexedPiece>(index.Pieces) : index.Near(pos, SnapSearchRadius);
            HashSet<int> allowed = null;
            if (!opts.SearchAll)
            {
                allowed = new HashSet<int>();
                foreach (var piece in nearby)
                {
                    allowed.Add(piece.Id);
                }
            }

            result = new PlaceResult
            {
                Hit = hit,
                Rot = rot,
                Manual = manual,
                ManualName = manual >= 0 ? moving.Snaps[manual].Name : null,
            };

            if (opts.Snapping)
            {
                var bestD = float.PositiveInfinity;
                var count = moving.Snaps.Count;
                for (var i = 0; i < count; i++)
                {
                    if (manual >= 0 && i != manual)
                    {
                        continue;
                    }

                    var from = pos + (rot * moving.Snaps[i].Local);
                    var found = false;
                    var foundD = 999999f;
                    var to = Vector3.zero;
                    var toPiece = -1;
                    foreach (var snap in index.SnapsNear(from, SnapDistance, allowed))
                    {
                        if (snap.Distance < foundD)
                        {
                            foundD = snap.Distance;
                            to = snap.Point;
                            toPiece = snap.Piece.Id;
                            found = true;
                        }
                    }

                    if (found && foundD < bestD)
                    {
                        bestD = foundD;
                        result.Snapped = true;
                        result.SnapFrom = from;
                        result.SnapTo = to;
                        result.SnapPiece = toPiece;
                    }
                }

                // 5. refuse a snap that would put the piece on one of the same kind.
                if (result.Snapped)
                {
                    var snapped = result.SnapTo - (result.SnapFrom - pos);
                    if (Overlaps(moving, snapped, rot, nearby))
                    {
                        result.SnapSkipped = true;
                        result.Snapped = false;
                        result.SnapPiece = -1;
                    }
                    else
                    {
                        pos = snapped;
                    }
                }

                // 6. Tomrer's own assist.
                if (!result.Snapped && opts.Assist && moving.Count == 1 && manual < 0
                    && Assist(index, moving, point, pos, rot, out var spot))
                {
                    result.Snapped = true;
                    result.Assisted = true;
                    result.SnapFrom = pos + (rot * spot.Local);
                    result.SnapTo = spot.To;
                    result.SnapPiece = spot.Piece;
                    pos = spot.Pos;
                }
            }

            result.Pos = pos;
            result.World = moving.World(pos, rot);
            result.Duplicate = Overlaps(moving, pos, rot, nearby);
            result.MovingSnaps = new Vector3[moving.Snaps.Count];
            for (var i = 0; i < moving.Snaps.Count; i++)
            {
                result.MovingSnaps[i] = pos + (rot * moving.Snaps[i].Local);
            }

            var snaps = new List<Vector3>();
            foreach (var piece in nearby)
            {
                snaps.AddRange(piece.Snaps);
            }

            result.NearbySnaps = snaps.ToArray();
            return true;
        }

        /// <summary>
        /// Step 6, Tomrer's own. Every pair of an own snap point and a snap point near the aimed
        /// point gives a spot. A spot counts when the piece there comes within half a metre of the
        /// aimed point, stays out of the ground and does not land on a piece of the same kind. The
        /// spot nearest the aimed point wins; between spots about as close, the one nearest to where
        /// the touch rule had put the piece.
        ///
        /// The game's own rule needs a snap point within half a metre, which from the editor's
        /// camera means hitting strips a few pixels high. This gives back the aim it costs.
        /// </summary>
        private static bool Assist(
            SceneIndex index, MovingSet moving, Vector3 point, Vector3 from, Quaternion rot, out AssistSpot best)
        {
            best = default(AssistSpot);
            var reach = moving.Reach + AssistReach;
            var spots = new List<AssistSpot>();

            foreach (var piece in index.Pieces)
            {
                foreach (var to in piece.Snaps)
                {
                    if (Vector3.Distance(to, point) > reach)
                    {
                        continue;
                    }

                    for (var i = 0; i < moving.Snaps.Count; i++)
                    {
                        var local = moving.Snaps[i].Local;
                        var pos = to - (rot * local);
                        var shapes = moving.Shapes(pos, rot);
                        var d = float.PositiveInfinity;
                        foreach (var shape in shapes)
                        {
                            d = Mathf.Min(d, Vector3.Distance(shape.ClosestPoint(point), point));
                        }

                        if (shapes.Length == 0)
                        {
                            d = Vector3.Distance(pos, point);
                        }

                        if (d > AssistReach)
                        {
                            continue;
                        }

                        spots.Add(new AssistSpot
                        {
                            Pos = pos,
                            Local = local,
                            To = to,
                            Piece = piece.Id,
                            Score = d + (0.05f * Vector3.Distance(pos, from)),
                        });
                    }
                }
            }

            spots.Sort((a, b) => a.Score.CompareTo(b.Score));
            foreach (var spot in spots)
            {
                var shapes = moving.Shapes(spot.Pos, rot);
                var low = spot.Pos.y;
                if (shapes.Length > 0)
                {
                    low = float.PositiveInfinity;
                    foreach (var shape in shapes)
                    {
                        low = Mathf.Min(low, shape.LowestY());
                    }
                }

                if (low < -AssistMaxDepth || Overlaps(moving, spot.Pos, rot, index.PiecesAt(spot.Pos, OverlapDistance)))
                {
                    continue;
                }

                best = spot;
                return true;
            }

            return false;
        }

        /// <summary>Player.IsOverlappingOtherPiece, run for every piece of the moving set.</summary>
        private static bool Overlaps(MovingSet moving, Vector3 pos, Quaternion rot, List<IndexedPiece> others)
        {
            if (others == null || others.Count == 0)
            {
                return false;
            }

            foreach (var placed in moving.World(pos, rot))
            {
                if (string.IsNullOrEmpty(placed.Prefab))
                {
                    continue;
                }

                var rotatedOk = placed.Entry != null && placed.Entry.AllowRotatedOverlap;
                foreach (var other in others)
                {
                    if (Vector3.Distance(placed.Pos, other.Pos) >= OverlapDistance
                        || string.IsNullOrEmpty(other.Prefab)
                        || !other.Prefab.StartsWith(placed.Prefab, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!rotatedOk || !(Quaternion.Angle(other.Rot, placed.Rot) > OverlapAngle))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private struct AssistSpot
        {
            public Vector3 Pos;

            /// <summary>The own snap point that was used, in pivot space.</summary>
            public Vector3 Local;

            public Vector3 To;
            public int Piece;
            public float Score;
        }
    }
}
