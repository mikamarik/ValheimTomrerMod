using System;
using UnityEngine;

namespace ValheimTomrer.Editor.View
{
    internal enum CameraMode
    {
        /// <summary>Circles a point in front of the camera. The cursor stays free.</summary>
        Orbit,

        /// <summary>First person, like a flying player. The cursor is held and every move turns the view.</summary>
        Free,
    }

    /// <summary>
    /// Where the pane looks from. Same two modes and the same numbers as the Tomrer editor
    /// (src/view/CameraControl.ts), so both editors feel the same.
    ///
    /// Everything here is in the editor scene's own space: the camera hangs under the scene root,
    /// so y = 0 is the grid and the ground, 8000 m under the player's world.
    /// </summary>
    internal sealed class EditorCamera
    {
        private const float OrbitPitchMin = -89.5f;   // orbit keeps the camera above the point it circles
        private const float OrbitPitchMax = -0.9f;
        private const float FreePitchMin = -89f;      // never quite straight up or down
        private const float FreePitchMax = 89f;
        private const float MinEye = 0.1f;            // keys and sticks stop this high above the ground
        private const float FlySpeed = 5f;            // m/s, Shift x3
        private const float FlyBoost = 3f;
        private const float PadFlySpeed = 6f;         // m/s, L1 x3
        private const float PadTurn = 150f;           // degrees a second
        private const float LookStep = 0.14f;         // degrees per mouse pixel
        private const float LookJump = 300f;          // pixels in one move: more is a jump, not a hand
        private const float DragSpeed = 0.6f;         // free turning by a drag, next to orbiting

        private readonly PreviewCamera _camera;
        private readonly Func<Vector2, float?> _surface;

        private Vector3 _pos = new Vector3(8f, 6f, 12f);
        private float _yaw;      // 0 looks along +Z, turning right makes it bigger
        private float _pitch;    // up is positive
        private float _dist = 10f;

        public EditorCamera(PreviewCamera camera, Func<Vector2, float?> surface)
        {
            _camera = camera;
            _surface = surface;
            LookFrom(_pos, Vector3.zero);
        }

        public CameraMode Mode { get; private set; } = CameraMode.Orbit;

        /// <summary>Free mode holds the cursor, so the mouse can turn the view.</summary>
        public bool WantsCursorLock => Mode == CameraMode.Free;

        /// <summary>Where the camera is, in the editor scene's space.</summary>
        public Vector3 Position => _pos;

        public float Pitch => _pitch;

        public float Yaw => _yaw;

        /// <summary>How far ahead the orbit point is.</summary>
        public float Distance => _dist;

        public Vector3 Forward => Quaternion.Euler(-_pitch, _yaw, 0f) * Vector3.forward;

        /// <summary>Right on the screen, level with the ground.</summary>
        public Vector3 Right => new Vector3(Mathf.Cos(_yaw * Mathf.Deg2Rad), 0f, -Mathf.Sin(_yaw * Mathf.Deg2Rad));

        public Vector3 Up => Vector3.Cross(Forward, Right);

        /// <summary>The point orbiting circles around.</summary>
        public Vector3 Pivot => _pos + Forward * _dist;

        public void SetMode(CameraMode mode)
        {
            if (mode == Mode)
            {
                return;
            }

            Mode = mode;

            // Back to orbit: the camera circles whatever the middle of the view shows.
            if (mode == CameraMode.Orbit)
            {
                var depth = _surface?.Invoke(new Vector2(0.5f, 0.5f));
                if (depth.HasValue && depth.Value > 0.3f && depth.Value < 200f)
                {
                    _dist = depth.Value;
                }

                _pitch = Limit(_pitch, 0f, OrbitPitchMin, OrbitPitchMax);
                Apply();
            }
        }

        /// <summary>Puts the camera at <paramref name="from"/>, looking at <paramref name="at"/>, which becomes the orbit point.</summary>
        public void LookFrom(Vector3 from, Vector3 at)
        {
            _pos = from;
            var direction = at - from;
            _dist = Mathf.Max(direction.magnitude, 0.01f);
            direction /= _dist;
            _yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            _pitch = Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
            Apply();
        }

        /// <summary>Looks at the box from its front (+Z side), a bit above and to one side.</summary>
        public void Frame(Bounds box)
        {
            var center = box.center;
            var size = Mathf.Max(Mathf.Max(box.size.x, box.size.y), Mathf.Max(box.size.z, 1f));
            var distance = size * 1.25f + 2.5f;
            var direction = new Vector3(0.28f, 0.5f, 1f).normalized;
            _camera.SetNearPlane(distance / 500f);
            LookFrom(center + direction * distance, center);
        }

        /// <summary>Moves the camera (and the orbit point with it). Keys and sticks stop just above the ground.</summary>
        public void Move(Vector3 by)
        {
            if (by.sqrMagnitude == 0f)
            {
                return;
            }

            var before = _pos.y;
            _pos += by;
            if (by.y < 0f)
            {
                _pos.y = Mathf.Max(_pos.y, Mathf.Min(before, MinEye));
            }

            Apply();
        }

