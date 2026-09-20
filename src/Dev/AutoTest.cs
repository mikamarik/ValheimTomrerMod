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
using ValheimTomrer.Editor.Input;
using ValheimTomrer.Editor.Placement;
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
    /// "editor_palette" builds the piece catalog and checks the palette's counts and filters;
    /// "editor_snap" runs the placing and snapping engine against a table of rays, with no UI;
    /// "editor_edit" drives placing, selecting, copying, turning, nudging and undo, then draws it;
    /// "editor_panels" checks the right panel: the build card, the selection fields and the problem list;
    /// "editor_keys" drives every key, the wheel and the mouse, plus the top bar and the dialogs;
    /// "editor_pad" drives every controller button through a made-up pad, plus the piece menu.
    /// "editor_build" builds a blueprint made in the editor, in the world, and edits it again;
    /// "editor_all" runs every scenario above in one game, then checks the mod wrote no art.
    /// </summary>
    internal static class AutoTest
    {
        private const string TestName = "aclab";
        private const string WorldSeed = "ACTEST01";

        /// <summary>The flat spot the world-building scenarios share, found once a run.</summary>
        private static readonly Quaternion BuildFacing = Quaternion.identity;

        /// <summary>Good enough to stop looking. The hoe pass after it does the real flattening.</summary>
        private const float FlatEnough = 0.4f;

        private static Vector3 _buildSpot;
        private static bool _haveBuildSpot;

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
            Log($"world set to quiet, {AutoTestPeace.Apply()} creatures removed");
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
                case "editor_snap":
                    scenario = TestEditorSnap(player);
                    break;
                case "editor_edit":
                    scenario = TestEditorEdit(player);
                    break;
                case "editor_panels":
                    scenario = TestEditorPanels(player);
                    break;
                case "editor_keys":
                    scenario = TestEditorKeys(player);
                    break;
                case "editor_pad":
                    scenario = TestEditorPad(player);
                    break;
                case "editor_build":
                    scenario = TestEditorBuild(player);
                    break;
                case "editor_all":
                    scenario = TestEverything(player);
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

        // ---------- scenario: editor_all ----------

        /// <summary>
        /// Every scenario, one after the other, in one game. A chained run is the only thing that
        /// catches a scenario leaving something behind for the next one, so this is the run that
        /// has to be green before shipping. It ends with the art guard.
        /// </summary>
        private static IEnumerator TestEverything(Player player)
        {
            // VT_CHAIN cuts the list down while hunting for the scenario that left something behind.
            var names = (Environment.GetEnvironmentVariable("VT_CHAIN")
                ?? "dump,probe,editor_open,editor_view,editor_files,editor_palette,editor_snap,"
                + "editor_edit,editor_panels,editor_keys,editor_pad,editor_build,blueprints")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var name in names)
            {
                var wasPass = _pass;
                var wasFail = _fail;
                Log($"===== {name}");
                var step = Step(name.Trim(), player);
                if (step == null)
                {
                    Check(false, "no such scenario: " + name);
                    continue;
                }

                // Its own Guard, so a scenario that throws costs one FAIL and the chain runs on.
                yield return ValheimTomrerPlugin.Instance.StartCoroutine(Guard(step));
                Log($"===== {name}: pass={_pass - wasPass} fail={_fail - wasFail}");
                yield return Reset(player);
            }

            CheckNoArtWritten();
        }

        private static IEnumerator Step(string name, Player player)
        {
            switch (name)
            {
                case "dump": return DumpPieces(player);
                case "probe": return Probe(player);
                case "blueprints": return TestBlueprints(player);
                case "editor_open": return TestEditorOpen(player);
                case "editor_view": return TestEditorView(player);
                case "editor_files": return TestEditorFiles(player);
                case "editor_palette": return TestEditorPalette(player);
                case "editor_snap": return TestEditorSnap(player);
                case "editor_edit": return TestEditorEdit(player);
                case "editor_panels": return TestEditorPanels(player);
                case "editor_keys": return TestEditorKeys(player);
                case "editor_pad": return TestEditorPad(player);
                case "editor_build": return TestEditorBuild(player);
                default: return null;
            }
        }

        /// <summary>Back to a known state between two scenarios, whatever the last one left open.</summary>
        private static IEnumerator Reset(Player player)
        {
            EditorSession.Close();
            BlueprintMode.Exit();
            PadReader.Fake = null;
            BlueprintLibrary.UserFolder = null;
            BlueprintLibrary.Reload();
            PieceCatalog.Clear();
            if (player != null)
            {
                player.SetGodMode(true);
                player.m_lastToolUseTime = 0f;
            }

            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>
        /// The one rule the whole mod hangs on: it writes blueprints and nothing else. Walks every
        /// folder the mod can write to and fails on anything that is not a .blueprint text file or
        /// the autotest's own output (screenshots, .txt reports, the log, the test worlds).
        /// </summary>
        private static void CheckNoArtWritten()
        {
            var art = new[]
            {
                ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".psd", ".tif", ".tiff", ".dds",
                ".exr", ".hdr", ".webp", ".svg", ".ico", ".mat", ".fbx", ".obj", ".glb", ".gltf",
                ".dae", ".blend", ".mesh", ".asset", ".prefab", ".unity", ".bundle", ".assetbundle",
                ".shader", ".shadergraph", ".spriteatlas", ".ttf", ".otf", ".anim", ".controller",
            };

            var strays = new List<string>();
            var blueprints = 0;

            // 1. The mod's own folder under BepInEx/config: blueprints only, and they must be text.
            var mine = Path.Combine(Paths.ConfigPath, "ValheimTomrer");
            foreach (var file in Files(mine))
            {
                if (!Ext(file).Equals(".blueprint"))
                {
                    strays.Add(file);
                }
                else if (!IsText(file))
                {
                    strays.Add(file + " (not text)");
                }
                else
                {
                    blueprints++;
                }
            }

            // 2. The deployed plugin folder: the build's DLL and symbols, nothing the mod made.
            foreach (var file in Files(Path.Combine(Paths.PluginPath, "ValheimTomrer")))
            {
                var ext = Ext(file);
                if (ext != ".dll" && ext != ".pdb")
                {
                    strays.Add(file);
                }
            }

            // 3. The test's own output folder. Screenshots are the test's, so .png is allowed in
            //    its root; the saved test worlds are the game's, so that folder is skipped.
            var saves = Path.Combine(OutDir, "saves") + Path.DirectorySeparatorChar;
            foreach (var file in Files(OutDir))
            {
                if (file.StartsWith(saves, StringComparison.Ordinal))
                {
                    continue;
                }

                var ext = Ext(file);
                var inRoot = Path.GetDirectoryName(file) == OutDir.TrimEnd(Path.DirectorySeparatorChar);
                if (ext == ".blueprint")
                {
                    blueprints++;
                    if (!IsText(file))
                    {
                        strays.Add(file + " (not text)");
                    }
                }
                else if (ext == ".png" && inRoot)
                {
                    continue; // the autotest's own screenshots
                }
                else if (ext != ".txt" && ext != ".log")
                {
                    strays.Add(file);
                }
            }

            var artFound = strays.Where(f => art.Contains(Ext(f))).ToList();
            Check(blueprints > 0, $"the mod wrote {blueprints} blueprint files");
            Check(artFound.Count == 0, artFound.Count == 0
                ? "no image, mesh, material or bundle file anywhere the mod writes"
                : "the mod wrote game art: " + string.Join(", ", artFound.ToArray()));
            Check(strays.Count == 0, strays.Count == 0
                ? "nothing but blueprints and test output on disk"
                : $"{strays.Count} unexpected files: " + string.Join(", ", strays.Take(10).ToArray()));
        }

        private static string[] Files(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal))
                .ToArray();
        }

        private static string Ext(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant();
        }

        /// <summary>Text, not a file with an innocent name: no zero bytes in the first kilobyte.</summary>
        private static bool IsText(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                for (var i = 0; i < Math.Min(bytes.Length, 1024); i++)
                {
                    if (bytes[i] == 0)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                Log($"cannot read {path}: {e.Message}");
                return false;
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
            var tool = player.GetRightItem();
            Log($"before the build: target={BlueprintMode.HasTarget}"
                + $" blocked={BlueprintMode.Blocked ?? "none"}"
                + $" rule={BlueprintRules.CheckCanBuild(player, resolved) ?? "ok"}"
                + $" tool={(tool != null ? tool.m_shared.m_name : "none")}"
                + $" stamina={player.GetStamina():0}/{player.GetMaxStamina():0}"
                + $" need={(tool != null ? tool.m_shared.m_attack.m_attackStamina : 0f):0}");
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

        // ---------- scenario: editor_edit ----------

        /// <summary>
        /// Editing. The first half drives the state API with no window at all: a wall is placed on
        /// the ground, a second one snaps to it, both are selected, copied, turned and nudged, then
        /// the whole session is undone and half of it redone. The second half opens the real window
        /// and puts a ghost in hand with two pieces selected, for the screenshot.
        /// </summary>
        private static IEnumerator TestEditorEdit(Player player)
        {
            yield return new WaitForSeconds(1f);

            PieceCatalog.Ensure();
            var wall = PieceCatalog.Find("woodwall");
            Check(PieceCatalog.Ready && wall != null, "the catalog is built and has woodwall");
            if (wall == null)
            {
                yield break;
            }

            yield return EditWithoutUi(wall);
            yield return EditInTheWindow(wall);
        }

        /// <summary>The store actions on their own, the way Tomrer's own tests drive them.</summary>
        private static IEnumerator EditWithoutUi(PieceEntry wall)
        {
            var doc = BlueprintDocument.New("Edit test");
            EditorState.Open(doc);
            Check(EditorState.Document == doc && doc.Pieces.Count == 0 && EditorState.Mode == EditMode.Idle
                && EditorState.SelectionCount == 0,
                "the editor starts idle on an empty blueprint");

            Check(EditorState.StartAdd(wall) && EditorState.Mode == EditMode.Place
                && EditorState.Action == PlaceAction.Add && EditorState.Held == wall,
                "a piece from the palette goes in hand");

            var first = EditAim(Vector3.zero);
            Check(first != null && EditorState.CommitPlacement(first), "the first wall dropped");
            Check(doc.Pieces.Count == 1 && Vector3.Distance(doc.Pieces[0].Position, new Vector3(0f, 1f, 0f)) < 1e-4f,
                $"one wall stands on the ground at {V4(doc.Pieces[0].Position)}, wanted (0,1,0)");
            Check(EditorState.Mode == EditMode.Place, "adding stays in hand, so the next one comes straight after");
            Check(EditorState.SelectionCount == 1, "the wall that was just dropped is selected");

            var second = EditAim(new Vector3(1.9f, 0f, 0f));
            Check(second != null && second.Snapped, "the second wall found the first wall's snap point");
            Check(second != null && EditorState.CommitPlacement(second), "the second wall dropped");
            Check(doc.Pieces.Count == 2 && Vector3.Distance(doc.Pieces[1].Position, new Vector3(2f, 1f, 0f)) < 1e-4f,
                $"it snapped a whole wall over, to {V4(doc.Pieces[1].Position)}, wanted (2,1,0)");

            EditorState.CancelMode();
            Check(EditorState.Mode == EditMode.Idle && EditorState.Moving == null, "Esc empties the hand");

            EditorState.SelectAll();
            Check(EditorState.SelectionCount == 2, $"both walls are selected: {EditorState.SelectionCount}");

            Check(EditorState.StartDuplicate() && EditorState.Action == PlaceAction.Duplicate
                && EditorState.Moving != null && EditorState.Moving.Count == 2,
                "the selection goes in hand as a copy");
            var copies = EditAim(new Vector3(0f, 0f, 8f));
            Check(copies != null && EditorState.CommitPlacement(copies), "the copies dropped");
            Check(doc.Pieces.Count == 4, $"four pieces now: {doc.Pieces.Count}");
            Check(EditorState.SelectionCount == 2 && EditorState.IsSelected(doc.Pieces[2].Id)
                && EditorState.IsSelected(doc.Pieces[3].Id),
                "the copies are what is selected, so the next action works on them");
            Check(EditorState.Mode == EditMode.Place, "copying stays in hand too");
            EditorState.CancelMode();

            EditorState.RotateSelection(1);
            var turned = EditorState.SelectedPieces();
            Check(turned.Count == 2 && Mathf.Abs(Mathf.DeltaAngle(YawOf(turned[0].Rotation), Placer.RotateStep)) < 0.01f,
                $"R turned the copies to {YawOf(turned[0].Rotation):0.##} degrees, wanted {Placer.RotateStep}");

            var before = EditorState.SelectedPieces()[0].Position;
            EditorState.Nudge(new Vector3(1f, 0f, 0f));
            var after = EditorState.SelectedPieces()[0].Position;
            Check(Vector3.Distance(after - before, new Vector3(1f, 0f, 0f)) < 1e-4f,
                $"an arrow key moved it by {V4(after - before)}, wanted (1,0,0)");

            Check(doc.UndoDepth == 5, $"the session made 5 undo steps: {doc.UndoDepth}");
            for (var i = 0; i < 5; i++)
            {
                EditorState.Undo();
            }

            Check(doc.Pieces.Count == 0, $"five undos brought the blueprint back to empty: {doc.Pieces.Count} pieces");
            Check(EditorState.SelectionCount == 0, "nothing is left selected either");

            EditorState.Redo();
            EditorState.Redo();
            Check(doc.Pieces.Count == 2, $"two redos brought both walls back: {doc.Pieces.Count} pieces");

            var was = doc.Pieces[0].Position;
            Check(EditorState.CenterOrigin() && Vector3.Distance(doc.Pieces[0].Position, was) > 1e-4f,
                $"centring the origin shifted the walls from {V4(was)} to {V4(doc.Pieces[0].Position)}");
            Check(!EditorState.CenterOrigin(), "a second centring does nothing: the origin is already there");
            EditorState.Undo();
            Check(doc.Pieces.Count == 2 && Vector3.Distance(doc.Pieces[0].Position, was) < 1e-4f,
                "undo put the walls back where they were");
            EditorState.Close();
            yield return null;
        }

        /// <summary>The window, with the ghost in hand and two pieces selected.</summary>
        private static IEnumerator EditInTheWindow(PieceEntry wall)
        {
            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open, "the key opened the editor");

            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            var document = EditorSession.Document;
            Check(ViewportHost.Ready && document != null && document.Pieces.Count >= 2,
                $"a kit stands in the pane after {waited:0.00} s: {(document != null ? document.Pieces.Count : 0)} pieces");
            if (document == null || document.Pieces.Count < 2 || ViewportHost.Pieces == null)
            {
                yield break;
            }

            Check(ViewportHost.Pieces.Count == document.Pieces.Count,
                $"the pane holds one copy per piece: {ViewportHost.Pieces.Count} of {document.Pieces.Count}");

            var plain = SampleView("before editing");

            EditorState.Select(new[] { document.Pieces[0].Id, document.Pieces[1].Id });
            EditorState.StartAdd(wall);

            // The aim follows the mouse, which the test cannot point. Give it a moment, then fall
            // back to the free camera, where the aim is always the middle of the pane.
            var tries = 0f;
            while ((EditorState.Aimed == null || !ViewportHost.Ghost.Visible) && tries < 2f)
            {
                tries += Time.deltaTime;
                if (tries > 0.6f && ViewportHost.Camera.Mode != CameraMode.Free)
                {
                    ViewportHost.SetMode(CameraMode.Free);
                }

                yield return null;
            }

            Check(EditorState.SelectionCount == 2, $"two pieces are selected: {EditorState.SelectionCount}");
            Check(EditorState.Aimed != null, $"the aim found a spot at pane point {ViewportHost.AimAt}");
            Check(ViewportHost.Ghost.PieceCount == 1 && ViewportHost.Ghost.Visible,
                $"the ghost stands where a click would drop the wall ({ViewportHost.Ghost.RendererCount} parts)");
            Check(ViewportHost.Boxes.BoxCount == 3,
                $"three boxes: one per selected piece and one round the group ({ViewportHost.Boxes.BoxCount})");
            Check(ViewportHost.Dots.Drawn > 0, $"snap dots drawn: {ViewportHost.Dots.Drawn}");
            Check(PieceListPanel.Selection.Count == 2,
                $"the blueprint's piece list shows the same selection: {PieceListPanel.Selection.Count}");

            yield return null;
            var edited = SampleView("with the ghost and the boxes");
            Check(Difference(plain, edited) > 0.01f,
                $"the ghost and the boxes changed the picture ({Difference(plain, edited) * 100f:0.0} % of the pixels)");
            yield return Screenshot("editor-edit-1-ghost");

            // And a click really builds: the piece lands in the document and stands in the pane.
            var count = document.Pieces.Count;
            Check(EditorState.CommitPlacement(EditorState.Aimed), "dropping the wall works");
            yield return null;
            yield return null;
            Check(document.Pieces.Count == count + 1,
                $"the blueprint grew by one: {document.Pieces.Count} pieces");
            Check(ViewportHost.Pieces.Count == document.Pieces.Count,
                $"the new wall stands in the pane too: {ViewportHost.Pieces.Count} copies");
            yield return Screenshot("editor-edit-2-placed");

            // And Ctrl+Z takes it back out again, copy and all.
            EditorState.Undo();
            yield return null;
            yield return null;
            Check(document.Pieces.Count == count && ViewportHost.Pieces.Count == count,
                $"undo took it out of both: {document.Pieces.Count} pieces, {ViewportHost.Pieces.Count} copies");
        }

        /// <summary>One ray straight down on a spot, the way the mouse aims at the ground.</summary>
        private static PlaceResult EditAim(Vector3 at)
        {
            EditorState.Aim(at + new Vector3(0f, 20f, 0f), Vector3.down, out var result);
            return result;
        }

        private static float YawOf(Quaternion q)
        {
            var forward = q * Vector3.forward;
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        // ---------- scenario: editor_panels ----------

        /// <summary>
        /// The right panel. A blueprint holding a seasonal piece, a locked piece, an unknown
        /// prefab, a piece of another tool, two pieces in the same spot and a scaled piece is
        /// written to a file and opened; its problem list is then compared row by row with the
        /// list the phase asks for. The window is opened on it for the rest: a check row is
        /// clicked, the selection fields are read, committed and reverted, and a new name is
        /// typed into the blueprint panel with the build card watching.
        /// </summary>
        private static IEnumerator TestEditorPanels(Player player)
        {
            yield return new WaitForSeconds(1f);
            yield return EquipHammer(player);

            var tool = player.GetBuildTool();
            Check(tool != null, "the hammer's piece table is in hand");
            if (tool == null)
            {
                yield break;
            }

            // A short, known unlock list, so "not unlocked yet" is a fact and not a guess.
            var knownBefore = new List<string>(player.m_knownRecipes);
            KnowAFewPieces(player, tool, 6);
            yield return null;

            PieceCatalog.Clear();
            PieceCatalog.Ensure();
            var normal = PieceCatalog.Unlocked.FirstOrDefault(p => p.Dlc.Length == 0 && p.Cost.Length > 0);
            var locked = PieceCatalog.All.FirstOrDefault(
                p => !p.Seasonal && p.Dlc.Length == 0 && !PieceCatalog.IsUnlocked(p));
            var seasonal = PieceCatalog.All.FirstOrDefault(p => p.Seasonal && p.Dlc.Length == 0);
            var hoe = OtherToolPiece();
            hoe = hoe != null && PieceCatalog.OtherTool(hoe) != null ? hoe : null;
            Check(normal != null && locked != null && seasonal != null,
                $"the fixture's pieces exist: normal={Named(normal)} locked={Named(locked)} "
                + $"seasonal={Named(seasonal)} otherTool={hoe ?? "none"}");
            if (normal == null || locked == null || seasonal == null)
            {
                yield break;
            }

            var document = PanelsFixture(normal, seasonal, locked, hoe);
            if (document == null)
            {
                yield break;
            }

            var checks = CheckProblemList(document, normal, seasonal, locked, hoe);
            CheckOtherProblems(normal);
            yield return PanelsInTheWindow(document, checks, normal);

            player.m_knownRecipes.Clear();
            foreach (var name in knownBefore)
            {
                player.m_knownRecipes.Add(name);
            }

            player.UpdateAvailablePiecesList();
            Log($"put the character's {knownBefore.Count} recipes back");
        }

        /// <summary>Cuts the character's recipe list down to a handful, so unlocks are known.</summary>
        private static void KnowAFewPieces(Player player, PieceTable tool, int count)
        {
            var few = new List<string>();
            foreach (var prefab in tool.m_pieces)
            {
                var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece == null || piece.m_repairPiece || piece.m_removePiece || !piece.m_enabled)
                {
                    continue;
                }

                if (!few.Contains(piece.m_name))
                {
                    few.Add(piece.m_name);
                }

                if (few.Count == count)
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
        }

        /// <summary>Writes the test blueprint and reads it back through the real reader.</summary>
        private static BlueprintDocument PanelsFixture(
            PieceEntry normal, PieceEntry seasonal, PieceEntry locked, string hoe)
        {
            var lines = new List<string>
            {
                "#Name:Panel test",
                "#Description:A blueprint with problems",
                "#Icon:vt_missingicon",
                "#Pieces",
                PieceFixtureLine(normal.PrefabName, 12f, null),
                PieceFixtureLine(normal.PrefabName, 12f, null),           // the same spot as the one above
                PieceFixtureLine(seasonal.PrefabName, 14f, null),
                PieceFixtureLine(locked.PrefabName, 16f, null),
                PieceFixtureLine("vt_nosuchpiece", 18f, null),
                PieceFixtureLine("vt_nosuchpiece", 20f, null),
                PieceFixtureLine(normal.PrefabName, 22f, "info;1;2;1"),   // a scale other than 1
            };

            if (hoe != null)
            {
                lines.Add(PieceFixtureLine(hoe, 24f, null));
            }

            lines.Add("");
            var folder = Path.Combine(OutDir, "panels-test");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "problems.blueprint");
            File.WriteAllText(path, string.Join("\n", lines.ToArray()));

            if (!DocumentStore.Open(path, out var document, out var error))
            {
                Check(false, "the test blueprint opens: " + error);
                return null;
            }

            Check(document.Pieces.Count == lines.Count - 5,
                $"the test blueprint holds {document.Pieces.Count} pieces");
            return document;
        }

        private static string PieceFixtureLine(string prefab, float x, string rest)
        {
            var line = $"{prefab};;{BlueprintFormat.FormatNumber(x, 4)};0;12;0;0;0;1";
            return rest == null ? line : line + ";" + rest;
        }

        /// <summary>The whole problem list, row by row, in the order the panel shows it.</summary>
        private static List<Check> CheckProblemList(
            BlueprintDocument document, PieceEntry normal, PieceEntry seasonal, PieceEntry locked, string hoe)
        {
            // Cost items plus station types, counted here and not by the card, so the card's own
            // overflow row is checked against something independent.
            var tokens = new HashSet<string>();
            var stations = new HashSet<string>();
            foreach (var entry in new[] { normal, seasonal, locked })
            {
                foreach (var cost in entry.Cost)
                {
                    if (cost.Amount > 0)
                    {
                        tokens.Add(cost.Token);
                    }
                }

                if (!string.IsNullOrEmpty(entry.StationToken))
                {
                    stations.Add(entry.StationToken);
                }
            }

            var slots = BlueprintCard.SlotCount();
            var needed = tokens.Count + stations.Count;

            var want = new List<string>
            {
                "Error|Unknown piece vt_nosuchpiece (2x). The game skips the whole blueprint.",
            };
            if (hoe != null)
            {
                want.Add($"Error|{hoe} is a {PieceCatalog.OtherTool(hoe).ToLowerInvariant()} piece (1 piece). "
                    + "The blueprint is never offered.");
            }

            want.Add($"Warning|{seasonal.DisplayName} is seasonal. The blueprint is offered only in its season.");
            want.Add($"Warning|{locked.DisplayName} is not unlocked yet. "
                + "The blueprint is not offered in build mode.");
            want.Add("Warning|1 piece with a scale other than 1. The mod builds them at normal size.");
            want.Add("Warning|2 pieces sit in the same spot as another piece of the same kind.");
            if (needed > slots)
            {
                want.Add($"Warning|{needed} cost items and stations. The card shows only {slots}.");
            }

            want.Add("Warning|The icon piece vt_missingicon is not in the blueprint. "
                + "The game shows the first piece's icon.");
            want.Add("Note|The origin is *");

            var got = Checks.Run(document);
            Check(got.Count == want.Count, $"the list has {want.Count} rows (is {got.Count})");
            for (var i = 0; i < Mathf.Max(got.Count, want.Count); i++)
            {
                var line = i < got.Count ? got[i].LevelWord + "|" + got[i].Message : "(missing)";
                var wanted = i < want.Count ? want[i] : "(nothing)";
                var ok = wanted.EndsWith("*")
                    ? line.StartsWith(wanted.Substring(0, wanted.Length - 1), StringComparison.Ordinal)
                    : line == wanted;
                Check(ok, $"row {i + 1}: {line}" + (ok ? "" : $"  |  wanted {wanted}"));
            }

            Check(Checks.Summary(got).StartsWith(hoe != null ? "2 errors" : "1 error"),
                $"the summary reads '{Checks.Summary(got)}'");
            return got;
        }

        /// <summary>The rows the fixture cannot show: a line that cannot be read, empty, too big, a full card.</summary>
        private static void CheckOtherProblems(PieceEntry normal)
        {
            var empty = DocumentStore.New("Empty");
            var rows = Checks.Run(empty);
            Check(rows.Count == 1 && rows[0].Level == CheckLevel.Error
                && rows[0].Message == "No pieces. The game skips an empty blueprint.",
                $"an empty blueprint has one row: {Row(rows, 0)}");

            rows = Checks.Run(empty, "line 7: expected at least 9 fields, got 3");
            Check(rows.Count == 2 && rows[0].Message
                == "Line 7 cannot be read (expected at least 9 fields, got 3). The game skips this file.",
                $"a line the reader rejects is a row: {Row(rows, 0)}");

            var big = DocumentStore.New("Too many");
            var many = new List<NewPiece>();
            for (var i = 0; i <= BlueprintFormat.MaxPieces; i++)
            {
                many.Add(new NewPiece
                {
                    PrefabName = normal.PrefabName,
                    Position = new Vector3(i % 50, i / 50, 0f),
                    Rotation = Quaternion.identity,
                });
            }

            big.AddPieces(many);
            rows = Checks.Run(big);
            Check(rows.Any(c => c.Level == CheckLevel.Error
                    && c.Message == $"{many.Count} pieces. The mod reads at most {BlueprintFormat.MaxPieces}."),
                $"{many.Count} pieces is one too many: {Row(rows, 0)}");

            // A blueprint with more materials and stations than the card has squares.
            var slots = BlueprintCard.SlotCount();
            var full = DocumentStore.New("Full card");
            var tokens = new HashSet<string>();
            var stations = new HashSet<string>();
            var x = 0f;
            foreach (var entry in PieceCatalog.All)
            {
                if (tokens.Count + stations.Count > slots)
                {
                    break;
                }

                var before = tokens.Count + stations.Count;
                foreach (var cost in entry.Cost)
                {
                    if (cost.Amount > 0)
                    {
                        tokens.Add(cost.Token);
                    }
                }

                if (!string.IsNullOrEmpty(entry.StationToken))
                {
                    stations.Add(entry.StationToken);
                }

                if (tokens.Count + stations.Count > before)
                {
                    full.AddPiece(entry.PrefabName, new Vector3(x += 4f, 0f, 0f), Quaternion.identity);
                }
            }

            var needed = tokens.Count + stations.Count;
            var wanted = $"{needed} cost items and stations. The card shows only {slots}.";
            rows = Checks.Run(full);
            Check(needed > slots && rows.Any(c => c.Message == wanted),
                $"{needed} cost items and stations do not fit on {slots} squares: '{wanted}'");

            var card = BlueprintCard.Build(full);
            Check(card.Slots.Count == slots && card.Hidden.Count == needed - slots,
                $"the card keeps {card.Slots.Count} and hides {card.Hidden.Count}");
            Check(card.Slots.Count < 2 || card.Slots[0].Amount >= card.Slots[1].Amount,
                "the card's cost slots go from the most needed down");
        }

        private static string Row(List<Check> rows, int index)
        {
            return index < rows.Count ? rows[index].LevelWord + " " + rows[index].Message : "(no rows)";
        }

        /// <summary>The three panels in the real window, driven the way a player would.</summary>
        private static IEnumerator PanelsInTheWindow(
            BlueprintDocument document, List<Check> checks, PieceEntry normal)
        {
            EditorSession.OpenDocument(document);
            yield return null;
            yield return null;
            yield return null;
            Check(ModUi.Open && EditorSession.Document == document, "the editor opened on the test blueprint");
            Check(!document.Dirty, "a freshly opened blueprint is not dirty");

            // ---- the problem list ----
            Check(ChecksPanel.RowCount == checks.Count,
                $"the panel draws a row per problem: {ChecksPanel.RowCount} of {checks.Count}");
            Check(ChecksPanel.HeadingText == "Checks   " + Checks.Summary(checks),
                $"the heading counts them: '{ChecksPanel.HeadingText}'");
            Check(ChecksPanel.RowText(0).EndsWith(checks[0].Message),
                $"the first row reads '{ChecksPanel.RowText(0)}'");

            var unknown = checks.FindIndex(c => c.Message.StartsWith("Unknown piece"));
            var wantIds = checks[unknown].Pieces;
            ChecksPanel.Click(unknown);
            yield return null;
            yield return null;
            Check(wantIds.Length == 2 && EditorState.SelectionCount == 2
                && wantIds.All(EditorState.IsSelected),
                $"clicking the unknown-piece row selected its {wantIds.Length} pieces "
                + $"(selection is {EditorState.SelectionCount})");
            Check(SelectionPanel.Mode == 2 && SelectionPanel.CountText == "2 pieces selected."
                && SelectionPanel.KindsText == "2x vt_nosuchpiece",
                $"the selection panel tallies them: '{SelectionPanel.CountText}' '{SelectionPanel.KindsText}'");

            // ---- the name, with the card watching ----
            var undoBefore = document.UndoDepth;
            var card = BlueprintCard.Build(document);
            Check(BlueprintPanel.CardText.StartsWith("A blueprint with problems\n")
                && BlueprintPanel.CardText.Contains($"{document.Pieces.Count} pieces. Wheel: rotate."),
                $"the card text reads '{BlueprintPanel.CardText.Replace("\n", " / ")}'");
            Check(BlueprintPanel.FilledSlots == card.Slots.Count,
                $"the card fills {BlueprintPanel.FilledSlots} of its {card.TotalSlots} squares");

            BlueprintPanel.NameField.text = "Panel tes";
            BlueprintPanel.NameField.text = "Panel test 2";
            yield return null;
            yield return null;
            Check(document.Dirty && document.Name == "Panel test 2",
                $"typing a name changed the blueprint: '{document.Name}', dirty={document.Dirty}");
            Check(document.UndoDepth == undoBefore + 1,
                $"the keystrokes are one undo step: {document.UndoDepth} of {undoBefore + 1}");
            Check(BlueprintPanel.CardName == "Panel test 2",
                $"the build card followed: '{BlueprintPanel.CardName}'");

            // ---- the icon chooser ----
            var kinds = document.Pieces.Select(p => p.PrefabName).Distinct().Count();
            Check(BlueprintPanel.ChoiceCount == kinds + 1,
                $"First plus one button per kind: {BlueprintPanel.ChoiceCount} for {kinds} kinds");
            Check(BlueprintPanel.IconWarningText.Length > 0,
                $"the missing icon piece is called out: '{BlueprintPanel.IconWarningText}'");
            BlueprintPanel.Choose(1);
            yield return null;
            yield return null;
            Check(BlueprintPanel.ChosenIcon == normal.PrefabName && BlueprintPanel.IconWarningText.Length == 0,
                $"picking the first kind set #Icon:{BlueprintPanel.ChosenIcon}");
            Check(ChecksPanel.RowCount == checks.Count - 1,
                $"and the icon problem left the list: {ChecksPanel.RowCount} of {checks.Count - 1}");

            // ---- the selection fields ----
            var scaled = document.Pieces.Last(p => p.PrefabName == normal.PrefabName);
            EditorState.Select(scaled.Id);
            yield return null;
            yield return null;
            Check(SelectionPanel.Mode == 1 && SelectionPanel.NameText == normal.DisplayName,
                $"one piece selected: '{SelectionPanel.NameText}'");
            Check(SelectionPanel.XField.text == "22" && SelectionPanel.ZField.text == "12"
                && SelectionPanel.YawField.text == "0",
                $"its place reads x={SelectionPanel.XField.text} z={SelectionPanel.ZField.text} "
                + $"yaw={SelectionPanel.YawField.text}");
            Check(SelectionPanel.ScaleText.StartsWith("Scale 1 x 2 x 1"),
                $"the scale warning reads '{SelectionPanel.ScaleText}'");
            Check(SelectionPanel.KeptText.Contains("Extra fields: info;1;2;1"),
                $"the kept fields are shown: '{SelectionPanel.KeptText.Replace("\n", " / ")}'");

            SelectionPanel.XField.text = "25.5";
            SelectionPanel.XField.onEndEdit.Invoke(SelectionPanel.XField.text);
            yield return null;
            yield return null;
            var moved = document.Find(scaled.Id);
            Check(Mathf.Abs(moved.Position.x - 25.5f) < 1e-4f && SelectionPanel.XField.text == "25.5",
                $"the x box moved the piece to {V4(moved.Position)}");

            SelectionPanel.XField.text = "nonsense";
            SelectionPanel.XField.onEndEdit.Invoke(SelectionPanel.XField.text);
            yield return null;
            Check(SelectionPanel.XField.text == "25.5"
                && Mathf.Abs(document.Find(scaled.Id).Position.x - 25.5f) < 1e-4f,
                $"a number it cannot read is put back: '{SelectionPanel.XField.text}'");

            // Esc: TMP restores the old text itself and marks the edit cancelled, so nothing is applied.
            AccessTools.Field(typeof(TMPro.TMP_InputField), "m_WasCanceled")
                .SetValue(SelectionPanel.XField, true);
            SelectionPanel.XField.onEndEdit.Invoke("99");
            yield return null;
            Check(SelectionPanel.XField.text == "25.5"
                && Mathf.Abs(document.Find(scaled.Id).Position.x - 25.5f) < 1e-4f,
                $"Esc reverts instead of applying: '{SelectionPanel.XField.text}'");
            AccessTools.Field(typeof(TMPro.TMP_InputField), "m_WasCanceled")
                .SetValue(SelectionPanel.XField, false);

            SelectionPanel.YawField.text = "45";
            SelectionPanel.YawField.onEndEdit.Invoke(SelectionPanel.YawField.text);
            yield return null;
            yield return null;
            Check(Mathf.Abs(Mathf.DeltaAngle(SelectionPanel.YawOf(document.Find(scaled.Id).Rotation), 45f)) < 0.01f,
                $"the yaw box turned it to {SelectionPanel.YawOf(document.Find(scaled.Id).Rotation):0.##} degrees");

            ViewportHost.Frame();
            yield return null;
            yield return Screenshot("editor-panels-1-right-panel");

            // Esc steps back one thing at a time, so the selection has to go before the window.
            EditorState.Select(Array.Empty<int>());
            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open, "Esc closed the editor");
        }

        private static string Named(PieceEntry entry)
        {
            return entry != null ? entry.PrefabName : "none";
        }

        // ---------- scenario: editor_keys ----------

        /// <summary>
        /// Every key, the wheel and the mouse, driven straight through the binding dispatcher
        /// (<see cref="Bindings.Press"/>), plus the top bar and the dialogs. One case per row of
        /// the help table, and the two rules that are easy to get wrong: a key while a text box
        /// has the keyboard does nothing, and Ctrl+S saves instead of flying down.
        /// </summary>
        private static IEnumerator TestEditorKeys(Player player)
        {
            yield return new WaitForSeconds(1f);

            PieceCatalog.Ensure();
            var wall = PieceCatalog.Find("woodwall");
            Check(PieceCatalog.Ready && wall != null, "the catalog is built and has woodwall");
            if (wall == null)
            {
                yield break;
            }

            // Its own blueprint folder: the player's files are never touched.
            var folder = Path.Combine(OutDir, "keys-test");
            var wasFolder = BlueprintLibrary.UserFolder;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            Directory.CreateDirectory(folder);
            BlueprintLibrary.UserFolder = folder;

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open, "the key opened the editor");

            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            var document = EditorSession.Document;
            Check(ViewportHost.Ready && document != null && document.Pieces.Count >= 3,
                $"a kit stands in the pane after {waited:0.00} s: "
                + $"{(document != null ? document.Pieces.Count : 0)} pieces");
            if (document == null || document.Pieces.Count < 3)
            {
                BlueprintLibrary.UserFolder = wasFolder;
                yield break;
            }

            ViewportHost.SetMode(CameraMode.Orbit);
            yield return CameraKeys();
            yield return EditKeys(document, wall);
            yield return SaveKeys(document);
            yield return MouseAndBars(document, wall);
            yield return KeysWhileTyping(document);
            yield return DialogKeys(document);

            BlueprintLibrary.UserFolder = wasFolder;
            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            Check(!ModUi.Open, "the editor closed");
        }

        /// <summary>B, W A S D, Space, Ctrl, Shift and F.</summary>
        private static IEnumerator CameraKeys()
        {
            var camera = ViewportHost.Camera;

            var mode = camera.Mode;
            Bindings.Press(KeyCode.B, KeyMods.None);
            var flipped = camera.Mode;
            Bindings.Press(KeyCode.B, KeyMods.None);
            Check(flipped != mode && camera.Mode == mode,
                $"B switches the camera: {mode} -> {flipped} -> {camera.Mode}");

            var forward = Travel(KeyCode.W, KeyMods.None, 0.2f);
            Check(forward.magnitude > 0.5f && Vector3.Dot(forward.normalized, camera.Forward) > 0.8f,
                $"W flies where the camera looks: {V4(forward)}");
            var back = Travel(KeyCode.S, KeyMods.None, 0.2f);
            Check(Vector3.Dot(back.normalized, camera.Forward) < -0.8f, $"S flies backwards: {V4(back)}");
            var right = Travel(KeyCode.D, KeyMods.None, 0.2f);
            Check(Vector3.Dot(right.normalized, camera.Right) > 0.9f, $"D flies right: {V4(right)}");
            var left = Travel(KeyCode.A, KeyMods.None, 0.2f);
            Check(Vector3.Dot(left.normalized, camera.Right) < -0.9f, $"A flies left: {V4(left)}");

            var up = Travel(KeyCode.Space, KeyMods.None, 0.2f);
            Check(up.y > 0.5f && Mathf.Abs(up.x) < 1e-3f, $"Space flies straight up: {V4(up)}");
            var down = Travel(KeyCode.LeftControl, KeyMods.Ctrl, 0.2f);
            Check(down.y < -0.5f && Mathf.Abs(down.x) < 1e-3f, $"Ctrl flies straight down: {V4(down)}");

            var slow = Travel(KeyCode.D, KeyMods.None, 0.1f).magnitude;
            var fast = Travel(KeyCode.D, KeyMods.Shift, 0.1f).magnitude;
            Check(Mathf.Abs((fast / Mathf.Max(slow, 1e-4f)) - 3f) < 0.05f,
                $"Shift flies 3 times faster: {fast:0.###} m against {slow:0.###} m");

            Bindings.SetMods(KeyMods.Shift);
            var noSnap = !EditorState.Snapping;
            Bindings.SetMods(KeyMods.None);
            Check(noSnap && EditorState.Snapping, "Shift held turns snapping off, letting go turns it back on");

            yield return null;
        }

        /// <summary>How far one key press of a fly key moves the camera in one step.</summary>
        private static Vector3 Travel(KeyCode key, KeyMods mods, float dt)
        {
            Bindings.Reset();
            var from = ViewportHost.Camera.Position;
            Bindings.Press(key, mods);
            Bindings.Fly(dt);
            Bindings.Reset();
            return ViewportHost.Camera.Position - from;
        }

        /// <summary>The editing keys: undo, redo, select all, duplicate, move, turn, nudge, delete.</summary>
        private static IEnumerator EditKeys(BlueprintDocument document, PieceEntry wall)
        {
            var count = document.Pieces.Count;
            var first = document.Pieces[0].Id;

            EditorState.Select(first);
            Bindings.Press(KeyCode.Delete, KeyMods.None);
            Check(document.Pieces.Count == count - 1, $"Delete removed the selection: {document.Pieces.Count}");
            Bindings.Press(KeyCode.Z, KeyMods.Ctrl);
            Check(document.Pieces.Count == count, $"Ctrl+Z put it back: {document.Pieces.Count}");
            Bindings.Press(KeyCode.Y, KeyMods.Ctrl);
            Check(document.Pieces.Count == count - 1, $"Ctrl+Y took it out again: {document.Pieces.Count}");
            Bindings.Press(KeyCode.Z, KeyMods.Cmd);
            Bindings.Press(KeyCode.Z, KeyMods.Cmd | KeyMods.Shift);
            Check(document.Pieces.Count == count - 1, $"Shift+Cmd+Z redoes as well: {document.Pieces.Count}");
            Bindings.Press(KeyCode.Z, KeyMods.Ctrl);
            Check(document.Pieces.Count == count, "and one more undo brings the piece back");

            EditorState.Select(document.Pieces[0].Id);
            Bindings.Press(KeyCode.Backspace, KeyMods.None);
            Check(document.Pieces.Count == count - 1, "Backspace deletes too");
            Bindings.Press(KeyCode.Z, KeyMods.Ctrl);

            Bindings.Press(KeyCode.A, KeyMods.Cmd);
            Check(EditorState.SelectionCount == count, $"Cmd+A selected all {EditorState.SelectionCount}");

            Bindings.Press(KeyCode.D, KeyMods.Cmd);
            Check(EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Duplicate
                && EditorState.Moving != null && EditorState.Moving.Count == count,
                $"Cmd+D put {count} copies in hand");
            EditorState.CancelMode();

            var id = document.Pieces[0].Id;
            EditorState.Select(id);
            Bindings.Press(KeyCode.G, KeyMods.None);
            Check(EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Move,
                "G took the selection in hand");
            EditorState.CancelMode();

            var turnable = document.Pieces.FirstOrDefault(
                p => PieceCatalog.Find(p.PrefabName) != null && PieceCatalog.Find(p.PrefabName).CanRotate);
            Check(turnable != null, "the kit has a piece that can turn");
            if (turnable != null)
            {
                EditorState.Select(turnable.Id);
                var yaw = YawOf(document.Find(turnable.Id).Rotation);
                Bindings.Press(KeyCode.R, KeyMods.None);
                var turned = YawOf(document.Find(turnable.Id).Rotation);
                Bindings.Press(KeyCode.R, KeyMods.Shift);
                var back = YawOf(document.Find(turnable.Id).Rotation);
                Check(Mathf.Abs(Mathf.DeltaAngle(turned, yaw + Placer.RotateStep)) < 0.01f
                    && Mathf.Abs(Mathf.DeltaAngle(back, yaw)) < 0.01f,
                    $"R turns {Placer.RotateStep} degrees and Shift+R turns back: {yaw:0.#} -> {turned:0.#} -> {back:0.#}");
            }

            // The arrows walk the ground axis the camera is closest to, so only the step is fixed.
            EditorState.Select(id);
            var was = document.Find(id).Position;
            Bindings.Press(KeyCode.UpArrow, KeyMods.None);
            var step = document.Find(id).Position - was;
            Check(Mathf.Abs(step.magnitude - Bindings.NudgeStep) < 1e-4f && Mathf.Abs(step.y) < 1e-6f
                && (Mathf.Abs(step.x) < 1e-6f || Mathf.Abs(step.z) < 1e-6f),
                $"an arrow key nudges {Bindings.NudgeStep} m along one ground axis: {V4(step)}");

            was = document.Find(id).Position;
            Bindings.Press(KeyCode.DownArrow, KeyMods.Alt);
            var fine = document.Find(id).Position - was;
            Check(Mathf.Abs(fine.magnitude - Bindings.NudgeFine) < 1e-4f && Vector3.Dot(fine, step) < 0f,
                $"with Alt it is {Bindings.NudgeFine} m, and Down goes the other way: {V4(fine)}");

            was = document.Find(id).Position;
            Bindings.Press(KeyCode.PageUp, KeyMods.None);
            Bindings.Press(KeyCode.PageDown, KeyMods.Alt);
            var lifted = document.Find(id).Position - was;
            Check(Mathf.Abs(lifted.y - (Bindings.NudgeStep - Bindings.NudgeFine)) < 1e-4f
                && Mathf.Abs(lifted.x) < 1e-6f,
                $"PageUp and PageDown move straight up and down: {V4(lifted)}");

            // Q and E walk the snap point, but only while something is in hand.
            Check(!Bindings.Press(KeyCode.E, KeyMods.None), "E does nothing while the hand is empty");
            EditorState.StartAdd(wall);
            var manual = EditorState.Manual;
            Bindings.Press(KeyCode.E, KeyMods.None);
            var next = EditorState.Manual;
            Bindings.Press(KeyCode.Q, KeyMods.None);
            Check(manual == -1 && next == 0 && EditorState.Manual == -1,
                $"E and Q walk the snap point: {manual} -> {next} -> {EditorState.Manual}");

            var steps = EditorState.Steps;
            Bindings.Wheel(1f, new Vector2(0.5f, 0.5f));
            Check(EditorState.Steps == steps + 1,
                $"one wheel notch turns the piece one step of {Placer.RotateStep} degrees");
            EditorState.CancelMode();

            var distance = ViewportHost.Camera.Distance;
            Bindings.Wheel(1f, new Vector2(0.5f, 0.5f));
            var closer = ViewportHost.Camera.Distance;
            Bindings.Wheel(-1f, new Vector2(0.5f, 0.5f));
            Check(closer < distance && ViewportHost.Camera.Distance > closer,
                $"with an empty hand the wheel zooms: {distance:0.##} -> {closer:0.##} "
                + $"-> {ViewportHost.Camera.Distance:0.##}");

            // F looks at the selection, and at everything when there is none.
            EditorState.Select(id);
            Bindings.Press(KeyCode.F, KeyMods.None);
            var box = EditorState.BoxOf(document.Find(id));
            Check(Vector3.Distance(ViewportHost.Camera.Pivot, box.center) < 0.5f,
                $"F looks at the selected piece: pivot {V4(ViewportHost.Camera.Pivot)}, piece {V4(box.center)}");
            EditorState.Select(Array.Empty<int>());
            Bindings.Press(KeyCode.F, KeyMods.None);
            var all = EditorState.BoxOf(new List<DocPiece>(document.Pieces));
            Check(all.HasValue && Vector3.Distance(ViewportHost.Camera.Pivot, all.Value.center) < 0.5f,
                "and at the whole blueprint when nothing is selected");

            // Esc walks back one step at a time.
            EditorState.StartAdd(wall);
            Check(Bindings.Cancel() && EditorState.Mode == EditMode.Idle, "Esc empties the hand first");
            EditorState.Select(id);
            Check(Bindings.Cancel() && EditorState.SelectionCount == 0, "the next Esc clears the selection");
            Check(!Bindings.Cancel(), "and the one after that has nothing left, so the window would close");

            yield return null;
        }

        /// <summary>Ctrl+S, and the file name the top bar shows.</summary>
        private static IEnumerator SaveKeys(BlueprintDocument document)
        {
            Check(EditorCommands.SaveAs("Keys test", true), "Save as wrote the blueprint");
            Check(!document.Dirty && !string.IsNullOrEmpty(document.SourcePath),
                $"it now has a file: {Path.GetFileName(document.SourcePath ?? "none")}");

            EditorState.Select(document.Pieces[0].Id);
            EditorState.Nudge(new Vector3(0f, 0f, 0.5f));
            Check(document.Dirty, "an edit made it dirty again");

            // Ctrl is the fly-down key, so this is the case that proves shortcuts win.
            Bindings.Reset();
            var was = ViewportHost.Camera.Position;
            Bindings.Press(KeyCode.S, KeyMods.Ctrl);
            Bindings.Fly(0.5f);
            Check(!document.Dirty, "Ctrl+S saved the blueprint");
            Check(Vector3.Distance(ViewportHost.Camera.Position, was) < 1e-4f,
                "and did not fly the camera down as a plain S would");

            TopBar.Tick();
            Check(TopBar.FileText.Contains("Keys test") && TopBar.FileText.Contains("keys-test.blueprint"),
                $"the top bar names the file: '{Strip(TopBar.FileText)}'");

            yield return null;
        }

        /// <summary>The mouse rows, the two view switches and the Save button's error count.</summary>
        private static IEnumerator MouseAndBars(BlueprintDocument document, PieceEntry wall)
        {
            var camera = ViewportHost.Camera;
            var yaw = camera.Yaw;
            camera.Drag(new Vector2(60f, 0f), 800f);
            Check(Mathf.Abs(Mathf.DeltaAngle(camera.Yaw, yaw)) > 1f,
                $"a right drag turns the camera: {yaw:0.#} -> {camera.Yaw:0.#} degrees");

            var at = camera.Position;
            camera.Pan(new Vector2(60f, 0f), 10f, 800f);
            Check(Vector3.Distance(camera.Position, at) > 0.1f,
                $"a middle drag pans it: {V4(camera.Position - at)}");

            ViewportHost.Frame();
            yield return null;

            // A click on the picture picks what is under it; the same click with Shift lets it go.
            EditorState.Select(Array.Empty<int>());
            var centre = ViewportHost.Scene.Root.TransformPoint(EditorState.BoxOf(document.Pieces[0]).center);
            if (ViewportHost.Raycast.Project(centre, out var screen))
            {
                ViewportHost.ClickAt(screen, false);
                var picked = EditorState.SelectionCount;
                ViewportHost.ClickAt(screen, true);
                Check(picked == 1 && EditorState.SelectionCount == 0,
                    $"a click selects one piece and Shift+click lets it go: {picked} -> {EditorState.SelectionCount}");
            }
            else
            {
                Check(false, "the first piece does not show on screen");
            }

            ViewportHost.BoxSelect(Vector2.zero, new Vector2(Screen.width, Screen.height), false);
            Check(EditorState.SelectionCount > 1,
                $"a drag over the whole pane box-selects {EditorState.SelectionCount} pieces");
            EditorState.Select(Array.Empty<int>());

            // Boxes: the models stop drawing and a wire box stands in for each piece.
            EditorCommands.ToggleBoxes();
            yield return null;
            yield return null;
            Check(EditorState.PieceBoxesOn && ViewportHost.Boxes.AllCount == document.Pieces.Count,
                $"Boxes draws one box per piece: {ViewportHost.Boxes.AllCount} of {document.Pieces.Count}");
            EditorCommands.ToggleBoxes();
            yield return null;
            yield return null;
            Check(!EditorState.PieceBoxesOn && ViewportHost.Boxes.AllCount == 0, "and switches back to the models");

            // Snap dots: only the dots go, snapping itself stays on.
            EditorState.StartAdd(wall);
            ViewportHost.SetMode(CameraMode.Free);
            var tries = 0f;
            while (EditorState.Aimed == null && tries < 2f)
            {
                tries += Time.deltaTime;
                yield return null;
            }

            var drawn = ViewportHost.Dots.Drawn;
            EditorCommands.ToggleSnapDots();
            yield return null;
            yield return null;
            Check(drawn > 0 && !EditorState.SnapDotsOn && ViewportHost.Dots.Drawn == 0,
                $"Snap dots switches them off: {drawn} -> {ViewportHost.Dots.Drawn}");
            EditorCommands.ToggleSnapDots();
            EditorState.CancelMode();
            ViewportHost.SetMode(CameraMode.Orbit);
            yield return null;

            // Save says what is wrong and still saves: an empty blueprint is one error.
            EditorState.SelectAll();
            EditorState.DeleteSelection();
            ChecksPanel.Refresh();
            TopBar.Tick();
            Check(TopBar.SaveText == "Save (1 error)" && TopBar.SaveIsRed,
                $"with an error the Save button reads '{TopBar.SaveText}' in red");
            EditorState.Undo();
            ChecksPanel.Refresh();
            TopBar.Tick();
            Check(TopBar.SaveText == "Save" && !TopBar.SaveIsRed,
                $"and goes back to '{TopBar.SaveText}' once the error is gone");

            // Move one piece first, or the kit's origin is already where centring would put it.
            EditorState.Select(document.Pieces[0].Id);
            EditorState.Nudge(new Vector3(3f, 0f, 0f));
            var pieces = document.Pieces.Count;
            var wasAt = document.Pieces[1].Position;
            EditorCommands.CenterOrigin();
            Check(document.Pieces.Count == pieces && Vector3.Distance(document.Pieces[1].Position, wasAt) > 1e-3f,
                $"Center origin moved the whole blueprint: {V4(wasAt)} -> {V4(document.Pieces[1].Position)}");
            EditorState.Undo();
            EditorState.Undo();
        }

        /// <summary>A key while a text box has the keyboard is not the editor's.</summary>
        private static IEnumerator KeysWhileTyping(BlueprintDocument document)
        {
            var field = BlueprintPanel.NameField;
            Check(field != null, "the blueprint panel has a name box");
            if (field == null)
            {
                yield break;
            }

            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
            var waited = 0f;
            while (!ModUi.Typing && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(ModUi.Typing, $"the name box has the keyboard after {waited:0.00} s");
            var count = document.Pieces.Count;
            EditorState.Select(document.Pieces[0].Id);
            var used = Bindings.Press(KeyCode.Delete, KeyMods.None)
                || Bindings.Press(KeyCode.G, KeyMods.None)
                || Bindings.Press(KeyCode.S, KeyMods.Ctrl);
            Check(!used && document.Pieces.Count == count && EditorState.Mode == EditMode.Idle,
                "Delete, G and Ctrl+S all do nothing while a text box has the keyboard");

            field.DeactivateInputField();
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
            yield return null;
            yield return null;
            Check(!ModUi.Typing, "the keys are the editor's again once the box lets go");
            EditorState.Select(Array.Empty<int>());
        }

        /// <summary>The open, save as, help and unsaved-changes dialogs, and the toasts.</summary>
        private static IEnumerator DialogKeys(BlueprintDocument document)
        {
            // ---- help, and the screenshot ----
            Check(Bindings.Press(KeyCode.H, KeyMods.None) && Dialogs.Kind == "help",
                $"H opens the help: '{Dialogs.TitleText}'");
            Check(!Bindings.Press(KeyCode.Delete, KeyMods.None), "and the editing keys are silent while it is up");
            yield return null;
            yield return null;
            yield return Screenshot("editor-keys-1-help");
            Check(Bindings.Cancel() && !Dialogs.IsOpen, "Esc closes the help");
            Check(Bindings.Press(KeyCode.Slash, KeyMods.Shift) && Dialogs.Kind == "help", "? opens it too");
            Dialogs.Close();

            // ---- open ----
            EditorCommands.OpenDialog();
            yield return null;
            Check(Dialogs.Kind == "open" && Dialogs.RowCount > 1,
                $"Open lists {Dialogs.RowCount} blueprints");
            var kits = 0;
            var mine = -1;
            for (var i = 0; i < Dialogs.RowCount; i++)
            {
                if (Dialogs.Row(i).IsKit)
                {
                    kits++;
                }
                else if (mine < 0)
                {
                    mine = i;
                }
            }

            Check(kits > 0 && mine >= 0, $"both lists are there: {kits} kits and the file we saved");
            Check(mine < 0 || (Dialogs.RowText(mine).Contains("keys-test.blueprint")
                && Dialogs.RowText(mine).Contains(" B")
                && Dialogs.RowText(mine).Contains(DateTime.Now.Year.ToString())),
                $"a row carries path, size and date: '{(mine >= 0 ? Dialogs.RowText(mine) : "none")}'");
            Dialogs.Close();

            // ---- save as ----
            Dialogs.SaveAs("Keys test");
            yield return null;
            Check(Dialogs.Kind == "saveAs" && Dialogs.NameField != null, "Save as opened with a name box");
            Check(Dialogs.NoteText.Contains("exists"), $"a name that is taken warns: '{Dialogs.NoteText}'");
            Dialogs.NameField.text = "a..b";
            Check(Dialogs.NoteText.StartsWith("Use letters"), $"two dots are refused: '{Dialogs.NoteText}'");
            Dialogs.NameField.text = " leading space";
            Check(Dialogs.NoteText.StartsWith("Use letters"), "so is a name that does not start with a letter");
            Dialogs.NameField.text = "Another name";
            Check(Dialogs.NoteText.Length == 0, $"a free name says nothing: '{Dialogs.NoteText}'");
            Dialogs.NameField.DeactivateInputField();
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
            Dialogs.Close();
            yield return null;

            // ---- the unsaved-changes question ----
            EditorState.Select(document.Pieces[0].Id);
            EditorState.Nudge(new Vector3(0f, 0f, 0.5f));
            Check(document.Dirty, "the blueprint has changes again");
            EditorCommands.NewBlueprint();
            yield return null;
            Check(Dialogs.Kind == "confirm", $"New asks first: '{Dialogs.TitleText}'");
            Dialogs.Close();
            Check(EditorSession.Document == document, "cancelling kept the blueprint");

            EditorCommands.NewBlueprint();
            Dialogs.Submit();
            yield return null;
            yield return null;
            Check(!Dialogs.IsOpen && EditorSession.Document != document
                && EditorSession.Document.Pieces.Count == 0,
                "answering Discard started an empty blueprint");

            // ---- open a kit from the list ----
            EditorCommands.OpenDialog();
            yield return null;
            var kit = -1;
            for (var i = 0; i < Dialogs.RowCount && kit < 0; i++)
            {
                if (Dialogs.Row(i).IsKit && Dialogs.Row(i).Error == null && Dialogs.Row(i).Pieces > 0)
                {
                    kit = i;
                }
            }

            Dialogs.ClickRow(kit);
            yield return null;
            yield return null;
            Check(kit >= 0 && !Dialogs.IsOpen && EditorSession.Document.Pieces.Count > 0,
                $"clicking a kit opened it: {EditorSession.Document.Pieces.Count} pieces");

            // ---- toasts ----
            Toasts.Clear();
            Toasts.Info("a note");
            Toasts.Error("a problem");
            Check(Toasts.Count == 2 && Toasts.LevelOf(0) == ToastLevel.Error,
                $"two messages are up, newest first: {Toasts.Count}");
            yield return new WaitForSeconds(Toasts.Seconds + 0.6f);
            Toasts.Tick();
            Check(Toasts.Count == 1 && Toasts.LevelOf(0) == ToastLevel.Error,
                $"the note went after {Toasts.Seconds} s and the error is still up: {Toasts.Count}");
            Toasts.Clear();
        }

        /// <summary>Rich text out of a label, so a log line reads as what a person sees.</summary>
        private static string Strip(string text)
        {
            return System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");
        }

        // ---------- scenario: editor_pad ----------

        private static PadState _pad;

        /// <summary>
        /// The whole controller map, driven through a made-up pad (<see cref="PadReader.Fake"/>),
        /// so no device has to be plugged in: every row of the help table in the order
        /// <see cref="PadBindings"/> dispatches them, both repeats, the piece menu, and the mouse
        /// taking the aim back from the crosshair.
        /// </summary>
        private static IEnumerator TestEditorPad(Player player)
        {
            yield return new WaitForSeconds(1f);

            PieceCatalog.Ensure();
            var wall = PieceCatalog.Find("woodwall");
            Check(PieceCatalog.Ready && wall != null, "the catalog is built and has woodwall");
            if (wall == null)
            {
                yield break;
            }

            // Its own blueprint folder: the player's files are never touched.
            var folder = Path.Combine(OutDir, "pad-test");
            var wasFolder = BlueprintLibrary.UserFolder;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            Directory.CreateDirectory(folder);
            BlueprintLibrary.UserFolder = folder;

            // Every piece in the menu, so a tab is a full grid and a held direction has room to run.
            var wasShowAll = EditorConfig.ShowAllPieces.Value;
            EditorConfig.ShowAllPieces.Value = true;

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open, "the key opened the editor");

            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            var document = EditorSession.Document;
            Check(ViewportHost.Ready && document != null && document.Pieces.Count >= 3,
                $"a kit stands in the pane after {waited:0.00} s: "
                + $"{(document != null ? document.Pieces.Count : 0)} pieces");
            if (document != null && document.Pieces.Count >= 3)
            {
                ViewportHost.SetMode(CameraMode.Orbit);
                _pad = new PadState();
                PadReader.Fake = _pad;
                yield return null;
                yield return null;

                yield return PadWakes();
                yield return PadDialogs(document);
                yield return PadMenu();
                yield return PadCamera();
                yield return PadPlacing(document, wall);
                yield return PadAimed(document);
                yield return PadMouse();
            }

            PadReader.Fake = null;
            _pad = null;
            EditorConfig.ShowAllPieces.Value = wasShowAll;
            BlueprintLibrary.UserFolder = wasFolder;
            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            Check(!ModUi.Open, "the editor closed");
        }

        /// <summary>Holds buttons for one read, then lets them go: one press, like a tap.</summary>
        private static IEnumerator Tap(params PadButton[] buttons)
        {
            var added = new List<PadButton>();
            foreach (var button in buttons)
            {
                if (_pad.Down.Add(button))
                {
                    added.Add(button);
                }
            }

            yield return null;
            yield return null;
            foreach (var button in added)
            {
                _pad.Down.Remove(button);
            }

            yield return null;
        }

        /// <summary>The same, held down for a while, for the repeats.</summary>
        private static IEnumerator Hold(PadButton button, float seconds)
        {
            var added = _pad.Down.Add(button);
            yield return Wait(seconds);
            if (added)
            {
                _pad.Down.Remove(button);
            }

            yield return null;
        }

        /// <summary>Frames until so many real seconds have passed. The editor reads unscaled time.</summary>
        private static IEnumerator Wait(float seconds)
        {
            var until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until)
            {
                yield return null;
            }
        }

        /// <summary>Row 1: the pad woke, so the crosshair aims and the hints switch names.</summary>
        private static IEnumerator PadWakes()
        {
            Check(!ViewportHost.PadAim, "the mouse aims until the pad is touched");
            yield return Tap(PadButton.L2);
            Check(ViewportHost.PadAim, "the pad woke: the crosshair takes the aim");

            var ps = EditorInput.Glyphs;
            Check(ps.Of(PadButton.Cross) == "×" && ps.Of(PadButton.L1) == "L1" && ps.Of(PadButton.Options) == "Options",
                $"a PlayStation pad names its buttons {ps.Of(PadButton.Cross)} {ps.Of(PadButton.Circle)} "
                + $"{ps.Of(PadButton.Square)} {ps.Of(PadButton.Triangle)}");

            var rows = Bindings.Pad;
            Check(rows.Length == 18, $"the help table has the whole controller half: {rows.Length} rows");
            Check(Array.Exists(rows, r => r.Keys == "×") && Array.Exists(rows, r => r.Keys == "L2 + R1"),
                "and the rows are written in those names");

            _pad.Ps = false;
            yield return null;
            yield return null;
            var xbox = EditorInput.Glyphs;
            Check(xbox.Of(PadButton.Cross) == "A" && xbox.Of(PadButton.L1) == "LB" && xbox.Of(PadButton.Options) == "Menu",
                $"an Xbox pad switches them to {xbox.Of(PadButton.Cross)} {xbox.Of(PadButton.Circle)} "
                + $"{xbox.Of(PadButton.Square)} {xbox.Of(PadButton.Triangle)}");
            Check(Array.Exists(Bindings.Pad, r => r.Keys == "A") && Array.Exists(Bindings.Pad, r => r.Keys == "LT + RB"),
                "and the help table follows");

            _pad.Ps = true;
            yield return null;
            yield return null;
        }

        /// <summary>Rows 2 and 3: a dialog eats the pad, Options opens and closes the help.</summary>
        private static IEnumerator PadDialogs(BlueprintDocument document)
        {
            yield return Tap(PadButton.Options);
            Check(Dialogs.Kind == "help", $"Options opens the help: '{Dialogs.TitleText}'");

            EditorState.Select(document.Pieces[0].Id);
            yield return Tap(PadButton.Square);
            Check(EditorState.Mode == EditMode.Idle, "the other buttons do nothing while it is up");

            yield return Tap(PadButton.Options);
            Check(!Dialogs.IsOpen, "Options closes the help again");

            yield return Tap(PadButton.Options);
            yield return Tap(PadButton.Circle);
            Check(!Dialogs.IsOpen, "circle closes a dialog too");
            EditorState.Select(Array.Empty<int>());
        }

        /// <summary>Row 8 and the piece menu: tabs, the highlight, the repeat, place and close.</summary>
        private static IEnumerator PadMenu()
        {
            EditorState.CancelMode();
            EditorState.Select(Array.Empty<int>());

            yield return Tap(PadButton.Cross);
            Check(PiecePicker.IsOpen, "cross opens the piece menu");
            Check(PiecePicker.TabCount == PieceCatalog.Tags.Count && PiecePicker.TabName == "Building",
                $"one tab per usage tag, open on Building: {PiecePicker.TabCount} tabs, '{PiecePicker.TabName}'");
            Check(PiecePicker.Count > PiecePicker.Columns * 2 && PiecePicker.LiveTiles >= PiecePicker.Count,
                $"the tab holds {PiecePicker.Count} pieces in a grid {PiecePicker.Columns} icons wide");

            var tab = PiecePicker.Tab;
            yield return Tap(PadButton.R1);
            var next = PiecePicker.Tab;
            yield return Tap(PadButton.L1);
            Check(next == tab + 1 && PiecePicker.Tab == tab,
                $"R1 and L1 change the tab: {tab} -> {next} -> {PiecePicker.Tab}");

            PiecePicker.NextTab(-PiecePicker.Tab);
            yield return Tap(PadButton.L1);
            var wrapped = PiecePicker.Tab;
            yield return Tap(PadButton.R1);
            Check(wrapped == PiecePicker.TabCount - 1 && PiecePicker.Tab == 0,
                $"L1 on the first tab wraps to the last: {wrapped} of {PiecePicker.TabCount}");

            for (var i = 0; i < PiecePicker.TabCount && PiecePicker.TabName != "Building"; i++)
            {
                PiecePicker.NextTab(1);
            }

            PiecePicker.Move(-PiecePicker.Count, 0);
            yield return Tap(PadButton.Right);
            var right = PiecePicker.Index;
            yield return Tap(PadButton.Down);
            var down = PiecePicker.Index;
            yield return Tap(PadButton.Up);
            Check(right == 1 && down == 1 + PiecePicker.Columns && PiecePicker.Index == 1,
                $"the D-pad moves by one and by a row: 0 -> {right} -> {down} -> {PiecePicker.Index}");

            PiecePicker.Move(-PiecePicker.Count, 0);
            _pad.Ls = new Vector2(1f, 0f);
            yield return null;
            yield return null;
            _pad.Ls = Vector2.zero;
            var stick = PiecePicker.Index;
            yield return null;
            Check(stick == 1, $"the left stick moves it too: {stick}");

            PiecePicker.Move(-PiecePicker.Count, 0);
            yield return Hold(PadButton.Right, 0.15f);
            var once = PiecePicker.Index;
            PiecePicker.Move(-PiecePicker.Count, 0);
            yield return Hold(PadButton.Right, 0.75f);
            var many = PiecePicker.Index;
            Check(once == 1 && many >= 3 && many <= 8,
                $"a held direction goes once, then repeats after {PadBindings.NavDelay} s every "
                + $"{PadBindings.NavEvery} s: {once} step in 0.15 s, {many} in 0.75 s");

            PiecePicker.Move(-PiecePicker.Count, 0);
            PiecePicker.Move(2 * PiecePicker.Columns + 3, 0);
            yield return null;
            yield return null;
            yield return Screenshot("editor-pad-1-picker");

            var chosen = PiecePicker.Current;
            yield return Tap(PadButton.Cross);
            Check(!PiecePicker.IsOpen && EditorState.Mode == EditMode.Place && EditorState.Held == chosen,
                $"cross places the highlighted piece: {(chosen != null ? chosen.DisplayName : "none")}");

            yield return Tap(PadButton.Cross);
            Check(PiecePicker.IsOpen && PiecePicker.Current == chosen, "it opens again on the piece in hand");

            yield return Tap(PadButton.Circle);
            Check(!PiecePicker.IsOpen && EditorState.Mode == EditMode.Place,
                "circle closes the menu and leaves the piece in hand");
            yield return Tap(PadButton.Circle);
            Check(EditorState.Mode == EditMode.Idle, "the next circle empties the hand");
        }

        /// <summary>Rows 6 and 14: the sticks turn the camera and fly it, the D-pad lifts it.</summary>
        private static IEnumerator PadCamera()
        {
            var camera = ViewportHost.Camera;
            ViewportHost.SetMode(CameraMode.Orbit);

            var yaw = camera.Yaw;
            var from = Time.unscaledTime;
            _pad.Rs = new Vector2(1f, 0f);
            yield return Wait(0.3f);
            _pad.Rs = Vector2.zero;
            var speed = Mathf.Abs(Mathf.DeltaAngle(yaw, camera.Yaw)) / Mathf.Max(Time.unscaledTime - from, 1e-3f);
            yield return null;
            Check(speed > 110f && speed < 190f,
                $"the right stick turns the camera at about 150 degrees a second: {speed:0}");

            yield return FlySpeed(camera, false);
            var one = _flySpeed;
            yield return FlySpeed(camera, true);
            var boosted = _flySpeed;
            Check(one > 4.5f && one < 7.5f, $"the left stick flies at about 6 m/s: {one:0.0}");
            Check(boosted / Mathf.Max(one, 1e-3f) > 2.5f && boosted / Mathf.Max(one, 1e-3f) < 3.5f,
                $"L1 flies 3 times faster: {boosted:0.0} m/s against {one:0.0}");

            var height = camera.Position.y;
            yield return Hold(PadButton.Up, 0.2f);
            var up = camera.Position.y;
            yield return Hold(PadButton.Down, 0.2f);
            Check(up > height + 0.5f && camera.Position.y < up - 0.5f,
                $"the D-pad flies up and down: {height:0.0} -> {up:0.0} -> {camera.Position.y:0.0}");
        }

        private static float _flySpeed;

        /// <summary>How fast the left stick moves the camera sideways, measured over a real window.</summary>
        private static IEnumerator FlySpeed(EditorCamera camera, bool boost)
        {
            if (boost)
            {
                _pad.Down.Add(PadButton.L1);
            }

            var at = camera.Position;
            var start = Time.unscaledTime;
            _pad.Ls = new Vector2(1f, 0f);
            yield return Wait(0.25f);
            _pad.Ls = Vector2.zero;
            _flySpeed = Vector3.Distance(camera.Position, at) / Mathf.Max(Time.unscaledTime - start, 1e-3f);
            if (boost)
            {
                _pad.Down.Remove(PadButton.L1);
            }

            yield return null;
        }

        /// <summary>Rows 5, 6, 7, 12 and 13, with a piece in hand over open ground.</summary>
        private static IEnumerator PadPlacing(BlueprintDocument document, PieceEntry wall)
        {
            _pad.Down.Add(PadButton.L1);
            yield return null;
            yield return null;
            var off = !EditorState.Snapping;
            _pad.Down.Remove(PadButton.L1);
            yield return null;
            yield return null;
            Check(off && EditorState.Snapping, "L1 held turns snapping off, letting go turns it back on");

            // Open ground, well away from the blueprint, so a wall can land with nothing in the way.
            ViewportHost.SetMode(CameraMode.Orbit);
            ViewportHost.Camera.LookFrom(new Vector3(30f, 9f, 24f), new Vector3(30f, 0f, 30f));
            EditorState.StartAdd(wall);
            var waited = 0f;
            while (EditorState.Aimed == null && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(EditorState.Aimed != null, "the crosshair aims at open ground with a wall in hand");

            var steps = EditorState.Steps;
            _pad.Down.Add(PadButton.L2);
            _pad.Rs = new Vector2(-1f, 0f);
            yield return null;
            yield return null;
            var once = EditorState.Steps - steps;
            yield return Wait(0.6f);
            var many = EditorState.Steps - steps;
            _pad.Rs = Vector2.zero;
            _pad.Down.Remove(PadButton.L2);
            yield return null;
            Check(once == 1 && many >= 3 && many <= 9,
                $"L2 and the right stick turn {Placer.RotateStep} degrees, once then every "
                + $"{PadBindings.TurnEvery} s after {PadBindings.TurnDelay} s: {once} step, then {many} in 0.6 s");

            var count = document.Pieces.Count;
            yield return Tap(PadButton.R2);
            Check(document.Pieces.Count == count + 1,
                $"R2 drops the piece in hand: {count} -> {document.Pieces.Count} pieces");

            var manual = EditorState.Manual;
            yield return Tap(PadButton.R3);
            var next = EditorState.Manual;
            yield return Tap(PadButton.L3);
            Check(manual == -1 && next == 0 && EditorState.Manual == -1,
                $"R3 and L3 walk the snap point while placing: {manual} -> {next} -> {EditorState.Manual}");
            EditorState.CancelMode();

            var before = document.Pieces.Count;
            yield return Tap(PadButton.Left);
            var undone = document.Pieces.Count;
            yield return Tap(PadButton.Right);
            Check(undone == before - 1 && document.Pieces.Count == before,
                $"the D-pad left and right undo and redo: {before} -> {undone} -> {document.Pieces.Count}");
            EditorState.Undo();
            yield return null;

            var turnable = document.Pieces.FirstOrDefault(
                p => PieceCatalog.Find(p.PrefabName) != null && PieceCatalog.Find(p.PrefabName).CanRotate);
            Check(turnable != null, "the kit has a piece that can turn");
            if (turnable != null)
            {
                EditorState.Select(turnable.Id);
                var yaw = YawOf(document.Find(turnable.Id).Rotation);
                _pad.Down.Add(PadButton.L2);
                _pad.Rs = new Vector2(-1f, 0f);
                yield return null;
                yield return null;
                _pad.Rs = Vector2.zero;
                _pad.Down.Remove(PadButton.L2);
                yield return null;
                var turned = YawOf(document.Find(turnable.Id).Rotation);
                Check(Mathf.Abs(Mathf.DeltaAngle(turned, yaw + Placer.RotateStep)) < 0.01f,
                    $"with an empty hand the same turns the selection: {yaw:0.#} -> {turned:0.#} degrees");
                EditorState.Undo();
            }

            var mode = ViewportHost.Camera.Mode;
            yield return Tap(PadButton.L3);
            var flipped = ViewportHost.Camera.Mode;
            yield return Tap(PadButton.L3);
            Check(flipped != mode && ViewportHost.Camera.Mode == mode,
                $"with an empty hand L3 switches the camera: {mode} -> {flipped} -> {ViewportHost.Camera.Mode}");

            ViewportHost.SetMode(CameraMode.Orbit);
            EditorState.Select(document.Pieces[0].Id);
            ViewportHost.Camera.LookFrom(new Vector3(40f, 20f, 40f), Vector3.zero);
            yield return Tap(PadButton.R3);
            var box = EditorState.BoxOf(document.Pieces[0]);
            Check(Vector3.Distance(ViewportHost.Camera.Pivot, box.center) < 0.5f,
                $"and R3 looks at the selection: pivot {V4(ViewportHost.Camera.Pivot)}, piece {V4(box.center)}");
            EditorState.Select(Array.Empty<int>());
        }

        /// <summary>Rows 7, 9, 10 and 11: what the crosshair is on.</summary>
        private static IEnumerator PadAimed(BlueprintDocument document)
        {
            ViewportHost.SetMode(CameraMode.Orbit);
            DocPiece aimed = null;
            foreach (var piece in document.Pieces)
            {
                EditorState.Select(piece.Id);
                ViewportHost.Frame();
                yield return null;
                yield return null;
                if (ViewportHost.AimPiece == piece.Id)
                {
                    aimed = piece;
                    break;
                }
            }

            Check(aimed != null, "the crosshair can be put on a piece of the blueprint");
            if (aimed == null)
            {
                yield break;
            }

            EditorState.Select(Array.Empty<int>());
            yield return Tap(PadButton.R2);
            var picked = EditorState.SelectionCount == 1 && EditorState.IsSelected(aimed.Id);
            yield return Tap(PadButton.L1, PadButton.R2);
            Check(picked && EditorState.SelectionCount == 0,
                "R2 takes the aimed piece and L1 + R2 puts it back out of the selection");

            yield return Tap(PadButton.L2, PadButton.R2);
            Check(EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Add
                && EditorState.Held != null && EditorState.Held.PrefabName == aimed.PrefabName,
                $"L2 + R2 puts another {aimed.PrefabName} in hand");
            EditorState.CancelMode();

            EditorState.Select(aimed.Id);
            yield return Tap(PadButton.Square);
            var moving = EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Move;
            yield return Tap(PadButton.Cross);
            Check(moving && !PiecePicker.IsOpen,
                "square moves the selection, and cross is silent while it is in hand");
            EditorState.CancelMode();

            EditorState.Select(aimed.Id);
            yield return Tap(PadButton.Triangle);
            Check(EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Duplicate,
                "triangle copies the selection");
            yield return Tap(PadButton.Circle);
            Check(EditorState.Mode == EditMode.Idle, "circle stops placing");
            yield return Tap(PadButton.Circle);
            Check(EditorState.SelectionCount == 0, "and the next one clears the selection");

            var count = document.Pieces.Count;
            yield return Tap(PadButton.R1);
            var gone = document.Pieces.Count;
            EditorState.Undo();
            yield return null;
            yield return null;
            Check(gone == count - 1 && document.Pieces.Count == count,
                $"R1 deletes the aimed piece: {count} -> {gone} -> {document.Pieces.Count}");

            var ids = new List<int> { aimed.Id };
            foreach (var piece in document.Pieces)
            {
                if (piece.Id != aimed.Id && ids.Count < 2)
                {
                    ids.Add(piece.Id);
                }
            }

            EditorState.Select(ids);
            count = document.Pieces.Count;
            yield return Tap(PadButton.R1);
            var both = document.Pieces.Count;
            EditorState.Undo();
            yield return null;
            yield return null;
            Check(both == count - 2,
                $"and the whole selection when the aimed piece is in it: {count} -> {both}");

            EditorState.Select(Array.Empty<int>());
            yield return Tap(PadButton.L2, PadButton.R1);
            Check(EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Duplicate
                && EditorState.Moving != null && EditorState.Moving.Count == 1,
                "L2 + R1 puts a copy of the aimed piece in hand instead");
            EditorState.CancelMode();
            EditorState.Select(Array.Empty<int>());

            // The research's trap: a selected button gets cross from the game's own UI as well.
            var button = EditorWindow.Root.GetComponentInChildren<UnityEngine.UI.Button>();
            Check(button != null, "the window has a button the UI can select");
            if (button != null)
            {
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(button.gameObject);
                yield return null;
                var had = ModUi.HasSelection;
                yield return Tap(PadButton.Cross);
                Check(had && !PiecePicker.IsOpen,
                    $"cross belongs to the selected button '{button.name}', so the menu does not also open");
                yield return Tap(PadButton.Cross);
                Check(PiecePicker.IsOpen, "the pad took the aim with that press, so the next cross opens it");
                PiecePicker.Close();
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
                yield return null;
            }
        }

        /// <summary>A real mouse move over the pane takes the aim back from the crosshair.</summary>
        private static IEnumerator PadMouse()
        {
            ViewportHost.SetMode(CameraMode.Orbit);
            yield return null;
            var camera = ViewportHost.Camera;
            var ahead = ViewportHost.Scene.Root.TransformPoint(camera.Position + (camera.Forward * 5f));
            if (!ViewportHost.Raycast.Project(ahead, out var screen))
            {
                Check(false, "the middle of the pane has no screen point");
                yield break;
            }

            yield return Tap(PadButton.L2);
            Check(ViewportHost.PadAim, "the pad has the aim again");
            ViewportHost.MouseMoved(screen);
            Check(!ViewportHost.PadAim, "the first mouse move over the pane gives it back");

            yield return Tap(PadButton.L2);
            ViewportHost.MouseMoved(screen + new Vector2(2f, 0f));
            var wobble = ViewportHost.PadAim;
            ViewportHost.MouseMoved(screen + new Vector2(9f, 0f));
            Check(wobble && !ViewportHost.PadAim,
                "a 2 px wobble keeps the crosshair, a 7 px move gives the aim back to the mouse");
        }

        // ---------- scenario: editor_build ----------

        /// <summary>
        /// The editor and the build tool, joined up. Three walls are placed in the editor, saved
        /// under a new name, then handed to the hammer with the top bar's Build this button. The
        /// blueprint is built in the world and checked piece by piece, and the editor key takes it
        /// back out of the hammer's hand.
        /// </summary>
        private static IEnumerator TestEditorBuild(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);

            PieceCatalog.Ensure();
            var wall = PieceCatalog.Find("woodwall");
            Check(PieceCatalog.Ready && wall != null, "the catalog is built and has woodwall");
            if (wall == null)
            {
                yield break;
            }

            // An earlier scenario may have cleared the recipes, so unlock this one on purpose.
            player.m_knownRecipes.Add(wall.Piece.m_name);
            player.UpdateAvailablePiecesList();

            // Its own blueprint folder: the player's files are never touched.
            var folder = Path.Combine(OutDir, "build-test");
            var wasFolder = BlueprintLibrary.UserFolder;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            Directory.CreateDirectory(folder);
            BlueprintLibrary.UserFolder = folder;
            BlueprintLibrary.Reload();

            yield return EditorToHammer(player, wall);

            BlueprintMode.Exit();
            RemoveOldTestBuildings(player);
            BlueprintLibrary.UserFolder = wasFolder;
            BlueprintLibrary.Reload();
        }

        /// <summary>Place three walls, save them, hand them to the hammer, build them, edit them again.</summary>
        private static IEnumerator EditorToHammer(Player player, PieceEntry wall)
        {
            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open, "the key opened the editor");
            if (!ModUi.Open)
            {
                yield break;
            }

            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(ViewportHost.Ready, $"the 3D pane is ready after {waited:0.00} s");

            // ---- three walls in a row ----
            EditorSession.Replace(DocumentStore.New("Build test"));
            yield return null;
            var document = EditorSession.Document;
            Check(document != null && document.Pieces.Count == 0, "the editor starts on an empty blueprint");
            if (document == null)
            {
                yield break;
            }

            for (var i = 0; i < 3; i++)
            {
                EditorState.StartAdd(wall);
                var spot = EditAim(new Vector3(i * 1.9f, 0f, 0f));
                Check(spot != null && EditorState.CommitPlacement(spot), $"wall {i + 1} dropped");
            }

            EditorState.CancelMode();
            Check(document.Pieces.Count == 3, $"three walls stand in the blueprint: {document.Pieces.Count}");
            if (document.Pieces.Count != 3)
            {
                yield break;
            }

            Log("editor pieces: " + string.Join(", ", document.Pieces.Select(p => V4(p.Position))));
            Check(Vector3.Distance(document.Pieces[0].Position, new Vector3(0f, 1f, 0f)) < 1e-4f,
                $"the first one sits on the ground at {V4(document.Pieces[0].Position)}, wanted (0,1,0)");
            Check(document.Pieces.Select(p => p.Position).Distinct().Count() == 3, "all three are in different spots");

            // ---- no hammer out: it says so instead of doing nothing ----
            var hammer = player.GetRightItem();
            player.UnequipItem(hammer);
            yield return new WaitForSeconds(0.3f);
            Toasts.Clear();
            Check(!EditorCommands.BuildThis() && ModUi.Open && !BlueprintMode.Active,
                "with no hammer out Build this does nothing and the window stays");
            Check(Toasts.Count == 1 && Toasts.LevelOf(0) == ToastLevel.Error,
                $"and it says why: '{Toasts.TextOf(0)}'");
            player.EquipItem(hammer);
            yield return new WaitForSeconds(0.5f);
            Check(player.InPlaceMode(), "the hammer is out again");

            // ---- save as, and the library picks the file up on its own ----
            Check(EditorCommands.SaveAs("Build test", true), "Save as wrote the blueprint");
            var path = document.SourcePath;
            Check(!string.IsNullOrEmpty(path) && File.Exists(path),
                $"it has a file now: {Path.GetFileName(path ?? "none")}");
            var listed = BlueprintLibrary.All.FirstOrDefault(b => b.SourcePath == path);
            Check(listed != null && listed.Name == "Build test" && listed.Pieces.Count == 3,
                $"the library reloaded and holds it: {(listed != null ? listed.Pieces.Count + " pieces" : "not there")}");

            // ---- Build this: save the change, close, put the blueprint in the hammer ----
            EditorState.Select(document.Pieces[0].Id);
            EditorState.Nudge(new Vector3(0f, 0f, 0.5f));
            Check(document.Dirty, "an edit made it dirty again");
            var moved = document.Pieces[0].Position;

            TopBar.Tick();
            Check(TopBar.BuildEnabled && TopBar.BuildText == "Build this",
                $"the top bar's Build button is ready: '{TopBar.BuildText}'");
            TopBar.ClickBuild();
            yield return new WaitForSeconds(0.5f);

            Check(!document.Dirty, "Build this saved the change first");
            Check(!ModUi.Open, "and closed the window");
            var current = BlueprintMode.Current;
            Check(current != null && current.Name == "Build test" && current.Parts.Count == 3,
                $"the blueprint is in the hammer's hand: {(current != null ? current.Name + ", " + current.Parts.Count + " pieces" : "nothing")}");
            Check(ReferenceEquals(player.GetRightItem(), hammer), "it equipped nothing: the same hammer is still in hand");
            if (current == null)
            {
                yield break;
            }

            var reread = BlueprintLibrary.All.FirstOrDefault(b => b.SourcePath == path);
            Check(reread != null && Vector3.Distance(reread.Pieces[0].Position, moved) < 1e-3f,
                $"the file on disk holds the moved wall: {(reread != null ? V4(reread.Pieces[0].Position) : "not there")},"
                + $" wanted {V4(moved)}");

            yield return BuildInTheWorld(player, current);
            yield return EditItAgain(player, path);
            yield return LockedStillOpens(player, wall);
        }

        /// <summary>The blueprint the editor handed over, built where the player aims.</summary>
        private static IEnumerator BuildInTheWorld(Player player, ResolvedBlueprint current)
        {
            ClearInventoryExceptHammer(player);
            foreach (var cost in current.TotalCost)
            {
                player.GetInventory().AddItem(cost.m_resItem.gameObject.name, cost.m_amount, 1, 0, 0L, "", false);
            }

            // A wall needs a workbench in range. It is not what is tested, so it comes for free.
            foreach (var station in current.Stations.Where(s => !current.OwnStations.Contains(s.m_name)))
            {
                if (CraftingStation.HaveBuildStationInRange(station.m_name, player.transform.position) == null)
                {
                    player.PlacePiece(
                        station.GetComponent<Piece>(),
                        player.transform.position - player.transform.forward * 3f,
                        Quaternion.identity,
                        false);
                    yield return null;
                }
            }

            yield return new WaitForSeconds(0.5f);
            yield return AimAtGround(player);
            Check(BlueprintMode.HasTarget && BlueprintMode.Blocked == null,
                "the preview stands on a free spot: " + (BlueprintMode.Blocked ?? "ok"));

            var root = BlueprintMode.PreviewRoot;
            Check(root != null, "the preview is in the world");
            if (root == null)
            {
                yield break;
            }

            var wanted = current.Parts.Select(p => root.TransformPoint(p.Source.Position)).ToList();
            var center = root.position;
            player.m_lastToolUseTime = 0f;
            var existing = new HashSet<Piece>(PiecesAround(player, center));
            if (!BlueprintMode.TryBuild(player))
            {
                Check(false, "the blueprint built: " + (BlueprintRules.CheckCanBuild(player, current) ?? "no reason given"));
            }
            else
            {
                Check(true, "the blueprint built");
            }

            var built = PiecesAround(player, center).Where(p => !existing.Contains(p)).ToList();
            Check(built.Count == wanted.Count, $"three pieces exist: {built.Count} of {wanted.Count}");
            if (built.Count == 0)
            {
                yield break;
            }

            var worst = wanted.Max(w => built.Min(p => Vector3.Distance(p.transform.position, w)));
            Check(worst < 0.01f, $"every piece stands where the blueprint says, worst {worst:0.####} m off");

            yield return new WaitForSeconds(2f);
            player.m_lookPitch = 12f;
            yield return Screenshot("editor-build-1-built");

            // Support checks start at once; give them time to break it if they will.
            yield return new WaitForSeconds(15f);
            var standing = built.Count(p => p != null);
            Check(standing == wanted.Count, $"still standing after 15 s: {standing} of {wanted.Count}");
            yield return Screenshot("editor-build-2-after-15s");
        }

        /// <summary>The way back: the editor key edits the blueprint the hammer is holding.</summary>
        private static IEnumerator EditItAgain(Player player, string path)
        {
            Check(BlueprintMode.Active, "the blueprint is still in hand after building");
            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open, "the key opened the editor again");

            var back = EditorSession.Document;
            Check(back != null && back.Name == "Build test" && back.Pieces.Count == 3,
                $"on the blueprint that was in hand, not the last kit: '{(back != null ? back.Name : "nothing")}'");
            Check(back != null && back.SourcePath == path, "and on its own file, so Save writes it straight back");
            Check(!BlueprintMode.Active, "the build tool let go of it");

            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            Check(!ModUi.Open, "the editor closed");
        }

        /// <summary>A blueprint this character cannot build is out of the build cycle, not out of the editor.</summary>
        private static IEnumerator LockedStillOpens(Player player, PieceEntry wall)
        {
            var notLearned = PieceCatalog.All.FirstOrDefault(e => !PieceCatalog.IsUnlocked(e));
            Check(notLearned != null, "this character has not learned every piece");
            if (notLearned == null)
            {
                yield break;
            }

            var mixed = BlueprintDocument.New("Locked test");
            mixed.AddPiece(wall.PrefabName, Vector3.zero, Quaternion.identity);
            mixed.AddPiece(notLearned.PrefabName, new Vector3(0f, 0f, 4f), Quaternion.identity);
            Check(ResolvedBlueprint.TryResolve(mixed.ToBlueprint(), out var half, out var error)
                    && !BlueprintRules.IsAvailable(player, half),
                $"a blueprint holding a locked {notLearned.PrefabName} is kept out of the build cycle"
                + (error != null ? ": " + error : ""));

            EditorSession.OpenDocument(mixed);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open && EditorSession.Document == mixed && mixed.Pieces.Count == 2,
                "but the editor opens it all the same");
            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
        }

        // ---------- scenario: editor_snap ----------

        /// <summary>
        /// The placing and snapping engine, with no UI at all: a table of rays against small scenes
        /// built straight from the catalog, each one asserting where the piece lands and whether it
        /// snapped. The geometry every case leans on is written to snap.txt, so a case that breaks
        /// after a game update can be told apart from a case that was aimed wrong.
        /// </summary>
        private static IEnumerator TestEditorSnap(Player player)
        {
            yield return new WaitForSeconds(1f);

            PieceCatalog.Ensure();
            Check(PieceCatalog.Ready, "piece catalog built");
            if (!PieceCatalog.Ready)
            {
                yield break;
            }

            _snapLog = new StringBuilder();
            DumpSnapPieces();
            yield return null;

            CheckShapes();
            CheckMovingSets();
            CheckSnapCases();

            var path = Path.Combine(OutDir, "snap.txt");
            File.WriteAllText(path, _snapLog.ToString());
            Log("wrote " + path);
        }

        private static StringBuilder _snapLog;

        /// <summary>Pieces every case leans on. Their numbers go in snap.txt.</summary>
        private static readonly string[] SnapPieces =
        {
            "woodwall", "wood_floor", "wood_pole", "wood_roof", "piece_workbench",
        };

        private static void DumpSnapPieces()
        {
            var flagged = new List<string>();
            foreach (var entry in PieceCatalog.All)
            {
                if (entry.GroundPiece || entry.ClipGround || entry.ClipEverything)
                {
                    flagged.Add($"{entry.PrefabName}(ground={entry.GroundPiece},clipGround={entry.ClipGround},"
                        + $"clipAll={entry.ClipEverything})");
                }
            }

            SnapLine($"hammer pieces that skip the touch rule: {flagged.Count} {string.Join(" ", flagged)}");
            SnapLine("");

            foreach (var name in SnapPieces)
            {
                var entry = PieceCatalog.Find(name);
                if (entry == null)
                {
                    SnapLine(name + ": not in the hammer's table");
                    continue;
                }

                SnapLine($"{entry.PrefabName} | bounds {Box(entry.Bounds)} | ground={entry.GroundPiece} "
                    + $"clipGround={entry.ClipGround} clipAll={entry.ClipEverything} rotate={entry.CanRotate} "
                    + $"water={entry.WaterPiece} rotatedOverlap={entry.AllowRotatedOverlap}");
                for (var i = 0; i < entry.SnapPoints.Length; i++)
                {
                    SnapLine($"  snap {i} {V4(entry.SnapPoints[i])} \"{entry.SnapNames[i]}\"");
                }

                DumpColliders("  touch", entry.TouchColliders);
                DumpColliders("  ray  ", entry.RayColliders);
                DumpColliders("  snap ", entry.SnapSearchColliders);
                SnapLine("");
            }
        }

        private static void DumpColliders(string what, PieceCollider[] colliders)
        {
            foreach (var collider in colliders)
            {
                SnapLine($"{what} {collider.Kind} centre {V4(collider.Center)} size {V4(collider.Size)} "
                    + $"euler {V4(collider.Rotation.eulerAngles)} r={collider.Radius:0.###} "
                    + $"p0 {V4(collider.P0)} p1 {V4(collider.P1)} layer={collider.LayerName}");
            }
        }

        /// <summary>The three shapes on their own, where the numbers can be worked out by hand.</summary>
        private static void CheckShapes()
        {
            var box = new PlaceShape
            {
                Kind = ShapeKind.Box,
                Center = new Vector3(0f, 1f, 0f),
                Rotation = Quaternion.Euler(0f, 45f, 0f),
                Half = new Vector3(1f, 1f, 1f),
            };
            var corner = Mathf.Sqrt(2f);
            SnapNear(box.ClosestPoint(new Vector3(0f, 1f, 10f)), new Vector3(0f, 1f, corner), "box closest point, turned 45");
            Check(Mathf.Abs(box.LowestY()) < 1e-4f, $"box lowest y {box.LowestY():0.####}, wanted 0");
            Check(box.RayHit(new Vector3(0f, 10f, 0f), Vector3.down, out var boxHit)
                && Vector3.Distance(boxHit.Point, new Vector3(0f, 2f, 0f)) < 1e-4f
                && Vector3.Distance(boxHit.Normal, Vector3.up) < 1e-4f,
                "ray hits the box top at y = 2, facing up");
            Check(!box.RayHit(new Vector3(0f, 1f, 0f), Vector3.down, out _), "a ray that starts inside the box misses it");

            var sphere = new PlaceShape
            {
                Kind = ShapeKind.Sphere,
                Center = new Vector3(0f, 2f, 0f),
                Rotation = Quaternion.identity,
                Half = Vector3.one * 0.5f,
                Radius = 0.5f,
                P0 = new Vector3(0f, 2f, 0f),
                P1 = new Vector3(0f, 2f, 0f),
            };
            SnapNear(sphere.ClosestPoint(new Vector3(0f, 9f, 0f)), new Vector3(0f, 2.5f, 0f), "sphere closest point");
            Check(Mathf.Abs(sphere.LowestY() - 1.5f) < 1e-4f, $"sphere lowest y {sphere.LowestY():0.####}, wanted 1.5");
            Check(sphere.RayHit(new Vector3(0f, 10f, 0f), Vector3.down, out var sphereHit)
                && Mathf.Abs(sphereHit.Point.y - 2.5f) < 1e-4f, "ray hits the sphere top at y = 2.5");

            var capsule = new PlaceShape
            {
                Kind = ShapeKind.Capsule,
                Center = new Vector3(0f, 2f, 0f),
                Rotation = Quaternion.identity,
                Half = new Vector3(0.5f, 1.5f, 0.5f),
                Radius = 0.5f,
                P0 = new Vector3(0f, 1f, 0f),
                P1 = new Vector3(0f, 3f, 0f),
            };
            SnapNear(capsule.ClosestPoint(new Vector3(9f, 2f, 0f)), new Vector3(0.5f, 2f, 0f), "capsule closest point");
            Check(Mathf.Abs(capsule.LowestY() - 0.5f) < 1e-4f, $"capsule lowest y {capsule.LowestY():0.####}, wanted 0.5");
            Check(capsule.RayHit(new Vector3(0f, 10f, 0f), Vector3.down, out var capsuleHit)
                && Mathf.Abs(capsuleHit.Point.y - 3.5f) < 1e-4f, "ray hits the capsule cap at y = 3.5");
        }

        /// <summary>One piece pivots on itself, a group on the bottom centre of its union box.</summary>
        private static void CheckMovingSets()
        {
            var wall = PieceCatalog.Find("woodwall");
            var bench = PieceCatalog.Find("piece_workbench");
            if (wall == null || bench == null)
            {
                Check(false, "woodwall and piece_workbench are in the catalog");
                return;
            }

            var one = MovingSet.Of(new List<MovingPiece> { Moving(wall, new Vector3(7f, 3f, -2f), 0) });
            Check(one.Count == 1 && one.Pivot == new Vector3(7f, 3f, -2f) && one.Pieces[0].Pos == Vector3.zero
                && one.Single == wall && one.Snaps.Count == wall.SnapPoints.Length,
                "one piece pivots on itself and keeps its own flags");

            var group = MovingSet.Of(new List<MovingPiece>
            {
                Moving(wall, new Vector3(0f, 1f, 0f), 0),
                Moving(wall, new Vector3(2f, 1f, 0f), 0),
            });
            var offset = group.Pieces[1].Pos - group.Pieces[0].Pos;
            Check(group.Count == 2 && group.Single == null && group.Snaps.Count == 2 * wall.SnapPoints.Length
                && Vector3.Distance(offset, new Vector3(2f, 0f, 0f)) < 1e-4f
                && Mathf.Abs(group.Pivot.x - 1f) < 1e-4f,
                $"a group pivots on the bottom centre of its box: pivot {V4(group.Pivot)}, offset {V4(offset)}");

            var empty = MovingSet.One(bench);
            Check(empty.Snaps.Count == 0 && empty.TouchCount > 0 && empty.Reach < 1e-4f,
                $"a piece with no snap points is fine: {empty.Snaps.Count} snaps, reach {empty.Reach:0.###}");
            SnapLine($"moving sets | one pivot {V4(one.Pivot)} | group pivot {V4(group.Pivot)} reach {group.Reach:0.###}"
                + $" | wall reach {MovingSet.One(wall).Reach:0.###}");
        }

        /// <summary>
        /// The table. Every case builds its own little scene, fires one ray straight down from 20 m
        /// and checks where the pivot ends up. The wanted numbers are whole metres because the
        /// pieces snap on a grid: a wall is 2 m wide and 2 m tall around its pivot.
        /// </summary>
        private static void CheckSnapCases()
        {
            var wall = PieceCatalog.Find("woodwall");
            var floor = PieceCatalog.Find("wood_floor");
            var pole = PieceCatalog.Find("wood_pole");
            var roof = PieceCatalog.Find("wood_roof");
            var bench = PieceCatalog.Find("piece_workbench");
            if (wall == null || floor == null || pole == null || roof == null || bench == null)
            {
                Check(false, "every piece the snap table needs is in the catalog");
                return;
            }

            // 1. open ground: the touch rule lifts the wall by half its height.
            var r = Place("wall on ground", Scene(), MovingSet.One(wall), new Vector3(0f, 0f, 0f), Rule());
            SnapCase("wall on ground", r, new Vector3(0f, 1f, 0f), false);
            Check(r != null && r.Hit.Terrain && r.Hit.Piece < 0, "the wall's ray landed on the ground");

            // 2. the aimed point carries the wall in x and z, and the wheel turns it.
            var turned = Rule();
            turned.Steps = 4; // 4 x 22.5 = 90 degrees
            r = Place("wall on ground, turned", Scene(), MovingSet.One(wall), new Vector3(3.25f, 0f, -1.5f), turned);
            SnapCase("wall on ground, turned", r, new Vector3(3.25f, 1f, -1.5f), false);
            Check(r != null && Quaternion.Angle(r.Rot, Quaternion.Euler(0f, 90f, 0f)) < 1e-3f,
                "four wheel steps turn the wall 90 degrees");

            // 3. beside a standing wall: the side snap points pull it one wall over.
            r = Place("wall beside a wall", Scene(Standing(1, wall, new Vector3(0f, 1f, 0f))),
                MovingSet.One(wall), new Vector3(1.9f, 0f, 0f), Rule());
            SnapCase("wall beside a wall", r, new Vector3(2f, 1f, 0f), true);
            Check(r != null && r.SnapPiece == 1 && Vector3.Distance(r.SnapTo, new Vector3(1f, 2f, 0f)) < 1e-4f,
                "it snapped to the standing wall's own top corner");

            // 4. aimed at the wall's top face, so the ray lands on a piece, not the ground.
            r = Place("wall on top of a wall", Scene(Standing(1, wall, new Vector3(0f, 1f, 0f))),
                MovingSet.One(wall), new Vector3(0f, 0f, 0f), Rule());
            SnapCase("wall on top of a wall", r, new Vector3(0f, 3f, 0f), true);
            Check(r != null && !r.Hit.Terrain && r.Hit.Piece == 1, "that ray landed on the standing wall, not the ground");

            // 5. floors snap edge to edge, flat on the ground.
            r = Place("floor to floor", Scene(Standing(1, floor, Vector3.zero)),
                MovingSet.One(floor), new Vector3(1.9f, 0f, 0f), Rule());
            SnapCase("floor to floor", r, new Vector3(2f, 0f, 0f), true);

            // 6. a roof on the wall's top: its low edge goes on the wall's top corner.
            r = Place("roof to wall top", Scene(Standing(1, wall, new Vector3(0f, 1f, 0f))),
                MovingSet.One(roof), new Vector3(0f, 0f, 0f), Rule());
            SnapCase("roof to wall top", r, new Vector3(0f, 2f, -1f), true);

            // 7. the snap is refused when it would put the pole on the standing pole.
            r = Place("same kind refused", Scene(Standing(1, pole, new Vector3(0f, 0.5f, 0f))),
                MovingSet.One(pole), new Vector3(0.3f, 0f, 0f), Rule());
            SnapCase("same kind refused", r, new Vector3(0.3f, 0.5f, 0f), false);
            Check(r != null && r.SnapSkipped && !r.Duplicate,
                "the refused snap is reported, and where it stopped is not a duplicate");

            // 8. the no-snap modifier leaves the wall where the touch rule put it.
            var free = Rule();
            free.Snapping = false;
            r = Place("no-snap modifier", Scene(Standing(1, wall, new Vector3(0f, 1f, 0f))),
                MovingSet.One(wall), new Vector3(1.9f, 0f, 0f), free);
            SnapCase("no-snap modifier", r, new Vector3(1.9f, 1f, 0f), false);
            Check(r != null && !r.SnapSkipped, "with snapping off no snap is even looked for");

            // 9. a chosen snap point: the bottom left corner goes on the aimed point, then snaps.
            var manual = Rule();
            manual.Manual = SnapIndex(wall, new Vector3(-1f, -1f, 0f));
            r = Place("chosen snap point", Scene(Standing(1, wall, new Vector3(0f, 1f, 0f))),
                MovingSet.One(wall), new Vector3(1.4f, 0f, 0f), manual);
            SnapCase("chosen snap point", r, new Vector3(2f, 1f, 0f), true);
            Check(r != null && r.Manual == manual.Manual && !string.IsNullOrEmpty(r.ManualName),
                $"the chosen snap point is kept and named: {(r != null ? r.ManualName : "-")}");

            // 10. how Q and E walk the list.
            Check(Placer.WrapManual(-2, 4) == 3 && Placer.WrapManual(4, 4) == -1 && Placer.WrapManual(2, 4) == 2
                && Placer.WrapManual(0, 0) == -1,
                "the chosen snap point wraps round the list, and stays automatic when there are none");

            // 11. a group keeps the shape it was picked up with, and lands on the ground.
            var group = MovingSet.Of(new List<MovingPiece>
            {
                Moving(wall, new Vector3(0f, 1f, 0f), 1),
                Moving(wall, new Vector3(2f, 1f, 0f), 2),
            });
            r = Place("group on ground", Scene(), group, new Vector3(5f, 0f, -5f), Rule());
            var kept = r != null && r.World.Length == 2
                && Vector3.Distance(r.World[1].Pos - r.World[0].Pos, new Vector3(2f, 0f, 0f)) < 1e-4f;
            Check(kept, "a group keeps the offset between its pieces");
            Check(r != null && Mathf.Abs(Lowest(group, r)) < 1e-4f,
                $"the group lands on the ground: lowest point {(r != null ? Lowest(group, r) : -1f):0.####}");
            var turnedGroup = Rule();
            turnedGroup.Steps = 4;
            r = Place("group turned", Scene(), group, new Vector3(5f, 0f, -5f), turnedGroup);
            Check(r != null && r.World.Length == 2
                && Vector3.Distance(r.World[1].Pos - r.World[0].Pos, new Vector3(0f, 0f, -2f)) < 1e-4f
                && Quaternion.Angle(r.World[0].Rot, Quaternion.Euler(0f, 90f, 0f)) < 1e-3f,
                "turning the group turns the offset with it");

            // 12. a ground piece ignores the touch rule: its pivot goes straight on the aimed point.
            var ground = GroundPiece();
            Check(ground != null, "a ground piece was found to test the touch bypass with");
            if (ground != null)
            {
                r = Place("ground piece " + ground.PrefabName, Scene(), MovingSet.One(ground), new Vector3(4f, 0f, 4f), Rule());
                SnapCase("ground piece " + ground.PrefabName, r, new Vector3(4f, 0f, 4f), false);
            }

            // 13. a piece with no snap points at all still goes through every step. The workbench is
            // not a ground piece in 1.0, so the touch rule runs: its collider starts a little above
            // the pivot, so the pivot ends up that far under the ground.
            Check(bench.SnapPoints.Length == 0, "the workbench has no snap points at all");
            var lift = bench.TouchColliders[0].Center.y - (bench.TouchColliders[0].Size.y * 0.5f);
            r = Place("no snap points", Scene(), MovingSet.One(bench), new Vector3(4f, 0f, 4f), Rule());
            SnapCase("no snap points", r, new Vector3(4f, -lift, 4f), false);

            // 14. too far for the game's rule, close enough for the assist.
            var assist = Rule();
            assist.Assist = true;
            r = Place("assist", Scene(Standing(1, wall, new Vector3(0f, 1f, 0f))),
                MovingSet.One(wall), new Vector3(2.6f, 0f, 0f), assist);
            SnapCase("assist", r, new Vector3(2f, 1f, 0f), true);
            Check(r != null && r.Assisted, "that snap came from the assist, not from the game's rule");

            // 15. the same aim without the assist: the wall stays where it was aimed.
            r = Place("assist off", Scene(Standing(1, wall, new Vector3(0f, 1f, 0f))),
                MovingSet.One(wall), new Vector3(2.6f, 0f, 0f), Rule());
            SnapCase("assist off", r, new Vector3(2.6f, 1f, 0f), false);
        }

        // ---------- snap helpers ----------

        /// <summary>The game's rule on its own: snapping on, automatic snap point, no assist.</summary>
        private static PlaceOptions Rule()
        {
            return new PlaceOptions { Manual = -1, Snapping = true, Assist = false };
        }

        private static SceneIndex Scene(params ScenePiece[] pieces)
        {
            return new SceneIndex(pieces);
        }

        private static ScenePiece Standing(int id, PieceEntry entry, Vector3 pos)
        {
            return new ScenePiece { Id = id, Prefab = entry.PrefabName, Pos = pos, Rot = Quaternion.identity, Entry = entry };
        }

        private static MovingPiece Moving(PieceEntry entry, Vector3 pos, int id)
        {
            return new MovingPiece { Id = id, Prefab = entry.PrefabName, Pos = pos, Rot = Quaternion.identity, Entry = entry };
        }

        /// <summary>One ray, straight down from 20 m over the given spot.</summary>
        private static PlaceResult Place(string what, SceneIndex index, MovingSet moving, Vector3 at, PlaceOptions opts)
        {
            var origin = at + new Vector3(0f, 20f, 0f);
            if (!Placer.Place(index, moving, origin, Vector3.down, opts, out var result))
            {
                SnapLine($"{what}: the ray met nothing");
                return null;
            }

            SnapLine($"{what}: pos {V4(result.Pos)} snapped={result.Snapped} assisted={result.Assisted} "
                + $"skipped={result.SnapSkipped} duplicate={result.Duplicate} terrain={result.Hit.Terrain} "
                + $"hit {V4(result.Hit.Point)} normal {V4(result.Hit.Normal)} from {V4(result.SnapFrom)} to {V4(result.SnapTo)}");
            return result;
        }

        private static void SnapCase(string what, PlaceResult result, Vector3 want, bool snapped)
        {
            if (result == null)
            {
                Check(false, what + ": the ray met nothing");
                return;
            }

            Check(Vector3.Distance(result.Pos, want) < 1e-4f && result.Snapped == snapped,
                $"{what}: pos {V4(result.Pos)} snapped={result.Snapped}, wanted {V4(want)} snapped={snapped}");
        }

        private static void SnapNear(Vector3 got, Vector3 want, string what)
        {
            Check(Vector3.Distance(got, want) < 1e-4f, $"{what}: {V4(got)}, wanted {V4(want)}");
        }

        /// <summary>
        /// A piece the touch rule skips. The hammer's table is looked at first; the other build
        /// tools are the fallback, since in 1.0 the hammer may hold none.
        /// </summary>
        private static PieceEntry GroundPiece()
        {
            foreach (var entry in PieceCatalog.All)
            {
                if (entry.GroundPiece || entry.ClipGround || entry.ClipEverything)
                {
                    SnapLine($"ground piece from the hammer: {entry.PrefabName}");
                    return entry;
                }
            }

            var db = ObjectDB.instance;
            if (db == null)
            {
                return null;
            }

            foreach (var item in db.m_items)
            {
                var drop = item != null ? item.GetComponent<ItemDrop>() : null;
                var table = drop != null ? drop.m_itemData.m_shared.m_buildPieces : null;
                if (table == null)
                {
                    continue;
                }

                foreach (var prefab in table.m_pieces)
                {
                    var entry = PieceEntry.Read(prefab, 0);
                    if (entry != null && (entry.GroundPiece || entry.ClipGround || entry.ClipEverything))
                    {
                        SnapLine($"ground piece from {item.name}: {entry.PrefabName} ground={entry.GroundPiece} "
                            + $"clipGround={entry.ClipGround} clipAll={entry.ClipEverything} "
                            + $"snaps={entry.SnapPoints.Length} touch={entry.TouchColliders.Length}");
                        return entry;
                    }
                }
            }

            return null;
        }

        /// <summary>The index of the snap point at a given spot on the piece, or -1.</summary>
        private static int SnapIndex(PieceEntry entry, Vector3 local)
        {
            for (var i = 0; i < entry.SnapPoints.Length; i++)
            {
                if (Vector3.Distance(entry.SnapPoints[i], local) < 1e-4f)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Height of the lowest point of everything the set carries, where it landed.</summary>
        private static float Lowest(MovingSet moving, PlaceResult result)
        {
            var low = float.PositiveInfinity;
            foreach (var shape in moving.Shapes(result.Pos, result.Rot))
            {
                low = Mathf.Min(low, shape.LowestY());
            }

            return low;
        }

        private static void SnapLine(string line)
        {
            _snapLog?.AppendLine(line);
        }

        private static string V4(Vector3 v)
        {
            return $"({v.x:0.####},{v.y:0.####},{v.z:0.####})";
        }

        private static string Box(Bounds b)
        {
            return $"min {V4(b.min)} max {V4(b.max)}";
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
        /// The spawn stones are a no-build zone, so go to the flattest dry meadow near the middle of
        /// the world. The search starts from a fixed point and the player lands facing a fixed way,
        /// so every scenario in a run, and every run, builds on the same ground. Without that a
        /// chained run shifts the spot by a metre each time and a piece can lose its support.
        /// Heights come from the world generator, which needs no loaded area; the teleport waits for it.
        /// </summary>
        private static IEnumerator MoveToBuildSpot(Player player)
        {
            if (!_haveBuildSpot)
            {
                var best = Vector3.zero;
                var bestBumps = float.MaxValue;
                for (var distance = 60f; distance <= 400f && bestBumps > FlatEnough; distance += 20f)
                {
                    for (var angle = 0; angle < 360 && bestBumps > FlatEnough; angle += 15)
                    {
                        // Polar sweep from the middle of the world, where the spawn stones stand.
                        var spot = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                        var bumps = Bumpiness(spot, out var height);
                        if (bumps < bestBumps)
                        {
                            bestBumps = bumps;
                            best = new Vector3(spot.x, height, spot.z);
                        }
                    }
                }

                _buildSpot = best;
                _haveBuildSpot = true;
                Log($"build spot {best}, {best.magnitude:0} m from the middle, ground varies {bestBumps:0.00} m");
            }

            yield return new WaitForSeconds(2.5f); // teleport cooldown after spawning
            Check(player.TeleportTo(_buildSpot + Vector3.up, BuildFacing, true), "teleport to the build spot");
            while (player.IsTeleporting())
            {
                yield return null;
            }

            // The same facing every time: the aim, and so the spot the blueprint lands on, follows it.
            player.m_lookYaw = BuildFacing;
            player.m_lookPitch = 0f;
            yield return new WaitForSeconds(3f);
            ClearVegetation(player.transform.position, 20f);
            yield return new WaitForSeconds(1f);
            var before = Standing(player.transform.position, 8f);
            yield return LevelGround(player.transform.position, 14f);
            Check(Standing(player.transform.position, 8f) < 0.1f,
                $"the ground is flat: it varied {before:0.00} m, now {Standing(player.transform.position, 8f):0.00} m");
            Check(!Location.IsInsideNoBuildLocation(player.transform.position), $"moved to {player.transform.position}");
        }

        /// <summary>
        /// Height spread of the ground in a 16 m square, sampled every 2 m; MaxValue for water or
        /// another biome. A coarse pass throws away the hills first, so the dense one runs rarely.
        /// The blueprint is built inside this square, so the number is what decides whether a piece
        /// ends up with nothing under it.
        /// </summary>
        private static float Bumpiness(Vector3 spot, out float height)
        {
            var world = WorldGenerator.instance;
            var water = ZoneSystem.instance.m_waterLevel;
            height = world.GetHeight(spot.x, spot.z);
            if (world.GetBiome(spot) != Heightmap.Biome.Meadows || height < water + 2f)
            {
                return float.MaxValue;
            }

            if (Spread(world, spot, water, 8f, 8f, height) > 1f)
            {
                return float.MaxValue;   // a hill: not worth the dense sweep
            }

            return Spread(world, spot, water, 8f, 2f, height);
        }

        private static float Spread(WorldGenerator world, Vector3 spot, float water, float reach, float step, float height)
        {
            var min = height;
            var max = height;
            for (var x = -reach; x <= reach; x += step)
            {
                for (var z = -reach; z <= reach; z += step)
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

        /// <summary>
        /// Levels the ground, the way a player levels a site with the hoe before building. Valheim's
        /// meadows roll by a metre or more over a 16 m square, and no seed has a square flat enough
        /// for a kit to stand on everywhere, so without this the support check is a coin flip.
        ///
        /// A TerrainOp applies itself to every heightmap in range in its own Awake and then deletes
        /// itself, so the object is built switched off and switched on once its settings are in.
        /// </summary>
        private static IEnumerator LevelGround(Vector3 center, float radius)
        {
            var settings = new TerrainOp.Settings
            {
                m_level = true,
                m_levelRadius = radius,
                m_square = true,
                m_smooth = false,
                m_paintCleared = false,
            };

            // The operation travels as a routed RPC that carries only the op's prefab name, and the
            // far end reads the settings out of ObjectDB. A made-up op has to be registered there
            // or it arrives as an error and nothing happens. The keeper is switched off, so its own
            // Awake never fires; only the live object below applies itself.
            const string name = "ValheimTomrer_LevelGround";
            var hash = name.GetStableHashCode();
            var keeper = new GameObject(name);
            keeper.SetActive(false);
            keeper.AddComponent<TerrainOp>().m_settings = settings;
            ObjectDB.instance.m_terrainOpsByHash[hash] = keeper.GetComponent<TerrainOp>();

            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.position = center;
            go.AddComponent<TerrainOp>().m_settings = settings;
            go.SetActive(true);   // Awake applies it to every heightmap in range, then deletes it

            yield return new WaitForSeconds(1.5f);
            ObjectDB.instance.m_terrainOpsByHash.Remove(hash);
            UnityEngine.Object.Destroy(keeper);
            Log($"levelled the ground {radius:0} m around {V(center)}");
        }

        /// <summary>The height spread of the loaded ground, which is what a piece actually stands on.</summary>
        private static float Standing(Vector3 center, float reach)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var x = -reach; x <= reach; x += 2f)
            {
                for (var z = -reach; z <= reach; z += 2f)
                {
                    var at = new Vector3(center.x + x, center.y + 50f, center.z + z);
                    if (Physics.Raycast(at, Vector3.down, out var hit, 200f, LayerMask.GetMask("terrain")))
                    {
                        min = Mathf.Min(min, hit.point.y);
                        max = Mathf.Max(max, hit.point.y);
                    }
                }
            }

            return max > min ? max - min : 0f;
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
