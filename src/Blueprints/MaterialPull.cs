using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Blueprints
{
    /// <summary>Single-player gathering for an expedition, using the game's own inventory transfers.</summary>
    internal static class MaterialPull
    {
        private static readonly Dictionary<string, bool> Selection = new Dictionary<string, bool>();

        private static ItemDrop.ItemData Definition(string token)
        {
            if (ObjectDB.instance == null)
            {
                return null;
            }

            foreach (var prefab in ObjectDB.instance.m_items)
            {
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop != null && drop.m_itemData.m_shared.m_name == token)
                {
                    return drop.m_itemData;
                }
            }

            return null;
        }

        public static bool IsNonTeleportable(string token)
        {
            var item = Definition(token);
            return item != null && !item.m_shared.m_teleportable;
        }

        public static bool IsBulk(string token)
        {
            var item = Definition(token);
            var prefab = item?.m_dropPrefab;
            // ItemData on an ObjectDB prefab may not have m_dropPrefab set yet.
            var name = prefab != null ? prefab.name : null;
            if (name == null && ObjectDB.instance != null)
            {
                foreach (var candidate in ObjectDB.instance.m_items)
                {
                    var drop = candidate != null ? candidate.GetComponent<ItemDrop>() : null;
                    if (drop != null && drop.m_itemData.m_shared.m_name == token)
                    {
                        name = candidate.name;
                        break;
                    }
                }
            }

            var excluded = BuildConfig.PullExcludedPrefabs?.Value
                ?? "Wood,RoundLog,FineWood,ElderBark,YggdrasilWood,Blackwood,Stone,Grausten";
            foreach (var entry in excluded.Split(','))
            {
                if (string.Equals(entry.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool Selected(string token)
        {
            return Selection.TryGetValue(token, out var on) ? on : !IsBulk(token);
        }

        public static void Select(string token, bool on) => Selection[token] = on;

        public static string Pull(Player player, BlueprintDocument document)
        {
            if (player == null || document == null || player.IsDead() || player.IsTeleporting())
            {
                return "No live player or blueprint to pull for.";
            }

            var bag = player.GetInventory();
            var sources = MaterialSources.Around(player);
            var tally = BlueprintCard.Materials(document, sources, false);
            var moved = 0;
            var missing = 0;
            var noRoom = 0;
            try
            {
                foreach (var row in tally.Rows)
                {
                    if (!Selected(row.Item))
                    {
                        continue;
                    }

                    var wanted = Mathf.Max(0, row.Need - bag.CountItems(row.Item));
                    var available = Mathf.Max(0, sources.Count(row.Item) - bag.CountItems(row.Item));
                    missing += Mathf.Max(0, wanted - available);
                    var target = Mathf.Min(wanted, available);
                    var remaining = target;
                    foreach (var chest in sources.Chests)
                    {
                        if (remaining == 0)
                        {
                            break;
                        }
                        if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid()
                            || !chest.m_nview.IsOwner())
                        {
                            continue;
                        }

                        var from = chest.GetInventory();
                        if (from == null || ReferenceEquals(from, bag))
                        {
                            continue;
                        }

                        foreach (var item in new List<ItemDrop.ItemData>(from.GetAllItems()))
                        {
                            if (remaining == 0)
                            {
                                break;
                            }
                            if (item.m_shared.m_name != row.Item || !from.ContainsItem(item))
                            {
                                continue;
                            }

                            // Fill matching stacks before empty slots. Native MoveItemToThis
                            // preserves metadata and changes/saves both real inventories.
                            for (var pass = 0; pass < 2 && remaining > 0 && from.ContainsItem(item); pass++)
                            {
                                for (var y = 0; y < bag.GetHeight() && remaining > 0 && from.ContainsItem(item); y++)
                                {
                                    for (var x = 0; x < bag.GetWidth() && remaining > 0 && from.ContainsItem(item); x++)
                                    {
                                        var at = bag.GetItemAt(x, y);
                                        if (pass == 0 && (at == null || !item.IsSameType(at) || !at.IsSameType(item)))
                                        {
                                            continue;
                                        }
                                        if (pass == 1 && at != null)
                                        {
                                            continue;
                                        }
                                        var room = at == null ? item.m_shared.m_maxStackSize
                                            : Mathf.Max(0, at.m_shared.m_maxStackSize - at.m_stack);
                                        var amount = Mathf.Min(remaining, Mathf.Min(item.m_stack, room));
                                        if (amount <= 0 || !bag.CanAddItem(item, amount))
                                        {
                                            continue;
                                        }

                                        var beforeBag = bag.CountItems(row.Item);
                                        var beforeSource = from.CountItems(row.Item);
                                        bag.MoveItemToThis(from, item, amount, x, y);
                                        var gained = bag.CountItems(row.Item) - beforeBag;
                                        var taken = beforeSource - from.CountItems(row.Item);
                                        if (gained != taken || gained < 0 || gained > amount)
                                        {
                                            throw new InvalidOperationException("Inventory transfer counts did not match.");
                                        }
                                        moved += gained;
                                        remaining -= gained;
                                    }
                                }
                            }
                        }
                    }
                    noRoom += remaining;
                }
            }
            catch (Exception error)
            {
                ValheimTomrerPlugin.Log.LogError("Material pull stopped: " + error);
                return "Pull stopped after " + moved + " items. Check the BepInEx log before retrying.";
            }

            return "Pulled " + moved + " items. " + missing + " unavailable nearby; "
                + noRoom + " available items could not be transferred. Unselected materials stay in storage.";
        }
    }
}
