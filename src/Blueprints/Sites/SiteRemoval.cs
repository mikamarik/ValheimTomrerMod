using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Editor.Placement;

namespace ValheimTomrer.Blueprints.Sites
{
    /// <summary>
    /// "Whole structure": takes down every built piece of an unfinished build the way the hammer's
    /// Remove takes down one (<c>Player.RemovePiece</c>): the same checks, the same game calls, and so
    /// the same materials back, dropped where each piece stood. Top to bottom, the reverse of the
    /// build order.
    ///
    /// A piece the game would not let the player remove stays, and the message says why. So does
    /// every piece it needs to stand (the support model), so nothing is left to fall and break.
    /// One hammer swing for all of it, as for a build click.
    /// </summary>
    internal static class SiteRemoval
    {
        /// <summary>A piece further than this from one that stays is never tested as its support.</summary>
        private const float HoldRange = 16f;

        /// <summary>Why the game would not let the player remove a piece. The counts in the message follow this order.</summary>
        internal enum Why
        {
            Tool,
            CannotBeRemoved,
            NoBuildZone,
            Ward,
            Station,
            InUse,
        }

        internal sealed class Result
        {
            /// <summary>The site's pieces standing in the world when it started.</summary>
            public int Built;

            public int Removed;

            /// <summary>Pieces the game would not let go, by reason. A station reason is per station ("$piece_workbench").</summary>
            public readonly List<(Why Why, string Station, int Count)> Left = new List<(Why, string, int)>();

            /// <summary>Pieces that could go but stay, because a piece that stays needs them to stand.</summary>
            public int HoldingUp;

            /// <summary>Why nothing was done at all (too far, no stamina), or null.</summary>
            public string Blocked;

            /// <summary>The pieces taken down, for the log and the tests. Destroyed at the end of the frame.</summary>
            public readonly List<Piece> Pieces = new List<Piece>();

            public int LeftCount => Left.Sum(l => l.Count);
        }

        /// <summary>The last take-down, for the tests.</summary>
        public static Result Last { get; private set; }

        /// <summary>
        /// Reads the world again, then takes down every built piece the game lets the player remove.
        /// Changes nothing when <see cref="Result.Blocked"/> is set. The caller forgets the plan.
        /// </summary>
        public static Result TakeDown(Player player, Site site)
        {
            var result = new Result();
            Last = result;
            var pieces = SiteTracker.BuiltPieces(site);
            var built = Enumerable.Range(0, site.Total).Where(i => pieces[i] != null).ToList();
            result.Built = built.Count;
            if (built.Count == 0)
            {
                return result;
            }

            var distance = Mathf.Sqrt(site.WorldBox.SqrDistance(player.transform.position));
            if (distance > BlueprintMode.ContinueRange)
            {
                result.Blocked = $"Too far from {site.Name}. Come within {BlueprintMode.ContinueRange:0} m.";
                return result;
            }

            // The game checks the stamina for one swing before each remove. Here one swing does all.
            var tool = player.GetRightItem();
            if (tool == null)
            {
                result.Blocked = "Take the hammer in hand first.";
                return result;
            }

            if (!player.HaveStamina(tool.m_shared.m_attack.m_attackStamina))
            {
                Hud.instance.StaminaBarEmptyFlash();
                result.Blocked = "Not enough stamina.";
                return result;
            }

            var refused = new Dictionary<int, (Why Why, string Station)>();
            foreach (var i in built)
            {
                var why = WhyNot(player, tool, pieces[i], out var station);
                if (why.HasValue)
                {
                    refused[i] = (why.Value, station);
                }
            }

            var holding = HoldingUp(site, built, refused.Keys);
            result.HoldingUp = holding.Count;
            foreach (var group in refused.Values.GroupBy(r => r).OrderBy(g => g.Key.Why))
            {
                result.Left.Add((group.Key.Why, group.Key.Station, group.Count()));
            }

            // Top to bottom: the reverse of the order a click builds in.
            var order = PartialBuild.BuildOrder(site.Resolved, built.Where(i => !refused.ContainsKey(i) && !holding.Contains(i)));
            order.Reverse();
            Vector3? first = null;
            foreach (var i in order)
            {
                var piece = pieces[i];
                if (piece == null || piece.m_nview == null || !piece.m_nview.IsValid())
                {
                    continue;
                }

                first = first ?? piece.transform.position;
                result.Pieces.Add(piece);
                RemoveOne(player, piece);
                result.Removed++;
            }

            if (first.HasValue)
            {
                Swing(player, tool, first.Value);
            }

            ValheimTomrerPlugin.Log.LogInfo($"took down {site.Name}: {result.Removed} of {result.Built} pieces, "
                + $"{result.LeftCount} refused ({string.Join(", ", result.Left.Select(l => $"{l.Count} {l.Why}{(l.Station != null ? " " + l.Station : "")}"))}), "
                + $"{result.HoldingUp} kept to hold them up");
            return result;
        }

