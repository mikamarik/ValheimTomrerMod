using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;
using ValheimTomrer.Editor.Ui;
using ValheimTomrer.Editor.View;

namespace ValheimTomrer.Editor
{
    /// <summary>
    /// Turns a building that is already standing into a blueprint. The capture key puts a
    /// rectangle on the ground under the aim. It follows the aim, the wheel turns it, Shift or Alt
    /// with the wheel resize it, and the pieces it will take glow yellow. The key again captures
    /// what is inside and opens it in the editor with Save as up. Esc stops it.
    ///
    /// It runs with the editor window closed, because the player has to aim in the world. So
    /// ModUi.Blocking stays false and the game keeps its input, with two small exceptions while
    /// the rectangle is up: the wheel does not zoom the camera (EditorInputBlockPatches), and
    /// the Esc that stops the capture does not also open the pause menu.
    ///
    /// Reading the pieces is a local query. Nothing is moved, removed or sent anywhere. The glow is
    /// a colour on the piece's renderers, through the game's own MaterialMan, and comes off again.
    /// </summary>
    internal static class WorldCapture
    {
        /// <summary>The name a captured blueprint starts with.</summary>
        public const string Name = "Captured build";

        /// <summary>How far the rectangle can be put, the same 50 m the game's own place ray uses.</summary>
        public const float Reach = 50f;

        /// <summary>Each side of a new rectangle.</summary>
        public const float DefaultSide = 8f;

        public const float MinSide = 2f;
        public const float MaxSide = 64f;
        public const float SideStep = 2f;

        /// <summary>One wheel notch turns this far, the hammer's own step.</summary>
        public const float TurnStep = 22.5f;

        /// <summary>A piece counts when its pivot is at most this far under the ground at the centre...</summary>
        public const float Below = 8f;

        /// <summary>...and at most this far over it.</summary>
        public const float Above = 64f;

        /// <summary>A piece whose pivot is this close outside can still reach in, so it is checked for the orange glow.</summary>
        public const float EdgeReach = 4f;

        /// <summary>The glow is worked out again this often while the rectangle moves...</summary>
        public const float BusyRefresh = 0.3f;

        /// <summary>...and this often while it stands still, for pieces built or removed meanwhile.</summary>
        public const float StillRefresh = 1f;

        /// <summary>The game's own wheel scale and threshold (ZInput and Player.m_scrollAmountThreshold).</summary>
        private const float WheelScale = 0.15f;

        private const float WheelThreshold = 0.1f;

        private static readonly List<Piece> Around = new List<Piece>();
        private static readonly List<Piece> Taken = new List<Piece>();
        private static readonly List<Piece> Edge = new List<Piece>();
        private static readonly List<Collider> Colliders = new List<Collider>();

        private static CaptureBox _drawn;
        private static int _fallbackMask;
        private static bool _pinned;
        private static bool _moved;
        private static float _refreshedAt;
        private static float _wheel;
        private static float _wheelScale;
        private static int _escapeFrame = -1;
        private static Vector3 _shownShape;
        private static int _shownInside;
        private static int _shownLeft;

        /// <summary>The rectangle is on the ground right now.</summary>
        public static bool Active { get; private set; }

        /// <summary>The middle of the rectangle, on the ground.</summary>
        public static Vector3 Centre { get; private set; }

        /// <summary>Which way it faces, in degrees, 0 to 360.</summary>
        public static float Yaw { get; private set; }

        /// <summary>The side along the rectangle's own x.</summary>
        public static float Width { get; private set; } = DefaultSide;

        /// <summary>The side along the rectangle's own z.</summary>
        public static float Depth { get; private set; } = DefaultSide;

        /// <summary>Pieces inside right now, as of the last glow refresh.</summary>
        public static int InsideNow { get; private set; }

        /// <summary>Pieces across the edge with their pivot outside, as of the last refresh.</summary>
        public static int EdgeNow { get; private set; }

        /// <summary>Pieces inside that the hammer cannot build, as of the last refresh.</summary>
        public static int SkippedNow { get; private set; }

