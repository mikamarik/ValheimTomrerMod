using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>A blueprint piece matched to its game prefab.</summary>
    internal sealed class ResolvedPart
    {
        public BlueprintPiece Source;
        public GameObject Prefab;
        public Piece Piece;
    }

    /// <summary>
    /// A blueprint matched to the prefabs of the running game, with its total cost worked out once.
    /// Only valid while a world is loaded.
    /// </summary>
    internal sealed class ResolvedBlueprint
    {
        public Blueprint Blueprint { get; private set; }
        public List<ResolvedPart> Parts { get; private set; }
        public Sprite Icon { get; private set; }

        /// <summary>Summed material cost, one entry per item, most needed first.</summary>
        public List<Piece.Requirement> TotalCost { get; private set; }

        /// <summary>Crafting stations the pieces need, one entry per station type.</summary>
        public List<CraftingStation> Stations { get; private set; }

        /// <summary>Station types the blueprint builds itself, so its other pieces can count on them.</summary>
        public HashSet<string> OwnStations { get; private set; }

        public string Name => Blueprint.Name;

        public static bool TryResolve(Blueprint blueprint, out ResolvedBlueprint resolved, out string error)
        {
            resolved = null;
            error = null;

            var parts = new List<ResolvedPart>();
            var missing = new List<string>();
            foreach (var source in blueprint.Pieces)
            {
                var prefab = ZNetScene.instance.GetPrefab(source.PrefabName);
                var piece = prefab ? prefab.GetComponent<Piece>() : null;
                if (piece == null)
                {
                    missing.Add(source.PrefabName);
                    continue;
                }

                parts.Add(new ResolvedPart { Source = source, Prefab = prefab, Piece = piece });
            }

            if (missing.Count > 0)
            {
                error = $"{blueprint.Name}: unknown pieces {string.Join(", ", missing.Distinct())}";
                return false;
            }

            resolved = new ResolvedBlueprint
            {
                Blueprint = blueprint,
                Parts = parts,
                Icon = FindIcon(blueprint, parts),
                TotalCost = SumCost(parts),
                Stations = parts
                    .Where(p => p.Piece.m_craftingStation != null)
                    .Select(p => p.Piece.m_craftingStation)
                    .GroupBy(s => s.m_name)
                    .Select(g => g.First())
                    .ToList(),
                OwnStations = new HashSet<string>(parts
                    .Select(p => p.Prefab.GetComponentInChildren<CraftingStation>(true))
                    .Where(s => s != null)
                    .Select(s => s.m_name)),
            };
            return true;
        }

        private static Sprite FindIcon(Blueprint blueprint, List<ResolvedPart> parts)
        {
            var iconPart = parts.FirstOrDefault(p => p.Source.PrefabName == blueprint.IconPrefab) ?? parts[0];
            return iconPart.Piece.m_icon;
        }

        private static List<Piece.Requirement> SumCost(List<ResolvedPart> parts)
        {
            var totals = new Dictionary<string, Piece.Requirement>();
            foreach (var part in parts)
            {
                foreach (var requirement in part.Piece.m_resources)
                {
                    if (requirement.m_resItem == null || requirement.m_amount <= 0)
                    {
                        continue;
                    }

                    var item = requirement.m_resItem.m_itemData.m_shared.m_name;
                    if (!totals.TryGetValue(item, out var total))
                    {
                        total = new Piece.Requirement { m_resItem = requirement.m_resItem, m_amount = 0 };
                        totals.Add(item, total);
                    }

                    total.m_amount += requirement.m_amount;
                }
            }

            return totals.Values.OrderByDescending(r => r.m_amount).ToList();
        }
    }
}
