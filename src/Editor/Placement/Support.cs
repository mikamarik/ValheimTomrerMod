using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;

namespace ValheimTomrer.Editor.Placement
{
    /// <summary>
    /// How well every standing piece is held up, and which ones would break, worked out once for a
    /// scene. The pieces in hand are measured against it every time the aim moves.
    /// </summary>
    internal sealed class SupportMap
    {
        private readonly Dictionary<int, int> _byId = new Dictionary<int, int>();
        private readonly List<int> _fallen = new List<int>();

        internal SupportMap(Support.Body[] bodies, Dictionary<long, List<int>> grid, bool ground, Func<Vector3, float> groundAt)
        {
            Bodies = bodies;
            Grid = grid;
            Ground = ground;
            GroundAt = groundAt;
            for (var i = 0; i < bodies.Length; i++)
            {
                _byId[bodies[i].Id] = i;
                if (bodies[i].Gone)
                {
                    _fallen.Add(bodies[i].Id);
                }
            }
        }

        internal Support.Body[] Bodies { get; }

        internal Dictionary<long, List<int>> Grid { get; }

        internal bool Ground { get; }

        /// <summary>
        /// The ground height under a point, both in the model's space. Null: the editor's floor at
        /// y = 0. <see cref="Support.Evaluate"/> takes it from here, so the pieces in hand stand on
        /// the same ground as the scene.
        /// </summary>
        internal Func<Vector3, float> GroundAt { get; }

        /// <summary>Ids of the pieces that would break in the game, in the scene's order.</summary>
        public IReadOnlyList<int> Fallen => _fallen;

        /// <summary>
        /// The piece's support and its numbers. False for a piece the map does not know, or one
        /// with no WearNTear, which never falls.
        /// </summary>
        public bool TryGet(int id, out float value, out PieceSupport info)
        {
            value = 0f;
            info = null;
            if (!_byId.TryGetValue(id, out var i) || Bodies[i].Info == null)
            {
                return false;
            }

            value = Bodies[i].Gone ? 0f : Bodies[i].Value;
            info = Bodies[i].Info;
            return true;
        }

        public bool Falls(int id)
        {
            return _byId.TryGetValue(id, out var i) && Bodies[i].Gone;
        }

        /// <summary>The colour the game's hammer shows on the piece. Light blue for one it knows nothing of.</summary>
        public Color ColorOf(int id)
        {
            return TryGet(id, out var value, out var info) ? Support.ColorOf(value, info) : Support.GroundColor;
        }
    }

    /// <summary>
    /// The game's support rule (WearNTear.UpdateSupport), ported to plain C#, the same way
    /// <see cref="Placer"/> ports the placing rule: colliders are structs, nothing is instantiated
    /// and no physics call is made.
    ///
    /// What the game does, once a second for every piece:
    ///   1. grow each collider by 0.15 m on every side and look for what it touches;
    ///   2. the ground, or anything that is not a building piece, holds it fully: support = Max;
    ///   3. each piece it touches passes on its own support, minus a loss that grows with the
    ///      distance between the two centres: the horizontal loss, or less the more the touch is
    ///      straight under the centre, down to the vertical loss;
    ///   4. two touches under it on opposite sides (100 degrees or more apart, seen from above)
    ///      pass on the average of their straight-down values: a beam resting on two posts;
    ///   5. the best of these is its support. Under Min, it breaks.
    ///
    /// The game gets there by repeating that once a second until nothing changes. Here the same
    /// end state is reached directly: every piece starts at nothing, the grounded ones at Max, and
    /// the values are passed on until they stop growing. The ones under Min break, the rest is
    /// worked out again without them, until nothing more breaks.
    ///
    /// Two shortcuts, both the same ones the placing rule takes: a mesh collider is its box, and
    /// the ground is the editor's floor at y = 0. A build in the world passes the terrain's height
    /// instead (the groundAt callback), measured under the lowest point of each collider.
    /// </summary>
    internal static class Support
    {
        /// <summary>WearNTear.SetupColliders adds 0.3 m to every collider's size before looking.</summary>
        public const float Reach = 0.15f;