        /// <summary>Pieces the last capture took.</summary>
        public static int LastCount { get; private set; }

        /// <summary>Pieces it left out, because the hammer cannot build them.</summary>
        public static int LastSkipped { get; private set; }

        /// <summary>What the last capture said to the player.</summary>
        public static string LastMessage { get; private set; } = "";

        /// <summary>The outline stands in the world right now.</summary>
        public static bool Drawn => _drawn != null && _drawn.Visible;

        /// <summary>
        /// True on the frame the capture uses Esc, so the pause menu does not open with the same
        /// press. Read by the Menu.Update patch, which may run before or after <see cref="Tick"/>.
        /// </summary>
        public static bool TakesEscape =>
            _escapeFrame == Time.frameCount || (Active && ZInput.GetKeyDown(KeyCode.Escape));

        /// <summary>One frame, with the editor window closed: the key, Esc, the aim and the wheel.</summary>
        public static void Tick()
        {
            var key = EditorConfig.CaptureKey;
            var player = Player.m_localPlayer;
            if (key == null || player == null)
            {
                Cancel();
                return;
            }

            if (Active && ZInput.GetKeyDown(KeyCode.Escape))
            {
                _escapeFrame = Time.frameCount;
                Cancel();
                Say(player, "Capture off.");
                return;
            }

            if (ZInput.GetKeyDown(key.Value))
            {
                if (Active)
                {
                    Capture();
                    return;
                }

                if (!AimPoint(player, out var aim))
                {
                    Say(player, "Aim at the ground or at a piece, then press the key.");
                    return;
                }

                Begin(aim);
                return;
            }

            if (!Active)
            {
                return;
            }

            if (!_pinned && AimPoint(player, out var point))
            {
                MoveCentre(point);
            }

            var changed = Wheel();
            var hover = player.GetHoveringPiece();
            if (hover != null)
            {
                CaptureTint.Hovered(hover.gameObject);
            }

            if (changed)
            {
                Refresh();
            }
            else
            {
                var since = Time.unscaledTime - _refreshedAt;
                if ((_moved && since >= BusyRefresh) || since >= StillRefresh)
                {
                    Refresh();
                }
            }

            ShowStatus();
        }

        /// <summary>Puts the rectangle down, centred on a point. It keeps the size and turn it had last time.</summary>
        public static void Begin(Vector3 centre)
        {
            _drawn = _drawn ?? new CaptureBox();
            Active = true;
            _pinned = false;
            _wheel = 0f;
            _wheelScale = WheelScaleNow();
            PieceCatalog.Ensure();
            Centre = OnGround(centre);
            Refresh();
            ShowStatus();
            Say(Player.m_localPlayer, $"Press {KeyName()} again to capture, Esc to stop.");
        }

        /// <summary>
        /// Holds the rectangle on one spot: the aim stops moving it until the next start. The
        /// autotest uses it to put the rectangle exactly over a building.
        /// </summary>
        public static void Pin(Vector3 centre)
        {
            if (!Active)
            {
                return;
            }

            _pinned = true;
            Centre = OnGround(centre);
            Refresh();
            ShowStatus();
        }

        /// <summary>Back to the size and turn a first capture starts with.</summary>
        public static void ResetShape()
        {
            Yaw = 0f;
            Width = DefaultSide;
            Depth = DefaultSide;
        }

        /// <summary>Takes the rectangle, the glow and the status lines away. Safe to call at any time.</summary>
        public static void Cancel()
        {
            Active = false;
            _pinned = false;
            _drawn?.Hide();
            CaptureTint.Clear();
            CaptureHud.Hide();
        }

        /// <summary>Plugin OnDestroy: the outline and the status lines go with it.</summary>
        public static void Shutdown()
        {
            Cancel();
            _drawn?.Destroy();
            _drawn = null;
            CaptureHud.Destroy();
        }

