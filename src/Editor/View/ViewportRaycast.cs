using UnityEngine;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// Turns screen pixels into the pane's own space and back: where the mouse points inside the
    /// texture, what is under it, and where a point of the model shows on screen.
    ///
    /// Every physics query here carries the editor layer as its mask, so the editor can never hit
    /// the world the player is standing in, and the world can never hit the editor.
    /// </summary>
    internal sealed class ViewportRaycast
    {
        private const float Reach = 2000f;
        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        private readonly PreviewCamera _camera;
        private readonly RectTransform _image;
        private readonly EditorScene _scene;
        private readonly int _mask;

        public ViewportRaycast(PreviewCamera camera, RectTransform image, EditorScene scene)
        {
            _camera = camera;
            _image = image;
            _scene = scene;
            _mask = 1 << scene.Layer;
        }

        public int Mask => _mask;

        /// <summary>Where a screen point sits inside the pane, 0..1. False when it is outside it.</summary>
        public bool ScreenToViewport(Vector2 screen, out Vector2 viewport)
        {
            viewport = Vector2.zero;
            if (_image == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_image, screen, null, out var local))
            {
                return false;
            }

            var rect = _image.rect;
            viewport = new Vector2((local.x - rect.xMin) / rect.width, (local.y - rect.yMin) / rect.height);
            return viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
        }

        public Ray RayAt(Vector2 viewport)
        {
            return _camera.Unity.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
        }

        /// <summary>The first piece under a point of the pane. The ground does not count as a piece.</summary>
        public bool Pick(Vector2 viewport, out RaycastHit hit)
        {
            return Cast(RayAt(viewport), false, out hit);
        }

        /// <summary>The first piece or ground under a point of the pane, and how far it is.</summary>
        public float? SurfaceDistance(Vector2 viewport)
        {
            return Cast(RayAt(viewport), true, out var hit) ? hit.distance : (float?)null;
        }

        private bool Cast(Ray ray, bool withGround, out RaycastHit best)
        {
            best = default;
            var found = false;
            var count = Physics.RaycastNonAlloc(ray, Hits, Reach, _mask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var hit = Hits[i];
                if (!withGround && _scene != null && hit.collider == _scene.GroundCollider)
                {
                    continue;
                }

                if (!found || hit.distance < best.distance)
                {
                    best = hit;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>Where a point of the model shows on screen, in canvas pixels.</summary>
        public bool Project(Vector3 world, out Vector2 screen)
        {
            screen = Vector2.zero;
            if (_image == null)
            {
                return false;
            }

            var viewport = _camera.Unity.WorldToViewportPoint(world);
            var rect = _image.rect;
            var local = new Vector3(rect.xMin + viewport.x * rect.width, rect.yMin + viewport.y * rect.height, 0f);
            screen = RectTransformUtility.WorldToScreenPoint(null, _image.TransformPoint(local));
            return viewport.z > 0f;
        }

        /// <summary>The camera's forward and right on the ground: what the arrow keys nudge along.</summary>
        public void GroundAxes(out Vector3 forward, out Vector3 right)
        {
            forward = _camera.Transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 1e-6f ? Vector3.forward : forward.normalized;
            right = new Vector3(forward.z, 0f, -forward.x);
        }
    }
}
