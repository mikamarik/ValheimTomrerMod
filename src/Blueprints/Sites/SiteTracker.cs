using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ValheimTomrer.Blueprints.Sites
{
    /// <summary>
    /// Keeps the unfinished builds in step with the world, from <c>Plugin.Update</c>. Once a second,
    /// and right after a build (<see cref="RefreshSoon"/>):
    ///   1. which parts stand is read from the world (same prefab, pivot within 5 cm, turn within 2°);
    ///   2. a site with every part standing is finished: "X finished." and its file is deleted;
    ///   3. with the hammer out and the player within 64 m of a site's box, its ghost shows: built
    ///      parts hidden, the ones the next click builds light blue, the rest red.
    ///
    /// The next click's parts come from <see cref="PartialBuild.Plan(ResolvedBlueprint, Vector3, float, bool[], MaterialSources, bool)"/>,
    /// which is not cheap on a big blueprint (57 ms on 400 pieces). So it runs again for a site only
    /// when what it reads changed: the site's built flags, the counts of the items the site needs,
    /// the stations in reach of the site, the world's free-build keys, or costs on or off.
    /// Otherwise the last answer (<see cref="Site.Ready"/>) stands.
    ///
    /// A world change destroys every ghost with the scene. The tracker notices the new scene, reads
    /// the new world's sites, and builds their ghosts again when they are wanted.
    /// </summary>
    internal static class SiteTracker
    {
        /// <summary>Seconds between two refreshes.</summary>
        public const float Period = 1f;

        /// <summary>The ghost shows while the player is this close to the site's box.</summary>
        public const float ShowRange = 64f;

        /// <summary>The world is read only for sites this close: further away its pieces may not be loaded.</summary>
        public const float ReadRange = 128f;

        /// <summary>A world piece is a built part when its pivot is this close to the part's spot.</summary>
        public const float MatchDistance = 0.05f;

        /// <summary>And when it is turned no more than this many degrees from the part.</summary>
        public const float MatchDegrees = 2f;

        private static readonly List<Piece> Near = new List<Piece>();
        private static readonly Dictionary<string, List<Piece>> ByName = new Dictionary<string, List<Piece>>();
        private static readonly HashSet<Piece> Used = new HashSet<Piece>();
        private static readonly StringBuilder Key = new StringBuilder();

        private static ZNetScene _scene;
        private static PieceTable _hammer;
        private static float _nextRefresh;
        private static bool _refreshSoon;

        /// <summary>How many times a site's plan was worked out. For the tests: a refresh with nothing changed adds none.</summary>
        public static int PlanRuns { get; private set; }

        public static int Refreshes { get; private set; }

        /// <summary>The last "X finished." said. For the tests.</summary>
        public static string LastMessage { get; private set; }

        /// <summary>The next <see cref="Tick"/> refreshes, whatever the clock says. Call it after a build.</summary>
        public static void RefreshSoon()
        {
            _refreshSoon = true;
        }

        /// <summary>Once a frame, from <c>Plugin.Update</c>.</summary>
        public static void Tick()
        {
            var scene = ZNetScene.instance;
            if (scene == null)
            {
                if (!ReferenceEquals(_scene, null))
                {
                    // Back at the menu: the ghosts died with the scene.
                    _scene = null;
                    SiteStore.Clear();
                }

                return;
            }

            if (!ReferenceEquals(scene, _scene))
            {
                _scene = scene;
                _hammer = null;
                SiteStore.Load();
                _refreshSoon = true;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }

            if (!ValheimTomrerPlugin.ModEnabled.Value)
            {
                HideAll();
                return;
            }

            var finished = StepGhosts();
            if (finished || _refreshSoon || Time.time >= _nextRefresh)
            {
                Refresh(player);
            }
        }

        /// <summary>Does the whole refresh now: built flags, finished sites, ghosts and their looks.</summary>
        public static void Refresh(Player player)
        {
            Refreshes++;
            _refreshSoon = false;
            _nextRefresh = Time.time + Period;
            if (player == null)
            {
                return;
            }

            var hammerOut = HammerOut(player);
            var noCost = player.PlacementCostDisabled;
            MaterialSources sources = null;
            var haveSources = false;

            foreach (var site in SiteStore.All.ToList())
            {
                var distance = Mathf.Sqrt(site.WorldBox.SqrDistance(player.transform.position));
                if (distance <= ReadRange)
                {
                    ReadBuilt(site);
                }

                if (site.BuiltCount >= site.Total)
                {
                    Finish(player, site);
                    continue;
                }

                if (site.Ghost != null && !site.Ghost.IsAlive)
                {
                    site.DropGhost();
                }

                var show = hammerOut && distance <= ShowRange;
                if (show && site.Ghost == null)
                {
                    StartGhost(site);
                }

                // Still being built: it shows itself when done (StepGhosts asks for a refresh).
                if (site.Ghost == null || !site.Ghost.Done)
                {
                    continue;
                }

                if (show)
                {
                    // One list per refresh: it walks every loaded piece.
                    if (!haveSources)
                    {
                        sources = noCost ? null : MaterialSources.Around(player);
                        haveSources = true;
                    }

                    Plan(site, sources, noCost);
                    ApplyLooks(site);
                }

                site.Ghost.SetVisible(show);
            }
        }

        /// <summary>
        /// Works out the next click's parts for a site, only when what the plan reads changed since the
        /// last time. <paramref name="sources"/> null means an empty budget (or costs off).
        /// </summary>
        public static void Plan(Site site, MaterialSources sources, bool noCost)
        {
            var key = KeyOf(site, sources, noCost);
            if (site.Ready != null && key == site.ReadyKey)
            {
                return;
            }

            var chosen = PartialBuild.Plan(site.Resolved, site.RootPosition, site.RootYaw, site.Built, sources, noCost);
            var ready = new bool[site.Total];
            foreach (var index in chosen)
            {
                ready[index] = true;
            }

            site.Ready = ready;
            site.ReadyCount = chosen.Count;
            site.ReadyOrder = chosen;
            site.ReadyKey = key;
            PlanRuns++;
        }

        /// <summary>True when the build tool in hand is the hammer. The hoe and the cultivator are place mode too.</summary>
        public static bool HammerOut(Player player)
        {
            if (player == null || !player.InPlaceMode())
            {
                return false;
            }

            var table = player.GetBuildTool();
            if (table == null)
            {
                return false;
            }

            if (_hammer == null)
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Hammer") : null;
                var item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                _hammer = item != null ? item.m_itemData.m_shared.m_buildPieces : null;
            }

            return _hammer != null && (table == _hammer || table.name == _hammer.name);
        }

        /// <summary>Fills <see cref="Site.Built"/> from the pieces standing in the world now.</summary>
        public static void ReadBuilt(Site site)
        {
            ClearLists();
            Piece.GetAllPiecesInRadius(site.WorldBox.center, site.WorldBox.extents.magnitude + MatchDistance, Near);
            foreach (var piece in Near)
            {
                // Real pieces only: a copy of ours has no network object.
                if (piece == null || piece.m_nview == null || !piece.m_nview.IsValid())
                {
                    continue;
                }

                var name = Utils.GetPrefabName(piece.gameObject);
                if (!ByName.TryGetValue(name, out var list))
                {
                    list = new List<Piece>();
                    ByName[name] = list;
                }

                list.Add(piece);
            }

            var count = 0;
            var changed = false;
            for (var i = 0; i < site.Total; i++)
            {
                var found = false;
                if (ByName.TryGetValue(site.Resolved.Parts[i].Prefab.name, out var list))
                {
                    var at = site.WorldPosition(i);
                    var turn = site.WorldRotation(i);
                    foreach (var piece in list)
                    {
                        if (!Used.Contains(piece)
                            && (piece.transform.position - at).sqrMagnitude <= MatchDistance * MatchDistance
                            && Quaternion.Angle(piece.transform.rotation, turn) <= MatchDegrees)
                        {
                            Used.Add(piece);
                            found = true;
                            break;
                        }
                    }
                }

                if (site.Built[i] != found)
                {
                    site.Built[i] = found;
                    changed = true;
                }

                if (found)
                {
                    count++;
                }
            }

            ClearLists();
            site.BuiltCount = count;
            if (changed)
            {
                site.BuiltVersion++;
            }
        }

        /// <summary>Nothing keeps a world piece between two reads.</summary>
        private static void ClearLists()
        {
            Near.Clear();
            Used.Clear();
            foreach (var list in ByName.Values)
            {
                list.Clear();
            }
        }

        /// <summary>One fill step for every ghost still being built. True when one of them just got done.</summary>
        private static bool StepGhosts()
        {
            var done = false;
            foreach (var site in SiteStore.All)
            {
                if (site.GhostFill == null || site.Ghost == null)
                {
                    continue;
                }

                if (!site.Ghost.IsAlive)
                {
                    site.DropGhost();
                    continue;
                }

                if (site.GhostFill.MoveNext())
                {
                    continue;
                }

                // Built unturned, so its bounds are right. Now it goes where the site stands, hidden
                // until the refresh right after this gives every part its look.
                site.GhostFill = null;
                site.Ghost.SetVisible(false);
                site.Ghost.Root.SetPositionAndRotation(site.RootPosition, site.RootRotation);
                done = true;
            }

            return done;
        }

        private static void StartGhost(Site site)
        {
            var ghost = BlueprintPreview.Empty(site.Resolved, PreviewStyle.Site(), hideUntilDone: true);
            ghost.Root.name = "ValheimTomrer_Site_" + site.Name;
            ghost.Root.SetPositionAndRotation(site.RootPosition, Quaternion.identity);
            site.Ghost = ghost;
            site.GhostFill = ghost.Fill();
        }

        private static void ApplyLooks(Site site)
        {
            for (var i = 0; i < site.Total; i++)
            {
                var look = site.Built[i] ? PartLook.Hidden
                    : site.Ready != null && site.Ready[i] ? PartLook.Ready
                    : PartLook.Waiting;
                site.Ghost.SetPart(i, look);
            }
        }

        /// <summary>Every part stands: "X finished." and the file is deleted. Also the Continue click's last step.</summary>
        public static void Finish(Player player, Site site)
        {
            LastMessage = $"{site.Name} finished.";
            player.Message(MessageHud.MessageType.TopLeft, LastMessage);
            ValheimTomrerPlugin.Log.LogInfo($"site finished: {site.Name} at {site.RootPosition}, file {site.Path} deleted");
            SiteStore.Delete(site);
        }

        private static void HideAll()
        {
            foreach (var site in SiteStore.All)
            {
                if (site.Ghost != null && site.Ghost.IsAlive && site.Ghost.Done)
                {
                    site.Ghost.SetVisible(false);
                }
            }
        }

        /// <summary>
        /// Everything the plan reads, as text: the built flags, costs on or off, the count of every
        /// item the site needs, the stations that reach the site's box, and the free-build keys.
        /// The ground is left out: it only changes when the player digs.
        /// </summary>
        private static string KeyOf(Site site, MaterialSources sources, bool noCost)
        {
            Key.Length = 0;
            Key.Append(site.BuiltVersion).Append('|').Append(noCost ? 'f' : 'c').Append('|');
            foreach (var item in site.Items)
            {
                Key.Append(sources != null ? sources.Count(item) : 0).Append(',');
            }

            Key.Append('|');
            var zones = ZoneSystem.instance;
            Key.Append(zones != null && zones.GetGlobalKey(GlobalKeys.NoWorkbench) ? 'n' : 'w');
            foreach (var kind in site.Kinds)
            {
                Key.Append(zones != null && zones.GetGlobalKey(kind.FreeBuildKey()) ? '1' : '0');
            }

            Key.Append('|');
            foreach (var needed in site.Resolved.Stations)
            {
                foreach (var station in CraftingStation.m_allStations)
                {
                    if (station == null || station.m_name != needed.m_name)
                    {
                        continue;
                    }

                    // The range the game worked out last (extensions add to it). Read, not asked
                    // for: asking runs the station's own update, which throws on a vanilla ghost.
                    var range = Mathf.Max(station.m_buildRange, station.m_rangeBuild);
                    if (FlatDistance(site.WorldBox, station.transform.position) < range)
                    {
                        Key.Append(station.GetInstanceID()).Append(':').Append(Mathf.RoundToInt(range * 10f)).Append(',');
                    }
                }
            }

            return Key.ToString();
        }

        /// <summary>Distance seen from above from a point to a box, 0 inside it.</summary>
        private static float FlatDistance(Bounds box, Vector3 point)
        {
            var dx = Mathf.Max(box.min.x - point.x, 0f, point.x - box.max.x);
            var dz = Mathf.Max(box.min.z - point.z, 0f, point.z - box.max.z);
            return Mathf.Sqrt((dx * dx) + (dz * dz));
        }
    }
}
