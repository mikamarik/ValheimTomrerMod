using UnityEngine;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// Where the pane looks from. One camera: it always flies free, like a player in fly mode.
    /// Which region has the mouse is not a camera setting, it is <see cref="Ui.ViewportHost.Captured"/>.
    ///
    /// Everything here is in the editor scene's own space: the camera hangs under the scene root,
    /// so y = 0 is the grid and the ground, 8000 m under the player's world.
    /// </summary>
    internal sealed class EditorCamera
    {
        private const float PitchMin = -89f;          // never quite straight up or down
        private const float PitchMax = 89f;
        private const float MinEye = 0.1f;            // keys and sticks stop this high above the ground
        private const float FlySpeed = 5f;            // m/s, Shift x3
        private const float FlyBoost = 3f;
        private const float PadFlySpeed = 6f;         // m/s, L1 x3
        private const float PadTurn = 110f;           // degrees a second, the game's (PlayerController.LateUpdate)
        private const float LookStep = 0.05f;         // degrees per mouse pixel, the game's (ZInput's mouse delta scale)
        private const float LookJump = 300f;          // pixels in one move: more is a jump, not a hand
        private const float DragSpeed = 0.6f;         // a drag turns slower than the same pixels of mouse look

        private readonly PreviewCamera _camera;

        private Vector3 _pos = new Vector3(8f, 6f, 12f);
        private float _yaw;      // 0 looks along +Z, turning right makes it bigger
        private float _pitch;    // up is positive
        private float _dist = 10f;
        private float _near = 0.05f;

        public EditorCamera(PreviewCamera camera)
        {
            _camera = camera;
            LookFrom(_pos, Vector3.zero);
        }

        /// <summary>Where the camera is, in the editor scene's space.</summary>
        public Vector3 Position => _pos;

        public float Pitch => _pitch;

        public float Yaw => _yaw;

        public float FieldOfView => _camera.Unity.fieldOfView;

        /// <summary>How far ahead the point the camera turns, pans and zooms around sits.</summary>
        public float Distance => _dist;

        public Vector3 Forward => Quaternion.Euler(-_pitch, _yaw, 0f) * Vector3.forward;

        /// <summary>Right on the screen, level with the ground.</summary>
        public Vector3 Right => new Vector3(Mathf.Cos(_yaw * Mathf.Deg2Rad), 0f, -Mathf.Sin(_yaw * Mathf.Deg2Rad));

        public Vector3 Up => Vector3.Cross(Forward, Right);

        /// <summary>The point the camera looks at, <see cref="Distance"/> straight ahead.</summary>
        public Vector3 Pivot => _pos + Forward * _dist;

        /// <summary>Puts the camera at <paramref name="from"/>, looking at <paramref name="at"/>, which becomes the point it turns around.</summary>
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
            _near = distance / 500f;
            _camera.SetNearPlane(_near);
            LookFrom(center + direction * distance, center);
        }

        /// <summary>
        /// Looks from where another camera looked. A world change kills the old camera's objects,
        /// not its numbers, so the rebuilt pane comes back on the same view.
        /// </summary>
        public void TakePose(EditorCamera from)
        {
            _pos = from._pos;
            _yaw = from._yaw;
            _pitch = from._pitch;
            _dist = from._dist;
            _near = from._near;
            _camera.SetNearPlane(_near);
            Apply();
        }

        /// <summary>Moves the camera (and the point in front with it). Keys and sticks stop just above the ground.</summary>
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

        /// <summary>Turns in place. Right and up are positive, in degrees.</summary>
        public void Turn(float right, float up)
        {
            _yaw += right;
            _pitch = Limit(_pitch, up, PitchMin, PitchMax);
            Apply();
        }

        /// <summary>A mouse drag across the pane turns it. A drag over its whole height is a full circle.</summary>
        public void Drag(Vector2 pixels, float viewHeight)
        {
            var k = 360f / Mathf.Max(1f, viewHeight);
            Turn(pixels.x * k * DragSpeed, pixels.y * k * DragSpeed);
        }

        /// <summary>
        /// First person: every mouse move turns the view, the way the game's mouse look does
        /// (PlayerController.LateUpdate): 0.05 degrees a pixel times the game's Mouse sensitivity,
        /// with its invert setting. No setting of the mod's. A jump bigger than a hand is clipped.
        /// </summary>
        public void MouseLook(Vector2 pixels)
        {
            if (pixels == Vector2.zero)
            {
                return;
            }

            var step = LookStep * (ZInput.IsGamepadMouseActive() ? PlayerController.m_switchMouseSens : PlayerController.m_mouseSens);
            Turn(Mathf.Clamp(pixels.x, -LookJump, LookJump) * step,
                Mathf.Clamp(pixels.y, -LookJump, LookJump) * step * (PlayerController.m_invertMouse ? -1f : 1f));
        }

        /// <summary>Moves in the view plane by pane pixels, so the point <paramref name="depth"/> away follows the mouse.</summary>
        public void Pan(Vector2 pixels, float depth, float viewHeight)
        {
            var k = 2f * depth * Mathf.Tan(_camera.Unity.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, viewHeight);
            _pos += Right * (-pixels.x * k) + Up * (-pixels.y * k);
            Apply();
        }

        /// <summary>
        /// Wheel zoom: 5% of the way per notch, toward the pane point the caller gives. The caller
        /// aims at the cursor, or at the middle of the view once the pane has the mouse.
        ///
        /// <see cref="Distance"/> follows the wheel, because <see cref="Pivot"/>, the pan depth and
        /// the pad's framing all read it.
        /// </summary>
        public void Zoom(float delta, Vector2 viewport)
        {
            if (Mathf.Approximately(delta, 0f))
            {
                return;
            }

            var scale = Mathf.Pow(0.95f, Mathf.Abs(delta) * 0.01f);
            var next = delta < 0f ? _dist * scale : _dist / scale;
            _pos += LocalDirection(viewport) * (_dist - next);
            _dist = next;
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

        /// <summary>
        /// The game's own right-stick look: 110 degrees a second at full stick, times the game's
        /// Gamepad sensitivity, with its invert settings. No setting of the mod's.
        /// </summary>
        public void TurnPad(Vector2 stick, float dt)
        {
            if (stick == Vector2.zero)
            {
                return;
            }

            var step = PadTurn * PlayerController.m_gamepadSens * dt;
            Turn(stick.x * step * (PlayerController.m_invertCameraX ? -1f : 1f),
                stick.y * step * (PlayerController.m_invertCameraY ? -1f : 1f));
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