        /// <summary>Two touches this far apart, seen from above, hold a piece between them (c_ComMinAngle).</summary>
        public const float PairAngle = 100f;

        /// <summary>The colour of a piece on the ground, straight from WearNTear.Highlight.</summary>
        public static readonly Color GroundColor = new Color(0.6f, 0.8f, 1f);

        private const float CellSize = 2f;
        private const float Settled = 1e-4f;

        /// <summary>
        /// The colour the game's hammer puts on a piece (WearNTear.Highlight): light blue on the
        /// ground, then green, yellow, orange and red as the support runs down to the minimum.
        /// </summary>
        public static Color ColorOf(float value, PieceSupport info)
        {
            if (info == null || value >= info.Max)
            {
                return GroundColor;
            }

            var level = Level(value, info);
            var color = Color.Lerp(new Color(1f, 0f, 0f), new Color(0f, 1f, 0f), level);
            Color.RGBToHSV(color, out var hue, out _, out _);
            return Color.HSVToRGB(hue, Mathf.Lerp(1f, 0.5f, level), Mathf.Lerp(1.2f, 0.9f, level));
        }

        /// <summary>
        /// 0 at the minimum, 1 at half the maximum and above: where the colour sits between red and
        /// green (WearNTear.GetSupportColorValue).
        /// </summary>
        public static float Level(float value, PieceSupport info)
        {
            return info == null ? 1f : Mathf.Clamp01((value - info.Min) / ((info.Max * 0.5f) - info.Min));
        }

        /// <summary>Every piece that stays put while something is placed, and which of them would break.</summary>
        public static SupportMap Solve(SceneIndex index)
        {
            var bodies = new List<Body>();
            if (index != null)
            {
                foreach (var piece in index.Pieces)
                {
                    Add(bodies, Build(piece.Id, piece.Entry, piece.Pos, piece.Rot));
                }
            }

            return Solve(bodies, index == null || index.HasGround, null);
        }

        /// <summary>Every piece of a list, standing on the editor's floor, and which of them would break.</summary>
        public static SupportMap Solve(IEnumerable<ScenePiece> pieces)
        {
            return Solve(pieces, null);
        }

        /// <summary>
        /// Every piece of a list, standing on the ground <paramref name="groundAt"/> gives (a point
        /// in the model's space to the ground height there, in the same space), and which of them
        /// would break. Null is the editor's floor at y = 0.
        /// </summary>
        public static SupportMap Solve(IEnumerable<ScenePiece> pieces, Func<Vector3, float> groundAt)
        {
            var bodies = new List<Body>();
            foreach (var piece in pieces)
            {
                Add(bodies, Build(piece.Id, piece.Entry, piece.Pos, piece.Rot));
            }

            return Solve(bodies, true, groundAt);
        }

        private static void Add(List<Body> bodies, Body body)
        {
            if (body != null)
            {
                bodies.Add(body);
            }
        }

        private static SupportMap Solve(List<Body> bodies, bool ground, Func<Vector3, float> groundAt)
        {
            var nodes = bodies.ToArray();
            var grid = new Dictionary<long, List<int>>();
            for (var i = 0; i < nodes.Length; i++)
            {
                AddToGrid(grid, nodes[i].FoundBounds, i);
            }

            var candidates = new List<int>();
            var seen = new HashSet<int>();
            for (var i = 0; i < nodes.Length; i++)
            {
                Near(grid, nodes[i].BoxBounds, candidates, seen);
                Connect(nodes, i, candidates, ground, groundAt);
            }

            Relax(nodes, 0);
            return new SupportMap(nodes, grid, ground, groundAt);
        }