        /// <summary>
        /// What is inside the rectangle, as a blueprint in the editor, with Save as up. Pieces the
        /// hammer cannot build are left out and counted, so a captured blueprint is always one this
        /// mod can build again. True when it captured something.
        /// </summary>
        public static bool Capture()
        {
            if (!Active)
            {
                return false;
            }

            var centre = Centre;
            var turn = Quaternion.Euler(0f, Yaw, 0f);
            Cancel();

            LastCount = 0;
            LastSkipped = 0;
            var player = Player.m_localPlayer;
            if (player == null)
            {
                return false;
            }

            PieceCatalog.Ensure();
            Collect(Taken, null, out var skipped);
            LastSkipped = skipped;
            if (Taken.Count == 0)
            {
                Say(player, skipped > 0
                    ? $"Nothing in the rectangle the hammer can build. {Left(skipped)}"
                    : "Nothing in the rectangle.");
                return false;
            }

            // Into the rectangle's own frame, so a house built at 45 degrees comes back square.
            var back = Quaternion.Inverse(turn);
            var standing = new List<DocPiece>(Taken.Count);
            for (var i = 0; i < Taken.Count; i++)
            {
                var piece = Taken[i];
                standing.Add(new DocPiece(
                    i + 1,
                    Utils.GetPrefabName(piece.gameObject),
                    "",
                    back * (piece.transform.position - centre),
                    back * piece.transform.rotation,
                    null));
            }

            // The origin the Center origin button would pick, then bottom up, the way it is built.
            var origin = EditorState.BottomCentre(standing);
            var pieces = standing
                .Select(p => new NewPiece { PrefabName = p.PrefabName, Position = p.Position - origin, Rotation = p.Rotation })
                .OrderBy(p => p.Position.y)
                .ThenBy(p => p.Position.x)
                .ThenBy(p => p.Position.z)
                .ToList();

            var document = BlueprintDocument.New(Name);
            document.AddPieces(pieces);
            LastCount = pieces.Count;
            ValheimTomrerPlugin.Log.LogInfo(
                $"capture: {LastCount} pieces, {skipped} skipped, centre {centre}, turned {Yaw:0.#}, "
                + $"{Width:0.#} x {Depth:0.#} m, origin {origin}");

            var message = $"Captured {LastCount} pieces." + (skipped > 0 ? " " + Left(skipped) : "");
            LastMessage = message;

            // Save as only once the captured blueprint is the open one. A kept blueprint with
            // unsaved changes asks first, and a dialog opened straight away would close that
            // question and save the old blueprint under the new name.
            EditorSession.OpenDocument(document, () =>
            {
                Dialogs.SaveAs(Name);
                Toasts.Ok(message);
            });

            if (!ModUi.Open)
            {
                Say(player, message);
            }

            return true;
        }

        /// <summary>
        /// Sorts the loaded pieces around the rectangle. <paramref name="taken"/> gets the ones a
        /// capture would take: built by a player, pivot inside the rectangle and the height window,
        /// and a kind the hammer has. <paramref name="edge"/>, when given, gets the ones whose pivot
        /// is outside but whose body reaches in. <paramref name="skipped"/> counts the ones inside
        /// the hammer cannot build.
        /// </summary>
        public static void Collect(List<Piece> taken, List<Piece> edge, out int skipped)
        {
            skipped = 0;
            taken.Clear();
            edge?.Clear();

            var back = Quaternion.Inverse(Quaternion.Euler(0f, Yaw, 0f));
            var halfWidth = Width * 0.5f;
            var halfDepth = Depth * 0.5f;
            var low = Centre.y - Below;
            var high = Centre.y + Above;
            var radius = Mathf.Sqrt((halfWidth * halfWidth) + (halfDepth * halfDepth) + (Above * Above)) + EdgeReach;

            Around.Clear();
            Piece.GetAllPiecesInRadius(Centre, radius, Around);
            foreach (var piece in Around)
            {
                if (piece == null || !piece.IsPlacedByPlayer())
                {
                    continue;
                }

                var position = piece.transform.position;
                var local = back * (position - Centre);
                var inside = Mathf.Abs(local.x) <= halfWidth && Mathf.Abs(local.z) <= halfDepth
                    && position.y >= low && position.y <= high;
                if (inside)
                {
                    if (PieceCatalog.Find(Utils.GetPrefabName(piece.gameObject)) == null)
                    {
                        skipped++;
                        continue;
                    }

                    taken.Add(piece);
                    continue;
                }

                if (edge != null
                    && Mathf.Abs(local.x) <= halfWidth + EdgeReach
                    && Mathf.Abs(local.z) <= halfDepth + EdgeReach
                    && ReachesIn(piece, back, halfWidth, halfDepth, low, high))
                {
                    edge.Add(piece);
                }
            }

            taken.Sort((a, b) => a.transform.position.y.CompareTo(b.transform.position.y));
        }

