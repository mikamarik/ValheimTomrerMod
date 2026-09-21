using System.Collections.Generic;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Where a blueprint build takes its materials from: the player's inventory, then every chest in
    /// range, nearest first. The list is made once by <see cref="Around"/>; counts are read live, so
    /// one list serves a whole build.
    ///
    /// Items leave only through the game's own <c>Inventory.RemoveItem</c>. A chest saves its items
    /// on that call, as it does when the player takes them by hand.
    /// </summary>
    internal sealed class MaterialSources
    {
        private static readonly List<Piece> Near = new List<Piece>();

        /// <summary>Take order: the inventory first, then the chests nearest first.</summary>
        private readonly List<Source> _sources = new List<Source>();

        private readonly List<Container> _chests = new List<Container>();

        private MaterialSources(float range)
        {
            Range = range;
        }

        /// <summary>How many chests are in the list (carts and ship holds included).</summary>
        public int ChestCount => _chests.Count;

        /// <summary>The range the chests were found in, in metres. 0 when chests are off.</summary>
        public float Range { get; }

        /// <summary>The chests in take order, nearest first.</summary>
        public IReadOnlyList<Container> Chests => _chests;

        /// <summary>The player's inventory and the chests around the player that pass <see cref="Counts"/>.</summary>
        public static MaterialSources Around(Player player)
        {
            var useChests = BuildConfig.UseChests == null || BuildConfig.UseChests.Value;
            var range = BuildConfig.ChestRange != null ? BuildConfig.ChestRange.Value : 20f;
            var sources = new MaterialSources(useChests ? range : 0f);
            sources._sources.Add(new Source(player.GetInventory(), player));
            if (!useChests || range <= 0f)
            {
                return sources;
            }

            var from = player.transform.position;
            var playerId = player.GetPlayerID();
            var found = new List<(Container Box, float Distance)>();
            Near.Clear();
            Piece.GetAllPiecesInRadius(from, range, Near);
            foreach (var piece in Near)
            {
                // A cart's or a ship's hold sits on a child object, so look there too.
                var box = piece != null ? piece.GetComponentInChildren<Container>() : null;
                if (box != null && Counts(box, piece, playerId))
                {
                    found.Add((box, Vector3.Distance(from, piece.transform.position)));
                }
            }

            Near.Clear();
            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            foreach (var entry in found)
            {
                sources._chests.Add(entry.Box);
                sources._sources.Add(new Source(entry.Box.GetInventory(), entry.Box));
            }

            return sources;
        }

        /// <summary>How many of this item the inventory and the chests hold together.</summary>
        public int Count(string itemName)
        {
            var total = 0;
            foreach (var source in _sources)
            {
                total += source.Count(itemName);
            }

            return total;
        }

        /// <summary>
        /// Takes this many, the inventory first, then the nearest chest. Takes nothing and returns
        /// false when all of them together do not have enough.
        /// </summary>
        public bool Take(string itemName, int amount)
        {
            if (amount <= 0)
            {
                return true;
            }

            if (Count(itemName) < amount)
            {
                return false;
            }

            foreach (var source in _sources)
            {
                var take = Mathf.Min(source.Count(itemName), amount);
                if (take <= 0)
                {
                    continue;
                }

                source.Remove(itemName, take);
                amount -= take;
                if (amount <= 0)
                {
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// A chest counts when the player could open it right now, by the game's own rules: placed by
        /// a player, not private to someone else, and let in by any ward that guards it.
        /// </summary>
        private static bool Counts(Container box, Piece piece, long playerId)
        {
            if (!piece.IsPlacedByPlayer() || box.GetInventory() == null)
            {
                return false;
            }

            // The game saves a chest's items only on the client that owns it. A take from a chest
            // someone else owns would come back on its next load.
            if (box.m_nview == null || !box.m_nview.IsValid() || !box.m_nview.IsOwner())
            {
                return false;
            }

            if (!MayOpen(box, piece, playerId))
            {
                return false;
            }

            return !box.m_checkGuardStone || PrivateArea.CheckAccess(box.transform.position, 0f, flash: false);
        }

        /// <summary>
        /// Container.CheckAccess reads the Piece on the container's own object. A cart's or a ship's
        /// hold has none there, so for those the same rule runs on the piece that was found.
        /// </summary>
        private static bool MayOpen(Container box, Piece piece, long playerId)
        {
            if (box.m_piece != null)
            {
                return box.CheckAccess(playerId);
            }

            switch (box.m_privacy)
            {
                case Container.PrivacySetting.Public:
                    return true;
                case Container.PrivacySetting.Private:
                    return piece.GetCreator() == playerId;
                default:
                    return false;
            }
        }

        /// <summary>
        /// One place items can come from: an inventory and the object that keeps it alive. A source
        /// that is not a game inventory (a drawer) can take its place by overriding the two calls.
        /// </summary>
        private class Source
        {
            private readonly Inventory _inventory;
            private readonly Object _owner;

            public Source(Inventory inventory, Object owner)
            {
                _inventory = inventory;
                _owner = owner;
            }

            /// <summary>0 once the chest is gone, so a list kept across frames stays safe.</summary>
            public virtual int Count(string itemName)
            {
                return _owner != null && _inventory != null ? _inventory.CountItems(itemName) : 0;
            }

            /// <summary>Only called with an amount <see cref="Count"/> just gave, so all of it is there.</summary>
            public virtual void Remove(string itemName, int amount)
            {
                // RemoveItem returns nothing, which is why Take counts first.
                _inventory.RemoveItem(itemName, amount);
            }
        }
    }
}
