using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor
{
    internal enum CardSlotKind
    {
        Cost,
        Station,
    }

    /// <summary>One square of the build card: a material with its amount, or a station.</summary>
    internal sealed class CardSlot
    {
        public CardSlotKind Kind;
        public string Name;
        public Sprite Icon;

        /// <summary>Cost slots only.</summary>
        public int Amount;

        /// <summary>Station slots only: the blueprint builds this station itself, so it counts as present.</summary>
        public bool Own;
    }

    /// <summary>
    /// What the game's build card will show for a blueprint, worked out the same way the card
    /// itself does (<see cref="Blueprints.ResolvedBlueprint"/> and
    /// <see cref="Blueprints.BlueprintInfoCard"/>), but from a document being edited.
    ///
    /// The same numbers as Tomrer's src/game/card.ts, so the editor and the browser agree.
    /// </summary>
    internal sealed class BlueprintCard
    {
        /// <summary>Slots on the vanilla card when the HUD is not up to ask.</summary>
        public const int DefaultSlots = 6;

        public string Name = "";

        public Sprite Icon;

        /// <summary>The whole text under the name, with the line break the game puts in.</summary>
        public string Description = "";

        /// <summary>What fits on the card.</summary>
        public readonly List<CardSlot> Slots = new List<CardSlot>();

        /// <summary>What does not fit.</summary>
        public readonly List<CardSlot> Hidden = new List<CardSlot>();

        public int TotalSlots = DefaultSlots;

        /// <summary>How many squares the running game's card has.</summary>
        public static int SlotCount()
        {
            var hud = Hud.instance;
            return hud != null && hud.m_requirementItems != null && hud.m_requirementItems.Length > 0
                ? hud.m_requirementItems.Length
                : DefaultSlots;
        }

        public static BlueprintCard Build(BlueprintDocument document)
        {
            var card = new BlueprintCard { TotalSlots = SlotCount() };
            if (document == null)
            {
                return card;
            }

            var known = new List<PieceEntry>(document.Pieces.Count);
            foreach (var piece in document.Pieces)
            {
                var entry = PieceCatalog.Find(piece.PrefabName);
                if (entry != null)
                {
                    known.Add(entry);
                }
            }

            card.Name = document.Name ?? "";

            // The #Icon: piece when the blueprint has it, else the first piece, the way the game picks.
            var iconPiece = known.FirstOrDefault(e => e.PrefabName == document.IconPrefab) ?? known.FirstOrDefault();
            card.Icon = iconPiece != null ? iconPiece.Icon : null;

            var help = $"{document.Pieces.Count} pieces. Wheel: rotate. "
                + $"{ValheimTomrerPlugin.BlueprintKey.Value}: next blueprint.";
            card.Description = string.IsNullOrEmpty(document.Description) ? help : document.Description + "\n" + help;

            var all = CostSlots(known);
            all.AddRange(StationSlots(known));
            for (var i = 0; i < all.Count; i++)
            {
                (i < card.TotalSlots ? card.Slots : card.Hidden).Add(all[i]);
            }

            return card;
        }

        /// <summary>One slot per material, most needed first, the way the card sorts them.</summary>
        private static List<CardSlot> CostSlots(List<PieceEntry> known)
        {
            var order = new List<string>();
            var totals = new Dictionary<string, CardSlot>();
            foreach (var entry in known)
            {
                foreach (var cost in entry.Cost)
                {
                    if (cost.Amount <= 0)
                    {
                        continue;
                    }

                    if (totals.TryGetValue(cost.Token, out var slot))
                    {
                        slot.Amount += cost.Amount;
                        continue;
                    }

                    totals[cost.Token] = new CardSlot
                    {
                        Kind = CardSlotKind.Cost,
                        Name = cost.Name,
                        Icon = cost.Icon,
                        Amount = cost.Amount,
                    };
                    order.Add(cost.Token);
                }
            }

            // OrderByDescending keeps the first-seen order between equal amounts, like the game's own sort.
            return order.Select(token => totals[token]).OrderByDescending(slot => slot.Amount).ToList();
        }

        /// <summary>One slot per station type, in first-seen order.</summary>
        private static List<CardSlot> StationSlots(List<PieceEntry> known)
        {
            var own = new HashSet<string>(known
                .Where(e => !string.IsNullOrEmpty(e.OwnStationToken))
                .Select(e => e.OwnStationToken));

            var slots = new List<CardSlot>();
            var seen = new HashSet<string>();
            foreach (var entry in known)
            {
                if (string.IsNullOrEmpty(entry.StationToken) || !seen.Add(entry.StationToken))
                {
                    continue;
                }

                slots.Add(new CardSlot
                {
                    Kind = CardSlotKind.Station,
                    Name = entry.StationName,
                    Icon = entry.StationIcon,
                    Own = own.Contains(entry.StationToken),
                });
            }

            return slots;
        }
    }
}
