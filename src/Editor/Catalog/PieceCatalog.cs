using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace ValheimTomrer.Editor.Catalog
{
    /// <summary>
    /// Every building piece the hammer knows, read once per world off the prefabs.
    ///
    /// Two lists are kept: <see cref="All"/> is the hammer's whole table, <see cref="Unlocked"/> is
    /// what this character has learned. <see cref="Visible"/> picks one from the config switch.
    /// The repair and remove tools are in neither: they are not pieces you can put in a blueprint.
    ///
    /// Nothing is instantiated and nothing is written back. Phase 0 timed the walk over all 398
    /// prefabs at 36 ms with no asset loads, so it is done in one go when the editor opens.
    /// </summary>
    internal static class PieceCatalog
    {
        private static readonly List<PieceEntry> AllPieces = new List<PieceEntry>();
        private static readonly List<PieceEntry> UnlockedPieces = new List<PieceEntry>();
        private static readonly HashSet<PieceEntry> UnlockedSet = new HashSet<PieceEntry>();
        private static readonly Dictionary<string, PieceEntry> ByPrefab = new Dictionary<string, PieceEntry>();
        private static readonly Dictionary<string, string> ToolOf = new Dictionary<string, string>();
        private static readonly List<string> TagsPresent = new List<string>();

        private static PieceTable _table;
        private static bool _staleUnlocks = true;

        /// <summary>Goes up whenever the lists change, so the palette can notice.</summary>
        public static int Generation { get; private set; }

        /// <summary>The hammer's whole table, minus repair and remove.</summary>
        public static IReadOnlyList<PieceEntry> All => AllPieces;

        /// <summary>What this character has unlocked, minus repair and remove.</summary>
        public static IReadOnlyList<PieceEntry> Unlocked => UnlockedPieces;

        /// <summary>What the palette shows: the config switch picks one of the two lists.</summary>
        public static IReadOnlyList<PieceEntry> Visible =>
            EditorConfig.ShowAllPieces != null && EditorConfig.ShowAllPieces.Value ? AllPieces : UnlockedPieces;

        /// <summary>Usage tags that at least one piece carries, in the game's order.</summary>
        public static IReadOnlyList<string> Tags => TagsPresent;

        public static bool Ready => AllPieces.Count > 0;

        /// <summary>The table the lists were read from. Null before the first build.</summary>
        public static PieceTable Table => _table;

        /// <summary>
        /// Builds the catalog if it is missing or the build tool changed, and refreshes the
        /// unlocked list if the player learned something. Cheap to call every frame.
        /// </summary>
        public static bool Ensure()
        {
            var table = FindTable();
            if (table == null)
            {
                return false;
            }

            if (table != _table)
            {
                Build(table);
                return true;
            }

            if (_staleUnlocks)
            {
                ReadUnlocked();
                Generation++;
            }

            return AllPieces.Count > 0;
        }

        /// <summary>The player learned or forgot a recipe: read the unlocked list again next tick.</summary>
        public static void Invalidate()
        {
            _staleUnlocks = true;
        }

        /// <summary>Drops everything. Called on a world change and on plugin unload.</summary>
        public static void Clear()
        {
            AllPieces.Clear();
            UnlockedPieces.Clear();
            UnlockedSet.Clear();
            ByPrefab.Clear();
            ToolOf.Clear();
            TagsPresent.Clear();
            _table = null;
            _staleUnlocks = true;
            Generation++;
        }

        public static PieceEntry Find(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return null;
            }

            return ByPrefab.TryGetValue(prefabName, out var entry) ? entry : null;
        }

        /// <summary>
        /// The tool a piece belongs to when it is not a hammer piece: "Hoe", "Cultivator" and the
        /// like. Null for hammer pieces and for names the game does not know.
        /// </summary>
        public static string OtherTool(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName) || ByPrefab.ContainsKey(prefabName))
            {
                return null;
            }

            return ToolOf.TryGetValue(prefabName, out var tool) ? tool : null;
        }

        public static bool IsUnlocked(PieceEntry entry)
        {
            return entry != null && UnlockedSet.Contains(entry);
        }

        /// <summary>
        /// Short reason this piece cannot go in a blueprint right now, or null when it is fine.
        /// The piece list panel shows it as a tag on the row.
        /// </summary>
        public static string Problem(string prefabName)
        {
            var entry = Find(prefabName);
            if (entry == null)
            {
                var tool = OtherTool(prefabName);
                return tool != null ? tool.ToLowerInvariant() + " piece" : "unknown";
            }

            if (entry.Dlc.Length > 0 && DLCMan.instance != null && !DLCMan.instance.IsDLCInstalled(entry.Dlc))
            {
                return "dlc";
            }

            if (entry.Seasonal)
            {
                return "seasonal";
            }

            return IsUnlocked(entry) ? null : "locked";
        }

        // ---------- building ----------

        /// <summary>
        /// The hammer's table. The player's own build tool when a hammer is in hand, else the one
        /// off the Hammer item, so the editor works with empty hands too.
        /// </summary>
        private static PieceTable FindTable()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                return null;
            }

            var table = player.GetBuildTool();
            if (table != null)
            {
                return table;
            }

            var db = ObjectDB.instance;
            var hammer = db != null ? db.GetItemPrefab("Hammer") : null;
            var item = hammer != null ? hammer.GetComponent<ItemDrop>() : null;
            return item != null ? item.m_itemData.m_shared.m_buildPieces : null;
        }

        private static void Build(PieceTable table)
        {
            var clock = Stopwatch.StartNew();
            AllPieces.Clear();
            ByPrefab.Clear();
            _table = table;

            var order = 0;
            var tools = 0;
            foreach (var prefab in table.m_pieces)
            {
                var entry = PieceEntry.Read(prefab, order++);
                if (entry == null)
                {
                    continue;
                }

                if (entry.RepairPiece || entry.RemovePiece)
                {
                    tools++;
                    continue;
                }

                AllPieces.Add(entry);
                ByPrefab[entry.PrefabName] = entry;
            }

            TagsPresent.Clear();
            foreach (var tag in PieceEntry.AllTags)
            {
                if (AllPieces.Any(p => (p.Usage & tag) != 0))
                {
                    TagsPresent.Add(tag.ToString());
                }
            }

            ReadOtherTools(table);
            ReadUnlocked();
            Generation++;
            ValheimTomrerPlugin.Log.LogInfo(
                $"piece catalog: {AllPieces.Count} pieces ({tools} tools skipped), {UnlockedPieces.Count} unlocked, "
                + $"{TagsPresent.Count} tags, {ToolOf.Count} pieces of other tools, {clock.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// Pieces of the other build tools (hoe, cultivator, the feast table), so a blueprint that
        /// holds one can be told apart from a blueprint that holds a typo.
        /// </summary>
        private static void ReadOtherTools(PieceTable hammer)
        {
            ToolOf.Clear();
            var db = ObjectDB.instance;
            if (db == null)
            {
                return;
            }

            foreach (var prefab in db.m_items)
            {
                var item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                var table = item != null ? item.m_itemData.m_shared.m_buildPieces : null;
                if (table == null || table == hammer)
                {
                    continue;
                }

                var tool = Localization.instance != null
                    ? Localization.instance.Localize(item.m_itemData.m_shared.m_name)
                    : prefab.name;
                foreach (var piece in table.m_pieces)
                {
                    if (piece != null && !ByPrefab.ContainsKey(piece.name) && !ToolOf.ContainsKey(piece.name))
                    {
                        ToolOf[piece.name] = tool;
                    }
                }
            }
        }

        /// <summary>
        /// The unlocked list. The table's own set is the truth when the game has filled it; with no
        /// hammer in hand it is empty, so the same rule PieceTable.UpdateAvailable uses is applied here.
        /// </summary>
        private static void ReadUnlocked()
        {
            UnlockedPieces.Clear();
            _staleUnlocks = false;

            var player = Player.m_localPlayer;
            if (_table != null && _table.m_availablePieces.Count > 0)
            {
                foreach (var entry in AllPieces)
                {
                    if (_table.m_availablePieces.Contains(entry.Piece))
                    {
                        UnlockedPieces.Add(entry);
                    }
                }

                Index();
                return;
            }

            if (player == null)
            {
                Index();
                return;
            }

            var free = player.PlacementCostDisabled
                || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.AllPiecesUnlocked));
            foreach (var entry in AllPieces)
            {
                if (free)
                {
                    UnlockedPieces.Add(entry);
                    continue;
                }


                var inSeason = !entry.Seasonal
                    || (player.CurrentSeason != null && player.CurrentSeason.Pieces.Contains(entry.Prefab));
                if (inSeason && player.m_knownRecipes.Contains(entry.NameToken))
                {
                    UnlockedPieces.Add(entry);
                }
            }

            Index();
        }

        private static void Index()
        {
            UnlockedSet.Clear();
            foreach (var entry in UnlockedPieces)
            {
                UnlockedSet.Add(entry);
            }
        }
    }
}