        /// <summary>Works out the glow again and keeps the counts the status lines show.</summary>
        private static void Refresh()
        {
            _refreshedAt = Time.unscaledTime;
            _moved = false;
            Collect(Taken, Edge, out var skipped);
            InsideNow = Taken.Count;
            EdgeNow = Edge.Count;
            SkippedNow = skipped;
            CaptureTint.Show(Taken, Edge);
            _drawn?.Show(Centre, Yaw, Width, Depth);
        }

        /// <summary>
        /// True when a piece's colliders cross into the box. Each collider's own box is turned into
        /// the rectangle's frame corner by corner, so a wall lined up with the rectangle is measured
        /// exactly, and a turned one a little generously.
        /// </summary>
        private static bool ReachesIn(Piece piece, Quaternion back, float halfWidth, float halfDepth, float low, float high)
        {
            Colliders.Clear();
            piece.GetComponentsInChildren(false, Colliders);
            foreach (var collider in Colliders)
            {
                if (collider == null || !collider.enabled || collider.isTrigger)
                {
                    continue;
                }

                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                if (!Corners(collider, back, ref min, ref max))
                {
                    continue;
                }

                if (min.x <= halfWidth && max.x >= -halfWidth
                    && min.z <= halfDepth && max.z >= -halfDepth
                    && min.y <= high && max.y >= low)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The collider's box in the rectangle's frame: x and z turned, y left as world height.</summary>
        private static bool Corners(Collider collider, Quaternion back, ref Vector3 min, ref Vector3 max)
        {
            Bounds local;
            var owner = collider.transform;
            switch (collider)
            {
                case BoxCollider box:
                    local = new Bounds(box.center, box.size);
                    break;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    local = mesh.sharedMesh.bounds;
                    break;
                default:
                    // Spheres, capsules and the rest: their world box, already axis aligned.
                    local = collider.bounds;
                    owner = null;
                    break;
            }

            var e = local.extents;
            for (var i = 0; i < 8; i++)
            {
                var corner = local.center + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                var world = owner != null ? owner.TransformPoint(corner) : corner;
                var turned = back * (world - Centre);
                turned.y = world.y;
                min = Vector3.Min(min, turned);
                max = Vector3.Max(max, turned);
            }

            return true;
        }

        /// <summary>
        /// The wheel: alone it turns, with Shift the width, with Alt the depth, with both the two
        /// sides together. Read from the mouse itself, because the game's own wheel reads 0 while a
        /// capture is up (so the camera does not zoom). True when the rectangle changed.
        /// </summary>
        private static bool Wheel()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null)
            {
                return false;
            }

            _wheel += mouse.scroll.ReadValue().y * _wheelScale;
            int notch;
            if (_wheel > WheelThreshold)
            {
                notch = 1;
            }
            else if (_wheel < -WheelThreshold)
            {
                notch = -1;
            }
            else
            {
                return false;
            }

            _wheel = 0f;
            var shift = ZInput.GetKey(KeyCode.LeftShift, false) || ZInput.GetKey(KeyCode.RightShift, false);
            var alt = ZInput.GetKey(KeyCode.LeftAlt, false) || ZInput.GetKey(KeyCode.RightAlt, false);
            if (!shift && !alt)
            {
                Yaw = Mathf.Repeat(Yaw + (notch * TurnStep), 360f);
                return true;
            }

            if (shift)
            {
                Width = Side(Width + (notch * SideStep));
            }

            if (alt)
            {
                Depth = Side(Depth + (notch * SideStep));
            }

            return true;
        }

