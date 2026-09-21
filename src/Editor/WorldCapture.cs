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
    /// Turns a building that is already standing into a blueprint. The capture key picks two
    /// corners in the world, and what stands between them opens in the editor as a blueprint with
    /// no file yet.
    ///
    /// It runs with the editor window closed, because the player has to aim in the world. So it
    /// takes no input away from the game: it reads one key, draws one box, and that is all. The
    /// input takeover in EditorInputBlockPatches never sees it, since ModUi.Blocking stays false.
    ///
    /// Reading the pieces is a local query. Nothing is moved, changed, removed or sent anywhere.
    /// </summary>
    internal static class WorldCapture
    {
        /// <summary>The name a captured blueprint starts with.</summary>
        public const string Name = "Captured build";

        /// <summary>How far a corner can be picked, the same 50 m the game's own place ray uses.</summary>
        public const float Reach = 50f;

        /// <summary>The box starts this far under the lower corner, so a floor is inside it.</summary>
        public const float Under = 0.5f;

        /// <summary>And ends this far over the higher one, so a roof is inside it.</summary>
        public const float Over = 16f;

        private static CaptureBox _drawn;
        private static Vector3 _first;
        private static int _fallbackMask;

        /// <summary>A box is being picked right now: the first corner is in, the second is not.</summary>
        public static bool Active { get; private set; }

        /// <summary>The first corner, while a box is being picked.</summary>
        public static Vector3 FirstCorner => _first;

        /// <summary>The box as it stands, or the last one that was captured.</summary>
        public static Bounds Box { get; private set; }

        /// <summary>Pieces the last capture took.</summary>
        public static int LastCount { get; private set; }

        /// <summary>Pieces it left out, because the hammer cannot build them.</summary>
        public static int LastSkipped { get; private set; }

        /// <summary>The box stands in the world right now.</summary>
        public static bool Drawn => _drawn != null && _drawn.Visible;

        /// <summary>One frame, with the editor window closed. Reads the key and follows the aim.</summary>
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
                Cancel();
                Say(player, "Capture off.");
                return;
            }

            if (ZInput.GetKeyDown(key.Value))
            {
                if (!AimPoint(player, out var corner))
                {
                    Say(player, "Aim at the ground or at a piece, then press the key.");
                    return;
                }

                if (Active)
                {
                    Corner(corner);
                }
                else
                {
                    Begin(corner);
                }

                return;
            }

            // The box follows the aim while the second corner is open.
            if (Active && AimPoint(player, out var aim))
            {
                Box = BoxBetween(_first, aim);
                _drawn?.Show(Box);
            }
        }

        /// <summary>The first corner. The box follows the aim from here until the second one.</summary>
        public static void Begin(Vector3 corner)
        {
            _drawn = _drawn ?? new CaptureBox();
            Active = true;
            _first = corner;
            Box = BoxBetween(corner, corner);
            _drawn.Show(Box);
            Say(Player.m_localPlayer, "Aim at the other corner and press the key again.");
        }

        /// <summary>The second corner: collects what is in the box. True when it captured something.</summary>
        public static bool Corner(Vector3 corner)
        {
            if (!Active)
            {
                return false;
            }

            var box = BoxBetween(_first, corner);
            Cancel();
            Box = box;
            return Capture(box);
        }

        /// <summary>Drops a half-picked box. Safe to call at any time.</summary>
        public static void Cancel()
        {
            Active = false;
            _drawn?.Hide();
        }

        /// <summary>Plugin OnDestroy: the box object goes with it.</summary>
        public static void Shutdown()
        {
            Cancel();
            _drawn?.Destroy();
            _drawn = null;
        }

        /// <summary>
        /// Everything in the box, as a blueprint in the editor. Pieces the hammer cannot build are
        /// left out and counted, so a captured blueprint is always one this mod can build again.
        /// </summary>
        public static bool Capture(Bounds box)
        {
            LastCount = 0;
            LastSkipped = 0;
            var player = Player.m_localPlayer;
            if (player == null)
            {
                return false;
            }

            PieceCatalog.Ensure();
            var found = PiecesIn(box, out var skipped);
            LastSkipped = skipped;
            if (found.Count == 0)
            {
                Say(player, skipped > 0
                    ? $"Nothing in the box the hammer can build. {Left(skipped)}"
                    : "Nothing in the box.");
                return false;
            }

            // World positions first, so the origin is worked out the same way Center origin does.
            var standing = new List<DocPiece>(found.Count);
            for (var i = 0; i < found.Count; i++)
            {
                var piece = found[i];
                standing.Add(new DocPiece(
                    i + 1,
                    Utils.GetPrefabName(piece.gameObject),
                    "",
                    piece.transform.position,
                    piece.transform.rotation,
                    null));
            }

            var origin = EditorState.BottomCentre(standing);
            var pieces = new List<NewPiece>(standing.Count);
            foreach (var piece in standing)
            {
                pieces.Add(new NewPiece
                {
                    PrefabName = piece.PrefabName,
                    Position = piece.Position - origin,
                    Rotation = piece.Rotation,
                });
            }

            var document = BlueprintDocument.New(Name);
            document.AddPieces(pieces);
            LastCount = pieces.Count;
            ValheimTomrerPlugin.Log.LogInfo(
                $"capture: {LastCount} pieces, {skipped} skipped, origin {origin}, box {box}");

            EditorSession.OpenDocument(document);
            var message = $"Captured {LastCount} pieces." + (skipped > 0 ? " " + Left(skipped) : "");
            if (ModUi.Open)
            {
                Toasts.Ok(message);
            }
            else
            {
                Say(player, message);
            }

            return true;
        }

        /// <summary>
        /// The pieces in the box: built by a player, and of a kind the hammer has. Bottom up, so
        /// the file reads the way it would be built. <paramref name="skipped"/> counts the rest.
        /// </summary>
        public static List<Piece> PiecesIn(Bounds box, out int skipped)
        {
            skipped = 0;
            var kept = new List<Piece>();
            var all = new List<Piece>();
            Piece.GetAllPiecesInRadius(box.center, box.extents.magnitude + 1f, all);
            foreach (var piece in all)
            {
                if (piece == null || piece.transform == null || !piece.IsPlacedByPlayer())
                {
                    continue;
                }

                if (!box.Contains(piece.transform.position))
                {
                    continue;
                }

                if (PieceCatalog.Find(Utils.GetPrefabName(piece.gameObject)) == null)
                {
                    skipped++;
                    continue;
                }

                kept.Add(piece);
            }

            return kept
                .OrderBy(p => p.transform.position.y)
                .ThenBy(p => p.transform.position.x)
                .ThenBy(p => p.transform.position.z)
                .ToList();
        }

        /// <summary>
        /// The box two corners make: the rectangle between them, from half a metre under the lower
        /// one up to 16 m over the higher one. Two corners on flat ground still make a real box.
        /// </summary>
        public static Bounds BoxBetween(Vector3 a, Vector3 b)
        {
            var min = Vector3.Min(a, b);
            var max = Vector3.Max(a, b);
            min.y -= Under;
            max.y += Over;
            var box = new Bounds();
            box.SetMinMax(min, max);
            return box;
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

            // Skip anything that moves: a cart or a boat is no place for a corner.
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
            ValheimTomrerPlugin.Log.LogInfo("capture: " + text);
            player?.Message(MessageHud.MessageType.Center, text);
        }
    }
}
