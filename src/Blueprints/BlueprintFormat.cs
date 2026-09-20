using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Reads the text formats other Valheim blueprint mods share, so community files work:
    /// <c>.blueprint</c> (PlanBuild, Buildheim) and <c>.vbuild</c> (BuildShare).
    /// </summary>
    internal static class BlueprintFormat
    {
        public const int MaxPieces = 2000;

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

                // Old files wrote decimals with a comma.
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
                    new Vector3(Float(parts[5]), Float(parts[6]), Float(parts[7])),
                    new Quaternion(Float(parts[1]), Float(parts[2]), Float(parts[3]), Float(parts[4])));
            }

            return Finish(blueprint);
        }

        private static void ReadHeader(Blueprint blueprint, string line, ref bool readingPieces)
        {
            if (line.Equals("#Pieces", StringComparison.OrdinalIgnoreCase))
            {
                readingPieces = true;
                return;
            }

            // "#TerrainHeight:" and "#TerrainPaint:" hold grids of numbers, never pieces.
            if (line.Equals("#SnapPoints", StringComparison.OrdinalIgnoreCase)
                || line.Equals("#Description", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("#Terrain", StringComparison.OrdinalIgnoreCase))
            {
                readingPieces = false;
                return;
            }

            if (TryHeader(line, "#Name:", out var name) && name.Length > 0)
            {
                blueprint.Name = name;
            }
            else if (TryHeader(line, "#Description:", out var description))
            {
                blueprint.Description = description.Trim('"');
            }
            else if (TryHeader(line, "#Icon:", out var icon) && icon.Length > 0)
            {
                blueprint.IconPrefab = icon;
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

        private static void AddPiece(Blueprint blueprint, int lineNumber, string prefab, Vector3 position, Quaternion rotation)
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
                Position = position,
                Rotation = new Quaternion(
                    rotation.x / magnitude, rotation.y / magnitude, rotation.z / magnitude, rotation.w / magnitude),
            });

            if (blueprint.Pieces.Count > MaxPieces)
            {
                throw new FormatException($"more than {MaxPieces} pieces");
            }
        }

        private static Blueprint Finish(Blueprint blueprint)
        {
            if (blueprint.Pieces.Count == 0)
            {
                throw new FormatException("no pieces");
            }

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
