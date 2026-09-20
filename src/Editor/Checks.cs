using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor
{
    internal enum CheckLevel
    {
        Error,
        Warning,
        Note,
    }

    /// <summary>One problem with the open blueprint, and the pieces it is about.</summary>
    internal sealed class Check
    {
        public CheckLevel Level;
        public string Message;

        /// <summary>Piece ids the row selects when it is clicked. Null when the row is about the file.</summary>
        public int[] Pieces;

        public string LevelWord =>
            Level == CheckLevel.Error ? "Error" : Level == CheckLevel.Warning ? "Warning" : "Note";
    }

    /// <summary>
    /// Everything that can be wrong with a blueprint, in one list: what stops the game reading it,
    /// what keeps it out of build mode, and what only looks odd. The same list as Tomrer's
    /// src/game/checks.ts, with one difference: where the browser guesses whether our reader would
    /// take the file, the editor asks the running game. Every piece has to be in the hammer's table,
    /// and for the blueprint to be offered in build mode, in the unlocked list as well.
    /// </summary>
    internal static class Checks
    {
        /// <summary>Two pieces closer than this count as the same spot, the game's own overlap rule.</summary>
        public const float SameSpot = 0.05f;

        /// <summary>How far apart two turns have to be before a rotated overlap is allowed.</summary>
        public const float RotatedOverlapAngle = 10f;

        /// <summary>The origin may sit this far from the bottom centre before it is worth a note.</summary>
        public const float OriginTolerance = 1f;

        /// <summary>
        /// Runs every check over a document. <paramref name="readError"/> is what the reader said
        /// when the file was opened, so a line the game cannot read is reported as well.
        /// </summary>
        public static List<Check> Run(BlueprintDocument document, string readError = null)
        {
            var found = new List<Check>();
            if (document == null)
            {
                return found;
            }

            var pieces = document.Pieces;
            ReadError(found, readError);

            if (pieces.Count == 0)
            {
                // Not an error: a new blueprint is named and saved before it has a piece, and the
                // file reads back fine. Only the build tool has nothing to do with it.
                Add(found, CheckLevel.Warning, "No pieces yet. The build tool skips an empty blueprint.");
            }
            else if (pieces.Count > BlueprintFormat.MaxPieces)
            {
                Add(found, CheckLevel.Error,
                    $"{pieces.Count} pieces. The mod reads at most {BlueprintFormat.MaxPieces}.");
            }

            Kinds(found, pieces);
            Scaled(found, pieces);
            SameSpotPieces(found, pieces);
            CardSlots(found, document);
            Icon(found, document);
            Origin(found, pieces);

            // OrderBy is stable, so rows of the same level keep the order they were found in.
            return found.OrderBy(c => (int)c.Level).ToList();
        }

        /// <summary>"2 errors, 1 warning", or "" when the list is empty.</summary>
        public static string Summary(IList<Check> checks)
        {
            if (checks == null || checks.Count == 0)
            {
                return "";
            }

            var parts = new List<string>(3);
            Count(parts, checks, CheckLevel.Error, "error");
            Count(parts, checks, CheckLevel.Warning, "warning");
            Count(parts, checks, CheckLevel.Note, "note");
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// The scale fields of a piece line, when it has them: field 10 onwards, as other mods
        /// write them. Null when the line stopped earlier or the numbers cannot be read.
        /// </summary>
        public static Vector3? ScaleOf(DocPiece piece)
        {
            if (piece == null || string.IsNullOrEmpty(piece.Rest))
            {
                return null;
            }

            // Rest is field 9 onwards: extra info, then the three scale fields.
            var parts = piece.Rest.Split(';');
            if (parts.Length < 4)
            {
                return null;
            }

            var scale = new float[3];
            for (var i = 0; i < 3; i++)
            {
                // Old files wrote the decimal mark as a comma.
                if (!float.TryParse(
                        parts[i + 1].Replace(',', '.'),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out scale[i]))
                {
                    return null;
                }
            }

            return new Vector3(scale[0], scale[1], scale[2]);
        }

        /// <summary>The ids of the pieces that sit within 5 cm of another piece of the same kind.</summary>
        public static List<int> SameSpotIds(IReadOnlyList<DocPiece> pieces)
        {
            var grid = new Dictionary<string, List<DocPiece>>();
            foreach (var piece in pieces)
            {
                var key = CellKey(piece);
                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<DocPiece>();
                    grid[key] = list;
                }

                list.Add(piece);
            }

            var found = new List<int>();
            var seen = new HashSet<int>();
            foreach (var piece in pieces)
            {
                var entry = PieceCatalog.Find(piece.PrefabName);
                var rotatedOk = entry != null && entry.AllowRotatedOverlap;
                for (var dx = -1; dx <= 1 && !seen.Contains(piece.Id); dx++)
                {
                    for (var dy = -1; dy <= 1 && !seen.Contains(piece.Id); dy++)
                    {
                        for (var dz = -1; dz <= 1 && !seen.Contains(piece.Id); dz++)
                        {
                            var key = CellKey(piece, dx, dy, dz);
                            if (!grid.TryGetValue(key, out var list))
                            {
                                continue;
                            }

                            foreach (var other in list)
                            {
                                if (other.Id == piece.Id
                                    || Vector3.Distance(piece.Position, other.Position) >= SameSpot)
                                {
                                    continue;
                                }

                                if (rotatedOk && Quaternion.Angle(piece.Rotation, other.Rotation) > RotatedOverlapAngle)
                                {
                                    continue;
                                }

                                found.Add(piece.Id);
                                seen.Add(piece.Id);
                                break;
                            }
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// The box round the whole blueprint, measured the way the mod measures it
        /// (BlueprintPreview.MeasureBounds): from the first piece's position, then every piece the
        /// game knows. Null for an empty blueprint.
        /// </summary>
        public static Bounds? Box(IReadOnlyList<DocPiece> pieces)
        {
            if (pieces == null || pieces.Count == 0)
            {
                return null;
            }

            var box = new Bounds(pieces[0].Position, Vector3.zero);
            foreach (var piece in pieces)
            {
                var entry = PieceCatalog.Find(piece.PrefabName);
                if (entry == null || entry.Bounds.size == Vector3.zero)
                {
                    continue;
                }

                box.Encapsulate(EditorState.BoxOf(piece));
            }

            return box;
        }

        // ---------- the checks ----------

        private static void ReadError(List<Check> found, string readError)
        {
            if (string.IsNullOrEmpty(readError))
            {
                return;
            }

            // The reader says "line 7: expected at least 9 fields, got 3".
            var line = 0;
            var text = readError;
            var at = readError.IndexOf("line ", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var rest = readError.Substring(at + 5);
                var colon = rest.IndexOf(':');
                if (colon > 0 && int.TryParse(rest.Substring(0, colon), out line))
                {
                    text = rest.Substring(colon + 1).Trim();
                }
            }

            Add(found, CheckLevel.Error, line > 0
                ? $"Line {line} cannot be read ({text}). The game skips this file."
                : $"The file cannot be read ({text}). The game skips it.");
        }

        /// <summary>Everything that depends on the piece kind: unknown, other tool, seasonal, DLC, locked.</summary>
        private static void Kinds(List<Check> found, IReadOnlyList<DocPiece> pieces)
        {
            var order = new List<string>();
            var byPrefab = new Dictionary<string, List<int>>();
            foreach (var piece in pieces)
            {
                if (!byPrefab.TryGetValue(piece.PrefabName, out var ids))
                {
                    ids = new List<int>();
                    byPrefab[piece.PrefabName] = ids;
                    order.Add(piece.PrefabName);
                }

                ids.Add(piece.Id);
            }

            foreach (var prefab in order)
            {
                var ids = byPrefab[prefab].ToArray();
                var entry = PieceCatalog.Find(prefab);
                if (entry == null)
                {
                    var tool = PieceCatalog.OtherTool(prefab);
                    Add(found, CheckLevel.Error, tool != null
                            ? $"{prefab} is a {tool.ToLowerInvariant()} piece ({Plural(ids.Length, "piece")}). "
                                + "The blueprint is never offered."
                            : $"Unknown piece {prefab} ({ids.Length}x). The game skips the whole blueprint.",
                        ids);
                    continue;
                }

                if (entry.Seasonal)
                {
                    Add(found, CheckLevel.Warning,
                        $"{entry.DisplayName} is seasonal. The blueprint is offered only in its season.", ids);
                }

                if (entry.Dlc.Length > 0 && DLCMan.instance != null && !DLCMan.instance.IsDLCInstalled(entry.Dlc))
                {
                    Add(found, CheckLevel.Warning,
                        $"{entry.DisplayName} needs the {entry.Dlc} DLC, which is not installed.", ids);
                }

                // The live half of the check: the hammer has the piece, but this character has not
                // learned it, so build mode does not offer the blueprint. Seasonal pieces say so above.
                if (!entry.Seasonal && !PieceCatalog.IsUnlocked(entry))
                {
                    Add(found, CheckLevel.Warning,
                        $"{entry.DisplayName} is not unlocked yet. The blueprint is not offered in build mode.",
                        ids);
                }
            }
        }

        private static void Scaled(List<Check> found, IReadOnlyList<DocPiece> pieces)
        {
            var ids = new List<int>();
            foreach (var piece in pieces)
            {
                var scale = ScaleOf(piece);
                if (scale != null && (Mathf.Abs(scale.Value.x - 1f) > 1e-4f
                        || Mathf.Abs(scale.Value.y - 1f) > 1e-4f
                        || Mathf.Abs(scale.Value.z - 1f) > 1e-4f))
                {
                    ids.Add(piece.Id);
                }
            }

            if (ids.Count > 0)
            {
                Add(found, CheckLevel.Warning,
                    $"{Plural(ids.Count, "piece")} with a scale other than 1. The mod builds them at normal size.",
                    ids.ToArray());
            }
        }

        private static void SameSpotPieces(List<Check> found, IReadOnlyList<DocPiece> pieces)
        {
            var ids = SameSpotIds(pieces);
            if (ids.Count > 0)
            {
                Add(found, CheckLevel.Warning,
                    $"{Plural(ids.Count, "piece")} sit in the same spot as another piece of the same kind.",
                    ids.ToArray());
            }
        }

        private static void CardSlots(List<Check> found, BlueprintDocument document)
        {
            var card = BlueprintCard.Build(document);
            if (card.Hidden.Count > 0)
            {
                Add(found, CheckLevel.Warning,
                    $"{card.Slots.Count + card.Hidden.Count} cost items and stations. "
                        + $"The card shows only {card.TotalSlots}.");
            }
        }

        private static void Icon(List<Check> found, BlueprintDocument document)
        {
            var icon = document.IconPrefab;
            if (string.IsNullOrEmpty(icon) || document.Pieces.Any(p => p.PrefabName == icon))
            {
                return;
            }

            Add(found, CheckLevel.Warning,
                $"The icon piece {icon} is not in the blueprint. The game shows the first piece's icon.");
        }

        private static void Origin(List<Check> found, IReadOnlyList<DocPiece> pieces)
        {
            var box = Box(pieces);
            if (box == null)
            {
                return;
            }

            var bottom = new Vector3(box.Value.center.x, box.Value.min.y, box.Value.center.z);
            if (bottom.magnitude > OriginTolerance)
            {
                Add(found, CheckLevel.Note,
                    $"The origin is {bottom.magnitude:0.0} m from the bottom centre. Our mod doesn't mind; "
                        + "other mods place blueprints by the origin.");
            }
        }

        // ---------- shared ----------

        private static void Add(List<Check> found, CheckLevel level, string message, int[] pieces = null)
        {
            found.Add(new Check { Level = level, Message = message, Pieces = pieces });
        }

        private static void Count(List<string> parts, IList<Check> checks, CheckLevel level, string word)
        {
            var count = 0;
            foreach (var check in checks)
            {
                if (check.Level == level)
                {
                    count++;
                }
            }

            if (count > 0)
            {
                parts.Add(Plural(count, word));
            }
        }

        private static string Plural(int count, string word)
        {
            return count + " " + word + (count == 1 ? "" : "s");
        }

        private static string CellKey(DocPiece piece, int dx = 0, int dy = 0, int dz = 0)
        {
            return piece.PrefabName
                + "|" + (Mathf.FloorToInt(piece.Position.x / SameSpot) + dx)
                + "|" + (Mathf.FloorToInt(piece.Position.y / SameSpot) + dy)
                + "|" + (Mathf.FloorToInt(piece.Position.z / SameSpot) + dz);
        }
    }
}
