using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>A read-only leveling plan followed by a reversible, locally owned terrain transaction.</summary>
    internal sealed class FoundationTerrain : IDisposable
    {
        // Valheim's terrain grid interpolates between vertices. One diagonal of padding
        // keeps the entire foundation inside the levelled area, including rotated edges.
        public const float Margin = 1.5f;
        private const float MaxDelta = 8f;
        private const int MaxVertices = 16384;
        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly BlueprintFoundation _foundation;
        private readonly Vector3 _position;
        private readonly Quaternion _rotation;
        private bool _applied;
        private bool _committed;

        private sealed class Tile
        {
            public Heightmap Map;
            public TerrainComp Compiler;
            public readonly List<Cell> Cells = new List<Cell>();
            public int Operations;
            public Vector3 LastPoint;
            public float LastRadius;
        }

        private sealed class Cell
        {
            public int Index;
            public int X;
            public int Z;
            public float Level;
            public float Smooth;
            public bool Modified;
            public float Next;
        }

        private FoundationTerrain(BlueprintFoundation foundation, Vector3 position, Quaternion rotation)
        {
            _foundation = foundation;
            _position = position;
            _rotation = rotation;
        }

        public float Height => _position.y + _foundation.Bounds.min.y + 0.05f;

        public static Func<Vector3, float> ProjectedGround(BlueprintFoundation foundation, Vector3 position, Quaternion rotation)
        {
            return new FoundationTerrain(foundation, position, rotation).GroundAt;
        }

        /// <summary>The support solver works in blueprint space; world terrain is sampled outside the prepared area.</summary>
        public float GroundAt(Vector3 local)
        {
            if (_foundation.Contains(local, Margin)) return Height - _position.y;
            var world = _position + _rotation * local;
            return ZoneSystem.instance.GetGroundHeight(world) - _position.y;
        }

        internal static float TargetDelta(float currentHeight, float level, float smooth, float targetHeight)
        {
            return level + smooth + targetHeight - currentHeight;
        }

        /// <summary>Intersection of the editable height intervals of every foundation vertex.
        /// Heights are relative to the original terrain, including existing level and smooth edits.
        /// This only reads terrain; the build click still validates ownership and loaded coverage.</summary>
        public static bool TryHeightLimits(BlueprintFoundation foundation, Vector3 position, Quaternion rotation,
            out float minRootHeight, out float maxRootHeight)
        {
            minRootHeight = float.NegativeInfinity;
            maxRootHeight = float.PositiveInfinity;
            var bounds = foundation.Bounds;
            var back = Quaternion.Inverse(rotation);
            var count = 0;
            var nx = Mathf.Max(1, Mathf.CeilToInt((bounds.size.x + 2f * Margin) / 2f));
            var nz = Mathf.Max(1, Mathf.CeilToInt((bounds.size.z + 2f * Margin) / 2f));
            if ((long)(nx + 1) * (nz + 1) > MaxVertices) return false;
            for (var z = 0; z <= nz; z++)
            for (var x = 0; x <= nx; x++)
            {
                var local = new Vector3(Mathf.Lerp(bounds.min.x - Margin, bounds.max.x + Margin, x / (float)nx),
                    bounds.min.y, Mathf.Lerp(bounds.min.z - Margin, bounds.max.z + Margin, z / (float)nz));
                var map = Heightmap.FindHeightmap(position + rotation * local);
                if (map == null || map.IsDistantLod || !map.isActiveAndEnabled) return false;
            }
            foreach (var map in Heightmap.GetAllHeightmaps())
            {
                if (map == null || map.IsDistantLod || !map.isActiveAndEnabled || map.m_scale <= 0f) continue;
                var width = map.m_width;
                var origin = map.transform.position;
                var half = width * map.m_scale * 0.5f;
                var middle = back * (origin - position);
                var reach = half * 1.415f;
                if (middle.x + reach < bounds.min.x - Margin || middle.x - reach > bounds.max.x + Margin
                    || middle.z + reach < bounds.min.z - Margin || middle.z - reach > bounds.max.z + Margin) continue;
                var compiler = TerrainComp.FindTerrainCompiler(origin);
                if (compiler != null) compiler.CheckLoad();
                for (var z = 0; z <= width; z++)
                for (var x = 0; x <= width; x++)
                {
                    var world = new Vector3(origin.x - half + x * map.m_scale,
                        origin.y + map.GetHeight(x, z), origin.z - half + z * map.m_scale);
                    if (!foundation.Contains(back * (world - position), Margin)) continue;
                    if (++count > MaxVertices) return false;
                    var index = z * (width + 1) + x;
                    if (compiler != null && !Ready(compiler, index)) return false;
                    var level = compiler != null ? compiler.m_levelDelta[index] : 0f;
                    var smooth = compiler != null ? compiler.m_smoothDelta[index] : 0f;
                    var original = world.y - level - smooth;
                    if (float.IsNaN(original) || float.IsInfinity(original)) return false;
                    // Stay just inside the native cap to avoid float-rounding rejection on Apply.
                    minRootHeight = Mathf.Max(minRootHeight, original - (MaxDelta - 0.01f) - bounds.min.y - 0.05f);
                    maxRootHeight = Mathf.Min(maxRootHeight, original + (MaxDelta - 0.01f) - bounds.min.y - 0.05f);
                }
            }
            return count > 0;
        }

        public static bool TryPlan(BlueprintFoundation foundation, Vector3 position, Quaternion rotation,
            out FoundationTerrain plan, out string error)
        {
            plan = new FoundationTerrain(foundation, position, rotation);
            error = null;
            if (ZoneSystem.instance == null || ZNet.instance == null)
            {
                error = "Terrain is not ready.";
                return false;
            }
            // Check loaded coverage before creating any compiler or changing any height.
            var bounds = foundation.Bounds;
            var nx = Mathf.Max(1, Mathf.CeilToInt((bounds.size.x + 2f * Margin) / 2f));
            var nz = Mathf.Max(1, Mathf.CeilToInt((bounds.size.z + 2f * Margin) / 2f));
            if ((long)(nx + 1) * (nz + 1) > MaxVertices)
            {
                error = "Foundation is too large to prepare in one placement.";
                return false;
            }
            for (var z = 0; z <= nz; z++)
            for (var x = 0; x <= nx; x++)
            {
                var local = new Vector3(Mathf.Lerp(bounds.min.x - Margin, bounds.max.x + Margin, x / (float)nx),
                    bounds.min.y, Mathf.Lerp(bounds.min.z - Margin, bounds.max.z + Margin, z / (float)nz));
                var world = position + rotation * local;
                var map = Heightmap.FindHeightmap(world);
                if (map == null || map.IsDistantLod || !map.isActiveAndEnabled
                    || !ZNetScene.InActiveArea(world, ZNet.instance.GetReferencePosition()))
                {
                    error = "The whole foundation must be on loaded outdoor terrain.";
                    return false;
                }
                if (Location.IsInsideNoBuildLocation(world) || !PrivateArea.CheckAccess(world, 0f, false))
                {
                    error = "Ground preparation crosses a protected or no-build area.";
                    return false;
                }
            }

            var back = Quaternion.Inverse(rotation);
            var count = 0;
            foreach (var map in Heightmap.GetAllHeightmaps())
            {
                if (map == null || map.IsDistantLod || !map.isActiveAndEnabled || map.m_scale <= 0f) continue;
                var tile = new Tile { Map = map, Compiler = TerrainComp.FindTerrainCompiler(map.transform.position) };
                if (tile.Compiler != null) tile.Compiler.CheckLoad();
                var width = map.m_width;
                var origin = map.transform.position;
                var half = width * map.m_scale * 0.5f;
                // Coarse zone rejection avoids scanning all loaded maps for a small blueprint.
                var middle = back * (origin - position);
                var reach = half * 1.415f;
                if (middle.x + reach < bounds.min.x - Margin || middle.x - reach > bounds.max.x + Margin
                    || middle.z + reach < bounds.min.z - Margin || middle.z - reach > bounds.max.z + Margin) continue;
                for (var z = 0; z <= width; z++)
                for (var x = 0; x <= width; x++)
                {
                    var world = new Vector3(origin.x - half + x * map.m_scale,
                        origin.y + map.GetHeight(x, z), origin.z - half + z * map.m_scale);
                    if (!foundation.Contains(back * (world - position), Margin)) continue;
                    if (++count > MaxVertices) { error = "Foundation is too large to prepare in one placement."; return false; }
                    if (Location.IsInsideNoBuildLocation(world) || !PrivateArea.CheckAccess(world, 0f, false))
                    { error = "Ground preparation crosses a protected or no-build area."; return false; }
                    var index = z * (width + 1) + x;
                    var compiler = tile.Compiler;
                    if (compiler != null && !Ready(compiler, index))
                    { error = "Terrain data is not locally owned and ready."; return false; }
                    var level = compiler != null ? compiler.m_levelDelta[index] : 0f;
                    var smooth = compiler != null ? compiler.m_smoothDelta[index] : 0f;
                    var delta = TargetDelta(world.y, level, smooth, plan.Height);
                    if (float.IsNaN(delta) || float.IsInfinity(delta) || Mathf.Abs(delta) > MaxDelta)
                    { error = "This placement exceeds the game's terrain height limit. Choose a closer height."; return false; }
                    tile.Cells.Add(new Cell { Index = index, X = x, Z = z, Next = delta });
                }
                if (tile.Cells.Count > 0) plan._tiles.Add(tile);
            }
            if (count == 0) { error = "No editable terrain below this foundation."; return false; }
            return true;
        }

        private static bool Ready(TerrainComp compiler, int index)
        {
            return compiler.m_nview != null && compiler.m_nview.IsValid() && compiler.m_nview.IsOwner()
                && compiler.m_initialized && compiler.m_levelDelta != null && compiler.m_smoothDelta != null
                && compiler.m_modifiedHeight != null && index < compiler.m_levelDelta.Length
                && index < compiler.m_smoothDelta.Length && index < compiler.m_modifiedHeight.Length;
        }

        public bool Apply(out string error)
        {
            error = null;
            try
            {
                // Acquire and validate every affected tile before touching the first vertex.
                foreach (var tile in _tiles)
                {
                    tile.Compiler = tile.Map.GetAndCreateTerrainCompiler();
                    if (tile.Compiler == null) throw new InvalidOperationException("Terrain compiler unavailable.");
                    tile.Compiler.CheckLoad();
                    foreach (var cell in tile.Cells)
                    {
                        if (!Ready(tile.Compiler, cell.Index)) throw new InvalidOperationException("Terrain ownership or data changed.");
                        cell.Level = tile.Compiler.m_levelDelta[cell.Index];
                        cell.Smooth = tile.Compiler.m_smoothDelta[cell.Index];
                        cell.Modified = tile.Compiler.m_modifiedHeight[cell.Index];
                        cell.Next = TargetDelta(tile.Map.transform.position.y + tile.Map.GetHeight(cell.X, cell.Z),
                            cell.Level, cell.Smooth, Height);
                        if (float.IsNaN(cell.Next) || float.IsInfinity(cell.Next) || Mathf.Abs(cell.Next) > MaxDelta)
                            throw new InvalidOperationException("Terrain height limit exceeded.");
                    }
                    tile.Operations = tile.Compiler.m_operations;
                    tile.LastPoint = tile.Compiler.m_lastOpPoint;
                    tile.LastRadius = tile.Compiler.m_lastOpRadius;
                }
                _applied = true;
                foreach (var tile in _tiles)
                {
                    foreach (var cell in tile.Cells)
                    {
                        tile.Compiler.m_levelDelta[cell.Index] = cell.Next;
                        tile.Compiler.m_smoothDelta[cell.Index] = 0f;
                        tile.Compiler.m_modifiedHeight[cell.Index] = true;
                    }
                    tile.Compiler.m_operations++;
                    tile.Compiler.m_lastOpPoint = _position + _rotation * _foundation.Bounds.center;
                    tile.Compiler.m_lastOpRadius = _foundation.Bounds.extents.magnitude + Margin;
                    tile.Compiler.Save();
                    tile.Map.Poke(0, false);
                }
                Physics.SyncTransforms();
                return true;
            }
            catch (Exception e)
            {
                Dispose();
                error = "Ground preparation stopped: " + e.Message;
                ValheimTomrerPlugin.Log.LogError(error);
                return false;
            }
        }

        public void Commit() => _committed = true;

        public void Dispose()
        {
            if (!_applied || _committed) return;
            _applied = false;
            foreach (var tile in _tiles)
            {
                try
                {
                    foreach (var cell in tile.Cells)
                    {
                        tile.Compiler.m_levelDelta[cell.Index] = cell.Level;
                        tile.Compiler.m_smoothDelta[cell.Index] = cell.Smooth;
                        tile.Compiler.m_modifiedHeight[cell.Index] = cell.Modified;
                    }
                    tile.Compiler.m_operations = tile.Operations;
                    tile.Compiler.m_lastOpPoint = tile.LastPoint;
                    tile.Compiler.m_lastOpRadius = tile.LastRadius;
                    tile.Compiler.Save();
                    tile.Map.Poke(0, false);
                }
                catch (Exception e) { ValheimTomrerPlugin.Log.LogError("Terrain rollback failed: " + e); }
            }
            Physics.SyncTransforms();
        }
    }
}
