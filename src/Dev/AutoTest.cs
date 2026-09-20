#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;
using ValheimTomrer.Editor.Ui;
using ValheimTomrer.Editor.View;
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
    /// "blueprints" builds every shipped kit and checks unlocks, cost and support;
    /// "probe" measures what the in-game editor will be built on and writes probe.txt;
    /// "editor_open" opens the editor window with its key and checks the input takeover;
    /// "editor_view" fills the 3D pane with a kit and drives the camera through both its modes;
    /// "editor_files" round-trips every blueprint through the writer and runs the file commands;
    /// "editor_palette" builds the piece catalog and checks the palette's counts and filters.
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

            // The test drives the game with real key events. Without this the input system
            // disables the keyboard the moment the window loses focus and swallows them.
            UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior =
                UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
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
                case "probe":
                    scenario = Probe(player);
                    break;
                case "editor_open":
                    scenario = TestEditorOpen(player);
                    break;
                case "editor_view":
                    scenario = TestEditorView(player);
                    break;
                case "editor_files":
                    scenario = TestEditorFiles(player);
                    break;
                case "editor_palette":
                    scenario = TestEditorPalette(player);
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
            return MeshBounds(prefab, prefab.GetComponentsInChildren<MeshFilter>());
        }

        private static Bounds MeshBounds(GameObject prefab, MeshFilter[] filters)
        {
            var toRoot = prefab.transform.worldToLocalMatrix;
            var bounds = new Bounds();
            var first = true;
            foreach (var filter in filters)
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
            CheckGhostLook(resolved);
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
        /// The see-through preview is set up the way the vanilla one is. Pinned here because the
        /// editor shares the same builder: the editor's own model is solid, on its own layer and
        /// clickable, and none of that may leak back into blueprint mode.
        /// </summary>
        private static void CheckGhostLook(ResolvedBlueprint kit)
        {
            var root = BlueprintMode.PreviewRoot;
            if (root == null)
            {
                Check(false, "no preview to look at");
                return;
            }

            var ghost = LayerMask.NameToLayer("ghost");
            var strayLayer = root.GetComponentsInChildren<Transform>(true).Count(t => t.gameObject.layer != ghost);
            var liveCollider = root.GetComponentsInChildren<Collider>(true).Count(c => c.enabled);
            var casting = root.GetComponentsInChildren<MeshRenderer>(true)
                .Count(r => r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off);

            var fromPrefabs = new HashSet<Material>();
            foreach (var part in kit.Parts)
            {
                foreach (var renderer in part.Prefab.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material != null)
                        {
                            fromPrefabs.Add(material);
                        }
                    }
                }
            }

            int shared = 0, copies = 0;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                {
                    continue;
                }

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    if (fromPrefabs.Contains(material))
                    {
                        shared++;
                    }
                    else
                    {
                        copies++;
                    }
                }
            }

            Check(strayLayer == 0, $"the whole preview is on the ghost layer ({strayLayer} objects are not)");
            Check(liveCollider == 0, $"no collider is on in the preview ({liveCollider} are)");
            Check(casting == 0, $"the preview casts no shadows ({casting} renderers do)");
            Check(shared == 0 && copies > 0, $"the preview paints with its own material copies ({copies} copies, {shared} shared)");
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

        // ---------- scenario: probe ----------

        /// <summary>
        /// Measures the facts the in-game editor rests on, so nothing later is a guess: free layers,
        /// the HUD canvas and the font hanging off it, the UI sprite atlas, the input module and pad,
        /// whether a second camera can draw a real piece into a RenderTexture, and what the hammer
        /// pieces actually contain. Writes probe.txt (plain text) and changes nothing in the world.
        /// </summary>
        private static IEnumerator Probe(Player player)
        {
            yield return EquipHammer(player);
            yield return new WaitForSeconds(1f);

            var report = new StringBuilder();
            report.AppendLine($"ValheimTomrer probe | Unity {Application.unityVersion} | {DateTime.Now:yyyy-MM-dd HH:mm}");
            report.AppendLine();

            var editorLayer = ProbeLayers(report);
            report.AppendLine();
            ProbeHud(report);
            report.AppendLine();
            ProbeAtlas(report);
            report.AppendLine();
            ProbeInput(report);
            report.AppendLine();
            ProbeCameras(report, editorLayer);
            report.AppendLine();
            ProbePieces(report, player);

            var path = Path.Combine(OutDir, "probe.txt");
            File.WriteAllText(path, report.ToString());
            Check(File.Exists(path), "probe written to " + path);
        }

        /// <summary>Layer names, and the free layer the editor will draw its own copies on.</summary>
        private static int ProbeLayers(StringBuilder report)
        {
            var free = new List<int>();
            var names = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                names.Add($"{i}={(string.IsNullOrEmpty(name) ? "<free>" : name)}");
                if (string.IsNullOrEmpty(name))
                {
                    free.Add(i);
                }
            }

            report.AppendLine("layers: " + string.Join(" ", names));
            report.AppendLine("free layers: " + string.Join(",", free));
            foreach (var wanted in new[] { 3, 6, 7, 30 })
            {
                report.AppendLine($"  layer {wanted}: {(free.Contains(wanted) ? "free" : "taken by " + LayerMask.LayerToName(wanted))}");
            }

            var chosen = new[] { 30, 7, 6, 3 }.FirstOrDefault(free.Contains);
            if (chosen == 0)
            {
                chosen = free.Count > 0 ? free[free.Count - 1] : 0;
            }

            report.AppendLine($"editor layer: {chosen}");
            Check(free.Count > 0, $"free layer for the editor: {chosen} ({free.Count} free in total)");
            return chosen;
        }

        /// <summary>The HUD canvas, and the TMP font an own window can copy.</summary>
        private static void ProbeHud(StringBuilder report)
        {
            var hud = Hud.instance;
            Check(hud != null, "Hud.instance");
            if (hud == null)
            {
                return;
            }

            var parent = hud.transform.parent;
            report.AppendLine($"hud parent: {(parent != null ? parent.name : "<none>")} | root: {hud.transform.root.name}");

            var canvas = hud.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                var guiScaler = canvas.GetComponent<GuiScaler>();
                report.AppendLine($"  canvas {canvas.name}: mode={canvas.renderMode} order={canvas.sortingOrder}"
                    + $" scaler={(scaler != null ? scaler.uiScaleMode + " ref=" + scaler.referenceResolution + " ppu=" + scaler.referencePixelsPerUnit : "none")}"
                    + $" guiScaler={(guiScaler != null ? "yes" : "no")}");
            }

            var hover = hud.m_hoverName;
            report.AppendLine($"  m_hoverName: type={hover.GetType().Name}"
                + $" font={(hover.font != null ? hover.font.name : "null")}"
                + $" material={(hover.fontSharedMaterial != null ? hover.fontSharedMaterial.name : "null")}"
                + $" size={hover.fontSize}");
            Check(hover.font != null && hover.fontSharedMaterial != null, "TMP font and material read off Hud.m_hoverName");
        }

        /// <summary>The window chrome sprites all live in one atlas.</summary>
        private static void ProbeAtlas(StringBuilder report)
        {
            var atlases = Resources.FindObjectsOfTypeAll<UnityEngine.U2D.SpriteAtlas>();
            report.AppendLine("sprite atlases: " + string.Join(", ", atlases.Select(a => $"{a.name}({a.spriteCount})")));

            var ui = atlases.FirstOrDefault(a => a.name == "UIAtlas");
            Check(ui != null, "SpriteAtlas 'UIAtlas'");
            if (ui == null)
            {
                return;
            }

            foreach (var name in new[] { "woodpanel_trophys", "button", "text_field" })
            {
                var sprite = ui.GetSprite(name);
                Check(sprite != null, $"UIAtlas sprite '{name}'");
                if (sprite != null)
                {
                    report.AppendLine($"  {name}: {sprite.rect.width:0}x{sprite.rect.height:0}"
                        + $" border={sprite.border} ppu={sprite.pixelsPerUnit:0}");
                    UnityEngine.Object.Destroy(sprite); // GetSprite hands out a copy, not the atlas entry
                }
            }

            // The whole name list, so the next phase picks chrome without another run.
            var all = new Sprite[ui.spriteCount];
            ui.GetSprites(all);
            report.AppendLine("  all UIAtlas sprites: " + string.Join(" ", all
                .Where(s => s != null)
                .Select(s => s.name.Replace("(Clone)", ""))
                .OrderBy(n => n, StringComparer.Ordinal)));
            foreach (var sprite in all)
            {
                if (sprite != null)
                {
                    UnityEngine.Object.Destroy(sprite);
                }
            }
        }

        /// <summary>Which input module the UI runs on, and whether a pad is attached.</summary>
        private static void ProbeInput(StringBuilder report)
        {
            var events = UnityEngine.EventSystems.EventSystem.current;
            Check(events != null, "EventSystem.current");
            if (events != null)
            {
                var module = events.currentInputModule;
                report.AppendLine($"EventSystem {events.name}: active module = {(module != null ? module.GetType().FullName : "<none>")}");
                report.AppendLine("  modules on the object: " + string.Join(", ", events
                    .GetComponents<UnityEngine.EventSystems.BaseInputModule>()
                    .Select(m => m.GetType().FullName)));
            }

            var pad = UnityEngine.InputSystem.Gamepad.current;
            report.AppendLine(pad == null
                ? "gamepad: none connected"
                : $"gamepad: name={pad.name} layout={pad.layout} display={pad.displayName}");
            report.AppendLine("  input devices: " + string.Join(", ", UnityEngine.InputSystem.InputSystem.devices
                .Select(d => $"{d.layout} '{d.name}'")));
        }

        /// <summary>
        /// What the main camera draws, and the proof the editor pane is possible: an own camera on a
        /// free layer renders a copy of a real wall's meshes into a RenderTexture.
        /// </summary>
        private static void ProbeCameras(StringBuilder report, int layer)
        {
            report.AppendLine($"Game.instance: {(Game.instance != null ? "yes" : "no")}");
            var main = GameCamera.instance != null ? GameCamera.instance.m_camera : null;
            Check(main != null, "GameCamera main camera");
            if (main != null)
            {
                var mask = main.cullingMask;
                var drawn = Enumerable.Range(0, 32).Where(i => (mask & (1 << i)) != 0)
                    .Select(i => $"{i}:{(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? "<free>" : LayerMask.LayerToName(i))}");
                report.AppendLine($"main camera cullingMask=0x{mask:X8} draws: {string.Join(" ", drawn)}");
                report.AppendLine($"  editor layer {layer} drawn by the main camera: {((mask & (1 << layer)) != 0 ? "YES" : "no")}");
            }

            var clear = new Color(0f, 0f, 0.25f, 1f);
            var rt = new RenderTexture(64, 64, 24) { name = "vt_probe_rt" };
            var cameraObject = new GameObject("vt_probe_camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false; // rendered by hand, never every frame
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = clear;
            camera.cullingMask = 1 << layer;
            camera.orthographic = true;
            camera.orthographicSize = 1.6f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            camera.targetTexture = rt;
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 2000f, -5f), Quaternion.identity);

            var lightObject = new GameObject("vt_probe_light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << layer;
            light.intensity = 1.5f;
            lightObject.transform.rotation = Quaternion.Euler(40f, 20f, 0f);

            var wall = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("woodwall") : null;
            var model = wall != null ? CloneVisual(wall, layer, new Vector3(0f, 2000f, 0f)) : null;
            report.AppendLine($"probe model: {(model != null ? model.transform.childCount + " mesh parts of woodwall" : "none")}");

            camera.Render();

            var shot = new Texture2D(64, 64, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            shot.ReadPixels(new Rect(0f, 0f, 64f, 64f), 0, 0);
            shot.Apply();
            RenderTexture.active = previous;

            var pixels = shot.GetPixels();
            var nonBlack = pixels.Count(p => p.r + p.g + p.b > 0.02f);
            var onModel = pixels.Count(p => Mathf.Abs(p.r - clear.r) + Mathf.Abs(p.g - clear.g) + Mathf.Abs(p.b - clear.b) > 0.05f);
            report.AppendLine($"render test: 64x64 RenderTexture, own camera on layer {layer}, clear colour {clear}");
            report.AppendLine($"  non-black pixels {nonBlack}/{pixels.Length}, pixels covered by the wall {onModel}/{pixels.Length}");
            Check(nonBlack > 0, $"own camera renders into a RenderTexture ({nonBlack}/{pixels.Length} non-black pixels)");
            Check(onModel > 0, $"a real piece mesh shows up in it ({onModel} pixels)");

            camera.targetTexture = null;
            RenderTexture.active = null;
            UnityEngine.Object.Destroy(shot);
            UnityEngine.Object.Destroy(cameraObject);
            UnityEngine.Object.Destroy(lightObject);
            if (model != null)
            {
                UnityEngine.Object.Destroy(model);
            }

            rt.Release();
            UnityEngine.Object.Destroy(rt);
        }

        /// <summary>
        /// Copies only the meshes and materials of a prefab into plain objects. No ZNetView, no ZDO,
        /// nothing placed: this is how the editor shows a piece without building it.
        /// </summary>
        private static GameObject CloneVisual(GameObject prefab, int layer, Vector3 position)
        {
            var root = new GameObject("vt_probe_model") { layer = layer };
            root.transform.position = position;
            var toRoot = prefab.transform.worldToLocalMatrix;
            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh == null || renderer == null)
                {
                    continue;
                }

                var part = new GameObject(filter.name) { layer = layer };
                part.transform.SetParent(root.transform, false);
                var matrix = toRoot * filter.transform.localToWorldMatrix;
                part.transform.localPosition = matrix.GetColumn(3);
                part.transform.localRotation = matrix.rotation;
                part.transform.localScale = matrix.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
            }

            return root;
        }

        /// <summary>
        /// The catalog the palette and the snapping engine read: counts, and every piece kind that
        /// will need a fallback (no icon, nothing to draw, no collider, no snap points).
        /// </summary>
        private static void ProbePieces(StringBuilder report, Player player)
        {
            var tool = player.GetBuildTool();
            report.AppendLine($"hammer pieces: all={tool.m_pieces.Count} unlocked={tool.m_availablePieces.Count}");
            Check(tool.m_pieces.Count > 0, $"hammer piece table: {tool.m_pieces.Count} pieces, {tool.m_availablePieces.Count} unlocked");

            var wall = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("woodwall") : null;
            Check(wall != null, "ZNetScene.GetPrefab(\"woodwall\")");
            if (wall != null)
            {
                var piece = wall.GetComponent<Piece>();
                var points = new List<Transform>();
                piece.GetSnapPoints(points);
                report.AppendLine($"woodwall: snaps={points.Count} icon={(piece.m_icon != null ? piece.m_icon.name : "null")}"
                    + $" usage={piece.m_usage} comfortGroup={piece.m_comfortGroup}");
                Check(points.Count > 0 && piece.m_icon != null, $"woodwall has {points.Count} snap points and an icon");
            }

            var meshesBefore = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var texturesBefore = Resources.FindObjectsOfTypeAll<Texture>().Length;

            var flagged = new Dictionary<string, List<string>>();
            void Flag(string what, string name)
            {
                if (!flagged.TryGetValue(what, out var list))
                {
                    list = new List<string>();
                    flagged[what] = list;
                }

                list.Add(name);
            }

            var snaps = new List<Transform>();
            int meshFilters = 0, skinnedMeshes = 0, lodGroups = 0, instanceRenderers = 0;
            int withSkinned = 0, withLod = 0, withInstanced = 0, seen = 0;
            var slowestMs = 0d;
            var slowestName = "-";

            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var prefab in tool.m_pieces)
            {
                seen++;
                var startedAt = watch.Elapsed.TotalMilliseconds;
                if (prefab == null)
                {
                    Flag("null prefab", "#" + seen);
                    continue;
                }

                var piece = prefab.GetComponent<Piece>();
                if (piece == null)
                {
                    Flag("no Piece component", prefab.name);
                    continue;
                }

                if (piece.m_icon == null)
                {
                    Flag("no icon", prefab.name);
                }

                var filters = prefab.GetComponentsInChildren<MeshFilter>(true);
                var skins = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var lods = prefab.GetComponentsInChildren<LODGroup>(true);
                var instanced = prefab.GetComponentsInChildren<InstanceRenderer>(true);
                meshFilters += filters.Length;
                skinnedMeshes += skins.Length;
                lodGroups += lods.Length;
                instanceRenderers += instanced.Length;
                if (skins.Length > 0)
                {
                    withSkinned++;
                    Flag("SkinnedMeshRenderer", prefab.name);
                }

                if (lods.Length > 0)
                {
                    withLod++;
                }

                if (instanced.Length > 0)
                {
                    withInstanced++;
                    Flag("InstanceRenderer", prefab.name);
                }

                if (filters.Length == 0 && skins.Length == 0)
                {
                    Flag("nothing to draw", prefab.name);
                }

                if (MeshBounds(prefab, filters).size.sqrMagnitude < 1e-6f)
                {
                    Flag("empty mesh bounds", prefab.name);
                }

                if (prefab.GetComponentsInChildren<Collider>(true).Length == 0)
                {
                    Flag("no collider", prefab.name);
                }

                snaps.Clear();
                piece.GetSnapPoints(snaps);
                if (snaps.Count == 0)
                {
                    Flag("no snap points", prefab.name);
                }

                var took = watch.Elapsed.TotalMilliseconds - startedAt;
                if (took > slowestMs)
                {
                    slowestMs = took;
                    slowestName = prefab.name;
                }
            }

            watch.Stop();
            var total = watch.Elapsed.TotalMilliseconds;
            report.AppendLine($"piece walk: {seen} prefabs in {total:0.0} ms ({total / Mathf.Max(1, seen):0.00} ms each),"
                + $" slowest {slowestName} at {slowestMs:0.0} ms");
            report.AppendLine($"  components: MeshFilter {meshFilters}, SkinnedMeshRenderer {skinnedMeshes} (on {withSkinned} pieces),"
                + $" LODGroup {lodGroups} (on {withLod}), InstanceRenderer {instanceRenderers} (on {withInstanced})");
            foreach (var entry in flagged.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"  {entry.Key}: {entry.Value.Count} -> {string.Join(", ", entry.Value.Take(15))}"
                    + (entry.Value.Count > 15 ? ", ..." : ""));
            }

            var meshesAfter = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            var texturesAfter = Resources.FindObjectsOfTypeAll<Texture>().Length;
            report.AppendLine($"  loaded during the walk: meshes {meshesBefore}->{meshesAfter},"
                + $" textures {texturesBefore}->{texturesAfter}");
            report.AppendLine("  on-demand asset loading triggered by reading the prefabs: "
                + (meshesAfter > meshesBefore || texturesAfter > texturesBefore ? "YES" : "no"));
            Check(seen == tool.m_pieces.Count, $"walked all {seen} piece prefabs in {total:0} ms");
        }

        // ---------- scenario: editor_open ----------

        /// <summary>
        /// The editor window opens on its key, the game stops listening, Esc closes it and the
        /// game listens again. Keys are pressed for real, through the input system, so the whole
        /// path is covered: plugin Update -> ZInput -> EditorSession -> the Harmony patch set.
        /// </summary>
        private static IEnumerator TestEditorOpen(Player player)
        {
            // Build mode on: opening the editor has to close the build menu too.
            yield return EquipHammer(player);
            yield return new WaitForSeconds(1f);

            Log($"focus={Application.isFocused} keyboard={UnityEngine.InputSystem.Keyboard.current != null}"
                + $" cursor={Cursor.lockState}");
            Check(player.TakeInput(), "the player takes input before the editor opens");
            Check(!ValheimTomrer.Editor.Ui.ModUi.Open, "the editor starts closed");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);

            Check(ValheimTomrer.Editor.Ui.ModUi.Open, "the key opened the editor");
            Check(ValheimTomrer.Editor.Ui.EditorWindow.Visible, "the window is on screen");
            Check(!player.TakeInput(), "Player.TakeInput is false while the editor is open");
            Check(Cursor.lockState == CursorLockMode.None, $"the cursor is unlocked (is {Cursor.lockState})");
            Check(!Hud.IsPieceSelectionVisible(), "the build menu is hidden");
            yield return Screenshot("editor-1-open");

            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.5f);

            Check(!ValheimTomrer.Editor.Ui.ModUi.Open, "Esc closed the editor");
            Check(!ValheimTomrer.Editor.Ui.ModUi.Blocking, "the one-frame grace is over");
            Check(!ValheimTomrer.Editor.Ui.EditorWindow.Visible, "the window is gone");
            Check(player.TakeInput(), "the player takes input again");
            Check(!Menu.IsVisible(), "the closing Esc did not open the pause menu");
            yield return Screenshot("editor-2-closed");
        }

        // ---------- scenario: editor_view ----------

        /// <summary>
        /// The 3D pane: a kit standing on the grid, the camera framing it, orbiting, zooming and
        /// flying, and nothing left behind when the window closes. Three screenshots, and the
        /// picture itself is read back out of the render texture so a black pane cannot pass.
        /// </summary>
        private static IEnumerator TestEditorView(Player player)
        {
            yield return new WaitForSeconds(1f);
            Check(!ModUi.Open, "the editor starts closed");

            var mainMask = GameCamera.instance.m_camera.cullingMask;
            Check((mainMask & (1 << EditorConfig.Layer)) == 0,
                $"the player's camera does not draw layer {EditorConfig.Layer} (mask 0x{mainMask:X8})");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open, "the key opened the editor");

            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            var model = ViewportHost.Model;
            Check(ViewportHost.Ready, $"the kit stands in the pane after {waited:0.00} s");
            if (model == null || !ViewportHost.Ready)
            {
                yield break;
            }

            Check(model.Built == model.Total && model.Total > 0, $"every piece built: {model.Built}/{model.Total}");
            Check(ViewportHost.Scene != null && ViewportHost.Scene.IsAlive, "the editor scene is alive");
            Check(ViewportHost.Preview.Unity.cullingMask == 1 << EditorConfig.Layer,
                $"the pane's camera only draws layer {EditorConfig.Layer}");
            Log($"pane texture {ViewportHost.Preview.Width}x{ViewportHost.Preview.Height}"
                + $" | model bounds {model.LocalBounds} | pieces {model.Total}");

            var sceneRoot = ViewportHost.Scene.Root;
            Check(Mathf.Approximately(sceneRoot.position.y, EditorScene.Depth),
                $"the scene hangs at y={sceneRoot.position.y:0} , far under the world");

            CheckOddPieces();

            ViewportHost.Frame();
            yield return null;
            yield return null;
            var framed = SampleView("framed");
            Check(Painted(framed) > 0.2f, $"the pane is not empty: {Painted(framed) * 100f:0} % of it is not background");

            // The same ray the next phase will select with: masked to the editor layer, ground excluded.
            var picked = ViewportHost.Raycast.Pick(new Vector2(0.5f, 0.5f), out var hit);
            Check(picked && hit.collider.GetComponentInParent<Piece>() != null,
                $"the middle of the pane picks a piece: {(picked ? hit.collider.transform.root.name + " at " + hit.distance.ToString("0.0") + " m" : "nothing")}");
            yield return Screenshot("editor-view-1-framed");

            // Orbit a quarter turn and come closer.
            var camera = ViewportHost.Camera;
            var before = camera.Position;
            var distance = camera.Distance;
            camera.Orbit(90f, -8f);
            for (var notch = 0; notch < 6; notch++)
            {
                camera.Zoom(-100f, new Vector2(0.5f, 0.5f));
            }

            yield return null;
            yield return null;
            Check(camera.Distance < distance * 0.9f, $"the wheel came closer: {distance:0.0} m -> {camera.Distance:0.0} m");
            Check(Vector3.Distance(camera.Position, before) > 1f, "orbiting moved the camera");
            var orbited = SampleView("orbited");
            Check(Difference(framed, orbited) > 0.05f,
                $"the view changed after orbiting ({Difference(framed, orbited) * 100f:0} % of the pixels)");
            yield return Screenshot("editor-view-2-orbited");

            // Free camera: the cursor is held, the mouse turns the view, W flies forward.
            ViewportHost.SetMode(CameraMode.Free);
            ViewportHost.Frame();
            yield return new WaitForSeconds(0.3f);
            Check(camera.Mode == CameraMode.Free, "the free camera is on");
            Check(Cursor.lockState == CursorLockMode.Locked, $"the cursor is held (is {Cursor.lockState})");

            var yaw = camera.Yaw;
            camera.MouseLook(new Vector2(100f, 0f));
            Check(Mathf.Abs(Mathf.DeltaAngle(yaw, camera.Yaw) - 14f) < 0.5f,
                $"100 mouse pixels turned the view {Mathf.DeltaAngle(yaw, camera.Yaw):0.0} degrees (0.14 each)");

            var flyFrom = camera.Position;
            yield return HoldKey(UnityEngine.InputSystem.Key.W, 0.8f);
            var flew = Vector3.Distance(camera.Position, flyFrom);
            Check(flew > 1f, $"W flew the camera forward {flew:0.0} m");
            yield return null;
            var flown = SampleView("flown");
            Check(Difference(orbited, flown) > 0.05f,
                $"the view changed after flying ({Difference(orbited, flown) * 100f:0} % of the pixels)");
            yield return Screenshot("editor-view-3-free");

            // Esc gives the cursor back without closing; the second one closes.
            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.4f);
            Check(ModUi.Open, "Esc in free camera keeps the window open");
            Check(camera.Mode == CameraMode.Orbit, "Esc went back to the orbit camera");
            Check(Cursor.lockState == CursorLockMode.None, $"Esc gave the cursor back (is {Cursor.lockState})");

            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open, "the second Esc closed the editor");
            Check(ViewportHost.Scene == null && ViewportHost.Preview == null, "the pane let go of its scene and camera");
            Check(GameObject.Find("ValheimTomrer_EditorScene") == null, "the editor scene object is gone");
            Check(ViewportHost.Leaked() == 0, $"nothing left behind: {ViewportHost.Leaked()} objects still alive");
        }

        /// <summary>
        /// The odd pieces Phase 0 flagged. The artisan station is drawn by skinned meshes, which a
        /// mesh-only copy would lose, so it is built solid here the way the pane builds it.
        /// piece_repair (no mesh, no collider) turns out not to be a world prefab at all, so no
        /// blueprint can ever hold it; the box-collider fallback stays as cover for modded pieces.
        /// </summary>
        private static void CheckOddPieces()
        {
            Check(ZNetScene.instance.GetPrefab("piece_repair") == null,
                "piece_repair is not a world prefab, so the pane is never asked to draw it");

            var style = PreviewStyle.Solid(EditorConfig.Layer);
            foreach (var name in new[] { "piece_artisanstation" })
            {
                var single = new Blueprint { Name = "probe_" + name };
                single.Pieces.Add(new BlueprintPiece { PrefabName = name, Rotation = Quaternion.identity });
                if (!ResolvedBlueprint.TryResolve(single, out var resolved, out var error))
                {
                    Check(false, error);
                    continue;
                }

                var preview = BlueprintPreview.Create(resolved, style);
                var root = preview.Root;
                var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var meshes = root.GetComponentsInChildren<MeshRenderer>(true).Length;
                var colliders = root.GetComponentsInChildren<Collider>(true);
                var stray = root.GetComponentsInChildren<Transform>(true).Count(t => t.gameObject.layer != EditorConfig.Layer);
                Log($"{name}: {meshes} mesh renderers, {skinned.Length} skinned, {colliders.Length} colliders,"
                    + $" bounds {preview.LocalBounds.size}");

                Check(stray == 0, $"{name} is wholly on layer {EditorConfig.Layer} ({stray} objects are not)");
                Check(colliders.Any(c => c.enabled), $"{name} can be clicked: {colliders.Length} colliders, on");
                if (name == "piece_artisanstation")
                {
                    Check(skinned.Length > 0 && skinned.All(r => r.enabled && r.sharedMesh != null),
                        $"{name} keeps its skinned parts ({skinned.Length})");
                }

                preview.Destroy();
            }
        }

        /// <summary>The pane's picture, shrunk to 64x64, so two views can be compared.</summary>
        private static Color[] SampleView(string what)
        {
            var source = ViewportHost.Preview != null ? ViewportHost.Preview.Texture : null;
            if (source == null)
            {
                Check(false, "no pane texture to read for " + what);
                return new Color[0];
            }

            var small = RenderTexture.GetTemporary(64, 64, 0);
            Graphics.Blit(source, small);
            var shot = new Texture2D(64, 64, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            RenderTexture.active = small;
            shot.ReadPixels(new Rect(0f, 0f, 64f, 64f), 0, 0);
            shot.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(small);

            var pixels = shot.GetPixels();
            UnityEngine.Object.Destroy(shot);
            Log($"pane '{what}': {Painted(pixels) * 100f:0} % not background, mean {Mean(pixels)}");
            return pixels;
        }

        /// <summary>How much of the pane is something other than the flat background colour.</summary>
        private static float Painted(Color[] pixels)
        {
            var background = new Color32(0xB9, 0xC7, 0xD2, 0xFF);
            var painted = pixels.Count(p => Math.Abs(p.r - background.r / 255f) + Math.Abs(p.g - background.g / 255f)
                + Math.Abs(p.b - background.b / 255f) > 0.06f);
            return pixels.Length == 0 ? 0f : (float)painted / pixels.Length;
        }

        /// <summary>How many pixels two pane pictures differ in.</summary>
        private static float Difference(Color[] a, Color[] b)
        {
            if (a.Length == 0 || a.Length != b.Length)
            {
                return 0f;
            }

            var changed = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b) > 0.06f)
                {
                    changed++;
                }
            }

            return (float)changed / a.Length;
        }

        private static Color Mean(Color[] pixels)
        {
            var sum = new Vector3();
            foreach (var pixel in pixels)
            {
                sum += new Vector3(pixel.r, pixel.g, pixel.b);
            }

            sum /= Mathf.Max(1, pixels.Length);
            return new Color(sum.x, sum.y, sum.z);
        }

        /// <summary>
        /// Presses a key the way a person would: a state event into the input system, held for a
        /// few frames so the "went down this frame" edge cannot fall between two updates.
        /// </summary>
        private static IEnumerator PressKey(UnityEngine.InputSystem.Key key)
        {
            return HoldKey(key, 0f);
        }

        /// <summary>The same, held down for a while, for keys that are read every frame.</summary>
        private static IEnumerator HoldKey(UnityEngine.InputSystem.Key key, float seconds)
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null)
            {
                Check(false, "no keyboard device to press " + key);
                yield break;
            }

            UnityEngine.InputSystem.InputSystem.QueueStateEvent(
                keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(key));
            for (var frame = 0; frame < 4; frame++)
            {
                yield return null;
            }

            var until = Time.time + seconds;
            while (Time.time < until)
            {
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(
                    keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(key));
                yield return null;
            }

            UnityEngine.InputSystem.InputSystem.QueueStateEvent(
                keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            yield return null;
        }

        // ---------- scenario: editor_palette ----------

        /// <summary>
        /// The piece catalog and the palette panel. The character is given a handful of recipes, so
        /// the unlocked list is short and the config switch makes a visible difference. Then the
        /// window is opened and the grid, the chips, the search box and the piece list are checked
        /// against the hammer's own numbers.
        /// </summary>
        private static IEnumerator TestEditorPalette(Player player)
        {
            yield return new WaitForSeconds(1f);
            yield return EquipHammer(player);

            var tool = player.GetBuildTool();
            Check(tool != null, "the hammer's piece table is in hand");
            if (tool == null)
            {
                yield break;
            }

            Check(!player.PlacementCostDisabled, "placement cost is on, so the unlocked list is the real one");

            // A short, known unlock list. Without this the test would ride on whatever the saved
            // character happened to know.
            var knownBefore = new List<string>(player.m_knownRecipes);
            var few = new List<string>();
            foreach (var prefab in tool.m_pieces)
            {
                var piece = prefab.GetComponent<Piece>();
                if (piece == null || piece.m_repairPiece || piece.m_removePiece || !piece.m_enabled)
                {
                    continue;
                }

                if (!few.Contains(piece.m_name))
                {
                    few.Add(piece.m_name);
                }

                if (few.Count == 6)
                {
                    break;
                }
            }

            player.m_knownRecipes.Clear();
            foreach (var name in few)
            {
                player.m_knownRecipes.Add(name);
            }

            player.UpdateAvailablePiecesList();
            yield return null;

            var wantAll = tool.m_pieces.Count(p => Buildable(p));
            var wantUnlocked = tool.m_availablePieces.Count(p => !p.m_repairPiece && !p.m_removePiece);
            Log($"hammer table: {tool.m_pieces.Count} pieces, {wantAll} buildable, {wantUnlocked} unlocked");

            PieceCatalog.Clear();
            EditorConfig.ShowAllPieces.Value = false;
            var built = PieceCatalog.Ensure();
            Check(built && PieceCatalog.All.Count == wantAll,
                $"the catalog holds every piece but repair and remove: {PieceCatalog.All.Count} of {wantAll}");
            Check(PieceCatalog.Visible.Count == wantUnlocked,
                $"ShowAllPieces off: the palette shows the {wantUnlocked} unlocked pieces"
                + $" (is {PieceCatalog.Visible.Count})");
            Check(wantUnlocked < wantAll, $"the two lists really differ ({wantUnlocked} of {wantAll})");

            EditorConfig.ShowAllPieces.Value = true;
            Check(PieceCatalog.Visible.Count == wantAll,
                $"ShowAllPieces on: the palette shows all {wantAll} pieces (is {PieceCatalog.Visible.Count})");

            CheckCatalogDetail();

            // The window itself.
            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(1f);
            Check(ModUi.Open, "the key opened the editor");
            Check(EditorWindow.PalettePane != null && EditorWindow.PalettePane.gameObject.activeSelf,
                "the Pieces tab is the one on show");

            yield return null;
            yield return null;
            Check(Palette.ShownCount == wantAll, $"the grid lists all {wantAll} pieces (is {Palette.ShownCount})");
            Check(Palette.FooterText == $"{wantAll} of {wantAll} pieces.", $"footer reads '{Palette.FooterText}'");
            Check(Palette.TagChipCount == PieceCatalog.Tags.Count + 1,
                $"one chip per tag plus All: {Palette.TagChipCount} chips for {PieceCatalog.Tags.Count} tags");
            Check(Palette.MaterialChipCount > 5, $"the material filter has {Palette.MaterialChipCount} chips");

            // Virtualised: a few rows of widgets carry hundreds of pieces.
            Log($"tiles alive: {Palette.LiveTiles} for {Palette.ShownCount} pieces");
            Check(Palette.LiveTiles > 0 && Palette.LiveTiles < 120,
                $"only the rows in view exist: {Palette.LiveTiles} tiles for {Palette.ShownCount} pieces");

            // Search: an AND of the words, over display name and prefab.
            yield return SearchIs(player, "wood wall");
            yield return SearchIs(player, "beam");
            yield return SearchIs(player, "zzzz");
            Check(Palette.ShownCount == 0, "a search that matches nothing empties the grid");

            Palette.SetSearch("");
            yield return null;
            Check(Palette.ShownCount == wantAll, "clearing the search brings every piece back");

            // A tag chip.
            var tag = PieceCatalog.Tags.Contains("Roof") ? "Roof" : PieceCatalog.Tags[0];
            var wantTag = PieceCatalog.Visible.Count(p => p.UsageTags.Contains(tag));
            Palette.SetTag(tag);
            yield return null;
            Check(Palette.ShownCount == wantTag, $"the {tag} chip leaves {wantTag} pieces (is {Palette.ShownCount})");
            Palette.SetTag(null);
            yield return null;

            // Open the material row and a hover card, so the screenshot shows everything at once.
            Palette.SetMaterialsOpen(true);
            var card = PieceCatalog.Find("woodwall") ?? PieceCatalog.Visible[0];
            Palette.ShowCard(card);
            yield return null;
            yield return null;
            Check(Palette.CardVisible, $"the hover card is up for '{card.DisplayName}'");
            yield return Screenshot("editor-palette-1-grid");

            Palette.HideCard();
            Palette.SetMaterialsOpen(false);

            // The switch works live, with the window open.
            EditorConfig.ShowAllPieces.Value = false;
            yield return null;
            yield return null;
            Check(Palette.ShownCount == wantUnlocked,
                $"flipping the switch refreshed the open palette: {Palette.ShownCount} of {wantUnlocked}");
            yield return Screenshot("editor-palette-2-unlocked");

            EditorConfig.ShowAllPieces.Value = true;
            yield return null;
            yield return null;

            // The second tab.
            yield return TestPieceList(player);

            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open, "Esc closed the editor");

            player.m_knownRecipes.Clear();
            foreach (var name in knownBefore)
            {
                player.m_knownRecipes.Add(name);
            }

            player.UpdateAvailablePiecesList();
            Log($"put the character's {knownBefore.Count} recipes back");
        }

        private static bool Buildable(GameObject prefab)
        {
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            return piece != null && !piece.m_repairPiece && !piece.m_removePiece;
        }

        /// <summary>Types a search and compares the grid with the same filter run by hand.</summary>
        private static IEnumerator SearchIs(Player player, string text)
        {
            var words = text.ToLowerInvariant().Split(' ');
            var want = PieceCatalog.Visible.Count(p => words.All(w => p.SearchText.Contains(w)));
            Palette.SetSearch(text);
            yield return null;
            Check(Palette.ShownCount == want, $"search '{text}' leaves {want} pieces (is {Palette.ShownCount})");
        }

        /// <summary>What the catalog read off the prefabs, beyond the counts.</summary>
        private static void CheckCatalogDetail()
        {
            var noIcon = PieceCatalog.All.Count(p => p.Icon == null);
            Log($"pieces with no icon: {noIcon} (they get a blank slot with the prefab name)");

            var wall = PieceCatalog.Find("woodwall");
            Check(wall != null, "the catalog knows woodwall");
            if (wall != null)
            {
                Log($"woodwall: '{wall.DisplayName}' | tags {string.Join(",", wall.UsageTags)}"
                    + $" | cost {string.Join(",", wall.Cost.Select(c => c.Amount + " " + c.Name))}"
                    + $" | station {wall.StationName ?? "-"} | size {wall.Bounds.size}"
                    + $" | snaps {wall.SnapPoints.Length} | colliders {wall.Colliders.Length}"
                    + $" (ray {wall.RayColliders.Length}, touch {wall.TouchColliders.Length},"
                    + $" snap search {wall.SnapSearchColliders.Length})");
                Check(wall.SnapPoints.Length == 4 && wall.SnapNames.Length == 4,
                    $"woodwall has its 4 snap points, named ({wall.SnapNames.Length} names)");
                Check(wall.Cost.Length == 1 && wall.Cost[0].Amount == 2 && wall.Cost[0].Icon != null,
                    "woodwall costs 2 wood, with the item's own icon");
                Check(wall.StationName != null && wall.Icon != null, "woodwall has a station and an icon");
                Check(wall.RayColliders.Length > 0 && wall.SnapSearchColliders.Length > 0,
                    $"woodwall's colliders are split: {wall.RayColliders.Length} ray, "
                    + $"{wall.TouchColliders.Length} touch, {wall.SnapSearchColliders.Length} snap search");
                Check(wall.Bounds.size.x > 1.5f && wall.Bounds.size.y > 1.5f, $"woodwall measures {wall.Bounds.size}");
            }

            Check(PieceCatalog.All.All(p => !p.RepairPiece && !p.RemovePiece),
                "no repair or remove tool got into the catalog");
            Check(PieceCatalog.Tags.Count > 5 && PieceCatalog.Tags[0] == "Misc",
                $"tags in the game's order: {string.Join(", ", PieceCatalog.Tags)}");

            // A piece of another tool must be named as such, not called unknown.
            var other = OtherToolPiece();
            if (other != null)
            {
                var problem = PieceCatalog.Problem(other);
                Check(problem != null && problem.EndsWith("piece"),
                    $"'{other}' is reported as '{problem}'");
            }

            Check(PieceCatalog.Problem("not_a_piece_at_all") == "unknown", "a made-up name is 'unknown'");
        }

        /// <summary>The first piece of a build tool that is not the hammer (hoe, cultivator, ...).</summary>
        private static string OtherToolPiece()
        {
            var hammer = PieceCatalog.Table;
            foreach (var prefab in ObjectDB.instance.m_items)
            {
                var item = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                var table = item != null ? item.m_itemData.m_shared.m_buildPieces : null;
                if (table == null || table == hammer)
                {
                    continue;
                }

                foreach (var piece in table.m_pieces)
                {
                    if (piece != null && PieceCatalog.Find(piece.name) == null)
                    {
                        Log($"other tool: '{item.m_itemData.m_shared.m_name}' has {table.m_pieces.Count} pieces,"
                            + $" first '{piece.name}'");
                        return piece.name;
                    }
                }
            }

            return null;
        }

        /// <summary>The second tab: one row per piece of the open blueprint, and the selection hook.</summary>
        private static IEnumerator TestPieceList(Player player)
        {
            EditorWindow.SetLeftTab(1);
            yield return null;
            yield return null;

            var document = EditorSession.Document;
            Check(document != null && document.Pieces.Count > 0, "a blueprint is open in the editor");
            if (document == null)
            {
                yield break;
            }

            Check(PieceListPanel.RowCount == document.Pieces.Count,
                $"the list has a row per piece: {PieceListPanel.RowCount} of {document.Pieces.Count}");
            Check(PieceListPanel.LiveRows > 0 && PieceListPanel.LiveRows <= PieceListPanel.RowCount + 1,
                $"only the rows in view exist: {PieceListPanel.LiveRows} widgets for {PieceListPanel.RowCount} rows");
            Check(PieceListPanel.FooterText.EndsWith("pieces."), $"footer reads '{PieceListPanel.FooterText}'");

            var clicked = new List<int>();
            PieceListPanel.PieceClicked = (id, additive) => clicked.Add(id);
            PieceListPanel.SetSelection(new[] { document.Pieces[0].Id });
            yield return null;
            Check(PieceListPanel.Selection.Count == 1, "the next phase can push a selection into the list");
            PieceListPanel.PieceClicked = null;

            yield return Screenshot("editor-palette-3-list");
            EditorWindow.SetLeftTab(0);
            yield return null;
        }

        // ---------- scenario: editor_files ----------

        /// <summary>
        /// The document, the writer and the file commands. Every kit and every file the player has
        /// is read, written and read again; the writer's output for the workshop kit is compared
        /// with a fixture Tomrer wrote; a file is then made, saved, reopened, renamed and deleted in
        /// a temp folder, so the player's own blueprints are never touched.
        /// </summary>
        private static IEnumerator TestEditorFiles(Player player)
        {
            yield return new WaitForSeconds(1f);

            CheckNumbers();
            CheckFileNames();
            CheckRoundTrips();
            CheckFixture();
            CheckUndo();
            yield return null;
            CheckFileCommands();
        }

        /// <summary>The number cases the format hangs on. Everything else is rounding noise.</summary>
        private static void CheckNumbers()
        {
            Number(1f, 4, "1");
            Number(1.50f, 4, "1.5");
            Number(-0f, 4, "0");
            Number(-0.00001f, 4, "0");
            Number(1e-7f, 7, "0.0000001");
            Number(1e-7f, 4, "0");
            Number(-2.0313f, 4, "-2.0313");
            Number(0.7071068f, 7, "0.7071068");

            // Away from zero, not to the even digit: .NET would write 0.0002 for a float formatted
            // straight, because it rounds the shortest text that reads back as that float.
            Number(0.00025f, 4, "0.0003");

            // The float nearest 0.00005 is 0.000049999999, just under half, so it rounds to nothing.
            Number(0.00005f, 4, "0");
        }

        private static void Number(float value, int decimals, string want)
        {
            var got = BlueprintFormat.FormatNumber(value, decimals);
            Check(got == want, $"FormatNumber({value:R}, {decimals}) = {got}, wanted {want}");
        }

        private static void CheckFileNames()
        {
            Name("Camp hut", "camp-hut.blueprint");
            Name("  Tomrer 2!  ", "tomrer-2.blueprint");
            Name("***", "blueprint.blueprint");
            Name("", "blueprint.blueprint");
            Name(null, "blueprint.blueprint");
        }

        private static void Name(string name, string want)
        {
            var got = BlueprintFormat.FileNameFor(name);
            Check(got == want, $"FileNameFor('{name}') = {got}, wanted {want}");
        }

        /// <summary>Read, write, read again: the same pieces, and the text settles at once.</summary>
        private static void CheckRoundTrips()
        {
            var kits = DocumentStore.ListKits();
            var files = DocumentStore.ListUserFiles();
            Check(kits.Count > 0, $"kits inside the DLL: {kits.Count}");
            Log($"user files in {BlueprintLibrary.UserFolder}: {files.Count}");

            foreach (var kit in kits)
            {
                if (!DocumentStore.OpenKit(kit, out var document, out var error))
                {
                    Check(false, error);
                    continue;
                }

                Check(kit.ReadOnly && document.ReadOnly, $"kit '{kit.Name}' is read-only");
                RoundTrip("kit " + kit.Name, document.ToBlueprint());
            }

            foreach (var file in files)
            {
                if (file.Error != null)
                {
                    Check(false, $"{Path.GetFileName(file.Path)}: {file.Error}");
                    continue;
                }

                if (!DocumentStore.Open(file.Path, out var document, out var error))
                {
                    Check(false, error);
                    continue;
                }

                RoundTrip("file " + Path.GetFileName(file.Path), document.ToBlueprint());
            }
        }

        private static void RoundTrip(string what, Blueprint blueprint)
        {
            var first = BlueprintFormat.Write(blueprint);
            var again = BlueprintFormat.ParseBlueprint(blueprint.Name, first.Split('\n'));
            var second = BlueprintFormat.Write(again);

            Check(first == second, $"{what}: the text settles after one write ({first.Length} bytes)");
            Check(SamePieces(blueprint, again, out var why), $"{what}: {why}");
            Check(again.Name == blueprint.Name && again.Description == blueprint.Description
                && again.IconPrefab == blueprint.IconPrefab,
                $"{what}: headers survive ('{again.Name}' / '{again.Description}' / {again.IconPrefab ?? "-"})");
        }

        private static bool SamePieces(Blueprint a, Blueprint b, out string why)
        {
            if (a.Pieces.Count != b.Pieces.Count)
            {
                why = $"{a.Pieces.Count} pieces became {b.Pieces.Count}";
                return false;
            }

            var worstPosition = 0f;
            var worstRotation = 0f;
            for (var i = 0; i < a.Pieces.Count; i++)
            {
                var one = a.Pieces[i];
                var other = b.Pieces[i];
                if (one.PrefabName != other.PrefabName || one.Category != other.Category || one.Rest != other.Rest)
                {
                    why = $"piece {i} changed: {one.PrefabName};{one.Category};{one.Rest ?? "-"}"
                        + $" -> {other.PrefabName};{other.Category};{other.Rest ?? "-"}";
                    return false;
                }

                worstPosition = Mathf.Max(worstPosition, Vector3.Distance(one.Position, other.Position));
                worstRotation = Mathf.Max(worstRotation, Quaternion.Angle(one.Rotation, other.Rotation));
            }

            // Positions are written to a tenth of a millimetre, rotations to seven decimals.
            var ok = worstPosition <= 0.0001f && worstRotation <= 0.001f;
            why = $"{a.Pieces.Count} pieces come back the same"
                + $" (worst {worstPosition * 1000f:0.###} mm, {worstRotation:0.####} degrees)";
            return ok;
        }

        /// <summary>The workshop kit, written by us, has to be byte for byte what Tomrer writes.</summary>
        private static void CheckFixture()
        {
            var want = ReadFixture("ValheimTomrer.Fixtures.workshop.canonical.blueprint");
            var kit = DocumentStore.ListKits().FirstOrDefault(k => k.Name == "Workshop");
            if (want == null || kit == null)
            {
                Check(false, $"fixture or Workshop kit missing (fixture={(want == null ? "no" : "yes")})");
                return;
            }

            if (!DocumentStore.OpenKit(kit, out var document, out var error))
            {
                Check(false, error);
                return;
            }

            var got = BlueprintFormat.Write(document.ToBlueprint());
            Check(got == want, got == want
                ? "the workshop kit is written exactly like Tomrer writes it"
                : "the workshop kit differs from Tomrer's text:\n" + FirstDifference(want, got));
        }

        private static string ReadFixture(string resource)
        {
            using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                {
                    return null;
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string FirstDifference(string want, string got)
        {
            var wantLines = want.Split('\n');
            var gotLines = got.Split('\n');
            for (var i = 0; i < Math.Max(wantLines.Length, gotLines.Length); i++)
            {
                var a = i < wantLines.Length ? wantLines[i] : "<end>";
                var b = i < gotLines.Length ? gotLines[i] : "<end>";
                if (a != b)
                {
                    return $"  line {i + 1} want: {a}\n  line {i + 1} got : {b}";
                }
            }

            return "  same lines, different length";
        }

        /// <summary>Edits make snapshots, typing in one field makes one step, and the limit holds.</summary>
        private static void CheckUndo()
        {
            var document = DocumentStore.New("Undo test");
            Check(!document.Dirty && !document.CanUndo, "a new document is clean with nothing to undo");

            var id = document.AddPiece("woodwall", new Vector3(1f, 0f, 0f), Quaternion.identity);
            Check(document.Dirty && document.UndoDepth == 1, $"adding a piece made one step ({document.UndoDepth})");

            var before = document.Pieces[0];
            document.SetPiece(id, new Vector3(2f, 0f, 0f), Quaternion.identity);
            Check(before.Position.x == 1f && document.Pieces[0].Position.x == 2f,
                "the old snapshot still holds the old position");

            // Typing "Hut" into the name field: three keystrokes, one step.
            var depth = document.UndoDepth;
            document.SetName("H");
            document.SetName("Hu");
            document.SetName("Hut");
            Check(document.UndoDepth == depth + 1, $"typing in one field is one step ({document.UndoDepth - depth})");

            document.SetIcon("piece_workbench");
            Check(document.UndoDepth == depth + 2, "a different field starts a new step");

            document.Undo();
            Check(document.IconPrefab == null, "undo took the icon back");
            document.Undo();
            Check(document.Name == "Undo test", "undo took the whole typed name back at once");
            document.Redo();
            Check(document.Name == "Hut", "redo put it back");

            for (var i = 0; i < BlueprintDocument.UndoLimit + 50; i++)
            {
                document.AddPiece("woodwall", new Vector3(i, 0f, 0f), Quaternion.identity);
            }

            Check(document.UndoDepth == BlueprintDocument.UndoLimit,
                $"the undo stack stops at {BlueprintDocument.UndoLimit} ({document.UndoDepth})");
        }

        /// <summary>Make, save, reopen, rename, duplicate and delete, all inside a temp folder.</summary>
        private static void CheckFileCommands()
        {
            var real = BlueprintLibrary.UserFolder;
            var temp = Path.Combine(OutDir, "files-test");
            try
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, true);
                }

                Directory.CreateDirectory(temp);
                BlueprintLibrary.UserFolder = temp;

                var document = DocumentStore.New("Camp hut");
                Check(!DocumentStore.SaveAs(document, "Camp hut", false, out var error),
                    $"a blueprint with no pieces is not saved: {error}");
                Check(!DocumentStore.Save(document, out error),
                    $"a blueprint with no file of its own needs a new name: {error}");

                document.AddPiece("woodwall", new Vector3(0f, 0.5f, 0f), Quaternion.Euler(0f, 90f, 0f));
                document.AddPiece("woodwall", new Vector3(2f, 0.5f, 0f), Quaternion.identity);

                Check(DocumentStore.SaveAs(document, "Camp hut", false, out error), "save as 'Camp hut': " + error);
                var path = Path.Combine(temp, "camp-hut.blueprint");
                Check(File.Exists(path), "camp-hut.blueprint is on disk");
                Check(!document.Dirty && document.SourcePath == path, "the document is clean and knows its file");
                Check(!DocumentStore.SaveAs(document, "Camp hut", false, out error),
                    $"a second save as the same name is refused: {error}");
                Check(DocumentStore.SaveAs(document, "Camp hut", true, out error), "overwrite is allowed when asked");

                var text = File.ReadAllText(path);
                Check(text.StartsWith("#Name:Camp hut\n") && text.EndsWith("\n") && !text.Contains("\r"),
                    "the file starts with the name, ends with a line break and has LF line ends");
                Check(text.Contains("\nwoodwall;;2;0.5;0;0;0;0;1\n"),
                    "a piece with no turn and nothing kept is written with nine fields");

                var half = BlueprintFormat.PieceLine(new BlueprintPiece
                {
                    PrefabName = "woodwall",
                    Rotation = new Quaternion(0f, -1f, 0f, 0f),
                });
                Check(half == "woodwall;;0;0;0;0;1;0;0", $"a half turn is written 0;1;0;0 (got {half})");

                document.SetDescription("A small hut");
                Check(document.Dirty, "editing makes the document dirty again");
                Check(DocumentStore.Save(document, out error), "save over the same file: " + error);
                Check(!document.Dirty, "saving makes it clean");

                Check(DocumentStore.Open(path, out var reopened, out error), "reopen: " + error);
                Check(reopened.Pieces.Count == 2 && reopened.Name == "Camp hut"
                    && reopened.Description == "A small hut" && !reopened.ReadOnly,
                    $"what was saved comes back: {reopened.Pieces.Count} pieces, '{reopened.Name}', '{reopened.Description}'");
                Check(BlueprintLibrary.All.Any(b => b.Name == "Camp hut"),
                    "the build-mode list saw the new blueprint without a restart");

                Check(DocumentStore.Duplicate(path, out var copy, out error), "duplicate: " + error);
                Check(copy == Path.Combine(temp, "camp-hut-copy.blueprint") && File.Exists(copy),
                    $"the copy is next to it: {Path.GetFileName(copy ?? "-")}");

                Check(DocumentStore.Rename(path, "Long house", out var renamed, out error), "rename: " + error);
                Check(!File.Exists(path), "the old file is gone");
                Check(renamed == Path.Combine(temp, "long-house.blueprint") && File.Exists(renamed),
                    $"the new file is there: {Path.GetFileName(renamed ?? "-")}");
                Check(File.ReadAllText(renamed).StartsWith("#Name:Long house\n"), "the name inside changed too");

                var listed = DocumentStore.ListUserFiles();
                Check(listed.Count == 2, $"the folder lists both files ({listed.Count})");

                Check(DocumentStore.Delete(renamed, out error), "delete: " + error);
                Check(DocumentStore.Delete(copy, out error), "delete the copy: " + error);
                Check(DocumentStore.ListUserFiles().Count == 0, "the folder is empty again");
                Check(!BlueprintLibrary.All.Any(b => b.Name == "Long house"),
                    "the build-mode list dropped the deleted blueprint");

                CheckSectionsAreReadOnly(temp);
                CheckKeptFields(temp);
            }
            finally
            {
                BlueprintLibrary.UserFolder = real;
                BlueprintLibrary.Reload();
            }
        }

        /// <summary>
        /// A file as other mods write it: a "(Clone)" suffix, a category, decimals with a comma and
        /// scale fields after the rotation. Only the prefab name and the numbers are ours to change;
        /// everything else has to come back untouched. It sits in a subfolder, which the list walks.
        /// </summary>
        private static void CheckKeptFields(string folder)
        {
            var sub = Path.Combine(folder, "from-elsewhere");
            Directory.CreateDirectory(sub);
            var path = Path.Combine(sub, "kept.blueprint");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "#Name:Kept",
                "#Pieces",
                "woodwall(Clone);Building;1,5;0;-2;0;0;0;1;something;1;1;1",
                "",
            }));

            Check(DocumentStore.ListUserFiles().Any(f => f.Path == path), "the list walks subfolders");
            if (!DocumentStore.Open(path, out var document, out var error))
            {
                Check(false, error);
                return;
            }

            var piece = document.Pieces[0];
            Check(piece.PrefabName == "woodwall" && piece.Category == "Building" && piece.Rest == "something;1;1;1",
                $"the kept fields are read: prefab={piece.PrefabName} category={piece.Category} rest={piece.Rest}");

            var line = BlueprintFormat.PieceLine(document.ToBlueprint().Pieces[0]);
            Check(line == "woodwall;Building;1.5;0;-2;0;0;0;1;something;1;1;1",
                $"and written back with a dot and no (Clone): {line}");

            RoundTrip("file kept.blueprint", document.ToBlueprint());
            Directory.Delete(sub, true);
        }

        /// <summary>A file with snap points or terrain is opened, but only saved under a new name.</summary>
        private static void CheckSectionsAreReadOnly(string folder)
        {
            var path = Path.Combine(folder, "with-sections.blueprint");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "#Name:With sections",
                "#Keep:whatever",
                "#Pieces",
                "woodwall;;0;0;0;0;0;0;1;",
                "#SnapPoints",
                "0;0;0",
                "",
            }));

            Check(DocumentStore.Open(path, out var document, out var error), "open a file with sections: " + error);
            if (document == null)
            {
                return;
            }

            Check(document.HasSections && document.ReadOnly, "a file with sections is read-only");
            Check(!DocumentStore.Save(document, out error), $"it cannot be saved over: {error}");
            Check(BlueprintFormat.Write(document.ToBlueprint()).Contains("#Keep:whatever"),
                "an unknown header is written back");

            Check(DocumentStore.SaveAs(document, "Without sections", false, out error),
                "it can be saved under a new name: " + error);
            Check(!document.ReadOnly && !document.HasSections, "the new file is editable");

            File.Delete(path);
            File.Delete(Path.Combine(folder, "without-sections.blueprint"));
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