        /// <summary>
        /// "Workshop removed: 4 of 6 pieces taken down. Left standing: 2 need a workbench nearby, 1 holds
        /// them up." The plan itself is always gone by then.
        /// </summary>
        public static string Message(Site site, Result result)
        {
            var left = result.LeftCount + result.HoldingUp;
            if (left == 0)
            {
                return $"{site.Name} removed: {Pieces(result.Removed)} taken down.";
            }

            var parts = result.Left.Select(l => Reason(l.Why, l.Station, l.Count)).ToList();
            if (result.HoldingUp > 0)
            {
                var them = result.LeftCount == 1 ? "it" : "them";
                parts.Add(result.HoldingUp == 1 ? $"1 holds {them} up" : $"{result.HoldingUp} hold {them} up");
            }

            var head = result.Removed == 0
                ? $"Plan for {site.Name} removed, no piece taken down."
                : $"{site.Name} removed: {result.Removed} of {Pieces(result.Built)} taken down.";
            return $"{head} Left standing: {string.Join(", ", parts)}.";
        }

        /// <summary>
        /// Player.RemovePiece's checks, in its order, for one piece, with the tool rule of
        /// Player.UpdatePlacement in front. Null when the game would let the player remove it. Nothing
        /// is said and no ward flashes: the message counts them all at the end.
        /// </summary>
        internal static Why? WhyNot(Player player, ItemDrop.ItemData tool, Piece piece, out string station)
        {
            station = null;
            if (!ToolRemoves(tool, piece))
            {
                return Why.Tool;
            }

            if (!piece.m_canBeRemoved)
            {
                return Why.CannotBeRemoved;
            }

            var at = piece.transform.position;
            if (Location.IsInsideNoBuildLocation(at))
            {
                return Why.NoBuildZone;
            }

            if (!PrivateArea.CheckAccess(at, 0f, false))
            {
                return Why.Ward;
            }

            // CheckCanRemovePiece: the station the piece needs, near the player, not near the piece.
            if (!player.m_noPlacementCost && piece.m_craftingStation != null
                && !StationInRange(piece.m_craftingStation.m_name, player.transform.position)
                && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench))
            {
                station = piece.m_craftingStation.m_name;
                return Why.Station;
            }

            if (piece.GetComponent<ZNetView>() == null)
            {
                return Why.CannotBeRemoved;
            }

            if (!piece.CanBeRemoved())
            {
                return Why.InUse;
            }

            return null;
        }

        /// <summary>
        /// The tool rule in Player.UpdatePlacement: the hammer removes pieces, the serving tray removes
        /// feasts and food on display, each only its own kind.
        /// </summary>
        private static bool ToolRemoves(ItemDrop.ItemData tool, Piece piece)
        {
            var pieces = tool.m_shared.m_buildPieces;
            if (pieces == null)
            {
                return false;
            }

            var drop = piece.GetComponent<ItemDrop>();
            var feastLike = piece.GetComponent<Feast>() != null || (drop != null && drop.IsPiece());
            return (pieces.m_canRemovePieces && !feastLike) || (pieces.m_canRemoveFeasts && feastLike);
        }

