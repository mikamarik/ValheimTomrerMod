using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Placement;

namespace ValheimTomrer.Blueprints
{
    /// <summary>What one <see cref="PartialBuild.Plan(ResolvedBlueprint, Vector3, float, bool[], MaterialSources, bool)"/> call did. For the log and the tests.</summary>
    internal sealed class PlanStats
    {
        /// <summary>"nothing left", "no cost", "all paid" or "passes": which step of the plan answered.</summary>
        public string Route;

        /// <summary>Rounds of the loop in step 5. Each one solves the support once.</summary>
        public int Passes;

        public int Solves;
        public int Evaluates;

        /// <summary>Parts that fall even with the whole blueprint built. The support check skips them.</summary>
        public int Exempt;

        public double Milliseconds;
    }

    /// <summary>
    /// Which parts of a blueprint the next click builds: every part the materials pay for and that
    /// would stand, bottom to top. It only reads: the blueprint, where it stands, which parts are
    /// built, what the player has, the ground and the stations. Nothing changes in the world.
    ///
    /// The steps (the plan's "How the next batch is chosen"):
    ///   1. costs off: every unbuilt part, bottom to top;
    ///   2. the materials pay for every unbuilt part: the same, as the full build always did;
    ///   3. otherwise solve the support of the whole blueprint once. A part that falls even then is
    ///      not held back by the support check (the model is careful and can refuse a piece the
    ///      game would keep);
    ///   4. sort the unbuilt parts: height, then distance from the middle, then file order;
    ///   5. rounds until one adds nothing. Each round solves the support of what stands so far, then
    ///      takes every part in order that is paid for, has its station, and would stand on that.
    /// </summary>
    internal static class PartialBuild
    {
        /// <summary>The ground is read once per 5 cm cell and kept for the rest of the call.</summary>
        private const float GroundCells = 20f;

        /// <summary>The parts to build, in the order to build them. Empty when nothing can go up now.</summary>
        public static List<int> Plan(
            ResolvedBlueprint blueprint, Vector3 rootPosition, float rootYaw, bool[] built, MaterialSources sources, bool noCost)
        {
            var budget = noCost || sources == null ? new Dictionary<string, int>() : Budget(blueprint, built, sources);
            return Plan(blueprint, rootPosition, rootYaw, built, budget, noCost, null);
        }

        /// <summary>The same, with the budget given as item name to count. The tests call this one.</summary>
        internal static List<int> Plan(
            ResolvedBlueprint blueprint, Vector3 rootPosition, float rootYaw, bool[] built,
            IDictionary<string, int> budget, bool noCost, PlanStats stats)
        {
            var watch = Stopwatch.StartNew();
            var parts = blueprint.Parts;
            var count = parts.Count;
            var taken = new bool[count];
            if (built != null)
            {
                Array.Copy(built, taken, Math.Min(count, built.Length));
            }

            var unbuilt = Enumerable.Range(0, count).Where(i => !taken[i]).ToList();
            if (unbuilt.Count == 0)
            {
                Done(stats, "nothing left", watch);
                return unbuilt;
            }

            var costs = parts.Select(p => CostOf(p.Piece)).ToArray();

            // 1 and 2: bottom to top, as the full build always did. The game sorts out the support.
            if (noCost || Pays(unbuilt, costs, budget))
            {
                Done(stats, noCost ? "no cost" : "all paid", watch);
                return unbuilt.OrderBy(i => parts[i].Source.Position.y).ToList();
            }

            // 3. The whole blueprint, once.
            PieceCatalog.Ensure();
            var ground = GroundUnder(rootPosition, rootYaw);
            var scene = new ScenePiece[count];
            for (var i = 0; i < count; i++)
            {
                var name = parts[i].Prefab.name;
                scene[i] = new ScenePiece
                {
                    Id = i,
                    Prefab = name,
                    Pos = parts[i].Source.Position,
                    Rot = parts[i].Source.Rotation,
                    Entry = PieceCatalog.Find(name),
                };
            }

            var whole = Support.Solve(scene, ground);
            var exempt = new bool[count];
            for (var i = 0; i < count; i++)
            {
                exempt[i] = whole.Falls(i);
            }

            if (stats != null)
            {
                stats.Solves++;
                stats.Exempt = exempt.Count(e => e);
            }

            // 4. Height, then distance from the middle of the footprint, then file order.
            var middle = FootprintMiddle(parts);
            var order = unbuilt
                .OrderBy(i => parts[i].Source.Position.y)
                .ThenBy(i => Flat(parts[i].Source.Position, middle))
                .ThenBy(i => i)
                .ToList();

            // 5. Rounds.
            var stations = new StationCheck(blueprint, rootPosition, rootYaw);
            var left = new Dictionary<string, int>(budget);
            var chosen = new List<int>();
            var placed = new PlacedPiece[1];
            var values = new float[1];
            var falls = new bool[1];
            while (order.Any(i => !taken[i] && Affordable(costs[i], left) && stations.Has(i, taken)))
            {
                var map = Support.Solve(scene.Where(s => taken[s.Id]), ground);
                if (stats != null)
                {
                    stats.Passes++;
                    stats.Solves++;
                }

                var added = 0;
                foreach (var i in order)
                {
                    // A part skipped here does not end the round: a cheaper one further on can still go.
                    if (taken[i] || !Affordable(costs[i], left) || !stations.Has(i, taken))
                    {
                        continue;
                    }

                    if (!exempt[i])
                    {
                        placed[0] = new PlacedPiece { Id = i, Prefab = scene[i].Prefab, Pos = scene[i].Pos, Rot = scene[i].Rot, Entry = scene[i].Entry };
                        Support.Evaluate(map, placed, values, falls);
                        if (stats != null)
                        {
                            stats.Evaluates++;
                        }

                        // Held up only by a part chosen in this round: it waits for the next one.
                        if (falls[0])
                        {
                            continue;
                        }
                    }

                    taken[i] = true;
                    chosen.Add(i);
                    Spend(costs[i], left);
                    added++;
                }

                if (added == 0)
                {
                    break;
                }
            }

            Done(stats, "passes", watch);
            return chosen;
        }

