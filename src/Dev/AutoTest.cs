#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ValheimTomrer.Blueprints;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace ValheimTomrer.Dev
{
    /// <summary>
    /// Debug builds only. Plays a scripted session so features can be checked without a person:
    /// a throwaway character and world in their own save folder, a scenario, PASS/FAIL lines in
    /// the log, screenshots, then quit. Off unless VT_AUTOTEST is set (see scripts/autotest.sh).
    ///
    /// Scenarios: "dump" writes every hammer piece's size and snap points to pieces.txt;
    /// "blueprints" builds every shipped kit and checks unlocks, cost and support.
    /// </summary>
    internal static class AutoTest
    {
        private const string TestName = "aclab";
        private const string WorldSeed = "ACTEST01";

        private static int _pass;
        private static int _fail;
        private static bool _worldStarted;
        private static bool _scenarioStarted;

        private static string Scenario => Environment.GetEnvironmentVariable("VT_AUTOTEST");

        private static bool Enabled => !string.IsNullOrEmpty(Scenario);

        private static string OutDir => Environment.GetEnvironmentVariable("VT_OUTDIR")
            ?? Path.Combine(Paths.GameRootPath, "valheimtomrer-autotest");

        public static void Init()
        {
            if (!Enabled)
            {
                return;
            }

            // Own save folder: the player's real characters and worlds are never touched.
            var saves = Path.Combine(OutDir, "saves");
            Directory.CreateDirectory(saves);
            Utils.SetSaveDataPath(saves);
            Application.runInBackground = true;
            Log($"scenario={Scenario} out={OutDir}");
        }

        private static void Log(string message)
        {
            ValheimTomrerPlugin.Log.LogInfo("AUTOTEST " + message);
        }

        private static void Check(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
            }
            else
            {
                _fail++;
            }

            Log((ok ? "PASS " : "FAIL ") + what);
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Start))]
        private static class FejdStartupStartPatch
        {
            private static void Postfix(FejdStartup __instance)
            {
                if (Enabled && !_worldStarted)
                {
                    _worldStarted = true;
                    __instance.StartCoroutine(StartWorld(__instance));
                }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class PlayerOnSpawnedPatch
        {
            private static void Postfix(Player __instance)
            {
                if (Enabled && !_scenarioStarted && __instance == Player.m_localPlayer)
                {
                    _scenarioStarted = true;
                    ValheimTomrerPlugin.Instance.StartCoroutine(RunScenario(__instance));
                }
            }
        }

        /// <summary>What the menu does on "Start", minus the clicks.</summary>
        private static IEnumerator StartWorld(FejdStartup menu)
        {
            yield return new WaitForSeconds(1f);

            if (!PlayerProfile.HaveProfile(TestName))
            {
                var profile = new PlayerProfile(TestName, FileHelpers.FileSource.Local);
                profile.SetName("Aclab");
                profile.m_firstSpawn = false; // no valkyrie intro
                profile.Save();
                Log("created test character");
            }

            if (!World.HaveWorld(TestName))
            {
                var created = new World(TestName, WorldSeed) { m_fileSource = FileHelpers.FileSource.Local };
                SaveSystem.SetSaveNumber(0u);
                created.SaveWorldFWLData(DateTime.Now);
                Log("created test world");
            }

            Game.SetProfile(TestName, FileHelpers.FileSource.Local);
            var world = World.GetCreateWorld(TestName, FileHelpers.FileSource.Local);
            ZNet.SetServer(true, false, false, TestName, "", world);
            ZNet.ResetServerHost();
            menu.m_startingWorld = true;
            Log("loading world");
            menu.LoadMainScene();
        }

        private static IEnumerator RunScenario(Player player)
        {
            yield return new WaitForSeconds(3f);
            Log($"spawned at {player.transform.position}");
            player.SetGodMode(true);
            EnvMan.instance.m_debugTimeOfDay = true;
            EnvMan.instance.m_debugTime = 0.5f;
            EnvMan.instance.SetForceEnvironment("Clear");

            IEnumerator scenario;
            switch (Scenario)
            {
                case "dump":
                    scenario = DumpPieces(player);
                    break;
                case "blueprints":
                    scenario = TestBlueprints(player);
                    break;
                default:
                    Log("unknown scenario " + Scenario);
                    scenario = null;
                    break;
            }

            if (scenario != null)
            {
                yield return ValheimTomrerPlugin.Instance.StartCoroutine(Guard(scenario));
            }

            var summary = $"DONE pass={_pass} fail={_fail}";
            File.WriteAllText(Path.Combine(OutDir, "result.txt"), summary + "\n");
            Log(summary);
            yield return new WaitForSeconds(1f);
            Application.Quit();
        }

        /// <summary>
        /// Runs a scenario and turns an exception into a FAIL instead of a silent stop. Nested steps
        /// (yield return SomeStep()) run here too, since Unity would stop the whole chain on their errors.
        /// </summary>
        private static IEnumerator Guard(IEnumerator scenario)
        {
            var steps = new Stack<IEnumerator>();
            steps.Push(scenario);
            while (steps.Count > 0)
            {
                object current;
                try
                {
                    if (!steps.Peek().MoveNext())
                    {
                        steps.Pop();
                        continue;
                    }

                    current = steps.Peek().Current;
                }
                catch (Exception e)
                {
                    Check(false, "scenario threw: " + e);
                    yield break;
                }

                if (current is IEnumerator nested)
                {
                    steps.Push(nested);
                    continue;
                }

                yield return current;
            }
        }

        // ---------- scenario: dump ----------

        private static IEnumerator DumpPieces(Player player)
        {
            yield return EquipHammer(player);
            var tool = player.GetBuildTool();
            var text = new StringBuilder();
            foreach (var prefab in tool.m_pieces)
            {
                var piece = prefab.GetComponent<Piece>();
                var bounds = MeshBounds(prefab);
                var snaps = new List<Transform>();
                piece.GetSnapPoints(snaps);
                text.AppendLine($"{prefab.name} | {Localization.instance.Localize(piece.m_name)} | usage={piece.m_usage}"
                    + $" | station={(piece.m_craftingStation ? piece.m_craftingStation.m_name : "-")}"
                    + $" | cost={string.Join(",", piece.m_resources.Select(r => $"{r.m_resItem?.name}x{r.m_amount}"))}"
                    + $" | bounds min={V(bounds.min)} max={V(bounds.max)}"
                    + $" | snaps={string.Join(" ", snaps.Select(s => V(s.localPosition)))}");
            }

            var path = Path.Combine(OutDir, "pieces.txt");
            File.WriteAllText(path, text.ToString());
            Check(tool.m_pieces.Count > 0, $"dumped {tool.m_pieces.Count} pieces to {path}");
        }

        private static Bounds MeshBounds(GameObject prefab)
        {
            var toRoot = prefab.transform.worldToLocalMatrix;
            var bounds = new Bounds();
            var first = true;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                var matrix = toRoot * filter.transform.localToWorldMatrix;
                var mesh = filter.sharedMesh.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var offset = Vector3.Scale(mesh.extents, new Vector3(
                        (corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    var point = matrix.MultiplyPoint3x4(mesh.center + offset);
                    if (first)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        first = false;
                    }
                    else
                    {
                        bounds.Encapsulate(point);
                    }
                }
            }

            return bounds;
        }

        // ---------- scenario: blueprints ----------

        private static IEnumerator TestBlueprints(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);

            var kits = BlueprintLibrary.All;
            Check(kits.Count > 0, $"kits loaded: {kits.Count}");
            if (kits.Count == 0)
            {
                yield break;
            }

            // Nothing unlocked: the key must not offer any blueprint.
            player.m_knownRecipes.Clear();
            player.m_knownStations.Clear();
            player.UpdateAvailablePiecesList();
            BlueprintMode.Cycle(player);
            Check(!BlueprintMode.Active, "no blueprint offered while its pieces are locked");

            for (var i = 0; i < kits.Count; i++)
            {
                yield return TestKit(player, kits[i], i == 0);
            }

            BlueprintMode.Exit();
        }

        private static IEnumerator TestKit(Player player, Blueprint kit, bool withRefusals)
        {
            Log($"--- kit '{kit.Name}' ({kit.Pieces.Count} pieces)");
            if (!ResolvedBlueprint.TryResolve(kit, out var resolved, out var error))
            {
                Check(false, error);
                yield break;
            }

            // Unlock exactly this kit's pieces.
            foreach (var part in resolved.Parts)
            {
                player.m_knownRecipes.Add(part.Piece.m_name);
            }

            player.UpdateAvailablePiecesList();
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);

            BlueprintMode.Select(player, resolved);
            Check(BlueprintRules.IsAvailable(player, resolved), $"'{kit.Name}' offered once its pieces are unlocked");

            yield return AimAtGround(player);
            Check(BlueprintMode.HasTarget, "preview follows the aim");
            Check(BlueprintMode.Blocked == null, "spot is free: " + (BlueprintMode.Blocked ?? "ok"));
            yield return Screenshot(Slug(kit.Name) + "-1-preview");

            var root = BlueprintMode.PreviewRoot;
            var center = root != null ? root.position : player.transform.position;
            if (withRefusals)
            {
                var before = PiecesAround(player, center).Count;
                Check(!BlueprintMode.TryBuild(player), "refused without materials");
                Check(PiecesAround(player, center).Count == before, "nothing built when refused");
            }

            foreach (var cost in resolved.TotalCost)
            {
                player.GetInventory().AddItem(cost.m_resItem.gameObject.name, cost.m_amount, 1, 0, 0L, "", false);
            }

            // Kits that need a station they don't bring get one for free here; it is not what is tested.
            foreach (var station in resolved.Stations.Where(s => !resolved.OwnStations.Contains(s.m_name)))
            {
                if (CraftingStation.HaveBuildStationInRange(station.m_name, player.transform.position) == null)
                {
                    var stationPiece = station.GetComponent<Piece>();
                    player.PlacePiece(stationPiece, player.transform.position - player.transform.forward * 3f, Quaternion.identity, false);
                    yield return null;
                }
            }

            yield return new WaitForSeconds(0.5f);
            player.m_lastToolUseTime = 0f;
            var existing = new HashSet<Piece>(PiecesAround(player, center));
            Check(BlueprintMode.TryBuild(player), "built with exact materials");
            var built = PiecesAround(player, center).Where(p => !existing.Contains(p)).ToList();
            Check(built.Count == resolved.Parts.Count, $"all pieces exist: {built.Count}/{resolved.Parts.Count}");
            var left = resolved.TotalCost.Sum(c => player.GetInventory().CountItems(c.m_resItem.m_itemData.m_shared.m_name));
            Check(left == 0, $"materials taken (left over: {left})");

            yield return new WaitForSeconds(2f);
            yield return Screenshot(Slug(kit.Name) + "-2-built");

            // Support checks start at once for placed pieces; give them time to break if they will.
            yield return new WaitForSeconds(15f);
            var standing = built.Count(p => p != null);
            Check(standing == resolved.Parts.Count, $"still standing after 15 s: {standing}/{resolved.Parts.Count}");
            yield return Screenshot(Slug(kit.Name) + "-3-after-15s");

            BlueprintMode.Exit();
            yield return CheckCampWorks(player, kit, built.Where(p => p != null).ToList());

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// A camp is only useful if the game lets the player use it. Runs the game's own rules:
        /// bed (roof, cover, fire in range), workbench (roof, cover), fire, then rest at the bed
        /// long enough for smoke to fill the room if it will.
        /// </summary>
        private static IEnumerator CheckCampWorks(Player player, Blueprint kit, List<Piece> built)
        {
            var beds = built.Select(p => p.GetComponent<Bed>()).Where(b => b != null).ToList();
            var stations = built.Select(p => p.GetComponent<CraftingStation>()).Where(s => s != null).ToList();
            var fires = built.Select(p => p.GetComponent<Fireplace>()).Where(f => f != null).ToList();

            foreach (var fire in fires)
            {
                Check(fire.IsBurning(), "fire is burning");
                DescribeFire(fire);
            }

            foreach (var station in stations)
            {
                Cover.GetCoverForPoint(station.m_roofCheckPoint.position, out var cover, out var underRoof);
                Check(station.CheckUsable(player, false),
                    $"{Localization.instance.Localize(station.m_name)} usable: roof={underRoof} cover={cover:0.00} (needs 0.70)");
            }

            foreach (var bed in beds)
            {
                Cover.GetCoverForPoint(bed.GetSpawnPoint(), out var cover, out var underRoof);
                Check(underRoof && cover >= 0.8f, $"bed not exposed: roof={underRoof} cover={cover:0.00} (needs 0.80)");
                Check(EffectArea.IsPointInsideArea(bed.transform.position, EffectArea.Type.Heat) != null, "bed is warm (fire in range)");
            }

            if (beds.Count == 0 || fires.Count == 0)
            {
                yield break;
            }

            Check(player.TeleportTo(beds[0].GetSpawnPoint(), player.transform.rotation, false), "moved to the bed");
            while (player.IsTeleporting())
            {
                yield return null;
            }

            yield return new WaitForSeconds(3f);
            Check(player.InShelter(), "player sheltered at the bed");
            yield return Screenshot(Slug(kit.Name) + "-4-inside");

            yield return new WaitForSeconds(40f);
            var smoked = player.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectSmoked);
            var floor = beds[0].transform.position.y;
            var smokeInside = Smoke.s_smoke.Where(s => Vector3.Distance(s.transform.position, beds[0].transform.position) < 8f).ToList();
            Log($"smoke near the bed: {smokeInside.Count} puffs, lowest {(smokeInside.Count > 0 ? smokeInside.Min(s => s.transform.position.y) - floor : 0f):0.00} m above the floor");
            Check(!smoked, "no smoke at the bed after 40 s");
            Check(player.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectResting), "resting at the bed (sheltered and warm)");
            Check(fires.All(f => f != null && f.IsBurning()), "fire still burning (not choked by smoke)");
            yield return Screenshot(Slug(kit.Name) + "-5-after-40s");
        }

        /// <summary>The game's three reasons to smother a fire (Fireplace.CheckUnderTerrain), measured.</summary>
        private static void DescribeFire(Fireplace fire)
        {
            var pos = fire.transform.position;
            var zdo = fire.m_nview.GetZDO();
            var ground = Heightmap.GetHeight(pos, out var height) ? $"{height - pos.y:+0.00;-0.00} m" : "unknown";
            var coverFrom = pos + Vector3.up * fire.m_coverCheckOffset;
            var coverHit = Physics.Raycast(coverFrom, Vector3.up, out var hit, 0.5f, Fireplace.m_solidRayMask)
                ? $"{hit.collider.transform.root.name} at +{hit.point.y - pos.y:0.00} m"
                : "nothing";
            Log($"fire: fuel {zdo.GetFloat(ZDOVars.s_fuel):0.0}, state {zdo.GetInt(ZDOVars.s_state, 1)}, blocked {fire.m_blocked}"
                + $" | ground {ground} (allowed +{fire.m_checkTerrainOffset:0.00})"
                + $" | above +{fire.m_coverCheckOffset:0.00}..+{fire.m_coverCheckOffset + 0.5f:0.00} m: {coverHit}");

            var smoke = fire.m_smokeSpawner;
            if (smoke != null)
            {
                var touching = Physics.OverlapSphere(smoke.transform.position, smoke.m_testRadius, smoke.m_testMask.value)
                    .Select(c => c.transform.root.name).Distinct();
                Log($"fire smoke outlet at +{smoke.transform.position.y - pos.y:0.00} m, radius {smoke.m_testRadius:0.00},"
                    + $" blocked {smoke.IsBlocked()}, touching: {string.Join(", ", touching)}");
            }

            foreach (var area in fire.GetComponentsInChildren<EffectArea>(true))
            {
                var sphere = area.GetComponent<SphereCollider>();
                Log($"fire area {area.m_type} radius {(sphere ? sphere.radius * area.transform.lossyScale.x : -1f):0.00}");
            }
        }

        // ---------- helpers ----------

        private static IEnumerator EquipHammer(Player player)
        {
            var inventory = player.GetInventory();
            var hammer = inventory.GetAllItems().FirstOrDefault(i => i.m_dropPrefab && i.m_dropPrefab.name == "Hammer")
                ?? inventory.AddItem("Hammer", 1, 1, 0, 0L, "", false);
            if (!player.IsItemEquiped(hammer))
            {
                player.EquipItem(hammer);
            }

            yield return new WaitForSeconds(0.5f);
            Check(player.InPlaceMode(), "hammer equipped, build mode on");
        }

        /// <summary>
        /// The spawn stones are a no-build zone, so go to the flattest dry meadow nearby. Heights come
        /// from the world generator, which needs no loaded area; the teleport then waits for it.
        /// </summary>
        private static IEnumerator MoveToBuildSpot(Player player)
        {
            var from = player.transform.position;
            var best = from;
            var bestBumps = float.MaxValue;
            for (var distance = 60f; distance <= 400f && bestBumps > 0.3f; distance += 20f)
            {
                for (var angle = 0; angle < 360; angle += 15)
                {
                    var spot = from + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                    var bumps = Bumpiness(spot, out var height);
                    if (bumps < bestBumps)
                    {
                        bestBumps = bumps;
                        best = new Vector3(spot.x, height, spot.z);
                    }
                }
            }

            Log($"build spot {best}, {Vector3.Distance(from, best):0} m from spawn, ground varies {bestBumps:0.00} m");
            yield return new WaitForSeconds(2.5f); // teleport cooldown after spawning
            Check(player.TeleportTo(best + Vector3.up, player.transform.rotation, true), "teleport to the build spot");
            while (player.IsTeleporting())
            {
                yield return null;
            }

            yield return new WaitForSeconds(3f);
            ClearVegetation(player.transform.position, 20f);
            yield return new WaitForSeconds(1f);
            Check(!Location.IsInsideNoBuildLocation(player.transform.position), $"moved to {player.transform.position}");
        }

        /// <summary>Height spread of the ground in a 14 m square; MaxValue for water or another biome.</summary>
        private static float Bumpiness(Vector3 spot, out float height)
        {
            var world = WorldGenerator.instance;
            var water = ZoneSystem.instance.m_waterLevel;
            height = world.GetHeight(spot.x, spot.z);
            if (world.GetBiome(spot) != Heightmap.Biome.Meadows || height < water + 2f)
            {
                return float.MaxValue;
            }

            var min = height;
            var max = height;
            for (var x = -7f; x <= 7f; x += 3.5f)
            {
                for (var z = -7f; z <= 7f; z += 3.5f)
                {
                    var h = world.GetHeight(spot.x + x, spot.z + z);
                    if (h < water + 1f)
                    {
                        return float.MaxValue;
                    }

                    min = Mathf.Min(min, h);
                    max = Mathf.Max(max, h);
                }
            }

            return max - min;
        }

        /// <summary>Trees and rocks would catch the aim and block the view in screenshots.</summary>
        private static void ClearVegetation(Vector3 center, float radius)
        {
            var removed = 0;
            foreach (var collider in Physics.OverlapSphere(center, radius))
            {
                var view = collider.GetComponentInParent<ZNetView>();
                if (view == null || !view.IsValid() || view.GetComponent<Piece>() != null || view.GetComponent<Character>() != null)
                {
                    continue;
                }

                if (view.GetComponent<TreeBase>() || view.GetComponent<TreeLog>() || view.GetComponent<Destructible>()
                    || view.GetComponent<MineRock>() || view.GetComponent<MineRock5>() || view.GetComponent<Pickable>())
                {
                    ZNetScene.instance.Destroy(view.gameObject);
                    removed++;
                }
            }

            Log($"cleared {removed} trees and rocks around the build spot");
        }

        /// <summary>Tilts the view down until the aim lands on the ground a few meters ahead.</summary>
        private static IEnumerator AimAtGround(Player player)
        {
            var yaw = player.transform.eulerAngles.y;
            for (var pitch = 15f; pitch <= 60f; pitch += 3f)
            {
                player.m_lookYaw = Quaternion.Euler(0f, yaw, 0f);
                player.m_lookPitch = pitch;
                yield return null;
                yield return null;
                if (BlueprintMode.HasTarget && BlueprintMode.Blocked == null)
                {
                    Log($"aiming at pitch {pitch}");
                    yield break;
                }
            }
        }

        private static List<Piece> PiecesAround(Player player, Vector3 center)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(center, 30f, pieces);
            return pieces.Where(p => p.GetCreator() == player.GetPlayerID()).ToList();
        }

        private static void ClearInventoryExceptHammer(Player player)
        {
            var inventory = player.GetInventory();
            foreach (var item in inventory.GetAllItems().ToList())
            {
                if (!(item.m_dropPrefab && item.m_dropPrefab.name == "Hammer"))
                {
                    inventory.RemoveItem(item);
                }
            }
        }

        private static void RemoveOldTestBuildings(Player player)
        {
            var pieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(player.transform.position, 60f, pieces);
            foreach (var piece in pieces.Where(p => p.GetCreator() == player.GetPlayerID()))
            {
                ZNetScene.instance.Destroy(piece.gameObject);
            }
        }

        private static IEnumerator Screenshot(string name)
        {
            yield return new WaitForEndOfFrame();
            var path = Path.Combine(OutDir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return new WaitForSeconds(0.5f);
            Log("screenshot " + path);
        }

        private static string Slug(string name)
        {
            return new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        }

        private static string V(Vector3 v)
        {
            return $"({v.x:0.##},{v.y:0.##},{v.z:0.##})";
        }
    }
}
#endif