        /// <summary>Free turning, in place. Right and up are positive, in degrees.</summary>
        public void Turn(float right, float up)
        {
            _yaw += right;
            _pitch = Limit(_pitch, up, FreePitchMin, FreePitchMax);
            Apply();
        }

        /// <summary>Circles the orbit point. Right and up turn the view the same way as <see cref="Turn"/>.</summary>
        public void Orbit(float right, float up)
        {
            var pivot = Pivot;
            _yaw += right;
            _pitch = Limit(_pitch, up, OrbitPitchMin, OrbitPitchMax);
            _pos = pivot - Forward * _dist;
            Apply();
        }

        /// <summary>Turns by the mode: orbit or free.</summary>
        public void Rotate(float right, float up)
        {
            if (Mode == CameraMode.Orbit)
            {
                Orbit(right, up);
            }
            else
            {
                Turn(right, up);
            }
        }

        /// <summary>A mouse drag across the pane. A drag over its whole height is a full circle.</summary>
        public void Drag(Vector2 pixels, float viewHeight)
        {
            var k = 360f / Mathf.Max(1f, viewHeight);
            if (Mode == CameraMode.Orbit)
            {
                Orbit(pixels.x * k, pixels.y * k);
            }
            else
            {
                Turn(pixels.x * k * DragSpeed, pixels.y * k * DragSpeed);
            }
        }

        /// <summary>First person: every mouse move turns the view. A jump bigger than a hand is clipped.</summary>
        public void MouseLook(Vector2 pixels)
        {
            if (pixels == Vector2.zero)
            {
                return;
            }

            Turn(Mathf.Clamp(pixels.x, -LookJump, LookJump) * LookStep,
                Mathf.Clamp(pixels.y, -LookJump, LookJump) * LookStep);
        }

        /// <summary>Moves in the view plane by pane pixels, so the point <paramref name="depth"/> away follows the mouse.</summary>
        public void Pan(Vector2 pixels, float depth, float viewHeight)
        {
            var k = 2f * depth * Mathf.Tan(_camera.Unity.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, viewHeight);
            _pos += Right * (-pixels.x * k) + Up * (-pixels.y * k);
            Apply();
        }

        /// <summary>
        /// Wheel zoom: 5% of the way per notch, toward the cursor while orbiting and toward the
        /// middle of the view in free mode, where there is no cursor to aim with.
        /// </summary>
        public void Zoom(float delta, Vector2 viewport)
        {
            if (Mathf.Approximately(delta, 0f))
            {
                return;
            }

            var scale = Mathf.Pow(0.95f, Mathf.Abs(delta) * 0.01f);
            var at = Mode == CameraMode.Free ? new Vector2(0.5f, 0.5f) : viewport;
            var depth = Mode == CameraMode.Free ? _surface?.Invoke(at) ?? _dist : _dist;
            var next = delta < 0f ? depth * scale : depth / scale;
            _pos += LocalDirection(at) * (depth - next);
            if (Mode == CameraMode.Orbit)
            {
                _dist = next;
            }

            Apply();
        }

        /// <summary>Keys: forward/back, left/right, up/down, each -1..1. Shift makes it three times faster.</summary>
        public void FlyKeys(Vector3 wish, bool boost, float dt)
        {
            Fly(wish, FlySpeed * (boost ? FlyBoost : 1f), dt);
        }

        /// <summary>The pad's left stick flies, the right stick turns.</summary>
        public void FlyPad(Vector3 wish, bool boost, float dt)
        {
            Fly(wish, PadFlySpeed * (boost ? FlyBoost : 1f), dt);
        }

        public void TurnPad(Vector2 stick, float dt)
        {
            if (stick == Vector2.zero)
            {
                return;
            }

            Rotate(stick.x * PadTurn * dt, stick.y * PadTurn * dt);
        }

        private void Fly(Vector3 wish, float speed, float dt)
        {
            if (wish.sqrMagnitude == 0f)
            {
                return;
            }

            var by = (Forward * wish.z + Right * wish.x + Vector3.up * wish.y) * (speed * dt);
            Move(by);
        }

        /// <summary>The way the camera looks through a point of the pane, in the scene's own space.</summary>
        private Vector3 LocalDirection(Vector2 viewport)
        {
            var ray = _camera.Unity.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
            var parent = _camera.Transform != null ? _camera.Transform.parent : null;
            return parent != null ? parent.InverseTransformDirection(ray.direction) : ray.direction;
        }

        private void Apply()
        {
            var transform = _camera.Transform;
            if (transform != null)
            {
                transform.localPosition = _pos;
                transform.localRotation = Quaternion.Euler(-_pitch, _yaw, 0f);
            }
        }

        /// <summary>Adds to a pitch, inside the range. A pitch already outside it only moves back toward it.</summary>
        private static float Limit(float pitch, float by, float low, float high)
        {
            return Mathf.Clamp(pitch + by, Mathf.Min(low, pitch), Mathf.Max(high, pitch));
        }
    }
}