        /// <summary>
        /// Where the pieces in hand would land: the support of each and whether it would break,
        /// against a scene that is already solved. The scene's own values do not change; the pieces
        /// in hand hold each other up as well. True when any of them would break.
        ///
        /// Pass a real map: with none the ground is the editor's floor, whatever the caller solved on.
        /// </summary>
        public static bool Evaluate(SupportMap map, IList<PlacedPiece> placed, float[] values, bool[] falls)
        {
            var baseCount = map != null ? map.Bodies.Length : 0;
            var nodes = new Body[baseCount + placed.Count];
            if (map != null)
            {
                Array.Copy(map.Bodies, nodes, baseCount);
            }

            var moving = new List<int>(placed.Count);
            for (var k = 0; k < placed.Count; k++)
            {
                var body = Build(placed[k].Id, placed[k].Entry, placed[k].Pos, placed[k].Rot) ?? Empty(placed[k].Id);
                nodes[baseCount + k] = body;
                moving.Add(baseCount + k);
            }

            var ground = map == null || map.Ground;
            var groundAt = map?.GroundAt;
            var candidates = new List<int>();
            var seen = new HashSet<int>();
            for (var k = 0; k < placed.Count; k++)
            {
                var i = baseCount + k;
                if (map != null)
                {
                    Near(map.Grid, nodes[i].BoxBounds, candidates, seen);
                }
                else
                {
                    candidates.Clear();
                }

                foreach (var other in moving)
                {
                    if (other != i && nodes[i].BoxBounds.Intersects(nodes[other].FoundBounds))
                    {
                        candidates.Add(other);
                    }
                }

                Connect(nodes, i, candidates, ground, groundAt);
            }

            Relax(nodes, baseCount);
            var any = false;
            for (var k = 0; k < placed.Count; k++)
            {
                var body = nodes[baseCount + k];
                values[k] = body.Info == null ? float.PositiveInfinity : body.Gone ? 0f : body.Value;
                falls[k] = body.Gone;
                any |= body.Gone;
            }

            return any;
        }

        // ---------- one piece ----------

        /// <summary>One piece as the rule sees it, in the scene's space.</summary>
        internal sealed class Body
        {
            public int Id;

            /// <summary>Null: no WearNTear. It never falls and whatever touches it stands as on rock.</summary>
            public PieceSupport Info;

            public Vector3 Pos;
            public Vector3 Com;

            /// <summary>Its own colliders grown by <see cref="Reach"/>, what it looks for neighbours with.</summary>
            public PlaceShape[] Boxes;

            public Bounds[] BoxAabbs;

            /// <summary>What a neighbour's boxes find on it.</summary>
            public PlaceShape[] Found;

            /// <summary>Per found shape: a concave mesh, which the game measures by a ray down.</summary>
            public bool[] Concave;

            public Bounds BoxBounds;
            public Bounds FoundBounds;

            // Worked out by Connect and Relax.
            public bool Fixed;
            public bool Gone;
            public float Value;
            public List<Link> Links = new List<Link>();
            public List<Pair> Pairs = new List<Pair>();
        }

        /// <summary>A neighbour passes on its support times this factor.</summary>
        internal struct Link
        {
            public int To;
            public float Factor;
        }

        /// <summary>Two touches on opposite sides: the average of both, each times its own factor.</summary>
        internal struct Pair
        {
            public int A;
            public float FactorA;
            public int B;
            public float FactorB;
        }

        private struct Touch
        {
            public int Body;
            public float Factor;
            public Vector3 Flat;
        }