        /// <summary>
        /// CraftingStation.HaveBuildStationInRange, reading the range the station worked out last
        /// instead of asking for it: asking runs its extension update, which throws on the game's own
        /// one-piece ghost. A ghost or a copy is no station here, only a real network object is.
        /// </summary>
        private static bool StationInRange(string name, Vector3 point)
        {
            foreach (var station in CraftingStation.m_allStations)
            {
                if (station == null || station.m_name != name)
                {
                    continue;
                }

                var view = station.GetComponent<ZNetView>();
                if (view == null || !view.IsValid())
                {
                    continue;
                }

                var range = Mathf.Max(station.m_buildRange, station.m_rangeBuild);
                var flat = new Vector3(point.x, station.transform.position.y, point.z);
                if (Vector3.Distance(station.transform.position, flat) < range)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The body of Player.RemovePiece once its checks passed, call for call: the piece's own
        /// remove hook, then WearNTear.Remove (which drops the materials and plays the break), or the
        /// same for a piece with no WearNTear.
        /// </summary>
        private static void RemoveOne(Player player, Piece piece)
        {
            var removed = piece.GetComponent<IRemoved>();
            removed?.OnRemoved();
            var wear = piece.GetComponent<WearNTear>();
            if (wear != null)
            {
                wear.Remove();
            }
            else
            {
                var character = piece.GetComponent<Character>();
                if (character != null)
                {
                    character.Damage(new HitData(1E+10f));
                }
                else
                {
                    var view = piece.GetComponent<ZNetView>();
                    view.ClaimOwnership();
                    piece.DropResources();
                    piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation, piece.gameObject.transform, 1f, -1, player.GetZDOID());
                    player.m_removeEffects.Create(piece.transform.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());
                    ZNetScene.instance.Destroy(piece.gameObject);
                }
            }

            // What Player.UpdatePlacement does after each remove: the skill's debt and the game's count.
            if (player.m_buildPieces != null && player.m_buildPieces.m_skill != Skills.SkillType.None && player.m_buildRemoveDebt < 20)
            {
                player.m_buildRemoveDebt++;
                if (player.m_buildPieces.m_skill == Skills.SkillType.Crafting)
                {
                    Game.instance.IncrementPlayerStat(PlayerStatType.BuildPiecesRemoved);
                }
            }
        }

        /// <summary>One hammer swing for the whole take-down, the costs of removing one piece.</summary>
        private static void Swing(Player player, ItemDrop.ItemData tool, Vector3 at)
        {
            player.FaceLookDirection();
            player.m_zanim.SetTrigger(tool.m_shared.m_attack.m_attackAnimation);
            player.m_lastToolUseTime = Time.time;
            player.AddNoise(50f);
            player.UseStamina(player.GetBuildStamina());
            if (tool.m_shared.m_useDurability)
            {
                tool.m_durability -= player.GetPlaceDurability(tool) * Game.m_durabilityRate;
            }

            tool.m_shared.m_destroyEffect.Create(at, Quaternion.identity, null, 1f, -1, player.GetZDOID());
        }

        /// <summary>
        /// The removable pieces that must stay so that every refused piece still stands, by the
        /// support model. Tried top to bottom, each one near a refused piece: it goes when nothing that
        /// stays falls without it. Empty when nothing is refused, the usual case.
        /// </summary>
        private static HashSet<int> HoldingUp(Site site, List<int> built, ICollection<int> refused)
        {
            var keep = new HashSet<int>();
            if (refused.Count == 0)
            {
                return keep;
            }

            var scene = PartialBuild.SceneOf(site.Resolved);
            var ground = PartialBuild.GroundUnder(site.RootPosition, site.RootYaw);
            var now = Support.Solve(scene.Where(s => built.Contains(s.Id)), ground);

            // A refused piece the model already has falling is not one to hold: the model is careful.
            var targets = refused.Where(r => !now.Falls(r)).ToList();
            bool Stands(ICollection<int> standing)
            {
                var map = Support.Solve(scene.Where(s => standing.Contains(s.Id)), ground);
                return targets.All(t => !map.Falls(t));
            }

            if (targets.Count == 0 || Stands(refused))
            {
                return keep;
            }

            var candidates = built.Where(i => !refused.Contains(i)).ToList();
            var near = candidates
                .Where(i => targets.Any(t => (scene[i].Pos - scene[t].Pos).sqrMagnitude <= HoldRange * HoldRange))
                .ToList();
            var standing = new HashSet<int>(built.Except(candidates.Except(near)));
            var order = PartialBuild.BuildOrder(site.Resolved, near);
            order.Reverse();
            foreach (var i in order)
            {
                standing.Remove(i);
                if (!Stands(standing))
                {
                    standing.Add(i);
                    keep.Add(i);
                }
            }

            return keep;
        }

        private static string Reason(Why why, string station, int count)
        {
            var one = count == 1;
            switch (why)
            {
                case Why.Tool:
                    return $"{count} can't be removed with this tool";
                case Why.CannotBeRemoved:
                    return $"{count} can't be removed";
                case Why.NoBuildZone:
                    return $"{count} {(one ? "is" : "are")} in a no-build zone";
                case Why.Ward:
                    return $"{count} {(one ? "is" : "are")} inside a ward that is not yours";
                case Why.Station:
                    var name = Localization.instance.Localize(station ?? "$piece_workbench").ToLowerInvariant();
                    return $"{count} {(one ? "needs" : "need")} a {name} nearby";
                default:
                    return $"{count} {(one ? "is" : "are")} in use or not empty";
            }
        }

        private static string Pieces(int count)
        {
            return count == 1 ? "1 piece" : $"{count} pieces";
        }
    }
}
