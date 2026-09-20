using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Reads and writes the text formats other Valheim blueprint mods share, so community files
    /// work: <c>.blueprint</c> (PlanBuild, Buildheim, Infinity Hammer) and <c>.vbuild</c>
    /// (BuildShare, read only).
    /// </summary>
    internal static class BlueprintFormat
    {
        public const int MaxPieces = 2000;

        /// <summary>Decimals written for a position. Millimetres are plenty for building.</summary>
        public const int PositionDecimals = 4;

        /// <summary>Decimals written for a quaternion field, as the other mods write them.</summary>
        public const int RotationDecimals = 7;

        private const string Eol = "\n";

        /// <summary>
        /// <c>.blueprint</c>: <c>#Header:value</c> lines, then one piece per line:
        /// <c>prefab;category;posX;posY;posZ;rotX;rotY;rotZ;rotW;extraInfo[;scaleX;scaleY;scaleZ]</c>.
        /// Files with a <c>#Pieces</c> section only read pieces there. Old Infinity Hammer files have
        /// no such header and put the pieces after <c>#Terrain</c>, so without it every line with
        /// enough fields is a piece and shorter lines (snap points, terrain) are skipped.
        /// </summary>
        public static Blueprint ParseBlueprint(string fallbackName, IEnumerable<string> lines)
        {
            var blueprint = new Blueprint { Name = fallbackName };
            var allLines = lines.Select(l => l.Trim()).ToList();
            var hasPiecesSection = allLines.Any(l => l.Equals("#Pieces", StringComparison.OrdinalIgnoreCase));
            var readingPieces = true;
            var lineNumber = 0;

            foreach (var line in allLines)
            {
                lineNumber++;
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#"))
                {
                    ReadHeader(blueprint, line, ref readingPieces);
                    continue;
                }

                // Old files wrote decimals with a comma. The raw split keeps the text fields as written.
                var raw = line.Split(';');
                var parts = line.Replace(',', '.').Split(';');
                if (hasPiecesSection ? !readingPieces : parts.Length < 9)
                {
                    continue;
                }

                if (parts.Length < 9)
                {
                    throw new FormatException($"line {lineNumber}: expected at least 9 fields, got {parts.Length}");
                }

                AddPiece(
                    blueprint,
                    lineNumber,
                    parts[0],
                    raw.Length > 1 ? raw[1] : "",
                    raw.Length > 9 ? string.Join(";", raw.Skip(9).ToArray()) : null,
                    new Vector3(Float(parts[2]), Float(parts[3]), Float(parts[4])),
                    new Quaternion(Float(parts[5]), Float(parts[6]), Float(parts[7]), Float(parts[8])));
            }

            return Finish(blueprint);
        }

        /// <summary><c>.vbuild</c>: one piece per line, <c>prefab rotX rotY rotZ rotW posX posY posZ</c>.</summary>
        public static Blueprint ParseVBuild(string name, IEnumerable<string> lines)
        {
            var blueprint = new Blueprint { Name = name };
            var lineNumber = 0;

            foreach (var raw in lines)
            {
                lineNumber++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                var parts = line.Replace(',', '.').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8)
                {
                    throw new FormatException($"line {lineNumber}: expected 8 fields, got {parts.Length}");
                }

                AddPiece(
                    blueprint,
                    lineNumber,
                    parts[0],
                    "",
                    null,
                    new Vector3(Float(parts[5]), Float(parts[6]), Float(parts[7])),
                    new Quaternion(Float(parts[1]), Float(parts[2]), Float(parts[3]), Float(parts[4])));
            }

            return Finish(blueprint);
        }

        // ---------- writing ----------

        /// <summary>
        /// The whole file as text: the headers we know, then the ones we kept, then <c>#Pieces</c>.
        /// LF line ends and a line break after the last piece, the shape every other mod reads.
        /// </summary>
        public static string Write(Blueprint blueprint)
        {
            var text = new StringBuilder();
            AppendHeader(text, "#Name:", blueprint.Name);
            AppendHeader(text, "#Description:", blueprint.Description);
            AppendHeader(text, "#Icon:", blueprint.IconPrefab);
            foreach (var extra in blueprint.ExtraHeaders)
            {
                var kept = OneLine(extra);
                if (kept.Length > 0)
                {
                    text.Append(kept).Append(Eol);
                }
            }

            text.Append("#Pieces").Append(Eol);
            foreach (var piece in blueprint.Pieces)
            {
                text.Append(PieceLine(piece)).Append(Eol);
            }

            return text.ToString();
        }

        /// <summary>
        /// One piece line: <c>prefab;category;x;y;z;rx;ry;rz;rw</c>, plus the fields we kept.
        /// </summary>
        public static string PieceLine(BlueprintPiece piece)
        {
            var rotation = Canonical(Normalized(piece.Rotation));
            var line = new StringBuilder();
            line.Append(piece.PrefabName).Append(';')
                .Append(OneLine(piece.Category)).Append(';')
                .Append(FormatNumber(piece.Position.x, PositionDecimals)).Append(';')
                .Append(FormatNumber(piece.Position.y, PositionDecimals)).Append(';')
                .Append(FormatNumber(piece.Position.z, PositionDecimals)).Append(';')
                .Append(FormatNumber(rotation.x, RotationDecimals)).Append(';')
                .Append(FormatNumber(rotation.y, RotationDecimals)).Append(';')
                .Append(FormatNumber(rotation.z, RotationDecimals)).Append(';')
                .Append(FormatNumber(rotation.w, RotationDecimals));
            if (piece.Rest != null)
            {
                line.Append(';').Append(OneLine(piece.Rest));
            }

            return line.ToString();
        }

        /// <summary>
        /// Dot as decimal mark, rounded to <paramref name="decimals"/>, trailing zeros dropped,
        /// <c>-0</c> written as <c>0</c>, never an exponent.
        /// <para>
        /// The float is widened to double first, on purpose. Formatting a float directly rounds the
        /// shortest text that reads back as that float, so 0.00005 lands on an exact half and .NET
        /// rounds it to 0.0000. The double holds the float's real value, 0.000050000002, which
        /// rounds to 0.0001, the same as Tomrer's writer.
        /// </para>
        /// </summary>
        public static string FormatNumber(float value, int decimals)
        {
            var text = ((double)value).ToString(
                "F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (text.IndexOf('.') >= 0)
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }

            return text.Length == 0 || text == "-0" ? "0" : text;
        }

        /// <summary>"Camp hut" -> "camp-hut.blueprint": lowercase letters and digits, anything else one dash.</summary>
        public static string FileNameFor(string name)
        {
            var slug = new StringBuilder();
            foreach (var c in (name ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    slug.Append(c);
                }
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-')
                {
                    slug.Append('-');
                }
            }

            while (slug.Length > 0 && slug[slug.Length - 1] == '-')
            {
                slug.Length--;
            }

            return (slug.Length > 0 ? slug.ToString() : "blueprint") + ".blueprint";
        }

        /// <summary>
        /// One sign for q and -q, which are the same rotation: w positive, or when w is 0 the first
        /// non-zero of y, x, z. A half turn then writes <c>0;1;0;0</c> like the kits, not <c>0;-1;0;0</c>.
        /// </summary>
        public static Quaternion Canonical(Quaternion q)
        {
            const float epsilon = 1e-9f;
            foreach (var v in new[] { q.w, q.y, q.x, q.z })
            {
                if (Mathf.Abs(v) > epsilon)
                {
                    return v < 0f ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;
                }
            }

            return q;
        }

        // ---------- shared ----------

        private static void AppendHeader(StringBuilder text, string prefix, string value)
        {
            var one = OneLine(value);
            if (one.Length > 0)
            {
                text.Append(prefix).Append(one).Append(Eol);
            }
        }

        /// <summary>A header value or kept field can never carry a line break: each run becomes one space.</summary>
        private static string OneLine(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            var text = new StringBuilder(value.Length);
            var wasBreak = false;
            foreach (var c in value)
            {
                if (c == '\r' || c == '\n')
                {
                    if (!wasBreak)
                    {
                        text.Append(' ');
                    }

                    wasBreak = true;
                }
                else
                {
                    text.Append(c);
                    wasBreak = false;
                }
            }

            return text.ToString().Trim();
        }

        private static Quaternion Normalized(Quaternion q)
        {
            var magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(magnitude) || magnitude < 0.0001f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }

        private static void ReadHeader(Blueprint blueprint, string line, ref bool readingPieces)
        {
            if (line.Equals("#Pieces", StringComparison.OrdinalIgnoreCase))
            {
                readingPieces = true;
                return;
            }

            // "#TerrainHeight:" and "#TerrainPaint:" hold grids of numbers, never pieces. We cannot
            // write those sections back, so a file that has them can only be saved under a new name.
            if (line.Equals("#SnapPoints", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#Terrain", StringComparison.OrdinalIgnoreCase))
            {
                readingPieces = false;
                blueprint.HasSections = true;
                return;
            }

            // A bare "#Description" starts a block of text, not pieces.
            if (line.Equals("#Description", StringComparison.OrdinalIgnoreCase))
            {
                readingPieces = false;
                return;
            }

            if (TryHeader(line, "#Name:", out var name))
            {
                if (name.Length > 0)
                {
                    blueprint.Name = name;
                }
            }
            else if (TryHeader(line, "#Description:", out var description))
            {
                blueprint.Description = description.Trim('"');
            }
            else if (TryHeader(line, "#Icon:", out var icon))
            {
                if (icon.Length > 0)
                {
                    blueprint.IconPrefab = icon;
                }
            }
            else
            {
                blueprint.ExtraHeaders.Add(line);
            }
        }

        private static bool TryHeader(string line, string header, out string value)
        {
            if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                value = line.Substring(header.Length).Trim();
                return true;
            }

            value = null;
            return false;
        }

        private static void AddPiece(
            Blueprint blueprint,
            int lineNumber,
            string prefab,
            string category,
            string rest,
            Vector3 position,
            Quaternion rotation)
        {
            // Captured names can carry a "(Clone)" suffix.
            var name = prefab.Split('(')[0].Trim();
            if (name.Length == 0)
            {
                throw new FormatException($"line {lineNumber}: missing prefab name");
            }

            var magnitude = Mathf.Sqrt(
                rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
            if (!IsFinite(position) || float.IsNaN(magnitude) || magnitude < 0.0001f)
            {
                throw new FormatException($"line {lineNumber}: invalid position or rotation");
            }

            blueprint.Pieces.Add(new BlueprintPiece
            {
                PrefabName = name,
                Category = category ?? "",
                Rest = rest,
                Position = position,
                Rotation = new Quaternion(
                    rotation.x / magnitude, rotation.y / magnitude, rotation.z / magnitude, rotation.w / magnitude),
            });

            if (blueprint.Pieces.Count > MaxPieces)
            {
                throw new FormatException($"more than {MaxPieces} pieces");
            }
        }

        /// <summary>
        /// A blueprint with no pieces is a file like any other: the editor writes one the moment a
        /// new blueprint is named and saved, and reads it back to carry on. Only the build tool
        /// needs pieces, and <see cref="BlueprintLibrary"/> keeps empty ones out of its list.
        /// </summary>
        private static Blueprint Finish(Blueprint blueprint)
        {
            return blueprint;
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private static float Float(string s)
        {
            return string.IsNullOrEmpty(s) ? 0f : float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
