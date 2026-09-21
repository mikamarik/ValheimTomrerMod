using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>One material of a list: what the pieces not built yet need, and what the player has.</summary>
    internal sealed class TallyRow
    {
        /// <summary>The item's name token, "$item_wood". The key for <see cref="MaterialSources.Count"/>.</summary>
        public string Item;

        /// <summary>In the player's language, "Wood".</summary>
        public string Name;

        public Sprite Icon;
        public int Have;
        public int Need;

        public bool Enough => Have >= Need;
    }

    internal enum StationState
    {
        /// <summary>A real one is near enough to build with.</summary>
        InRange,

        /// <summary>None near enough.</summary>
        NotInRange,

        /// <summary>The blueprint builds one itself, so its other pieces can count on it.</summary>
        InBlueprint,

        /// <summary>The world needs no station to build (the NoWorkbench key).</summary>
        NotNeeded,
    }

    /// <summary>One crafting station the pieces not built yet need.</summary>
    internal sealed class StationRow
    {
        /// <summary>The station's name token, "$piece_workbench".</summary>
        public string Station;

        public string Name;
        public Sprite Icon;
        public StationState State;

        public bool Ok => State != StationState.NotInRange;
    }

    /// <summary>What a materials list shows. Made by <see cref="MaterialTally.For"/>.</summary>
    internal sealed class Tally
    {
        public readonly List<TallyRow> Rows = new List<TallyRow>();
        public readonly List<StationRow> Stations = new List<StationRow>();

        /// <summary>How many pieces the next click builds. -1 when the caller did not work it out (no spot to plan at).</summary>
        public int CanBuildNow = -1;

        public int BuiltCount;
        public int Total;

        /// <summary>True when some parts may already stand: an unfinished build. The footer then starts "Built".</summary>
        public bool Continuing;

        /// <summary>Costs are off (no-cost mode): nothing is taken, every row counts as enough.</summary>
        public bool CostsOff;

        public int ChestCount;

        /// <summary>The range the chests were found in, in metres. 0 when chests are off.</summary>
        public float Range;

        public int Missing => Total - BuiltCount;
    }

    /// <summary>
    /// The numbers behind a materials list: one row per item the pieces not built yet need, with
    /// what the player has (the bag and the chests in range), one row per station, and the counts.
    /// Reads only. Needs no preview: the hammer card and the editor both call it.
    /// </summary>
    internal static class MaterialTally
    {
        /// <param name="blueprint">The blueprint.</param>
        /// <param name="built">Which parts already stand, or null when none do.</param>
        /// <param name="sources">What the player has. Null counts as nothing (or costs off).</param>
        /// <param name="costsOff">No-cost mode: every row is enough.</param>
        /// <param name="stationsAt">Where a real station must be in range. Default: the local player.</param>
        public static Tally For(
            ResolvedBlueprint blueprint, bool[] built, MaterialSources sources, bool costsOff = false, Vector3? stationsAt = null)
        {
            var tally = new Tally
            {
                Total = blueprint.Parts.Count,
                Continuing = built != null,
                CostsOff = costsOff,
                ChestCount = sources != null ? sources.ChestCount : 0,
                Range = sources != null ? sources.Range : 0f,
            };

            // What the parts not built yet cost, item by item. Free-build keys count (PartialBuild.CostOf).
            var needs = new Dictionary<string, int>();
            var icons = new Dictionary<string, Sprite>();
            var stations = new List<CraftingStation>();
            for (var i = 0; i < blueprint.Parts.Count; i++)
            {
                if (built != null && i < built.Length && built[i])
                {
                    tally.BuiltCount++;
                    continue;
                }

                var piece = blueprint.Parts[i].Piece;
                foreach (var cost in PartialBuild.CostOf(piece))
                {
                    needs.TryGetValue(cost.Key, out var amount);
                    needs[cost.Key] = amount + cost.Value;
                }

                var station = piece.m_craftingStation;
                if (station != null && stations.All(s => s.m_name != station.m_name))
                {
                    stations.Add(station);
                }
            }

            foreach (var requirement in blueprint.TotalCost)
            {
                var item = requirement.m_resItem.m_itemData.m_shared.m_name;
                if (!icons.ContainsKey(item))
                {
                    icons[item] = requirement.m_resItem.m_itemData.GetIcon();
                }
            }

            // Most needed first, then by name, so the order holds still while the counts change.
            foreach (var need in needs.OrderByDescending(n => n.Value).ThenBy(n => n.Key))
            {
                icons.TryGetValue(need.Key, out var icon);
                tally.Rows.Add(new TallyRow
                {
                    Item = need.Key,
                    Name = Localize(need.Key),
                    Icon = icon,
                    Have = sources != null ? sources.Count(need.Key) : 0,
                    Need = need.Value,
                });
            }

            var at = stationsAt ?? (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero);
            var noStations = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench);
            foreach (var station in stations)
            {
                tally.Stations.Add(new StationRow
                {
                    Station = station.m_name,
                    Name = Localize(station.m_name),
                    Icon = station.m_icon,
                    State = noStations ? StationState.NotNeeded
                        : blueprint.OwnStations.Contains(station.m_name) ? StationState.InBlueprint
                        : CraftingStation.HaveBuildStationInRange(station.m_name, at) != null ? StationState.InRange
                        : StationState.NotInRange,
                });
            }

            return tally;
        }

        private static string Localize(string token)
        {
            return Localization.instance != null ? Localization.instance.Localize(token) : token;
        }
    }
}
