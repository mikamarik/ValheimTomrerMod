using System.Collections.Generic;
using System.Linq;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// The same rules the hammer applies to one piece, applied to a whole blueprint: unlocked
    /// pieces, crafting stations in range, and paying for a piece from the inventory and the chests.
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

        /// <summary>
        /// Returns null when the player may build from the blueprint right now, else the reason:
        /// a piece not unlocked, or a station the blueprint does not bring that is not in range.
        /// Materials never refuse a build: a click builds what they pay for (<see cref="PartialBuild"/>).
        /// </summary>
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
        /// Takes one piece's materials: the inventory first, then the chests in range, nearest first.
        /// Nothing for a piece the world makes free. False and nothing taken when any item is short.
        /// </summary>
        public static bool PayFor(MaterialSources sources, Piece piece)
        {
            var cost = PartialBuild.CostOf(piece);
            foreach (var item in cost)
            {
                if (sources.Count(item.Key) < item.Value)
                {
                    return false;
                }
            }

            foreach (var item in cost)
            {
                sources.Take(item.Key, item.Value);
            }

            return true;
        }

        private static string Localize(string token)
        {
            return Localization.instance.Localize(token);
        }
    }
}
