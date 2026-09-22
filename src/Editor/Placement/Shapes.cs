using UnityEngine;
using ValheimTomrer.Editor.Catalog;

namespace ValheimTomrer.Editor.Placement
{
    /// <summary>What a collider turns into once it is in world space.</summary>
    internal enum ShapeKind
    {
        Box,
        Sphere,
        Capsule,
    }

    /// <summary>Where a ray met a shape.</summary>
    internal struct ShapeHit
    {
        /// <summary>Distance along the ray, in metres. The direction must have length 1.</summary>
        public float T;

        public Vector3 Point;
        public Vector3 Normal;
    }

    /// <summary>
    /// One collider of a piece, placed in world space. A mesh collider becomes its box: the game
    /// asks the physics engine for the real hull, which we have no way to rebuild off a prefab.
    ///
    /// Everything here is plain maths on structs. Nothing is instantiated and no physics call is
    /// made, so it runs headlessly and does not care about the scene.
    /// </summary>
    internal struct PlaceShape
    {
        public ShapeKind Kind;

        /// <summary>Middle of the box or the sphere. For a capsule, the middle of its segment.</summary>
        public Vector3 Center;

        public Quaternion Rotation;

        /// <summary>Half the box size. Filled in for every kind, so a rough test needs no cases.</summary>
        public Vector3 Half;

        public float Radius;

        /// <summary>Ends of the capsule's inner segment. Both are the centre for the other kinds.</summary>
        public Vector3 P0;

        public Vector3 P1;

        /// <summary>The same collider, moved and turned with the piece that carries it.</summary>
        public static PlaceShape Of(PieceCollider col, Vector3 pos, Quaternion rot)
        {
            var shape = new PlaceShape
            {
                Center = pos + (rot * col.Center),
                Rotation = rot * col.Rotation,
                Half = col.Size * 0.5f,
            };

            switch (col.Kind)
            {
                case ColliderKind.Sphere:
                    shape.Kind = ShapeKind.Sphere;
                    shape.Radius = col.Radius > 0f ? col.Radius : shape.Half.x;
                    shape.P0 = shape.Center;
                    shape.P1 = shape.Center;
                    break;

                case ColliderKind.Capsule:
                    shape.Kind = ShapeKind.Capsule;
                    shape.Radius = col.Radius;
                    shape.P0 = pos + (rot * col.P0);
                    shape.P1 = pos + (rot * col.P1);
                    break;

                default:
                    shape.Kind = ShapeKind.Box;
                    shape.P0 = shape.Center;
                    shape.P1 = shape.Center;
                    break;
            }

            return shape;
        }

        /// <summary>Collider.ClosestPoint: the point of the shape nearest to p, or p when p is inside.</summary>
        public Vector3 ClosestPoint(Vector3 p)
        {
            if (Kind == ShapeKind.Box)
            {
                var local = Quaternion.Inverse(Rotation) * (p - Center);
                var clamped = new Vector3(
                    Mathf.Clamp(local.x, -Half.x, Half.x),
                    Mathf.Clamp(local.y, -Half.y, Half.y),
                    Mathf.Clamp(local.z, -Half.z, Half.z));
                return Center + (Rotation * clamped);
            }

            var center = Kind == ShapeKind.Sphere ? Center : ClosestOnSegment(P0, P1, p);
            var away = p - center;
            var length = away.magnitude;
            return length <= Radius ? p : center + (away * (Radius / length));
        }

        /// <summary>Height of the shape's lowest point.</summary>
        public float LowestY()
        {
            if (Kind == ShapeKind.Sphere)
            {
                return Center.y - Radius;
            }

            if (Kind == ShapeKind.Capsule)
            {
                return Mathf.Min(P0.y, P1.y) - Radius;
            }

            return Center.y
                - (Mathf.Abs((Rotation * Vector3.right).y) * Half.x)
                - (Mathf.Abs((Rotation * Vector3.up).y) * Half.y)
                - (Mathf.Abs((Rotation * Vector3.forward).y) * Half.z);
        }

        /// <summary>
        /// A point at the height of <see cref="LowestY"/>. Where a whole face or edge is lowest, the
        /// middle of it, so a flat box gives the middle of its bottom face.
        /// </summary>
        public Vector3 LowestPoint()
        {
            if (Kind == ShapeKind.Sphere)
            {
                return Center + (Vector3.down * Radius);
            }

            if (Kind == ShapeKind.Capsule)
            {
                var low = P0.y < P1.y ? P0 : P1.y < P0.y ? P1 : (P0 + P1) * 0.5f;
                return low + (Vector3.down * Radius);
            }

            return Center
                - Down(Rotation * Vector3.right, Half.x)
                - Down(Rotation * Vector3.up, Half.y)
                - Down(Rotation * Vector3.forward, Half.z);
        }