        private static Body Build(int id, PieceEntry entry, Vector3 pos, Quaternion rot)
        {
            if (entry == null || entry.BodyColliders == null || entry.SupportColliders == null)
            {
                return null;
            }

            var info = entry.Support;
            var body = new Body
            {
                Id = id,
                Info = info,
                Pos = pos,
                Com = pos + (rot * (info != null ? info.ComOffset : Vector3.zero)),
                Boxes = new PlaceShape[info != null ? entry.BodyColliders.Length : 0],
                Found = new PlaceShape[entry.SupportColliders.Length],
                Concave = new bool[entry.SupportColliders.Length],
            };

            body.BoxAabbs = new Bounds[body.Boxes.Length];
            for (var i = 0; i < body.Boxes.Length; i++)
            {
                var collider = entry.BodyColliders[i];
                var shape = PlaceShape.Of(collider, pos, rot);
                if (collider.Kind == ColliderKind.Box)
                {
                    shape.Half += Vector3.one * Reach;
                }
                else
                {
                    // Any other kind is looked for with its box in world axes (collider.bounds).
                    var world = Aabb(shape);
                    shape = new PlaceShape
                    {
                        Kind = ShapeKind.Box,
                        Center = world.center,
                        Rotation = Quaternion.identity,
                        Half = world.extents + (Vector3.one * Reach),
                    };
                }

                shape.P0 = shape.Center;
                shape.P1 = shape.Center;
                body.Boxes[i] = shape;
                body.BoxAabbs[i] = Aabb(shape);
                body.BoxBounds = i == 0 ? body.BoxAabbs[i] : Grow(body.BoxBounds, body.BoxAabbs[i]);
            }

            for (var i = 0; i < body.Found.Length; i++)
            {
                var collider = entry.SupportColliders[i];
                body.Found[i] = PlaceShape.Of(collider, pos, rot);
                body.Concave[i] = collider.Kind == ColliderKind.Mesh && !collider.Convex;
                var aabb = Aabb(body.Found[i]);
                body.FoundBounds = i == 0 ? aabb : Grow(body.FoundBounds, aabb);
            }

            if (body.Boxes.Length == 0)
            {
                body.BoxBounds = new Bounds(pos, Vector3.zero);
            }

            if (body.Found.Length == 0)
            {
                body.FoundBounds = new Bounds(pos, Vector3.zero);
            }

            return body;
        }

        /// <summary>A piece the game does not have. It takes part in nothing.</summary>
        private static Body Empty(int id)
        {
            return new Body
            {
                Id = id,
                Boxes = Array.Empty<PlaceShape>(),
                BoxAabbs = Array.Empty<Bounds>(),
                Found = Array.Empty<PlaceShape>(),
                Concave = Array.Empty<bool>(),
                BoxBounds = new Bounds(Vector3.zero, Vector3.zero),
                FoundBounds = new Bounds(Vector3.zero, Vector3.zero),
            };
        }

