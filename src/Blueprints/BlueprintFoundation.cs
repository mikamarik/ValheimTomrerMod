using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Placement;

namespace ValheimTomrer.Blueprints
{
    /// <summary>The lowest solid layer, measured in blueprint space, independently of roof bounds.</summary>
    internal sealed class BlueprintFoundation
    {
        public Bounds Bounds { get; private set; }
        public Vector3[] Anchors { get; private set; }
        public static readonly string[] Names = { "Bottom center", "Front left", "Front right", "Back right", "Back left" };

        public static BlueprintFoundation Measure(ResolvedBlueprint blueprint, BlueprintPreview preview = null)
        {
            PieceCatalog.Ensure();
            var boxes = new List<Bounds>();
            var placementMask = LayerMask.GetMask(PieceEntry.PlaceRayLayers);
            for (var index = 0; index < blueprint.Parts.Count; index++)
            {
                var part = blueprint.Parts[index];
                var entry = PieceCatalog.Find(part.Source.PrefabName);
                var own = entry != null ? entry.Bounds : BlueprintPreview.OwnBounds(part.Prefab);
                var visual = new Bounds();
                var visualFirst = true;
                Encapsulate(ref visual, ref visualFirst, part.Source.Position + part.Source.Rotation * own.center,
                    own.size, part.Source.Rotation);
                if (preview != null && TryVisualBounds(preview, index, out var drawn)) visual = drawn;
                var first = true;
                var box = new Bounds();
                if (entry?.BodyColliders != null)
                {
                    foreach (var collider in entry.BodyColliders)
                    {
                        if (!collider.Active || !collider.Placed || collider.Trigger || collider.OnRigidbody) continue;
                        // BodyColliders also includes smoke/range/other utility volumes. Those
                        // are useful to the native support code but are not the foundation.
                        if ((placementMask & (1 << collider.Layer)) == 0) continue;
                        var rotation = part.Source.Rotation * collider.Rotation;
                        var center = part.Source.Position + part.Source.Rotation * collider.Center;
                        var candidate = new Bounds();
                        var candidateFirst = true;
                        Encapsulate(ref candidate, ref candidateFirst, center, collider.Size, rotation);
                        if (!FitsModel(candidate, visual)) continue;
                        if (first) { box = candidate; first = false; }
                        else box.Encapsulate(candidate);
                    }
                }
                if (first)
                {
                    box = visual;
                }
                boxes.Add(box);
            }
            var foundation = FromBoxes(boxes);
            // Prefer a real foundation-piece snap at each corner when it coincides in X/Z.
            // Renderer/collider undersides can be a few centimetres below the native snap
            // plane; using that underside to join two floors would leave a visible step.
            for (var anchor = 1; anchor < foundation.Anchors.Length; anchor++)
            {
                var wanted = foundation.Anchors[anchor];
                var best = 0.12f * 0.12f;
                for (var i = 0; i < blueprint.Parts.Count; i++)
                {
                    if (boxes[i].min.y > foundation.Bounds.min.y + 0.25f) continue;
                    var part = blueprint.Parts[i];
                    var entry = PieceCatalog.Find(part.Source.PrefabName);
                    if (entry?.SnapPoints == null) continue;
                    foreach (var snap in entry.SnapPoints)
                    {
                        var point = part.Source.Position + part.Source.Rotation * snap;
                        if (Mathf.Abs(point.y - foundation.Bounds.min.y) > 0.5f) continue;
                        var dx = point.x - wanted.x;
                        var dz = point.z - wanted.z;
                        var distance = dx * dx + dz * dz;
                        if (distance < best) { best = distance; foundation.Anchors[anchor] = point; }
                    }
                }
            }
            return foundation;
        }

        /// <summary>A placement body may be slightly larger than its mesh, never a remote utility volume.</summary>
        internal static bool FitsModel(Bounds body, Bounds visual)
        {
            var allowed = visual;
            allowed.Expand(1f); // Half a metre on each side for native collision skins.
            return allowed.Contains(body.min) && allowed.Contains(body.max);
        }

        private static bool TryVisualBounds(BlueprintPreview preview, int index, out Bounds bounds)
        {
            bounds = new Bounds();
            var piece = preview.Part(index);
            if (piece == null) return false;
            var first = true;
            foreach (var renderer in piece.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                var world = renderer.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var offset = Vector3.Scale(world.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                    var point = preview.Root.InverseTransformPoint(world.center + offset);
                    if (first) { bounds = new Bounds(point, Vector3.zero); first = false; }
                    else bounds.Encapsulate(point);
                }
            }
            return !first;
        }

        internal static BlueprintFoundation FromBoxes(IList<Bounds> boxes)
        {
            var low = float.PositiveInfinity;
            foreach (var box in boxes) low = Mathf.Min(low, box.min.y);
            var first = true;
            var footprint = new Bounds();
            foreach (var box in boxes)
            {
                // Include pieces whose undersides are on the same bottom layer.
                if (box.min.y > low + 0.25f) continue;
                var min = new Vector3(box.min.x, low, box.min.z);
                var max = new Vector3(box.max.x, low, box.max.z);
                if (first) { footprint = new Bounds(min, Vector3.zero); first = false; }
                footprint.Encapsulate(min);
                footprint.Encapsulate(max);
            }
            if (first) footprint = new Bounds(Vector3.zero, Vector3.one);
            var a = footprint.min;
            var b = footprint.max;
            return new BlueprintFoundation
            {
                Bounds = footprint,
                Anchors = new[]
                {
                    new Vector3(footprint.center.x, a.y, footprint.center.z),
                    new Vector3(a.x, a.y, b.z), new Vector3(b.x, a.y, b.z),
                    new Vector3(b.x, a.y, a.z), new Vector3(a.x, a.y, a.z),
                },
            };
        }

        private static void Encapsulate(ref Bounds box, ref bool first, Vector3 center, Vector3 size, Quaternion rotation)
        {
            for (var corner = 0; corner < 8; corner++)
            {
                var point = center + rotation * Vector3.Scale(size * 0.5f, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f));
                if (first) { box = new Bounds(point, Vector3.zero); first = false; }
                else box.Encapsulate(point);
            }
        }

        public bool Contains(Vector3 local, float margin = 0f)
        {
            return local.x >= Bounds.min.x - margin && local.x <= Bounds.max.x + margin
                && local.z >= Bounds.min.z - margin && local.z <= Bounds.max.z + margin;
        }

        /// <summary>Snap the selected whole-blueprint anchor to a nearby built piece's native point.</summary>
        public static Vector3 SnapTarget(Vector3 aim)
        {
            var best = aim;
            var distance = Placer.SnapDistance * Placer.SnapDistance;
            var points = new List<Transform>();
            var seen = new HashSet<Piece>();
            foreach (var collider in Physics.OverlapSphere(aim, Placer.SnapDistance,
                LayerMask.GetMask("piece"), QueryTriggerInteraction.Ignore))
            {
                var piece = collider.GetComponentInParent<Piece>();
                if (piece == null || piece.m_nview == null || !piece.m_nview.IsValid() || !seen.Add(piece)) continue;
                points.Clear();
                piece.GetSnapPoints(points);
                foreach (var point in points)
                {
                    if (point == null) continue;
                    var next = (point.position - aim).sqrMagnitude;
                    if (next < distance) { best = point.position; distance = next; }
                }
            }
            return best;
        }
    }
}
