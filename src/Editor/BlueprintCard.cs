using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor
{
    /// <summary>
    /// What the game's build card will show for a blueprint, worked out the same way the card
    /// itself does (<see cref="Blueprints.ResolvedBlueprint"/> and
    /// <see cref="Blueprints.BlueprintInfoCard"/>), but from a document being edited: the name,
    /// the icon, the text, and the materials list (<see cref="Materials"/>).
    /// </summary>
    internal sealed class BlueprintCard
    {
        public string Name = "";

        public Sprite Icon;

        /// <summary>The whole text under the name, with the line break the game puts in.</summary>
        public string Description = "";

        public static BlueprintCard Build(BlueprintDocument document)
        {
            var card = new BlueprintCard();
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

            // The same line BlueprintInfoCard writes into the real card. Keep the two together.
            var edit = EditorConfig.Key != null ? $" {EditorConfig.Key.Value}: edit it." : "";
            var help = $"{document.Pieces.Count} pieces. Wheel: rotate. "
                + $"{ValheimTomrerPlugin.BlueprintKey.Value}: next blueprint.{edit}";
            card.Description = string.IsNullOrEmpty(document.Description) ? help : document.Description + "\n" + help;
            return card;
        }

        /// <summary>
        /// The materials list for a document, the same numbers the hammer card shows: one row per
        /// item its pieces cost (need) with what the player has (have), and one row per station.
        /// Only the hammer's pieces count, as before: the build tool never builds a blueprint with
        /// any other piece, and the problem list says so.
        /// </summary>
        /// <param name="sources">What the player has where they stand in the world. Null: nothing.</param>
        /// <param name="costsOff">No-cost mode: every row is enough.</param>
        /// <param name="stationsAt">Where a real station must be in range: the player, not the blueprint.</param>
        public static Tally Materials(BlueprintDocument document, MaterialSources sources, bool costsOff, Vector3? stationsAt = null)
        {
            var pieces = new List<Piece>(document != null ? document.Pieces.Count : 0);
            var own = new HashSet<string>();
            if (document != null)
            {
                foreach (var docPiece in document.Pieces)
                {
                    var piece = PieceOf(docPiece.PrefabName, out var entry);
                    if (piece == null)
                    {
                        continue;
                    }

                    pieces.Add(piece);
                    if (!string.IsNullOrEmpty(entry.OwnStationToken))
                    {
                        own.Add(entry.OwnStationToken);
                    }
                }
            }

            return MaterialTally.For(pieces, own, null, costsOff ? null : sources, costsOff, stationsAt);
        }

        /// <summary>The game's own piece for a hammer piece's name, or null.</summary>
        private static Piece PieceOf(string prefabName, out PieceEntry entry)
        {
            entry = PieceCatalog.Find(prefabName);
            if (entry == null)
            {
                return null;
            }

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            return piece != null ? piece : entry.Piece;
        }
    }
}
