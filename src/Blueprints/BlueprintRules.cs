using System.Collections.Generic;
using System.Linq;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// The same rules the hammer applies to one piece, applied to a whole blueprint:
    /// unlocked pieces, materials in the inventory and the chests in range, crafting stations in range.
    /// </summary>
    internal static class BlueprintRules
    {
        /// <summary>
        /// Pieces the current build tool does not offer this player: not unlocked yet, or not a
        /// piece of this tool at all (a hoe piece in a hammer blueprint). Uses the hammer menu's own list.
        /// </summary>
        public static List<Piece> UnavailablePieces(Player player, ResolvedBlueprint blueprint)
        {
            var tool = player.GetBuildTool();
            if (tool == null)
            {
                return blueprint.Parts.Select(p => p.Piece).Distinct().ToList();
            }

            var available = new HashSet<string>(tool.m_availablePieces.Select(p => p.gameObject.name));
            return blueprint.Parts
                .Select(p => p.Piece)
                .Where(piece => !available.Contains(piece.gameObject.name)
                    || (piece.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(piece.m_dlc)))
                .Distinct()
                .ToList();
        }

        public static bool IsAvailable(Player player, ResolvedBlueprint blueprint)
        {
            return UnavailablePieces(player, blueprint).Count == 0;
        }

        /// <summary>Returns null when the player can build the blueprint right now, else the reason.</summary>
        public static string CheckCanBuild(Player player, ResolvedBlueprint blueprint)
        {
            var unavailable = UnavailablePieces(player, blueprint);
            if (unavailable.Count > 0)
            {
                return "Not unlocked yet: " + string.Join(", ", unavailable.Select(p => Localize(p.m_name)));
            }

            if (player.PlacementCostDisabled)
            {
                return null;
            }

            var sources = MaterialSources.Around(player);
            var missing = new List<string>();
            foreach (var need in MaterialNeeds(blueprint))
            {
                var have = sources.Count(need.Key);
                if (have < need.Value)
                {
                    missing.Add($"{need.Value - have} {Localize(need.Key)}");
                }
            }

            if (missing.Count > 0)
            {
                return "Missing: " + string.Join(", ", missing);
            }

            if (!ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
            {
                foreach (var station in blueprint.Stations)
                {
                    if (!blueprint.OwnStations.Contains(station.m_name)
                        && CraftingStation.HaveBuildStationInRange(station.m_name, player.transform.position) == null)
                    {
                        return "Needs a " + Localize(station.m_name) + " nearby";
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Takes the materials of every piece that is not free in this world, piece by piece: the
        /// inventory first, then the chests in range, nearest first. Call it after CheckCanBuild.
        /// </summary>
        public static void Pay(Player player, ResolvedBlueprint blueprint)
        {
            if (player.PlacementCostDisabled)
            {
                return;
            }

            var sources = MaterialSources.Around(player);
            var unpaid = 0;
            foreach (var part in blueprint.Parts)
            {
                if (ZoneSystem.instance.GetGlobalKey(part.Piece.FreeBuildKey()))
                {
                    continue;
                }

                foreach (var requirement in part.Piece.m_resources)
                {
                    if (requirement.m_resItem == null || requirement.m_amount <= 0)
                    {
                        continue;
                    }

                    if (!sources.Take(requirement.m_resItem.m_itemData.m_shared.m_name, requirement.m_amount))
                    {
                        unpaid++;
                    }
                }
            }

            var from = sources.ChestCount > 0 ? $" and {sources.ChestCount} chests within {sources.Range:0} m" : "";
            ValheimTomrerPlugin.Log.LogInfo($"paid for {blueprint.Name} from the inventory{from}"
                + (unpaid > 0 ? $", {unpaid} costs could not be paid" : ""));
        }

        /// <summary>Item name to amount, skipping pieces the world makes free.</summary>
        private static Dictionary<string, int> MaterialNeeds(ResolvedBlueprint blueprint)
        {
            var needs = new Dictionary<string, int>();
            foreach (var part in blueprint.Parts)
            {
                if (ZoneSystem.instance.GetGlobalKey(part.Piece.FreeBuildKey()))
                {
                    continue;
                }

                foreach (var requirement in part.Piece.m_resources)
                {
                    if (requirement.m_resItem == null || requirement.m_amount <= 0)
                    {
                        continue;
                    }

                    var item = requirement.m_resItem.m_itemData.m_shared.m_name;
                    needs.TryGetValue(item, out var amount);
                    needs[item] = amount + requirement.m_amount;
                }
            }

            return needs;
        }

        private static string Localize(string token)
        {
            return Localization.instance.Localize(token);
        }
    }
}