        private static float Side(float side)
        {
            return Mathf.Clamp(side, MinSide, MaxSide);
        }

        /// <summary>The game's scale for this mouse: a notch of an ordinary wheel, or a trackpad's fine steps.</summary>
        private static float WheelScaleNow()
        {
            try
            {
                return WheelScale * ZInput.GetScrollModifier();
            }
            catch (System.Exception e)
            {
                ValheimTomrerPlugin.Log.LogWarning("capture: no wheel scale from the game, using 1: " + e.Message);
                return WheelScale;
            }
        }

        private static void MoveCentre(Vector3 point)
        {
            var next = OnGround(point);
            if ((next - Centre).sqrMagnitude > 0.0001f)
            {
                Centre = next;
                _moved = true;
                _drawn?.Show(Centre, Yaw, Width, Depth);
            }
        }

        /// <summary>The same x and z, on the terrain. A miss keeps the height it came with.</summary>
        private static Vector3 OnGround(Vector3 point)
        {
            var zones = ZoneSystem.instance;
            if (zones != null)
            {
                point.y = zones.GetGroundHeight(point);
            }

            return point;
        }

        /// <summary>The two lines top left. Written again only when a number in them changed.</summary>
        private static void ShowStatus()
        {
            if (!Active)
            {
                CaptureHud.Hide();
                return;
            }

            var left = EdgeNow + SkippedNow;
            var shape = new Vector3(Width, Depth, Yaw);
            if (CaptureHud.Visible && shape == _shownShape && InsideNow == _shownInside && left == _shownLeft)
            {
                return;
            }

            _shownShape = shape;
            _shownInside = InsideNow;
            _shownLeft = left;
            CaptureHud.Show(
                $"Capture  {Width:0} x {Depth:0} m, turned {Yaw:0.#}°, {Pieces(InsideNow)}, {left} left out",
                $"Wheel: turn   Shift+wheel: width   Alt+wheel: depth   Shift+Alt+wheel: both   {KeyName()}: capture   Esc: cancel");
        }

        private static string Pieces(int count)
        {
            return count == 1 ? "1 piece" : $"{count} pieces";
        }

        private static string KeyName()
        {
            var key = EditorConfig.CaptureKey;
            return key != null ? key.Value.ToString() : "F8";
        }

        /// <summary>Where the player is looking, the same ray the hammer places with, but longer.</summary>
        private static bool AimPoint(Player player, out Vector3 point)
        {
            point = Vector3.zero;
            var camera = GameCamera.instance;
            if (camera == null)
            {
                return false;
            }

            var mask = player.m_placeRayMask != 0 ? player.m_placeRayMask : FallbackMask();
            if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, Reach, mask))
            {
                return false;
            }

            // Skip anything that moves: a cart or a boat is no place for the rectangle.
            if (hit.collider == null || hit.collider.attachedRigidbody != null)
            {
                return false;
            }

            point = hit.point;
            return true;
        }

        /// <summary>The game's own place mask, in case the player's copy is not filled in yet.</summary>
        private static int FallbackMask()
        {
            if (_fallbackMask == 0)
            {
                _fallbackMask = LayerMask.GetMask(
                    "Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle");
            }

            return _fallbackMask;
        }

        private static string Left(int skipped)
        {
            return skipped == 1
                ? "1 piece was left out: the hammer cannot build it."
                : $"{skipped} pieces were left out: the hammer cannot build them.";
        }

        private static void Say(Player player, string text)
        {
            LastMessage = text;
            ValheimTomrerPlugin.Log.LogInfo("capture: " + text);
            player?.Message(MessageHud.MessageType.Center, text);
        }
    }
}