        /// <summary>Half an axis, pointing up, or nothing when the axis is level.</summary>
        private static Vector3 Down(Vector3 axis, float half)
        {
            return Mathf.Abs(axis.y) < 1e-5f ? Vector3.zero : axis * (Mathf.Sign(axis.y) * half);
        }

        /// <summary>
        /// Ray against this shape. The direction must have length 1. A ray that starts inside the
        /// shape does not hit it, the same as Unity's own raycast.
        /// </summary>
        public bool RayHit(Vector3 origin, Vector3 dir, out ShapeHit hit)
        {
            hit = default(ShapeHit);
            return Kind == ShapeKind.Box ? RayBox(origin, dir, out hit) : RayRound(origin, dir, out hit);
        }

        /// <summary>Point of the segment a-b nearest to p.</summary>
        public static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            var square = Vector3.Dot(ab, ab);
            var t = square > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / square) : 0f;
            return a + (ab * t);
        }

        private bool RayBox(Vector3 origin, Vector3 dir, out ShapeHit hit)
        {
            hit = default(ShapeHit);
            var inverse = Quaternion.Inverse(Rotation);
            var localOrigin = inverse * (origin - Center);
            var localDir = inverse * dir;
            var near = float.NegativeInfinity;
            var far = float.PositiveInfinity;
            var axis = -1;
            var sign = 0f;

            for (var i = 0; i < 3; i++)
            {
                var half = Half[i];
                if (Mathf.Abs(localDir[i]) < 1e-12f)
                {
                    if (localOrigin[i] < -half || localOrigin[i] > half)
                    {
                        return false;
                    }

                    continue;
                }

                var enter = (-half - localOrigin[i]) / localDir[i];
                var leave = (half - localOrigin[i]) / localDir[i];
                var face = -1f;
                if (enter > leave)
                {
                    var swap = enter;
                    enter = leave;
                    leave = swap;
                    face = 1f;
                }

                if (enter > near)
                {
                    near = enter;
                    axis = i;
                    sign = face;
                }

                if (leave < far)
                {
                    far = leave;
                }

                if (near > far)
                {
                    return false;
                }
            }

            if (near < 0f || axis < 0)
            {
                return false;
            }

            var normal = Vector3.zero;
            normal[axis] = sign;
            hit = new ShapeHit { T = near, Point = origin + (dir * near), Normal = Rotation * normal };
            return true;
        }

        /// <summary>A sphere, or a capsule: the nearest of its two end spheres and its body.</summary>
        private bool RayRound(Vector3 origin, Vector3 dir, out ShapeHit hit)
        {
            hit = default(ShapeHit);
            var found = false;
            RaySphere(origin, dir, P0, ref hit, ref found);

            if (Kind != ShapeKind.Capsule)
            {
                return found;
            }

            RaySphere(origin, dir, P1, ref hit, ref found);

            var along = P1 - P0;
            var toStart = origin - P0;
            var alongSquare = Vector3.Dot(along, along);
            var alongDir = Vector3.Dot(along, dir);
            var alongStart = Vector3.Dot(along, toStart);
            var a = alongSquare - (alongDir * alongDir);
            if (alongSquare <= 1e-12f || a <= 1e-12f)
            {
                return found;
            }

            var b = (alongSquare * Vector3.Dot(dir, toStart)) - (alongStart * alongDir);
            var c = (alongSquare * Vector3.Dot(toStart, toStart)) - (alongStart * alongStart) - (Radius * Radius * alongSquare);
            var disc = (b * b) - (a * c);
            if (disc < 0f || c <= 0f)
            {
                return found;
            }

            var t = (-b - Mathf.Sqrt(disc)) / a;
            var height = alongStart + (t * alongDir);
            if (t < 0f || height <= 0f || height >= alongSquare || (found && t >= hit.T))
            {
                return found;
            }

            var point = origin + (dir * t);
            hit = new ShapeHit { T = t, Point = point, Normal = (point - ClosestOnSegment(P0, P1, point)).normalized };
            return true;
        }

        private void RaySphere(Vector3 origin, Vector3 dir, Vector3 center, ref ShapeHit hit, ref bool found)
        {
            var toCenter = origin - center;
            var b = Vector3.Dot(toCenter, dir);
            var c = Vector3.Dot(toCenter, toCenter) - (Radius * Radius);
            if (c < 0f)
            {
                return; // starts inside
            }

            var disc = (b * b) - c;
            if (disc < 0f)
            {
                return;
            }

            var t = -b - Mathf.Sqrt(disc);
            if (t < 0f || (found && t >= hit.T))
            {
                return;
            }

            var point = origin + (dir * t);
            hit = new ShapeHit { T = t, Point = point, Normal = (point - center).normalized };
            found = true;
        }
    }
}