        /// <summary>
        /// Item name to how many more the unbuilt parts need than the player has, most first. Empty
        /// when the materials are all there, or the world makes every part free.
        /// </summary>
        public static List<KeyValuePair<string, int>> Missing(ResolvedBlueprint blueprint, bool[] built, MaterialSources sources)
        {
            var missing = new List<KeyValuePair<string, int>>();
            foreach (var need in Needs(blueprint, built))
            {
                var have = sources != null ? sources.Count(need.Key) : 0;
                if (have < need.Value)
                {
                    missing.Add(new KeyValuePair<string, int>(need.Key, need.Value - have));
                }
            }

            return missing.OrderByDescending(m => m.Value).ToList();
        }

        /// <summary>"40 Wood, 12 Stone": the list <see cref="Missing"/> gives, in the player's language.</summary>
        public static string MissingText(IEnumerable<KeyValuePair<string, int>> missing)
        {
            return string.Join(", ", missing.Select(m => $"{m.Value} {Localization.instance.Localize(m.Key)}"));
        }

        /// <summary>What one piece costs here: item name and amount. Nothing when the world makes it free.</summary>
        public static List<KeyValuePair<string, int>> CostOf(Piece piece)
        {
            var cost = new List<KeyValuePair<string, int>>();
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
            {
                return cost;
            }

            foreach (var requirement in piece.m_resources)
            {
                if (requirement.m_resItem == null || requirement.m_amount <= 0)
                {
                    continue;
                }

                var item = requirement.m_resItem.m_itemData.m_shared.m_name;
                var at = cost.FindIndex(c => c.Key == item);
                if (at < 0)
                {
                    cost.Add(new KeyValuePair<string, int>(item, requirement.m_amount));
                }
                else
                {
                    cost[at] = new KeyValuePair<string, int>(item, cost[at].Value + requirement.m_amount);
                }
            }

            return cost;
        }

        /// <summary>
        /// The ground under a point in the blueprint's space, in that space: the terrain height under
        /// its world spot, minus the root's height. The root only ever turns around y. Where the
        /// terrain is not loaded there is no ground at all, the careful answer.
        /// </summary>
        internal static Func<Vector3, float> GroundUnder(Vector3 rootPosition, float rootYaw)
        {
            var turn = Quaternion.Euler(0f, rootYaw, 0f);
            var memo = new Dictionary<long, float>();
            return local =>
            {
                var world = rootPosition + (turn * local);
                var key = ((long)Mathf.RoundToInt(world.x * GroundCells) << 32) ^ (uint)Mathf.RoundToInt(world.z * GroundCells);
                if (!memo.TryGetValue(key, out var height))
                {
                    height = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(world, out var y)
                        ? y - rootPosition.y
                        : float.NegativeInfinity;
                    memo[key] = height;
                }

                return height;
            };
        }