        /// <summary>
        /// Steps 1 to 4 for one piece, turned into numbers: which neighbours pass on how much of
        /// their support. The values themselves come later, in <see cref="Relax"/>.
        /// </summary>
        private static void Connect(Body[] nodes, int i, List<int> candidates, bool ground, Func<Vector3, float> groundAt)
        {
            var body = nodes[i];
            body.Links.Clear();
            body.Pairs.Clear();
            body.Fixed = false;
            body.Gone = false;
            var info = body.Info;
            if (info == null)
            {
                return;
            }

            // A piece that never checks keeps the Max it was born with (WearNTear.Awake).
            if (!info.CanFall)
            {
                body.Fixed = true;
                return;
            }

            if (ground)
            {
                foreach (var box in body.Boxes)
                {
                    if (OnGround(box, groundAt))
                    {
                        body.Fixed = true;
                        return;
                    }
                }
            }

            var best = new Dictionary<int, float>();
            var touches = new List<Touch>();
            var com = body.Com;
            foreach (var j in candidates)
            {
                if (j == i)
                {
                    continue;
                }

                var other = nodes[j];
                if (!body.BoxBounds.Intersects(other.FoundBounds))
                {
                    continue;
                }

                for (var b = 0; b < body.Boxes.Length; b++)
                {
                    if (!body.BoxAabbs[b].Intersects(other.FoundBounds))
                    {
                        continue;
                    }

                    for (var k = 0; k < other.Found.Length; k++)
                    {
                        if (!Overlaps(body.Boxes[b], other.Found[k]))
                        {
                            continue;
                        }

                        // Not a building piece: it holds like the ground does.
                        if (other.Info == null)
                        {
                            body.Fixed = true;
                            body.Links.Clear();
                            body.Pairs.Clear();
                            return;
                        }

                        if (!other.Info.Holds)
                        {
                            continue;
                        }

                        var d = Vector3.Distance(com, other.Com) + 0.1f;
                        var toPivot = Vector3.Distance(com, other.Pos) + 0.1f;
                        if (toPivot < d && !info.ExactCom)
                        {
                            d = toPivot;
                        }

                        var factor = 1f - (info.HorizontalLoss * d);
                        var point = SupportPoint(com, other, k);
                        if (point.y < com.y + 0.05f)
                        {
                            var down = (point - com).normalized;
                            if (down.y < 0f)
                            {
                                var t = Mathf.Acos(1f - Mathf.Abs(down.y)) / (Mathf.PI / 2f);
                                factor = Mathf.Max(factor, 1f - (Mathf.Lerp(info.HorizontalLoss, info.VerticalLoss, t) * d));
                            }

                            touches.Add(new Touch
                            {
                                Body = j,
                                Factor = 1f - (info.VerticalLoss * d),
                                Flat = new Vector3(point.x - com.x, 0f, point.z - com.z),
                            });
                        }

                        if (factor > 0f && (!best.TryGetValue(j, out var had) || factor > had))
                        {
                            best[j] = factor;
                        }
                    }
                }
            }

            foreach (var pair in best)
            {
                body.Links.Add(new Link { To = pair.Key, Factor = pair.Value });
            }

            for (var a = 0; a < touches.Count - 1; a++)
            {
                for (var b = a + 1; b < touches.Count; b++)
                {
                    if ((touches[a].Factor > 0f || touches[b].Factor > 0f)
                        && Vector3.Angle(touches[a].Flat, touches[b].Flat) >= PairAngle)
                    {
                        body.Pairs.Add(new Pair
                        {
                            A = touches[a].Body,
                            FactorA = touches[a].Factor,
                            B = touches[b].Body,
                            FactorB = touches[b].Factor,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// The grown box reaches the ground. With a ground callback: its lowest point is at or under
        /// the ground right there. On a slope a box can touch higher ground at another corner; this
        /// then says no where the game says yes, which is the careful side.
        /// </summary>
        private static bool OnGround(PlaceShape box, Func<Vector3, float> groundAt)
        {
            if (groundAt == null)
            {
                return box.LowestY() <= 0f;
            }

            var low = box.LowestPoint();
            return low.y <= groundAt(low);
        }

        /// <summary>
        /// Where a neighbour holds the piece (WearNTear.FindSupportPoint): the neighbour's point
        /// nearest to the centre of mass, or for a concave mesh, the point straight under it.
        /// </summary>
        private static Vector3 SupportPoint(Vector3 com, Body other, int k)
        {
            var shape = other.Found[k];
            if (!other.Concave[k])
            {
                return shape.ClosestPoint(com);
            }

            if (shape.RayHit(com, Vector3.down, out var hit) && hit.T <= 10f)
            {
                return hit.Point;
            }

            return (com + other.Com) * 0.5f;
        }

        /// <summary>
        /// Step 5, for the nodes from <paramref name="from"/> on; the ones before are already solved
        /// and stay as they are. Values start at nothing and are passed on until they settle, the
        /// pieces under their minimum break, and the rest is worked out again without them.
        /// </summary>
        private static void Relax(Body[] nodes, int from)
        {
            var count = nodes.Length - from;
            if (count <= 0)
            {
                return;
            }

            // Who reads whom, so a change is only passed to the pieces it can move.
            var readers = new List<int>[count];
            for (var i = from; i < nodes.Length; i++)
            {
                foreach (var link in nodes[i].Links)
                {
                    AddReader(readers, from, link.To, i);
                }

                foreach (var pair in nodes[i].Pairs)
                {
                    AddReader(readers, from, pair.A, i);
                    AddReader(readers, from, pair.B, i);
                }
            }

            var queue = new Queue<int>();
            var queued = new bool[count];
            while (true)
            {
                for (var i = from; i < nodes.Length; i++)
                {
                    var body = nodes[i];
                    if (body.Info == null || body.Gone)
                    {
                        continue;
                    }

                    body.Value = body.Fixed ? body.Info.Max : 0f;
                    if (!body.Fixed)
                    {
                        queue.Enqueue(i);
                        queued[i - from] = true;
                    }
                }

                while (queue.Count > 0)
                {
                    var i = queue.Dequeue();
                    queued[i - from] = false;
                    var body = nodes[i];
                    var value = ValueOf(nodes, body);
                    if (value <= body.Value + (Settled * body.Info.Max))
                    {
                        continue;
                    }

                    body.Value = value;
                    var list = readers[i - from];
                    if (list == null)
                    {
                        continue;
                    }

                    foreach (var reader in list)
                    {
                        if (!queued[reader - from] && !nodes[reader].Fixed && !nodes[reader].Gone)
                        {
                            queue.Enqueue(reader);
                            queued[reader - from] = true;
                        }
                    }
                }

                var broke = false;
                for (var i = from; i < nodes.Length; i++)
                {
                    var body = nodes[i];
                    if (body.Info != null && !body.Gone && !body.Fixed && body.Value < body.Info.Min)
                    {
                        body.Gone = true;
                        broke = true;
                    }
                }

                if (!broke)
                {
                    return;
                }
            }
        }

        private static float ValueOf(Body[] nodes, Body body)
        {
            var value = 0f;
            foreach (var link in body.Links)
            {
                var other = nodes[link.To];
                if (!other.Gone)
                {
                    value = Mathf.Max(value, link.Factor * other.Value);
                }
            }

            foreach (var pair in body.Pairs)
            {
                var a = nodes[pair.A];
                var b = nodes[pair.B];
                if (!a.Gone && !b.Gone)
                {
                    value = Mathf.Max(value, ((pair.FactorA * a.Value) + (pair.FactorB * b.Value)) * 0.5f);
                }
            }

            return Mathf.Min(value, body.Info.Max);
        }

        private static void AddReader(List<int>[] readers, int from, int read, int reader)
        {
            if (read < from)
            {
                return;   // already solved, it never changes
            }

            var list = readers[read - from];
            if (list == null)
            {
                list = new List<int>();
                readers[read - from] = list;
            }

            list.Add(reader);
        }

        // ---------- shapes ----------

        /// <summary>Physics.OverlapBox for one grown box against one collider. Touching counts.</summary>
        private static bool Overlaps(PlaceShape box, PlaceShape other)
        {
            switch (other.Kind)
            {
                case ShapeKind.Sphere:
                    return (box.ClosestPoint(other.Center) - other.Center).sqrMagnitude <= other.Radius * other.Radius;

                case ShapeKind.Capsule:
                    return SegmentDistance(box, other.P0, other.P1) <= other.Radius;

                default:
                    return BoxBox(box, other);
            }
        }

        /// <summary>Two turned boxes, by the separating axis test: the 3 + 3 faces and the 9 edge pairs.</summary>
        private static bool BoxBox(PlaceShape a, PlaceShape b)
        {
            var ax = a.Rotation * Vector3.right;
            var ay = a.Rotation * Vector3.up;
            var az = a.Rotation * Vector3.forward;
            var bx = b.Rotation * Vector3.right;
            var by = b.Rotation * Vector3.up;
            var bz = b.Rotation * Vector3.forward;
            var t = b.Center - a.Center;

            return !Apart(ax, t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(ay, t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(az, t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(bx, t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(by, t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(bz, t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(ax, bx), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(ax, by), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(ax, bz), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(ay, bx), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(ay, by), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(ay, bz), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(az, bx), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(az, by), t, a, ax, ay, az, b, bx, by, bz)
                && !Apart(Vector3.Cross(az, bz), t, a, ax, ay, az, b, bx, by, bz);
        }

        private static bool Apart(
            Vector3 axis, Vector3 t,
            PlaceShape a, Vector3 ax, Vector3 ay, Vector3 az,
            PlaceShape b, Vector3 bx, Vector3 by, Vector3 bz)
        {
            if (axis.sqrMagnitude < 1e-6f)
            {
                return false;   // two parallel edges: the face axes already cover it
            }

            var ra = (a.Half.x * Mathf.Abs(Vector3.Dot(ax, axis)))
                + (a.Half.y * Mathf.Abs(Vector3.Dot(ay, axis)))
                + (a.Half.z * Mathf.Abs(Vector3.Dot(az, axis)));
            var rb = (b.Half.x * Mathf.Abs(Vector3.Dot(bx, axis)))
                + (b.Half.y * Mathf.Abs(Vector3.Dot(by, axis)))
                + (b.Half.z * Mathf.Abs(Vector3.Dot(bz, axis)));
            return Mathf.Abs(Vector3.Dot(t, axis)) > ra + rb;
        }

        /// <summary>The shortest distance from a box to a segment. It is convex along the segment, so a search finds it.</summary>
        private static float SegmentDistance(PlaceShape box, Vector3 p0, Vector3 p1)
        {
            float At(float s)
            {
                var p = Vector3.Lerp(p0, p1, s);
                return Vector3.Distance(box.ClosestPoint(p), p);
            }

            var lo = 0f;
            var hi = 1f;
            for (var step = 0; step < 40; step++)
            {
                var m1 = lo + ((hi - lo) / 3f);
                var m2 = hi - ((hi - lo) / 3f);
                if (At(m1) <= At(m2))
                {
                    hi = m2;
                }
                else
                {
                    lo = m1;
                }
            }

            return Mathf.Min(At((lo + hi) * 0.5f), Mathf.Min(At(0f), At(1f)));
        }

        /// <summary>The box in world axes around a shape (collider.bounds).</summary>
        private static Bounds Aabb(PlaceShape shape)
        {
            switch (shape.Kind)
            {
                case ShapeKind.Sphere:
                    return new Bounds(shape.Center, Vector3.one * (shape.Radius * 2f));

                case ShapeKind.Capsule:
                {
                    var box = new Bounds(shape.P0, Vector3.zero);
                    box.Encapsulate(shape.P1);
                    box.Expand(shape.Radius * 2f);
                    return box;
                }

                default:
                {
                    var r = shape.Rotation;
                    var extent =
                        Abs(r * new Vector3(shape.Half.x, 0f, 0f))
                        + Abs(r * new Vector3(0f, shape.Half.y, 0f))
                        + Abs(r * new Vector3(0f, 0f, shape.Half.z));
                    return new Bounds(shape.Center, extent * 2f);
                }
            }
        }

        private static Bounds Grow(Bounds box, Bounds more)
        {
            box.Encapsulate(more);
            return box;
        }

        private static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }

        // ---------- the grid ----------

        private static void AddToGrid(Dictionary<long, List<int>> grid, Bounds box, int index)
        {
            var min = Cell(box.min);
            var max = Cell(box.max);
            for (var x = min.x; x <= max.x; x++)
            {
                for (var y = min.y; y <= max.y; y++)
                {
                    for (var z = min.z; z <= max.z; z++)
                    {
                        var key = Key(x, y, z);
                        if (!grid.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            grid[key] = list;
                        }

                        list.Add(index);
                    }
                }
            }
        }

        /// <summary>Every body whose found box shares a cell with the given box, once each.</summary>
        private static void Near(Dictionary<long, List<int>> grid, Bounds box, List<int> found, HashSet<int> seen)
        {
            found.Clear();
            seen.Clear();
            var min = Cell(box.min);
            var max = Cell(box.max);
            for (var x = min.x; x <= max.x; x++)
            {
                for (var y = min.y; y <= max.y; y++)
                {
                    for (var z = min.z; z <= max.z; z++)
                    {
                        if (!grid.TryGetValue(Key(x, y, z), out var list))
                        {
                            continue;
                        }

                        foreach (var index in list)
                        {
                            if (seen.Add(index))
                            {
                                found.Add(index);
                            }
                        }
                    }
                }
            }
        }

        private static Vector3Int Cell(Vector3 p)
        {
            return new Vector3Int(
                Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
        }

        private static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }
    }
}