        /// <summary>Item name to how many the player has, for every item the unbuilt parts need. Read once.</summary>
        private static Dictionary<string, int> Budget(ResolvedBlueprint blueprint, bool[] built, MaterialSources sources)
        {
            var budget = new Dictionary<string, int>();
            foreach (var item in Needs(blueprint, built).Keys)
            {
                budget[item] = sources.Count(item);
            }

            return budget;
        }

        /// <summary>Item name to amount, over the parts not built yet.</summary>
        private static Dictionary<string, int> Needs(ResolvedBlueprint blueprint, bool[] built)
        {
            var needs = new Dictionary<string, int>();
            for (var i = 0; i < blueprint.Parts.Count; i++)
            {
                if (built != null && i < built.Length && built[i])
                {
                    continue;
                }

                foreach (var cost in CostOf(blueprint.Parts[i].Piece))
                {
                    needs.TryGetValue(cost.Key, out var amount);
                    needs[cost.Key] = amount + cost.Value;
                }
            }

            return needs;
        }

        private static bool Pays(List<int> parts, List<KeyValuePair<string, int>>[] costs, IDictionary<string, int> budget)
        {
            var needs = new Dictionary<string, int>();
            foreach (var i in parts)
            {
                foreach (var cost in costs[i])
                {
                    needs.TryGetValue(cost.Key, out var amount);
                    needs[cost.Key] = amount + cost.Value;
                }
            }

            foreach (var need in needs)
            {
                if (!budget.TryGetValue(need.Key, out var have) || have < need.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Affordable(List<KeyValuePair<string, int>> cost, Dictionary<string, int> left)
        {
            foreach (var item in cost)
            {
                if (!left.TryGetValue(item.Key, out var have) || have < item.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static void Spend(List<KeyValuePair<string, int>> cost, Dictionary<string, int> left)
        {
            foreach (var item in cost)
            {
                left[item.Key] -= item.Value;
            }
        }

        /// <summary>The middle of the box around every pivot, seen from above.</summary>
        private static Vector3 FootprintMiddle(List<ResolvedPart> parts)
        {
            var box = new Bounds(parts[0].Source.Position, Vector3.zero);
            foreach (var part in parts)
            {
                box.Encapsulate(part.Source.Position);
            }

            return box.center;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            return new Vector2(a.x - b.x, a.z - b.z).magnitude;
        }

        private static void Done(PlanStats stats, string route, Stopwatch watch)
        {
            if (stats != null)
            {
                stats.Route = route;
                stats.Milliseconds = watch.Elapsed.TotalMilliseconds;
            }
        }

        /// <summary>
        /// Step 5c: a part that needs a station has one when the world has none to build with, a real
        /// one is in range of the part's spot (the game's own ghost test), or a station of the
        /// blueprint that is built or chosen stands within its build range. The real stations do not
        /// change during a plan, so each part asks the world once.
        /// </summary>
        private sealed class StationCheck
        {
            private readonly List<ResolvedPart> _parts;
            private readonly Vector3[] _world;
            private readonly sbyte[] _real;
            private readonly List<(int Part, string Name, float Range)> _own = new List<(int, string, float)>();
            private readonly bool _noWorkbench;

            public StationCheck(ResolvedBlueprint blueprint, Vector3 rootPosition, float rootYaw)
            {
                _parts = blueprint.Parts;
                var turn = Quaternion.Euler(0f, rootYaw, 0f);
                _world = _parts.Select(p => rootPosition + (turn * p.Source.Position)).ToArray();
                _real = new sbyte[_parts.Count];
                _noWorkbench = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench);
                for (var i = 0; i < _parts.Count; i++)
                {
                    var station = _parts[i].Prefab.GetComponentInChildren<CraftingStation>(true);
                    if (station != null)
                    {
                        _own.Add((i, station.m_name, station.m_rangeBuild));
                    }
                }
            }

            public bool Has(int i, bool[] taken)
            {
                var needs = _parts[i].Piece.m_craftingStation;
                if (needs == null || _noWorkbench)
                {
                    return true;
                }

                foreach (var own in _own)
                {
                    if (taken[own.Part] && own.Name == needs.m_name && Flat(_world[own.Part], _world[i]) < own.Range)
                    {
                        return true;
                    }
                }

                if (_real[i] == 0)
                {
                    _real[i] = (sbyte)(CraftingStation.HaveBuildStationInRange(needs.m_name, _world[i]) != null ? 1 : -1);
                }

                return _real[i] > 0;
            }
        }
    }
}
