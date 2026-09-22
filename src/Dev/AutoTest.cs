#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Blueprints.Sites;
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
using PadKey = UnityEngine.InputSystem.LowLevel.GamepadButton;

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
    /// "probe_build" measures what building from chests and partial builds rest on, to probe-build.txt
    /// (a probe like "probe", but left out of editor_all: it places and removes chests and floors);
    /// "build_sources" builds a kit paid from the inventory and the chests in range, nearest first;
    /// "build_partial" builds what the materials pay for and what would stand, bottom to top;
    /// "build_sites" keeps what a click left unbuilt in a file, and shows its missing parts as ghosts;
    /// "build_continue" continues an unfinished build: locked preview, the finishing click, Remove and its window;
    /// "card_materials" checks the hammer card's materials list: rows, numbers, colours, footer, Continue, two columns;
    /// "hint_row" checks the controls of blueprint mode, Continue and the capture in the game's own hint row, keyboard and pad;
    /// "editor_open" opens the editor window with its key and checks the input takeover;
    /// "editor_view" fills the 3D pane with a kit and drives the camera through both its modes;
    /// "editor_files" round-trips every blueprint through the writer and runs the file commands;
    /// "editor_palette" builds the piece catalog and checks the palette's counts and filters;
    /// "editor_snap" runs the placing and snapping engine against a table of rays, with no UI;
    /// "editor_edit" drives placing, selecting, copying, turning, nudging and undo, then draws it;
    /// "editor_panels" checks the right panel: the build card, the selection fields and the problem list;
    /// "editor_keys" drives every key, the wheel and the mouse, plus the top bar and the dialogs;
    /// "editor_pad" drives every controller button through a made-up pad, plus the piece menu;
    /// "editor_focus" walks the top bar and the two panels with the pad, and presses what it finds;
    /// "editor_keep" closes the editor on a changed blueprint and checks the key comes back to all of it;
    /// "editor_build" builds a blueprint made in the editor, in the world, and edits it again;
    /// "editor_capture" builds a kit turned 45 degrees, captures it with the turned rectangle, and compares it to the file;
    /// "editor_support" holds the editor's support rule against the game's, then checks the ghost's colours;
    /// "editor_all" runs every scenario above but probe_build in one game, then checks the mod wrote
    /// no art, in the unfinished-build folder too.
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

            // Unfinished builds go to the test's own folder, never the player's. Each run starts empty.
            SiteStore.RootOverride = SitesFolder;
            DeleteSitesFolder();
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
                case "probe_build":
                    scenario = ProbeBuild(player);
                    break;
                case "build_sources":
                    scenario = TestBuildSources(player);
                    break;
                case "build_partial":
                    scenario = TestBuildPartial(player);
                    break;
                case "build_sites":
                    scenario = TestBuildSites(player);
                    break;
                case "build_continue":
                    scenario = TestBuildContinue(player);
                    break;
                case "card_materials":
                    scenario = TestCardMaterials(player);
                    break;
                case "hint_row":
                    scenario = TestHintRow(player);
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
                case "editor_focus":
                    scenario = TestEditorFocus(player);
                    break;
                case "editor_keep":
                    scenario = TestEditorKeep(player);
                    break;
                case "editor_build":
                    scenario = TestEditorBuild(player);
                    break;
                case "editor_capture":
                    scenario = TestEditorCapture(player);
                    break;
                case "editor_support":
                    scenario = TestEditorSupport(player);
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

            // A run never leaves the player's config file changed, whatever the scenario flipped.
            ResetSettings();

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
                + "editor_edit,editor_panels,editor_keys,editor_pad,editor_focus,editor_keep,editor_build,"
                + "editor_capture,editor_support,blueprints,build_sources,build_partial,build_sites,"
                + "build_continue,card_materials,hint_row")
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
                case "probe_build": return ProbeBuild(player);
                case "build_sources": return TestBuildSources(player);
                case "build_partial": return TestBuildPartial(player);
                case "build_sites": return TestBuildSites(player);
                case "build_continue": return TestBuildContinue(player);
                case "card_materials": return TestCardMaterials(player);
                case "hint_row": return TestHintRow(player);
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
                case "editor_focus": return TestEditorFocus(player);
                case "editor_keep": return TestEditorKeep(player);
                case "editor_build": return TestEditorBuild(player);
                case "editor_capture": return TestEditorCapture(player);
                case "editor_support": return TestEditorSupport(player);
                default: return null;
            }
        }

        /// <summary>Back to a known state between two scenarios, whatever the last one left open.</summary>
        private static IEnumerator Reset(Player player)
        {
            // Closing keeps the blueprint for the next open. The next scenario starts from nothing.
            EditorSession.Forget();
            FocusNav.Leave();
            SiteRemovePopup.Press(SiteRemovePopup.Choice.Cancel);
            BlueprintMode.Exit();
            WorldCapture.Cancel();
            WorldCapture.ResetShape();
            PadReader.Fake = null;
            BlueprintLibrary.UserFolder = null;
            BlueprintLibrary.Reload();
            ClearSites();
            PieceCatalog.Clear();
            ResetSettings();
            if (player != null)
            {
                player.SetGodMode(true);
                player.m_lastToolUseTime = 0f;
                player.m_noPlacementCost = false;
            }

            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>
        /// Every editor and build setting back to its default. A scenario that flips one must not
        /// decide what the next one sees, and the run must not leave the player's config file changed.
        /// </summary>
        private static void ResetSettings()
        {
            Default(EditorConfig.CaptureKey);
            Default(EditorConfig.ShowAllPieces);
            Default(EditorConfig.SnapDots);
            Default(EditorConfig.Boxes);
            Default(BuildConfig.UseChests);
            Default(BuildConfig.ChestRange);
        }

        private static void Default<T>(BepInEx.Configuration.ConfigEntry<T> entry)
        {
            if (entry != null && !Equals(entry.Value, entry.DefaultValue))
            {
                entry.Value = (T)entry.DefaultValue;
            }
        }

        /// <summary>
        /// What the art walk found before a scenario cleared its unfinished builds. The chain clears
        /// the sites folder between scenarios, so the walk at the end never sees a site file.
        /// </summary>
        private static readonly HashSet<string> StraysSeen = new HashSet<string>();

        private static readonly HashSet<string> SiteFilesSeen = new HashSet<string>();

        /// <summary>The files of the unfinished builds the store held, told by the store, not the walk.</summary>
        private static readonly HashSet<string> SitesKept = new HashSet<string>();

        /// <summary>One art walk now, kept for the guard at the end of the chain.</summary>
        private static void NoteWrittenFiles()
        {
            foreach (var site in SiteStore.All)
            {
                if (!string.IsNullOrEmpty(site.Path) && File.Exists(site.Path))
                {
                    SitesKept.Add(Path.GetFullPath(site.Path));
                }
            }

            var walk = WalkWrittenFiles();
            StraysSeen.UnionWith(walk.Strays);
            SiteFilesSeen.UnionWith(walk.SiteFiles);
        }

        private sealed class ArtWalk
        {
            public readonly List<string> Strays = new List<string>();
            public readonly List<string> SiteFiles = new List<string>();
            public int Blueprints;
        }

        /// <summary>
        /// The one rule the whole mod hangs on: it writes blueprints and nothing else. Walks every
        /// folder the mod can write to and fails on anything that is not a .blueprint text file or
        /// the autotest's own output (screenshots, .txt reports, the log, the test worlds). Also
        /// fails when the walks missed an unfinished-build file the store kept, or saw none at all,
        /// so a change to the folder list cannot drop the sites folder without anyone noticing.
        /// A VT_CHAIN run in which no scenario kept a site skips that part with a note.
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

            var walk = WalkWrittenFiles();
            var strays = new HashSet<string>(StraysSeen);
            strays.UnionWith(walk.Strays);
            var siteFiles = new HashSet<string>(SiteFilesSeen);
            siteFiles.UnionWith(walk.SiteFiles);
            var missed = SitesKept.Where(f => !siteFiles.Contains(f)).ToList();

            var artFound = strays.Where(f => art.Contains(Ext(f))).ToList();
            Check(walk.Blueprints > 0, $"the mod wrote {walk.Blueprints} blueprint files");
            // The whole chain always keeps sites. A VT_CHAIN run of editor scenarios keeps none, and
            // then there is nothing the walk could have missed.
            if (Environment.GetEnvironmentVariable("VT_CHAIN") != null && SitesKept.Count == 0 && siteFiles.Count == 0)
            {
                Log("art guard: no scenario in this VT_CHAIN kept an unfinished build, so the sites check has nothing to look for");
            }
            else
            {
                Check(siteFiles.Count > 0 && missed.Count == 0,
                    $"the walk saw {siteFiles.Count} unfinished-build files under {SiteStore.Root}, and every one of the {SitesKept.Count} the store kept"
                    + (missed.Count > 0 ? " | not seen: " + string.Join(", ", missed.Take(5).ToArray()) : ""));
            }

            Check(artFound.Count == 0, artFound.Count == 0
                ? "no image, mesh, material or bundle file anywhere the mod writes, sites included"
                : "the mod wrote game art: " + string.Join(", ", artFound.ToArray()));
            Check(strays.Count == 0, strays.Count == 0
                ? "nothing but blueprints and test output on disk"
                : $"{strays.Count} unexpected files: " + string.Join(", ", strays.Take(10).ToArray()));
        }

        /// <summary>Every folder the mod can write to, walked once. Changes nothing.</summary>
        private static ArtWalk WalkWrittenFiles()
        {
            var result = new ArtWalk();
            var strays = result.Strays;
            var blueprints = 0;
            var sites = Path.GetFullPath(SiteStore.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            void NoteSite(string file)
            {
                if (file.StartsWith(sites, StringComparison.Ordinal))
                {
                    result.SiteFiles.Add(Path.GetFullPath(file));
                }
            }

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
                    NoteSite(file);
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
                    else
                    {
                        NoteSite(file);
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

            result.Blueprints = blueprints;
            return result;
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

        // ---------- scenario: editor_support ----------

        /// <summary>
        /// The support rule. First the numbers: test structures are built in the real world, the
        /// game is left to settle their support, and every piece is held against the editor's port
        /// of the rule. Then the editor: the ghost's colour, a drop that would fall is refused, the
        /// problem list, and the tint on the piece under the aim.
        /// </summary>
        private static IEnumerator TestEditorSupport(Player player)
        {
            yield return EquipHammer(player);
            WriteSupportStats(player);
            PieceCatalog.Ensure();
            if (Environment.GetEnvironmentVariable("VT_SUPPORT_EDITOR_ONLY") == null)
            {
                yield return SupportAgainstTheGame(player);
            }

            yield return SupportWithoutUi();
            yield return SupportInTheWindow();
        }

        private struct Planned
        {
            public string Structure;
            public string Prefab;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        /// <summary>
        /// The test structures, in blueprint space: y = 0 is the ground, +z is ahead of the player.
        /// Each one leans on a different part of the rule, and they stand far enough apart that no
        /// two touch.
        /// </summary>
        private static List<Planned> SupportPlan()
        {
            var plan = new List<Planned>();
            void Add(string structure, string prefab, Vector3 pos, float yaw = 0f)
            {
                plan.Add(new Planned { Structure = structure, Prefab = prefab, Pos = pos, Rot = Quaternion.Euler(0f, yaw, 0f) });
            }

            // Straight up: the vertical loss, until a pole breaks.
            for (var k = 0; k <= 10; k++)
            {
                Add("wood pole stack", "wood_pole2", new Vector3(-10f, 1f + (2f * k), 4f));
            }

            // Straight out: the horizontal loss.
            Add("wood beams out from a pole", "wood_pole2", new Vector3(-6f, 1f, 4f));
            for (var k = 0; k < 6; k++)
            {
                Add("wood beams out from a pole", "wood_beam", new Vector3(-5f + (2f * k), 2f, 4f));
            }

            // Held from both ends: the pair rule.
            Add("wood bridge", "wood_pole2", new Vector3(-6f, 1f, 8f));
            Add("wood bridge", "wood_pole2", new Vector3(2f, 1f, 8f));
            for (var k = 0; k < 4; k++)
            {
                Add("wood bridge", "wood_beam", new Vector3(-5f + (2f * k), 2f, 8f));
            }

            // Floors: flat pieces side by side.
            foreach (var corner in new[] { new Vector3(4f, 1f, 7f), new Vector3(6f, 1f, 7f), new Vector3(4f, 1f, 9f), new Vector3(6f, 1f, 9f) })
            {
                Add("wood floors out from four poles", "wood_pole2", corner);
            }

            for (var k = 0; k < 4; k++)
            {
                Add("wood floors out from four poles", "wood_floor", new Vector3(5f + (2f * k), 2f, 8f));
            }

            // Stone holds a lot straight up and nothing sideways. Wood on stone is capped at wood's own most.
            for (var k = 0; k < 3; k++)
            {
                Add("stone stack", "stone_wall_2x1", new Vector3(-10f, 0.5f + k, 12f));
            }

            Add("stone stack", "stone_wall_2x1", new Vector3(-8f, 2.5f, 12f));
            Add("stone stack", "wood_pole2", new Vector3(-10f, 4f, 12f));

            // Iron: a small loss both ways.
            Add("iron beams out from an iron pole", "woodiron_pole", new Vector3(6f, 1f, 12f));
            for (var k = 0; k < 3; k++)
            {
                Add("iron beams out from an iron pole", "woodiron_beam", new Vector3(7f + (2f * k), 2f, 12f));
            }

            // Turned boxes against each other.
            var turn = Quaternion.Euler(0f, 45f, 0f);
            Add("wood beams turned 45 degrees", "wood_pole2", new Vector3(-8f, 1f, -8f));
            for (var k = 0; k < 4; k++)
            {
                Add("wood beams turned 45 degrees", "wood_beam", new Vector3(-8f, 2f, -8f) + (turn * new Vector3(1f + (2f * k), 0f, 0f)), 45f);
            }

            // Nothing at all under it.
            Add("a floor in the air", "wood_floor", new Vector3(10f, 5f, -8f));

            // A real building.
            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop");
            if (kit != null)
            {
                foreach (var piece in kit.Pieces)
                {
                    plan.Add(new Planned
                    {
                        Structure = "workshop kit",
                        Prefab = piece.PrefabName,
                        Pos = new Vector3(0f, 0f, -8f) + piece.Position,
                        Rot = piece.Rotation,
                    });
                }
            }

            return plan;
        }

        /// <summary>
        /// Builds the plan in the world all at once, the way a blueprint is built, and waits until
        /// the game's support stops changing. Then every piece is compared: standing or fallen, and
        /// its support value.
        /// </summary>
        private static IEnumerator SupportAgainstTheGame(Player player)
        {
            yield return MoveToBuildSpot(player);
            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(0.5f);

            var origin = player.transform.position;
            if (Physics.Raycast(origin + (Vector3.up * 20f), Vector3.down, out var ground, 60f, LayerMask.GetMask("terrain")))
            {
                origin.y = ground.point.y;
            }

            var plan = SupportPlan();
            var scene = new List<ScenePiece>();
            var built = new WearNTear[plan.Count];
            for (var i = 0; i < plan.Count; i++)
            {
                var entry = PieceCatalog.Find(plan[i].Prefab);
                if (entry == null)
                {
                    Check(false, $"the catalog has {plan[i].Prefab}");
                    continue;
                }

                scene.Add(new ScenePiece { Id = i, Prefab = plan[i].Prefab, Pos = plan[i].Pos, Rot = plan[i].Rot, Entry = entry });
                var copy = UnityEngine.Object.Instantiate(entry.Prefab, origin + plan[i].Pos, plan[i].Rot);
                var piece = copy.GetComponent<Piece>();
                piece.m_creator = player.GetPlayerID();
                piece.m_nview.GetZDO().Set(ZDOVars.s_creator, player.GetPlayerID());
                built[i] = copy.GetComponent<WearNTear>();
                if (built[i] != null)
                {
                    built[i].OnPlaced();
                }
            }

            // The game passes support on once a second per piece, so a tall stack takes a while.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var predicted = Support.Solve(scene);
            Log($"support of {scene.Count} pieces worked out in {watch.Elapsed.TotalMilliseconds:0.0} ms");

            var last = Snapshot(built);
            var still = 0;
            var waited = 0f;
            while (waited < 90f && (still < 4 || waited < 12f))
            {
                yield return new WaitForSeconds(1f);
                waited += 1f;
                var now = Snapshot(built);
                still = SameSnapshot(last, now) ? still + 1 : 0;
                last = now;
            }

            Log($"the game's support settled after {waited:0} s");
            yield return Screenshot("support-1-world");

            // Built all at once, the game can keep a piece stronger than where it settles when built
            // one by one: WearNTear.UpdateSupport keeps its old value while the pieces under it do
            // not change. So the game may hold more than the editor says, never less, and a piece
            // the editor lets stand must stand.
            var text = new StringBuilder("\n\nall at once\nstructure | piece | position | game | editor\n");
            foreach (var group in plan.Select((p, i) => i).GroupBy(i => plan[i].Structure))
            {
                var wrong = new List<string>();
                var standing = 0;
                var fallen = 0;
                var exact = 0;
                var careful = 0;
                foreach (var i in group)
                {
                    var alive = built[i] != null;
                    var game = alive ? built[i].m_support : 0f;
                    var falls = predicted.Falls(i);
                    predicted.TryGet(i, out var mine, out var info);
                    var allowed = info != null ? Mathf.Max(0.5f, info.Max * 0.01f) : 0.5f;
                    text.AppendLine($"{plan[i].Structure} | {plan[i].Prefab} | {V(plan[i].Pos)} | "
                        + $"{(alive ? game.ToString("0.00") : "fell")} | {(falls ? "falls" : mine.ToString("0.00"))}");

                    if (!alive && !falls)
                    {
                        wrong.Add($"{plan[i].Prefab} at {V(plan[i].Pos)} fell in the game, the editor says it stands at {mine:0.00}");
                    }
                    else if (alive && !falls && game < mine - allowed)
                    {
                        wrong.Add($"{plan[i].Prefab} at {V(plan[i].Pos)}: game {game:0.00} is under the editor's {mine:0.00}");
                    }
                    else if (alive == !falls && (!alive || Mathf.Abs(game - mine) <= allowed))
                    {
                        exact++;
                    }
                    else
                    {
                        careful++;
                    }

                    standing += alive ? 1 : 0;
                    fallen += alive ? 0 : 1;
                }

                Check(wrong.Count == 0,
                    $"{group.Key}: {standing} stand and {fallen} fall in the game; the editor has {exact} of "
                    + $"{standing + fallen} exactly, {careful} weaker than the game, none stronger"
                    + (wrong.Count > 0 ? " | " + string.Join(" | ", wrong) : ""));
            }

            yield return OneByOne(player, origin, text);
            File.AppendAllText(Path.Combine(OutDir, "support.txt"), text.ToString());

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// A stack of poles built the way a player builds it: one pole, wait until the game has
        /// settled it, the next. This is the history the editor's numbers are, so they must match
        /// to the hundredth, and the pole that breaks is the one the editor refuses.
        /// </summary>
        private static IEnumerator OneByOne(Player player, Vector3 origin, StringBuilder text)
        {
            var entry = PieceCatalog.Find("wood_pole2");
            var built = new List<WearNTear>();
            var scene = new List<ScenePiece>();
            var broke = -1;
            text.AppendLine("\none by one\npole | game | editor");
            for (var k = 0; k < 10 && broke < 0; k++)
            {
                var pos = new Vector3(10f, 1f + (2f * k), 2f);
                var copy = UnityEngine.Object.Instantiate(entry.Prefab, origin + pos, Quaternion.identity);
                var piece = copy.GetComponent<Piece>();
                piece.m_creator = player.GetPlayerID();
                piece.m_nview.GetZDO().Set(ZDOVars.s_creator, player.GetPlayerID());
                var wear = copy.GetComponent<WearNTear>();
                wear.OnPlaced();
                built.Add(wear);
                scene.Add(new ScenePiece { Id = k, Prefab = entry.PrefabName, Pos = pos, Rot = Quaternion.identity, Entry = entry });

                var last = -2f;
                for (var waited = 0; waited < 8; waited++)
                {
                    yield return new WaitForSeconds(1f);
                    var now = wear != null ? wear.m_support : -1f;
                    if (waited >= 2 && Mathf.Abs(now - last) < 0.001f)
                    {
                        break;
                    }

                    last = now;
                }

                if (wear == null)
                {
                    broke = k;
                }
            }

            var predicted = Support.Solve(scene);
            var wrong = new List<string>();
            for (var k = 0; k < built.Count; k++)
            {
                var alive = built[k] != null;
                predicted.TryGet(k, out var mine, out _);
                var falls = predicted.Falls(k);
                text.AppendLine($"{k + 1} | {(alive ? built[k].m_support.ToString("0.00") : "fell")} | {(falls ? "falls" : mine.ToString("0.00"))}");
                if (alive == falls || (alive && Mathf.Abs(built[k].m_support - mine) > 0.01f))
                {
                    wrong.Add($"pole {k + 1}: game {(alive ? built[k].m_support.ToString("0.00") : "fell")}, editor {(falls ? "falls" : mine.ToString("0.00"))}");
                }
            }

            Check(broke == 8 && wrong.Count == 0,
                $"built one pole at a time, the game breaks pole {broke + 1} and the editor has every pole's support to 0.01"
                + (wrong.Count > 0 ? " | " + string.Join(" | ", wrong) : ""));
        }

        private static float[] Snapshot(WearNTear[] built)
        {
            var values = new float[built.Length];
            for (var i = 0; i < built.Length; i++)
            {
                values[i] = built[i] != null ? built[i].m_support : -1f;
            }

            return values;
        }

        private static bool SameSnapshot(float[] a, float[] b)
        {
            for (var i = 0; i < a.Length; i++)
            {
                if (Mathf.Abs(a[i] - b[i]) > 0.001f)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The state API with no window: poles stacked one on the other with the placing rule until
        /// the editor refuses one, the problem list, and a group that holds itself up.
        /// </summary>
        private static IEnumerator SupportWithoutUi()
        {
            var pole = PieceCatalog.Find("wood_pole2");
            var floor = PieceCatalog.Find("wood_floor");
            Check(pole != null && floor != null && pole.Support != null,
                "the catalog has wood_pole2 and wood_floor, with their support numbers");
            if (pole == null || floor == null || pole.Support == null)
            {
                yield break;
            }

            Check(pole.Support.Max == 100f && pole.Support.Min == 10f && pole.Support.VerticalLoss == 0.125f
                && pole.Support.HorizontalLoss == 0.2f && pole.Support.Holds && pole.Support.CanFall,
                $"wood is read off the game: most {pole.Support.Max}, least {pole.Support.Min}, "
                + $"losses {pole.Support.HorizontalLoss} / {pole.Support.VerticalLoss}");

            var doc = BlueprintDocument.New("Support test");
            EditorState.Open(doc);
            EditorState.StartAdd(pole);

            // On open ground: full support, the game's light blue.
            var open = EditAim(new Vector3(6f, 0f, 0f));
            Check(open != null && open.Support != null && open.Support[0] >= 100f && !open.WouldFall,
                $"a pole on the ground has full support: {(open != null && open.Support != null ? open.Support[0] : -1f)}");
            Check(open != null && Support.ColorOf(open.Support[0], pole.Support) == Support.GroundColor,
                "and wears the game's light blue");

            // Stacked from above, each pole loses an eighth of its support per metre and a bit.
            var expected = 100f;
            var refusedAt = -1;
            for (var k = 1; k <= 10 && refusedAt < 0; k++)
            {
                var aimed = EditAim(Vector3.zero);
                if (aimed == null || aimed.Support == null)
                {
                    Check(false, $"pole {k}: the aim found no spot");
                    break;
                }

                var wantY = 1f + (2f * (k - 1));
                var landed = Mathf.Abs(aimed.Pos.y - wantY) < 0.01f;
                if (aimed.WouldFall)
                {
                    var count = doc.Pieces.Count;
                    Check(!EditorState.CommitPlacement(aimed) && doc.Pieces.Count == count,
                        $"pole {k} would have {expected:0.00} support, under 10: the click does nothing");
                    Check(EditorState.Message != null && EditorState.Message.Contains("fall down"),
                        $"and the editor says why: '{EditorState.Message}'");
                    refusedAt = k;
                    break;
                }

                var ok = landed && Mathf.Abs(aimed.Support[0] - expected) < 0.05f && EditorState.CommitPlacement(aimed);
                Check(ok, $"pole {k} lands at y {aimed.Pos.y:0.00} with support {aimed.Support[0]:0.00}, "
                    + $"wanted y {wantY:0.00} and {expected:0.00}");
                expected *= 1f - (0.125f * 2.1f);
            }

            Check(refusedAt == 9, $"the editor refuses the ninth pole, like the game: refused pole {refusedAt}");
            Check(!Checks.Run(doc).Any(c => c.Message.Contains("fall down")), "the eight that stand are not on the problem list");

            // A floor in the air: the problem list names it.
            EditorState.CancelMode();
            var air = doc.AddPiece("wood_floor", new Vector3(20f, 6f, 0f), Quaternion.identity);
            var rows = Checks.Run(doc);
            var row = rows.FirstOrDefault(c => c.Message.Contains("fall down"));
            Check(row != null && row.Level == CheckLevel.Warning && row.Pieces != null
                && row.Pieces.Length == 1 && row.Pieces[0] == air,
                $"a floor in the air is on the problem list: {(row != null ? row.Message : "(no row)")}");
            Check(EditorState.Stability.Falls(air) && EditorState.Stability.ColorOf(air) == Support.ColorOf(0f, floor.Support),
                "and the editor's own map has it falling, red");

            // A group that holds itself up can be moved into the air as long as it stands on the ground.
            EditorState.Select(doc.Pieces.Where(p => p.PrefabName == "wood_pole2").Take(3).Select(p => p.Id));
            EditorState.StartDuplicate();
            var copies = EditAim(new Vector3(-8f, 0f, 0f));
            Check(copies != null && !copies.WouldFall && copies.Support.Length == 3
                && copies.Support.All(v => v > 10f),
                $"three stacked poles in hand hold each other up: {(copies != null && copies.Support != null ? string.Join(", ", copies.Support.Select(v => v.ToString("0.0"))) : "-")}");
            EditorState.Close();

            // The standing pieces are solved again after every edit, so a big blueprint must not
            // stall the frame: a 20 x 20 floor on 441 poles, 841 pieces, each touching up to 8.
            var big = new List<ScenePiece>();
            for (var x = 0; x <= 20; x++)
            {
                for (var z = 0; z <= 20; z++)
                {
                    big.Add(new ScenePiece { Id = big.Count, Prefab = "wood_pole2", Pos = new Vector3(2f * x, 1f, 2f * z), Rot = Quaternion.identity, Entry = pole });
                    if (x < 20 && z < 20)
                    {
                        big.Add(new ScenePiece { Id = big.Count, Prefab = "wood_floor", Pos = new Vector3((2f * x) + 1f, 2f, (2f * z) + 1f), Rot = Quaternion.identity, Entry = floor });
                    }
                }
            }

            Support.Solve(big);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var solved = Support.Solve(big);
            var ms = watch.Elapsed.TotalMilliseconds;
            Check(ms < 250.0 && solved.Fallen.Count == 0,
                $"{big.Count} pieces are worked out in {ms:0} ms, none falls");
            yield return null;
        }

        /// <summary>
        /// The window: the ghost wears the support colour on the picture, blinks red where it would
        /// fall, and the piece under the aim takes the game's tint.
        /// </summary>
        private static IEnumerator SupportInTheWindow()
        {
            var pole = PieceCatalog.Find("wood_pole2");
            if (pole == null)
            {
                yield break;
            }

            var doc = BlueprintDocument.New("Support window");
            var stack = new List<NewPiece>();
            for (var k = 0; k < 8; k++)
            {
                stack.Add(new NewPiece { PrefabName = "wood_pole2", Position = new Vector3(0f, 1f + (2f * k), 0f), Rotation = Quaternion.identity });
            }

            doc.AddPieces(stack);
            var deepBefore = DeepObjects(out _);
            EditorSession.OpenDocument(doc);
            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(ModUi.Open && ViewportHost.Ready, "the editor opened on a stack of eight poles");
            ViewportHost.Capture();
            EditorState.StartAdd(pole);

            // Open ground: blue.
            ViewportHost.Camera.LookFrom(new Vector3(8f, 4f, -5f), new Vector3(8f, 0f, 0f));
            yield return WaitForAim(r => r.Pos.y < 1.5f && !r.WouldFall);
            var blue = EditorState.Aimed;
            Check(blue != null && !ViewportHost.Ghost.ShowingBad
                && ViewportHost.Ghost.ColorOf(0) == WithAlpha(Support.GroundColor, 0.6f),
                $"on open ground the ghost is the game's light blue: {ViewportHost.Ghost.ColorOf(0)}");
            yield return Screenshot("support-2-ghost-blue");
            var blueTint = GhostTint();
            Check(blueTint.b > blueTint.r, $"and the picture turns bluer where it stands: change {blueTint}");

            // The top of the stack: the ninth pole would fall.
            ViewportHost.Camera.LookFrom(new Vector3(0.5f, 22f, -4f), new Vector3(0f, 16f, 0f));
            yield return WaitForAim(r => r.WouldFall);
            var red = EditorState.Aimed;
            Check(red != null && red.WouldFall && ViewportHost.Ghost.ShowingBad,
                $"on top of the stack the ghost blinks red: would fall = {(red != null && red.WouldFall)}");
            yield return Screenshot("support-3-ghost-falls");
            var redTint = GhostTint();
            Check(redTint.r > redTint.b, $"and the picture turns redder where it stands: change {redTint}");
            var count = doc.Pieces.Count;
            if (red != null && ViewportHost.Raycast.Project(ViewportHost.Scene.Root.TransformPoint(red.Pos), out var at))
            {
                ViewportHost.ClickAt(at, false);
            }

            yield return null;
            Check(doc.Pieces.Count == count, $"a click there adds nothing: {doc.Pieces.Count} pieces");

            // Empty hands, the aim on the fourth pole: it takes the game's tint.
            EditorState.CancelMode();
            ViewportHost.Camera.LookFrom(new Vector3(0f, 7f, -6f), new Vector3(0f, 7f, 0f));
            var fourth = doc.Pieces[3].Id;
            waited = 0f;
            while (ViewportHost.AimPiece != fourth && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(ViewportHost.AimPiece == fourth && ViewportHost.Pieces.TintedId == fourth,
                $"the pole under the aim wears its support colour: aimed {ViewportHost.AimPiece}, tinted {ViewportHost.Pieces.TintedId}");
            yield return Screenshot("support-4-hover");

            ViewportHost.Release();
            EditorSession.Close();
            yield return null;
            yield return null;

            // The ghost is built hidden. Its copies once woke up after the guard was off and each
            // became a real piece, saved in the world 8000 m down: one per piece put in hand.
            var deepAfter = DeepObjects(out var sample);
            Check(deepAfter == deepBefore,
                $"the editor left nothing in the world under the player: {deepBefore} deep objects before, {deepAfter} after"
                + (deepAfter != deepBefore ? " | " + sample : ""));
        }

        /// <summary>World objects deep under the ground: where the editor's scene is. There must be none.</summary>
        private static int DeepObjects(out string sample)
        {
            var deep = ZDOMan.instance.m_objectsByID.Values.Where(z => z.GetPosition().y < -1000f).ToList();
            sample = string.Join(", ", deep.Take(5).Select(z =>
            {
                var prefab = ZNetScene.instance.GetPrefab(z.GetPrefab());
                return (prefab != null ? prefab.name : z.GetPrefab().ToString()) + " " + V(z.GetPosition());
            }));
            return deep.Count;
        }

        private static IEnumerator WaitForAim(Func<PlaceResult, bool> wanted)
        {
            var waited = 0f;
            while ((EditorState.Aimed == null || !wanted(EditorState.Aimed) || !ViewportHost.Ghost.Visible) && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return null;
        }

        /// <summary>
        /// How the ghost changes the picture: the pane with it, minus the pane without it, averaged
        /// over the pixels it covers. A light blue ghost pulls blue up, a red one red.
        /// </summary>
        private static Color GhostTint()
        {
            ViewportHost.Preview.Render();
            var with = SampleView("with the ghost");
            ViewportHost.Ghost.Hide();
            ViewportHost.Preview.Render();
            var without = SampleView("without the ghost");
            var sum = Vector3.zero;
            var covered = 0;
            for (var i = 0; i < with.Length && i < without.Length; i++)
            {
                var d = new Vector3(with[i].r - without[i].r, with[i].g - without[i].g, with[i].b - without[i].b);
                if (Mathf.Abs(d.x) + Mathf.Abs(d.y) + Mathf.Abs(d.z) > 0.03f)
                {
                    sum += d;
                    covered++;
                }
            }

            sum /= Mathf.Max(1, covered);
            Log($"the ghost covers {covered} of {with.Length} sampled pixels");
            return new Color(sum.x, sum.y, sum.z);
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        private static void WriteSupportStats(Player player)
        {
            var tool = player.GetBuildTool();
            var text = new StringBuilder();
            var byMaterial = new Dictionary<string, int>();
            int noWear = 0, noSupports = 0, noCheck = 0, comOffset = 0, forceCom = 0;
            int meshConcave = 0, meshConvex = 0, meshReadable = 0, inactive = 0, disabled = 0, childWear = 0;
            var concaveNames = new List<string>();
            foreach (var prefab in tool.m_pieces)
            {
                var wear = prefab.GetComponent<WearNTear>();
                var inChild = prefab.GetComponentInChildren<WearNTear>(true);
                if (wear == null && inChild != null)
                {
                    childWear++;
                }

                var line = new StringBuilder(prefab.name);
                if (wear == null)
                {
                    noWear++;
                    line.Append(" | no WearNTear");
                }
                else
                {
                    var m = wear.m_materialType.ToString();
                    byMaterial[m] = byMaterial.TryGetValue(m, out var n) ? n + 1 : 1;
                    if (!wear.m_supports) noSupports++;
                    if (!wear.m_noSupportWear) noCheck++;
                    if (wear.m_comOffset != Vector3.zero) comOffset++;
                    if (wear.m_forceCorrectCOMCalculation) forceCom++;
                    line.Append($" | {m} supports={wear.m_supports} check={wear.m_noSupportWear} com={V(wear.m_comOffset)} force={wear.m_forceCorrectCOMCalculation}");
                }

                foreach (var c in prefab.GetComponentsInChildren<Collider>(true))
                {
                    var active = true;
                    for (var t = c.transform; t != null; t = t.parent)
                    {
                        active &= t.gameObject.activeSelf;
                    }

                    if (!active) inactive++;
                    if (!c.enabled) disabled++;
                    var kind = c.GetType().Name.Replace("Collider", "");
                    if (c is MeshCollider mc)
                    {
                        if (mc.convex) meshConvex++;
                        else
                        {
                            meshConcave++;
                            if (!c.isTrigger && active && c.enabled) concaveNames.Add(prefab.name);
                        }

                        if (mc.sharedMesh != null && mc.sharedMesh.isReadable) meshReadable++;
                        kind += mc.convex ? "(convex)" : "(concave)";
                        kind += mc.sharedMesh != null ? $"[{mc.sharedMesh.name} r={mc.sharedMesh.isReadable} v={(mc.sharedMesh.isReadable ? mc.sharedMesh.vertexCount : -1)}]" : "[no mesh]";
                    }

                    line.Append($" | {kind} {LayerMask.LayerToName(c.gameObject.layer)}"
                        + $"{(c.isTrigger ? " trigger" : "")}{(!c.enabled ? " off" : "")}{(!active ? " inactive" : "")}"
                        + $"{(c.attachedRigidbody != null || c.GetComponentInParent<Rigidbody>(true) != null ? " rigidbody" : "")}");
                }

                text.AppendLine(line.ToString());
            }

            var summary = $"pieces={tool.m_pieces.Count} noWearNTear={noWear} wearInChildOnly={childWear} "
                + $"materials={string.Join(",", byMaterial.Select(p => p.Key + "=" + p.Value))} "
                + $"supportsFalse={noSupports} noSupportCheck={noCheck} comOffset={comOffset} forceCom={forceCom} "
                + $"meshConvex={meshConvex} meshConcave={meshConcave} meshReadable={meshReadable} "
                + $"collidersInactive={inactive} collidersDisabled={disabled}";
            text.Insert(0, summary + "\nconcave (solid, live): " + string.Join(" ", concaveNames.Distinct()) + "\n\n");
            var path = Path.Combine(OutDir, "support.txt");
            File.WriteAllText(path, text.ToString());
            Log(summary);
            Check(tool.m_pieces.Count > 0, $"support data of {tool.m_pieces.Count} pieces written to {path}");
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

            yield return SquareLikeB(player);
            BlueprintMode.Exit();
        }

        /// <summary>
        /// Square on the pad, in build mode, is the blueprint key: the same entries in the same order,
        /// then back to normal building. With L2 held it does not cycle: it opens the editor on the
        /// blueprint in hand.
        /// </summary>
        private static IEnumerator SquareLikeB(Player player)
        {
            BlueprintMode.Exit();
            yield return EquipHammer(player);
            var key = (UnityEngine.InputSystem.Key)Enum.Parse(typeof(UnityEngine.InputSystem.Key), ValheimTomrerPlugin.BlueprintKey.Value.ToString());
            var keys = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                yield return PressKey(key);
                keys.Add(BlueprintMode.EntryName ?? "off");
                if (!BlueprintMode.Active)
                {
                    break;
                }
            }

            WorldPadOn();
            var pads = new List<string>();
            for (var i = 0; i < 12; i++)
            {
                yield return PadPress(false, PadKey.West);
                pads.Add(BlueprintMode.EntryName ?? "off");
                if (!BlueprintMode.Active)
                {
                    break;
                }
            }

            Check(keys.Count >= 2 && keys.Last() == "off" && keys.SequenceEqual(pads),
                $"square cycles like {ValheimTomrerPlugin.BlueprintKey.Value}: {string.Join(" > ", keys)} | square: {string.Join(" > ", pads)}");

            yield return PadPress(false, PadKey.West);
            var inHand = BlueprintMode.Current;
            var entry = BlueprintMode.EntryName;
            var shows = _inventoryShows;
            yield return PadPress(true, PadKey.West);
            yield return new WaitForSeconds(0.5f);
            var document = EditorSession.Document;
            Check(inHand != null && ModUi.Open && document != null && document.Name == inHand.Name && !BlueprintMode.Active,
                $"L2 + square does not cycle: it opened the editor on '{(document != null ? document.Name : "nothing")}', the one in hand ('{entry}')");
            Check(_inventoryShows == shows && !player.IsSitting(), "and nothing else happened: no inventory, no sitting");
            EditorSession.Forget();
            yield return WorldPadOff();
            yield return new WaitForSeconds(0.3f);
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

            if (withRefusals)
            {
                // Materials never refuse a click any more: it builds what they pay for, here nothing,
                // keeps the plan and goes straight to Continue on it.
                var planAt = BlueprintMode.PreviewRoot;
                var planPos = planAt != null ? planAt.position : player.transform.position;
                var planRot = planAt != null ? planAt.rotation : Quaternion.identity;
                var before = PiecesAround(player, planPos).Count;
                var bagBefore = player.GetInventory().GetAllItems().Sum(i => i.m_stack);
                var sites = SiteStore.All.Count;
                Check(!BlueprintMode.TryBuild(player), "without materials the click builds nothing");
                Check(PiecesAround(player, planPos).Count == before
                    && player.GetInventory().GetAllItems().Sum(i => i.m_stack) == bagBefore,
                    "without materials nothing is built and nothing is taken");
                var said = BlueprintMode.LastMessage ?? "";
                var named = resolved.TotalCost.All(c => said.Contains(Localization.instance.Localize(c.m_resItem.m_itemData.m_shared.m_name)));
                Check(said.Contains("Missing") && named, $"and the message names what is missing: '{said}'");
                yield return CheckContinuesAfterClick(sites, "no materials");
                var plan = BlueprintMode.LastSite;
                Check(plan != null && Vector3.Distance(plan.RootPosition, planPos) < 0.001f && Quaternion.Angle(plan.RootRotation, planRot) < 0.1f,
                    "the plan stands where the preview stood");

                // Nothing of it stands, so one Remove forgets the plan with no window. Then the
                // blueprint again, for the turn and the build below.
                var opened = SiteRemovePopup.Opened;
                BlueprintMode.PressRemove(player);
                Check(plan != null && !SiteStore.All.Contains(plan) && !BlueprintMode.Active && plan.Ghost == null
                    && SiteRemovePopup.Opened == opened && !UnifiedPopup.IsVisible() && BlueprintMode.LastMessage == $"Plan for {kit.Name} removed.",
                    $"nothing built: one Remove forgets it at once, no window, blueprint mode off: '{BlueprintMode.LastMessage}'");
                BlueprintMode.Select(player, resolved);
                yield return AimAtGround(player);
                Check(BlueprintMode.HasTarget && BlueprintMode.Blocked == null, "the blueprint is in hand again: " + (BlueprintMode.Blocked ?? "ok"));
            }

            // Once, on the first kit: the turn, then the build below proves it reached the pieces.
            var turnedTo = BlueprintMode.RotationSteps;
            if (withRefusals)
            {
                yield return TestTurn(player);
                turnedTo = BlueprintMode.RotationSteps;
                yield return Screenshot(Slug(kit.Name) + "-1b-turned");
            }

            var root = BlueprintMode.PreviewRoot;
            var center = root != null ? root.position : player.transform.position;
            var rootRot = root != null ? root.rotation : Quaternion.identity;

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
            var turn = Quaternion.Euler(0f, turnedTo * BlueprintMode.RotationStep, 0f);
            var hadRoot = root != null;
            Check(BlueprintMode.TryBuild(player), "built with exact materials");
            var built = PiecesAround(player, center).Where(p => !existing.Contains(p)).ToList();
            Check(built.Count == resolved.Parts.Count, $"all pieces exist: {built.Count}/{resolved.Parts.Count}");

            // Every piece stands where the turned preview showed it, facing the turned way. The pose
            // was read before the click: a build in full ends blueprint mode and drops the preview.
            var off = hadRoot ? 0 : resolved.Parts.Count;
            foreach (var part in hadRoot ? resolved.Parts : new List<ResolvedPart>())
            {
                var position = center + (rootRot * part.Source.Position);
                var rotation = turn * part.Source.Rotation;
                if (!built.Any(p => Vector3.Distance(p.transform.position, position) < 0.05f
                    && Quaternion.Angle(p.transform.rotation, rotation) < 1f))
                {
                    off++;
                }
            }

            Check(hadRoot && Quaternion.Angle(rootRot, turn) < 0.01f && off == 0,
                $"built facing {turnedTo * BlueprintMode.RotationStep:0.#} degrees like the preview, pieces off: {off}");
            var left = resolved.TotalCost.Sum(c => player.GetInventory().CountItems(c.m_resItem.m_itemData.m_shared.m_name));
            Check(left == 0, $"materials taken (left over: {left})");
            yield return CheckBackToPiece(player, $"{kit.Name} built in full");

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
        /// The wheel and the pad turn the blueprint, through the game's own input and the real
        /// BlueprintMode.HandleInput: a scroll queued on the mouse, then a made-up controller
        /// added to the input system with L2 held and the right stick pushed. Ends one or two
        /// steps off the start, never on it, so the build after it proves the turn.
        /// </summary>
        private static IEnumerator TestTurn(Player player)
        {
            var start = BlueprintMode.RotationSteps;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null)
            {
                Check(false, "no mouse device to scroll");
                yield break;
            }

            UnityEngine.InputSystem.InputSystem.QueueDeltaStateEvent(mouse.scroll, new Vector2(0f, 1f));
            for (var frame = 0; frame < 4; frame++)
            {
                yield return null;
            }

            var root = BlueprintMode.PreviewRoot;
            var yaw = root != null ? root.eulerAngles.y : float.NaN;
            Check(BlueprintMode.RotationSteps == start + 1
                && Mathf.Abs(Mathf.DeltaAngle(yaw, (start + 1) * BlueprintMode.RotationStep)) < 0.01f,
                $"one wheel notch turns the preview one step: {start} -> {BlueprintMode.RotationSteps}, yaw {yaw:0.#}");

            // A controller of its own, so the test does not need one plugged in.
            var pad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>("AutoTestPad");
            var source = ZInput.m_inputSource;
            try
            {
                var left = new UnityEngine.InputSystem.LowLevel.GamepadState { leftTrigger = 1f, rightStick = new Vector2(-1f, 0f) };
                var right = new UnityEngine.InputSystem.LowLevel.GamepadState { leftTrigger = 1f, rightStick = new Vector2(1f, 0f) };

                // Held for 0.6 s: one step at once, then the game's repeat after a quarter second.
                var from = BlueprintMode.RotationSteps;
                var first = 0;
                var until = Time.time + 0.6f;
                var frames = 0;
                while (Time.time < until || frames < 4)
                {
                    UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad, left);
                    yield return null;
                    frames++;
                    if (first == 0 && BlueprintMode.RotationSteps != from)
                    {
                        first = BlueprintMode.RotationSteps - from;
                        if (!ZInput.IsGamepadActive())
                        {
                            Log("the game had not switched to the pad on its own");
                        }
                    }
                }

                var peak = BlueprintMode.RotationSteps;
                Log($"pad: layout={ZInput.InputLayout} gamepadActive={ZInput.IsGamepadActive()} "
                    + $"first={first} held 0.6 s: {from} -> {peak}");
                Check(first == 1 && peak - from >= 3,
                    $"L2 + right stick left turns one step at once, then repeats: {from} -> {peak}");

                // The other way, back down to one or two steps off the start.
                until = Time.time + 3f;
                while (BlueprintMode.RotationSteps > start + 2 && Time.time < until)
                {
                    UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad, right);
                    yield return null;
                }

                UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad, new UnityEngine.InputSystem.LowLevel.GamepadState());
                for (var frame = 0; frame < 4; frame++)
                {
                    yield return null;
                }

                var end = BlueprintMode.RotationSteps;
                Check(end < peak && end > start && end <= start + 2,
                    $"L2 + right stick right turns it back: {peak} -> {end} (start {start})");
            }
            finally
            {
                // Back to the mouse the game's own way, so its hints switch back too.
                UnityEngine.InputSystem.InputSystem.RemoveDevice(pad);
                ZInput.instance?.OnInput(source, true);
                Log($"input back to {ZInput.m_inputSource}, pad active: {ZInput.IsGamepadActive()}");
            }

            // Turned, the building can reach the player; aim again if it does.
            yield return null;
            if (!BlueprintMode.HasTarget || BlueprintMode.Blocked != null)
            {
                yield return AimAtGround(player);
            }
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

            yield return ProbeFocus();
        }

        /// <summary>
        /// What a focus walk over the panels would find: the input module the UI runs on, whether
        /// a pad competes with it, what the EventSystem has selected, how many widgets each region
        /// holds and in what order, and whether the canvas picks one by itself. Writes focus.txt.
        /// Opens and closes the editor window, changes nothing in the world.
        /// </summary>
        private static IEnumerator ProbeFocus()
        {
            var report = new StringBuilder();
            report.AppendLine($"ValheimTomrer focus probe | {DateTime.Now:yyyy-MM-dd HH:mm}");
            report.AppendLine();

            var events = UnityEngine.EventSystems.EventSystem.current;
            var module = events != null ? events.currentInputModule : null;
            report.AppendLine("input module: " + (module != null ? module.GetType().FullName : "<none>"));

            var pad = UnityEngine.InputSystem.Gamepad.current;
            report.AppendLine("Gamepad.current: " + (pad == null ? "null" : $"{pad.name} layout={pad.layout}"));
            report.AppendLine("PadReader.Fake: " + (PadReader.Fake == null ? "null" : "set"));
            report.AppendLine();

            // A blueprint with one piece in it, so "one piece selected" is a real state and the
            // selection panel's buttons can switch on.
            PieceCatalog.Ensure();
            var entry = PieceCatalog.Find("woodwall") ?? (PieceCatalog.All.Count > 0 ? PieceCatalog.All[0] : null);
            Check(entry != null, "focus probe found a piece for its blueprint");
            if (entry == null)
            {
                yield break;
            }

            var document = BlueprintDocument.New("focus probe");
            var id = document.AddPiece(entry.PrefabName, Vector3.zero, Quaternion.identity);
            EditorSession.OpenDocument(document);
            yield return null;
            yield return null;
            Check(ModUi.Open, "focus probe opened the editor window");

            report.AppendLine("EventSystem selection, window just open: " + Selection(events));
            PadBindings.Tick();
            yield return null;
            report.AppendLine("EventSystem selection, after one PadBindings.Tick: " + Selection(events));

            var handler = EditorWindow.Root != null ? EditorWindow.Root.GetComponent<UIGroupHandler>() : null;
            report.AppendLine("UIGroupHandler on the editor canvas: " + (handler == null
                ? "<none>"
                : $"priority={handler.m_groupPriority} m_defaultElement="
                    + (handler.m_defaultElement == null ? "null" : handler.m_defaultElement.name)));
            report.AppendLine();

            EditorState.Select(Array.Empty<int>());
            yield return null;
            yield return null;
            report.AppendLine($"nothing selected (selection={EditorState.SelectionCount}):");
            yield return ProbeRegions(report);

            EditorState.Select(id);
            yield return null;
            yield return null;
            report.AppendLine();
            report.AppendLine($"one piece selected (selection={EditorState.SelectionCount}):");
            yield return ProbeRegions(report);

            EditorSession.Close();
            yield return null;

            var focus = Path.Combine(OutDir, "focus.txt");
            File.WriteAllText(focus, report.ToString());
            Check(File.Exists(focus), "focus probe written to " + focus);
        }

        /// <summary>The four regions a walk would cover, with the left panel on each of its tabs.</summary>
        private static IEnumerator ProbeRegions(StringBuilder report)
        {
            ProbeWidgets(report, "TopBar", EditorWindow.TopBar);

            EditorWindow.SetLeftTab(0);
            yield return null;
            ProbeWidgets(report, "LeftPanel tab 0 (Pieces)", EditorWindow.LeftPanel);

            EditorWindow.SetLeftTab(1);
            yield return null;
            ProbeWidgets(report, "LeftPanel tab 1 (In blueprint)", EditorWindow.LeftPanel);

            EditorWindow.SetLeftTab(0);
            yield return null;
            ProbeWidgets(report, "RightPanel", EditorWindow.RightPanel);
        }

        /// <summary>
        /// Every Selectable in one region, in hierarchy order, with the ones a walk could land on
        /// first. The canvas is screen space overlay, so a widget's position is already in pixels.
        /// </summary>
        private static void ProbeWidgets(StringBuilder report, string name, RectTransform region)
        {
            if (region == null)
            {
                report.AppendLine($"  {name}: <missing>");
                return;
            }

            var all = region.GetComponentsInChildren<UnityEngine.UI.Selectable>(false);
            var live = all.Where(s => s.interactable).ToList();
            report.AppendLine($"  {name}: {live.Count} interactable of {all.Length} active");
            for (var i = 0; i < live.Count; i++)
            {
                var at = live[i].transform.position;
                report.AppendLine($"    {i}. {live[i].GetType().Name} '{live[i].name}' at ({at.x:0},{at.y:0})");
            }
        }

        private static string Selection(UnityEngine.EventSystems.EventSystem events)
        {
            var go = events != null ? events.currentSelectedGameObject : null;
            if (go == null)
            {
                return "null";
            }

            var parent = go.transform.parent;
            return (parent != null ? parent.name + "/" : "") + go.name;
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

                // What the game's UI module sends as Submit and Cancel to the selected widget. A
                // text box takes Submit as its own submit, whatever key or button sent it.
                if (module is UnityEngine.InputSystem.UI.InputSystemUIInputModule ui)
                {
                    foreach (var (label, reference) in new[] { ("submit", ui.submit), ("cancel", ui.cancel), ("move", ui.move) })
                    {
                        var action = reference != null ? reference.action : null;
                        report.AppendLine($"  ui {label}: " + (action == null
                            ? "<none>"
                            : string.Join(", ", action.bindings.Select(b => b.effectivePath))));
                    }
                }
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

        // ---------- scenario: probe_build ----------

        /// <summary>
        /// Measures what building from chests, partial builds and the materials list rest on, so no
        /// later phase leans on a guess. Writes probe-build.txt: one line per question, details
        /// indented under it. It places chests, a cart, a karve and floors, and removes them all.
        /// Checks only prove the probe itself ran; the answers are facts, not pass or fail.
        /// </summary>
        private static IEnumerator ProbeBuild(Player player)
        {
            var report = new StringBuilder();
            report.AppendLine($"ValheimTomrer build probe | Unity {Application.unityVersion} | {DateTime.Now:yyyy-MM-dd HH:mm}");
            report.AppendLine();
            var path = Path.Combine(OutDir, "probe-build.txt");
            void Save() => File.WriteAllText(path, report.ToString());

            ProbeGameSource(report);
            report.AppendLine("baseline | run outside the game before any change: "
                + "./scripts/autotest.sh editor_capture and blueprints (numbers in .claude/handoff/build-sites.md)");
            Save();

            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            yield return null;
            yield return null;
            PieceCatalog.Ensure();
            Check(PieceCatalog.Ready, "the piece catalog is built");

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop")
                ?? BlueprintLibrary.All.FirstOrDefault(b => b.Pieces.Count > 2);
            ResolvedBlueprint resolved = null;
            var error = "no kit";
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out error))
            {
                Check(false, "the workshop kit resolves: " + error);
                Save();
                yield break;
            }

            ProbeGround(report, player.transform.position);
            Save();
            ProbeSupportTimes(report, resolved);
            Save();
            yield return ProbeCardAndGhost(report, player, resolved);
            Save();
            yield return ProbeChests(report, player);
            Save();
            yield return ProbeGlow(report, player);
            Save();

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(1f);
            var left = new List<Piece>();
            Piece.GetAllPiecesInRadius(player.transform.position, 60f, left);
            var mine = left.Count(p => p != null && p.GetCreator() == player.GetPlayerID());
            Check(mine == 0, $"the probe left nothing standing: {mine} of its pieces remain");
            Check(File.Exists(path), "build probe written to " + path);
        }

        /// <summary>Whether the decompiled game is where later phases will look for it.</summary>
        private static void ProbeGameSource(StringBuilder report)
        {
            const string folder = "/tmp/valheim-src";
            var present = Directory.Exists(folder);
            var files = present ? Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Length : 0;
            report.AppendLine($"game source | {folder} {(present ? "present" : "missing")}, {files} .cs files");
            Log($"game source: {folder} {(present ? "present" : "missing")}, {files} .cs files");
        }

        /// <summary>What one ground-height call costs, and how flat the levelled build spot really is.</summary>
        private static void ProbeGround(StringBuilder report, Vector3 centre)
        {
            var zones = ZoneSystem.instance;
            zones.GetGroundHeight(centre); // the first ray can pay for set-up
            const int calls = 2000;
            var sum = 0f;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < calls; i++)
            {
                var x = ((i % 40) * 0.6f) - 12f;
                var z = ((i / 40) * 0.48f) - 12f;
                sum += zones.GetGroundHeight(new Vector3(centre.x + x, centre.y, centre.z + z));
            }

            var micros = watch.Elapsed.TotalMilliseconds * 1000.0 / calls;

            var misses = 0;
            float Spread(float reach)
            {
                var min = float.MaxValue;
                var max = float.MinValue;
                for (var x = -reach; x <= reach; x += 1f)
                {
                    for (var z = -reach; z <= reach; z += 1f)
                    {
                        if (!zones.GetGroundHeight(new Vector3(centre.x + x, centre.y, centre.z + z), out var h))
                        {
                            misses++;
                            continue;
                        }

                        min = Mathf.Min(min, h);
                        max = Mathf.Max(max, h);
                    }
                }

                return max >= min ? max - min : 0f;
            }

            var near = Spread(8f);
            var wide = Spread(12f);
            var edge = Spread(16f);
            report.AppendLine($"ground height | GetGroundHeight {micros:0.0} us a call ({calls} calls, mean height {sum / calls:0.00});"
                + $" spread over the levelled spot: {near:0.000} m within 8 m, {wide:0.000} m within 12 m,"
                + $" {edge:0.000} m within 16 m (levelled to 14 m); {misses} misses");
            Check(misses == 0, $"ground height: {micros:0.0} us a call, spread {near:0.000} m within 8 m, {misses} misses");
        }

        /// <summary>
        /// The cost of the support rule for Phase 3's pass loop: one Solve of the kit, one of the kit
        /// repeated 5 x 5 (400 pieces), and one Evaluate of the top piece against the rest of each.
        /// All in blueprint space, nothing in the world.
        /// </summary>
        private static void ProbeSupportTimes(StringBuilder report, ResolvedBlueprint kit)
        {
            var one = KitScene(kit, 1, 0f);
            var grid = KitScene(kit, 5, 8f);
            var oneSolve = TimeSolve(one, 20, out var oneMap, out var oneFirst);
            var gridSolve = TimeSolve(grid, 10, out var gridMap, out var gridFirst);
            var oneEval = TimeEvaluate(one, 200, out var oneTop, out var oneFalls, out var oneValue);
            var gridEval = TimeEvaluate(grid, 200, out var gridTop, out var gridFalls, out var gridValue);

            report.AppendLine($"support | Solve: kit ({one.Count} pieces) {oneSolve:0.00} ms (first call {oneFirst:0.00}),"
                + $" grid ({grid.Count} pieces) {gridSolve:0.00} ms (first call {gridFirst:0.00});"
                + $" Evaluate one piece: {oneEval:0.000} ms against the kit, {gridEval:0.000} ms against the grid");
            report.AppendLine($"  pieces the model says fall with everything built: kit {oneMap.Fallen.Count}, grid {gridMap.Fallen.Count}");
            report.AppendLine($"  the candidate: kit {oneTop} -> {(oneFalls ? "falls" : oneValue.ToString("0.00"))},"
                + $" grid {gridTop} -> {(gridFalls ? "falls" : gridValue.ToString("0.00"))}");
            Check(one.Count == kit.Parts.Count && grid.Count == kit.Parts.Count * 25,
                $"support: kit {oneSolve:0.00} ms, {grid.Count} pieces {gridSolve:0.00} ms, Evaluate {oneEval:0.000} / {gridEval:0.000} ms");
        }

        /// <summary>The kit, copied n x n times with a gap, as the editor's scene pieces.</summary>
        private static List<ScenePiece> KitScene(ResolvedBlueprint kit, int n, float spacing)
        {
            var scene = new List<ScenePiece>();
            for (var gx = 0; gx < n; gx++)
            {
                for (var gz = 0; gz < n; gz++)
                {
                    var offset = new Vector3((gx - ((n - 1) * 0.5f)) * spacing, 0f, (gz - ((n - 1) * 0.5f)) * spacing);
                    foreach (var part in kit.Parts)
                    {
                        scene.Add(new ScenePiece
                        {
                            Id = scene.Count,
                            Prefab = part.Prefab.name,
                            Pos = part.Source.Position + offset,
                            Rot = part.Source.Rotation,
                            Entry = PieceCatalog.Find(part.Prefab.name),
                        });
                    }
                }
            }

            return scene;
        }

        private static double TimeSolve(List<ScenePiece> scene, int runs, out SupportMap map, out double first)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            map = Support.Solve(scene);
            first = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (var i = 0; i < runs; i++)
            {
                map = Support.Solve(scene);
            }

            return watch.Elapsed.TotalMilliseconds / runs;
        }

        /// <summary>The highest piece (the last one on a tie) against a map solved without it.</summary>
        private static double TimeEvaluate(List<ScenePiece> scene, int runs, out string top, out bool falls, out float value)
        {
            var candidate = scene[0];
            foreach (var piece in scene)
            {
                if (piece.Pos.y >= candidate.Pos.y)
                {
                    candidate = piece;
                }
            }

            var map = Support.Solve(scene.Where(p => p != candidate).ToList());
            var placed = new List<PlacedPiece>
            {
                new PlacedPiece { Id = candidate.Id, Prefab = candidate.Prefab, Pos = candidate.Pos, Rot = candidate.Rot, Entry = candidate.Entry },
            };
            var values = new float[1];
            var fallen = new bool[1];
            Support.Evaluate(map, placed, values, fallen);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < runs; i++)
            {
                Support.Evaluate(map, placed, values, fallen);
            }

            top = $"{candidate.Prefab} at {V(candidate.Pos)}";
            falls = fallen[0];
            value = values[0];
            return watch.Elapsed.TotalMilliseconds / runs;
        }

        /// <summary>The kit in the hammer: its card, then whether its see-through workbench counts as a real one.</summary>
        private static IEnumerator ProbeCardAndGhost(StringBuilder report, Player player, ResolvedBlueprint kit)
        {
            foreach (var part in kit.Parts)
            {
                player.m_knownRecipes.Add(part.Piece.m_name);
            }

            player.UpdateAvailablePiecesList();
            yield return EquipHammer(player);
            BlueprintMode.Select(player, kit);
            yield return AimAtGround(player);

            // Hud.Update fills the card, and the copies' CraftingStation.Start runs a frame after they are made.
            for (var frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            ProbeCard(report);
            ProbeGhostStation(report, player);
            yield return Screenshot("probe-build-1-card");
            yield return ProbeEditorStation(report, player, kit);
            BlueprintMode.Exit();
            yield return null;
        }

        /// <summary>The build card: where the requirement slots sit, so Phase 6 can put its list under it.</summary>
        private static void ProbeCard(StringBuilder report)
        {
            var hud = Hud.instance;
            var slots = hud != null ? hud.m_requirementItems : null;
            if (slots == null || slots.Length == 0 || slots[0] == null)
            {
                report.AppendLine("card | no requirement slots on the HUD");
                Check(false, "the build card has requirement slots");
                return;
            }

            var slot = slots[0].transform;
            var slotParent = slot.parent;
            var layout = slotParent != null ? slotParent.GetComponent<UnityEngine.UI.LayoutGroup>() : null;
            var canvas = slot.GetComponentInParent<Canvas>();
            var rootCanvas = canvas != null ? canvas.rootCanvas : null;

            // The card is the ancestor right under the build HUD.
            Transform card = null;
            var buildHud = hud.m_buildHud != null ? hud.m_buildHud.transform : null;
            for (var t = slot; t != null; t = t.parent)
            {
                if (t.parent == buildHud)
                {
                    card = t;
                    break;
                }
            }

            var cardFrom = card != null ? "the child of m_buildHud" : "the slots' grandparent (m_buildHud is not above the slots)";
            card = card ?? (slotParent != null ? slotParent.parent : null);
            var cardRect = card as RectTransform;
            var active = slots.Count(s => s != null && s.activeInHierarchy);
            report.AppendLine($"card | {slots.Length} slots ({active} on now); card root '{(card != null ? card.name : "none")}' ({cardFrom})"
                + (cardRect != null ? $" size {V2(cardRect.rect.size)} anchors {V2(cardRect.anchorMin)}-{V2(cardRect.anchorMax)}"
                    + $" pivot {V2(cardRect.pivot)} pos {V2(cardRect.anchoredPosition)}" : "")
                + $"; slots' parent '{(slotParent != null ? slotParent.name : "none")}' layout group:"
                + $" {(layout != null ? layout.GetType().Name : "none")}; canvas scale {(rootCanvas != null ? rootCanvas.scaleFactor : 0f):0.###}");

            var chain = new List<string>();
            for (var t = slot; t != null; t = t.parent)
            {
                chain.Add(t.name);
                if (rootCanvas != null && t == rootCanvas.transform)
                {
                    break;
                }
            }

            report.AppendLine("  path: " + string.Join(" < ", chain));
            for (var t = slot; t != null; t = t.parent)
            {
                report.AppendLine("  " + DescribeRect(t));
                if (rootCanvas != null && t == rootCanvas.transform)
                {
                    break;
                }
            }

            for (var i = 0; i < slots.Length; i++)
            {
                var rect = slots[i] != null ? slots[i].transform as RectTransform : null;
                if (rect != null)
                {
                    report.AppendLine($"  slot {i} '{rect.name}' on={slots[i].activeSelf} pos {V2(rect.anchoredPosition)} size {V2(rect.rect.size)}"
                        + $" parent '{rect.parent.name}'");
                }
            }

            if (cardRect != null)
            {
                var corners = new Vector3[4];
                cardRect.GetWorldCorners(corners);
                report.AppendLine($"  card on screen (pixels, overlay canvas): bottom left {V2(corners[0])} top right {V2(corners[2])};"
                    + $" screen {Screen.width}x{Screen.height}");
            }

            var scaler = rootCanvas != null ? rootCanvas.GetComponent<UnityEngine.UI.CanvasScaler>() : null;
            if (rootCanvas != null)
            {
                report.AppendLine($"  canvas '{rootCanvas.name}' mode {rootCanvas.renderMode} scale {rootCanvas.scaleFactor:0.####}"
                    + (scaler != null ? $" | scaler {scaler.uiScaleMode} ref {V2(scaler.referenceResolution)} match {scaler.matchWidthOrHeight:0.##}"
                        + $" pixels per unit {scaler.referencePixelsPerUnit:0}" : " | no CanvasScaler"));
            }

            Check(active > 0 && card != null, $"the build card: {slots.Length} slots, root '{(card != null ? card.name : "none")}'");
        }

        private static string DescribeRect(Transform t)
        {
            var groups = t.GetComponents<UnityEngine.UI.LayoutGroup>().Select(g => g.GetType().Name).ToList();
            if (t.GetComponent<UnityEngine.UI.ContentSizeFitter>() != null)
            {
                groups.Add("ContentSizeFitter");
            }

            if (t.GetComponent<UnityEngine.UI.LayoutElement>() != null)
            {
                groups.Add("LayoutElement");
            }

            var extra = groups.Count > 0 ? " [" + string.Join(", ", groups) + "]" : "";
            if (!(t is RectTransform rect))
            {
                return $"'{t.name}' (no RectTransform){extra}";
            }

            return $"'{t.name}' on={t.gameObject.activeSelf} size {V2(rect.rect.size)} anchors {V2(rect.anchorMin)}-{V2(rect.anchorMax)}"
                + $" pivot {V2(rect.pivot)} pos {V2(rect.anchoredPosition)} scale {rect.localScale.x:0.##}{extra}";
        }

        /// <summary>
        /// The hammer preview holds a see-through workbench. Its ZNetView removes itself, and
        /// CraftingStation.Start lists any station without one, so it may count as a real workbench.
        /// </summary>
        private static void ProbeGhostStation(StringBuilder report, Player player)
        {
            var root = BlueprintMode.PreviewRoot;
            if (root == null)
            {
                report.AppendLine("ghost workbench | no hammer preview to test");
                Check(false, "the kit's hammer preview is up");
                return;
            }

            var copies = root.GetComponentsInChildren<CraftingStation>(true);
            var listed = CraftingStation.m_allStations.Count(s => s != null && s.transform.IsChildOf(root));
            var noCircle = copies.Count(c => c.m_areaMarker != null && c.m_areaMarkerCircle == null);
            var real = CraftingStation.m_allStations.Count(s => s != null && !s.transform.IsChildOf(root)
                && s.m_name == "$piece_workbench" && s.transform.position.y > -1000f
                && Flat(s.transform.position, root.position) < 50f);
            var found = StationInRange("$piece_workbench", root.position, out var throws, out var why);
            var fromGhost = found != null && found.transform.IsChildOf(root);
            var atPlayer = StationInRange("$piece_workbench", player.transform.position, out var playerThrows, out _);
            report.AppendLine($"ghost workbench | {(fromGhost ? "YES" : "no")}: HaveBuildStationInRange(\"$piece_workbench\", preview) returns "
                + $"{(found == null ? "null" : found.name + (fromGhost ? " (the preview's own copy)" : " (a real one)"))};"
                + $" real workbenches within 50 m: {real}; preview station copies: {copies.Length}"
                + $" (enabled {copies.Count(c => c.enabled)}), listed in m_allStations: {listed};"
                + $" at the player: {(atPlayer == null ? "null" : atPlayer.transform.IsChildOf(root) ? "the preview's copy" : "a real one")}");
            report.AppendLine($"  the call threw {throws} times before it answered ({why ?? "no error"}), {playerThrows} more at the player;"
                + $" preview copies with an area marker but no CircleProjector (stripped by BlueprintPreview): {noCircle}");
            Check(real == 0 && copies.Length > 0, $"ghost workbench test set up: {copies.Length} copies in the preview, {real} real ones within 50 m");
            Log($"ghost workbench counts as real: {fromGhost}");
        }

        /// <summary>
        /// The editor's own solid model is made by the same builder, 8000 m down. The station range
        /// check ignores height, so a copy there may count for a player standing over it.
        /// </summary>
        private static IEnumerator ProbeEditorStation(StringBuilder report, Player player, ResolvedBlueprint kit)
        {
            EditorSession.Open(kit);
            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            var ready = ViewportHost.Ready;
            for (var frame = 0; frame < 3; frame++)
            {
                yield return null;
            }

            List<CraftingStation> Deep() => CraftingStation.m_allStations
                .Where(s => s != null && s.transform.position.y < -1000f).ToList();

            var threw = 0;
            bool CountsOver(CraftingStation deep)
            {
                var over = new Vector3(deep.transform.position.x, player.transform.position.y, deep.transform.position.z);
                var hit = StationInRange(deep.m_name, over, out var throws, out _);
                threw += throws;
                return hit != null && hit.transform.position.y < -1000f;
            }

            var open = Deep();
            var openCounts = open.Count > 0 && CountsOver(open[0]);
            var where = open.Count > 0 ? V(open[0].transform.position) : "none";

            EditorSession.Close();
            BlueprintMode.Exit();
            for (var frame = 0; frame < 3; frame++)
            {
                yield return null;
            }

            var asleep = Deep();
            var asleepCounts = asleep.Count > 0 && CountsOver(asleep[0]);

            EditorSession.Forget();
            for (var frame = 0; frame < 3; frame++)
            {
                yield return null;
            }

            var forgotten = Deep();
            report.AppendLine($"  editor model: ready={ready}; {open.Count} station copies listed 8000 m down while open (first at {where}),"
                + $" counts for a player standing over it: {(openCounts ? "YES" : "no")};"
                + $" after closing (scene asleep): {asleep.Count} listed, counts: {(asleepCounts ? "YES" : "no")};"
                + $" after Forget: {forgotten.Count} listed; the range call threw {threw} times on the way");
            Log($"editor model stations: open {open.Count} (counts {openCounts}), asleep {asleep.Count} (counts {asleepCounts}), forgotten {forgotten.Count}");
        }

        /// <summary>
        /// HaveBuildStationInRange, but a copy that throws is counted instead of ending the probe. A
        /// throw resets that copy's timer first, so asking again gets past it.
        /// </summary>
        private static CraftingStation StationInRange(string name, Vector3 point, out int throws, out string why)
        {
            throws = 0;
            why = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    return CraftingStation.HaveBuildStationInRange(name, point);
                }
                catch (Exception e)
                {
                    throws++;
                    why = $"{e.GetType().Name} in CraftingStation.{(e.TargetSite != null ? e.TargetSite.Name : "?")}";
                }
            }

            return null;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            return new Vector2(a.x - b.x, a.z - b.z).magnitude;
        }

        /// <summary>
        /// Ten wood chests, a cart and a karve within 20 m: does the piece list find their
        /// containers, which need a look into the children, and does taking an item out of a chest
        /// reach its saved items at once.
        /// </summary>
        private static IEnumerator ProbeChests(StringBuilder report, Player player)
        {
            var centre = player.transform.position;
            var before = new HashSet<Piece>(PiecesAround(player, centre));
            var chest = PiecePrefab("piece_chest_wood");
            var cart = PiecePrefab("Cart");
            var karve = PiecePrefab("Karve");
            if (chest == null || cart == null || karve == null)
            {
                report.AppendLine($"chest finder | a prefab is missing: chest={chest != null} cart={cart != null} karve={karve != null}");
                Check(false, "the chest, cart and karve prefabs exist");
                yield break;
            }

            for (var i = 0; i < 10; i++)
            {
                var angle = i * 36f;
                player.PlacePiece(chest, OnGround(centre, angle, 10f), Quaternion.Euler(0f, angle + 180f, 0f), false);
            }

            player.PlacePiece(cart, OnGround(centre, 18f, 14f) + (Vector3.up * 0.3f), Quaternion.Euler(0f, 108f, 0f), false);
            player.PlacePiece(karve, OnGround(centre, 198f, 16f) + (Vector3.up * 0.5f), Quaternion.Euler(0f, 288f, 0f), false);
            yield return new WaitForSeconds(2f);

            var placed = PiecesAround(player, centre).Where(p => !before.Contains(p)).ToList();
            Check(placed.Count == 12, $"placed 10 chests, a cart and a karve: {placed.Count} new pieces");

            // One search as Phase 2 would run it, timed over many runs.
            var inRange = new List<Piece>();
            var hits = 0;
            Piece.GetAllPiecesInRadius(centre, 20f, inRange);
            const int runs = 200;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var r = 0; r < runs; r++)
            {
                inRange.Clear();
                Piece.GetAllPiecesInRadius(centre, 20f, inRange);
                foreach (var piece in inRange)
                {
                    var box = piece.GetComponent<Container>() ?? piece.GetComponentInChildren<Container>();
                    if (box != null)
                    {
                        hits++;
                    }
                }
            }

            var ms = watch.Elapsed.TotalMilliseconds / runs;

            var direct = 0;
            var children = new List<string>();
            var missed = new List<string>();
            var detail = new StringBuilder();
            foreach (var piece in placed)
            {
                var own = piece.GetComponent<Container>();
                var child = own == null ? piece.GetComponentInChildren<Container>() : null;
                var hidden = own == null && child == null ? piece.GetComponentInChildren<Container>(true) : null;
                var inside = inRange.Contains(piece);
                var box = own ?? child;
                if (inside && own != null)
                {
                    direct++;
                }
                else if (inside && child != null)
                {
                    children.Add(piece.name);
                }
                else
                {
                    missed.Add(piece.name + (inside ? "" : " (out of range)") + (hidden != null ? " (container switched off)" : ""));
                }

                var access = box != null ? box.CheckAccess(player.GetPlayerID()).ToString() : "-";
                detail.AppendLine($"  {piece.gameObject.name.Replace("(Clone)", "")} at {Vector3.Distance(centre, piece.transform.position):0.0} m:"
                    + $" {(own != null ? "own Container" : child != null ? "Container on child '" + child.name + "'" : "no Container")}"
                    + (box != null ? $" '{box.m_name}' {box.m_width}x{box.m_height} guard={box.m_checkGuardStone} access={access}"
                        + $" inventory={(box.GetInventory() != null)}" : ""));
            }

            report.AppendLine($"chest finder | found {direct + children.Count} of {placed.Count} placed (10 chests, a cart, a karve) within 20 m;"
                + $" with GetComponent: {direct}; needed InChildren: {(children.Count > 0 ? string.Join(", ", children.Select(n => n.Replace("(Clone)", ""))) : "none")};"
                + $" missed: {(missed.Count > 0 ? string.Join(", ", missed) : "none")};"
                + $" one search {ms:0.000} ms ({inRange.Count} pieces within 20 m of {Piece.s_allPieces.Count} loaded, {hits / runs} containers)");
            report.Append(detail);
            Log($"chest finder: {direct} direct, {children.Count} in children, {missed.Count} missed, {ms:0.000} ms");

            yield return ProbeChestSave(report, placed);

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(1f);
            Check(placed.All(p => p == null), "every chest, the cart and the karve are removed again");
        }

        /// <summary>Takes wood out of a chest the game's way and watches its saved items.</summary>
        private static IEnumerator ProbeChestSave(StringBuilder report, List<Piece> placed)
        {
            var box = placed.Select(p => p != null ? p.GetComponent<Container>() : null).FirstOrDefault(c => c != null);
            var zdo = box != null && box.m_nview != null ? box.m_nview.GetZDO() : null;
            if (zdo == null)
            {
                report.AppendLine("chest keeps what we took | no chest with a ZDO to test");
                Check(false, "a placed chest has a ZDO");
                yield break;
            }

            var wood = ObjectDB.instance.GetItemPrefab("Wood").GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            var inventory = box.GetInventory();
            inventory.AddItem("Wood", 20, 1, 0, 0L, "", false);
            yield return null;

            var start = zdo.GetByteArray(ZDOVars.s_items);
            var startRevision = zdo.DataRevision;
            inventory.RemoveItem(wood, 5);
            var sameFrame = zdo.GetByteArray(ZDOVars.s_items);
            var sameRevision = zdo.DataRevision;
            yield return null;
            var nextFrame = zdo.GetByteArray(ZDOVars.s_items);
            yield return new WaitForSeconds(1f);
            var later = zdo.GetByteArray(ZDOVars.s_items);

            var when = !SameBytes(start, sameFrame) ? "in the same frame"
                : !SameBytes(start, nextFrame) ? "one frame later"
                : !SameBytes(start, later) ? "within 1 s"
                : "not within 1 s";
            var inSave = WoodIn(sameFrame, box, wood);
            report.AppendLine($"chest keeps what we took | the ZDO items field changed {when}: wood in it {WoodIn(start, box, wood)} -> {inSave}"
                + $" (the chest's inventory says {inventory.CountItems(wood)}), data revision {startRevision} -> {sameRevision};"
                + $" the field is a byte array ({(sameFrame != null ? sameFrame.Length : 0)} bytes), not a string");
            Check(inSave >= 0, $"the chest's saved items read back: {inSave} wood, changed {when}");
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }

            return a.SequenceEqual(b);
        }

        /// <summary>How much wood a chest's saved items hold, read back through the game's own loader.</summary>
        private static int WoodIn(byte[] bytes, Container box, string wood)
        {
            if (bytes == null)
            {
                return -1;
            }

            var copy = new Inventory("probe", null, box.m_width, box.m_height);
            copy.Load(new ZPackage(bytes));
            return copy.CountItems(wood);
        }

        private static Piece PiecePrefab(string name)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            return prefab != null ? prefab.GetComponent<Piece>() : null;
        }

        private static Vector3 OnGround(Vector3 centre, float angle, float distance)
        {
            var spot = centre + (Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance);
            spot.y = ZoneSystem.instance.GetGroundHeight(spot);
            return spot;
        }

        /// <summary>
        /// Phase 1's refresh budget: 100 world pieces glow through MaterialMan the way
        /// WearNTear.Highlight does it, then go back. MaterialMan applies in its own Update, so that
        /// is called by hand to time the whole thing.
        /// </summary>
        private static IEnumerator ProbeGlow(StringBuilder report, Player player)
        {
            var centre = player.transform.position;
            var before = new HashSet<Piece>(PiecesAround(player, centre));
            var floor = PiecePrefab("wood_floor");
            if (floor == null)
            {
                report.AppendLine("glow | no wood_floor prefab");
                Check(false, "the wood_floor prefab exists");
                yield break;
            }

            for (var x = 0; x < 10; x++)
            {
                for (var z = 0; z < 10; z++)
                {
                    var spot = centre + new Vector3((x * 2f) - 9f, 0f, (z * 2f) - 9f);
                    spot.y = ZoneSystem.instance.GetGroundHeight(spot);
                    player.PlacePiece(floor, spot, Quaternion.identity, false);
                }
            }

            yield return new WaitForSeconds(1f);
            var pieces = PiecesAround(player, centre).Where(p => !before.Contains(p)).Select(p => p.gameObject).ToList();
            Check(pieces.Count == 100, $"placed 100 floors to glow: {pieces.Count}");

            var man = MaterialMan.instance;
            var color = new Color(1f, 0.9f, 0.12f);
            var emission = color * 0.35f;

            double Glow()
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                foreach (var go in pieces)
                {
                    man.SetValue(go, ShaderProps._EmissionColor, emission);
                    man.SetValue(go, ShaderProps._Color, color);
                }

                return watch.Elapsed.TotalMilliseconds;
            }

            double Unglow()
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                foreach (var go in pieces)
                {
                    man.ResetValue(go, ShaderProps._Color);
                    man.ResetValue(go, ShaderProps._EmissionColor);
                }

                return watch.Elapsed.TotalMilliseconds;
            }

            double Apply()
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                man.Update();
                return watch.Elapsed.TotalMilliseconds;
            }

            var firstSet = Glow();
            var firstApply = Apply();
            var lit = pieces.Count(go => HasColor(go, color));
            yield return Screenshot("probe-build-2-glow");

            var resetSet = Unglow();
            var resetApply = Apply();
            var clean = pieces.Count(go => !HasColor(go, color));

            var againSet = Glow();
            var againApply = Apply();
            var againReset = Unglow() + Apply();

            report.AppendLine($"glow | {pieces.Count} pieces: first glow {firstSet + firstApply:0.00} ms ({firstSet:0.00} to set, {firstApply:0.00} to apply),"
                + $" again {againSet + againApply:0.00} ms, reset {resetSet + resetApply:0.00} ms (again {againReset:0.00});"
                + $" the colour reached {lit} of them, the reset cleared {clean}");
            Check(lit == pieces.Count && clean == pieces.Count,
                $"glow on {pieces.Count} pieces took {firstSet + firstApply:0.00} ms and came off again: lit {lit}, clean {clean}");

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>True when the first renderer of a piece carries this colour in its property block.</summary>
        private static bool HasColor(GameObject go, Color color)
        {
            var renderer = go != null ? go.GetComponentInChildren<MeshRenderer>() : null;
            if (renderer == null)
            {
                return false;
            }

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            if (block.isEmpty)
            {
                return false;
            }

            var got = block.GetColor(ShaderProps._Color);
            return Mathf.Abs(got.r - color.r) < 0.01f && Mathf.Abs(got.g - color.g) < 0.01f && Mathf.Abs(got.b - color.b) < 0.01f;
        }

        private static string V2(Vector2 v)
        {
            return $"({v.x:0.#},{v.y:0.#})";
        }

        // ---------- scenario: build_sources ----------

        /// <summary>Kept in the 15 m chest on top of its share. What is left then shows the take order.</summary>
        private const int SourcesSpare = 3;

        /// <summary>
        /// The hammer pays from the inventory and the chests in range, nearest first. Three wood
        /// chests behind the player at 5, 15 and 30 m. The kit's cost is split 40 % in the inventory,
        /// 40 % in the 5 m chest and 20 % in the 15 m chest (plus a spare), and the 30 m chest holds a
        /// whole second copy that must never be touched.
        /// </summary>
        private static IEnumerator TestBuildSources(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(0.5f);

            Check(BuildConfig.UseChests != null && BuildConfig.UseChests.Value
                && BuildConfig.ChestRange != null && Mathf.Approximately(BuildConfig.ChestRange.Value, 20f),
                "the build settings start at their defaults: chests on, 20 m");

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop");
            ResolvedBlueprint resolved = null;
            var error = "no Workshop kit";
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out error))
            {
                Check(false, "the workshop kit resolves: " + error);
                yield break;
            }

            foreach (var part in resolved.Parts)
            {
                player.m_knownRecipes.Add(part.Piece.m_name);
            }

            player.UpdateAvailablePiecesList();
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);

            // RemoveItem by name takes any quality. That is only safe while a material has one.
            foreach (var cost in resolved.TotalCost)
            {
                var shared = cost.m_resItem.m_itemData.m_shared;
                Check(shared.m_maxQuality == 1, $"{shared.m_name} has one quality (max {shared.m_maxQuality}), so taking it by name is safe");
            }

            // ---- three wood chests behind the player, away from where the kit goes up ----
            var centre = player.transform.position;
            var pieces = new List<Piece>();
            yield return PlaceTestPiece(player, "piece_chest_wood", OnGround(centre, 180f, 5f), 0f, pieces);
            yield return PlaceTestPiece(player, "piece_chest_wood", OnGround(centre, 160f, 15f), 340f, pieces);
            var farAngle = DryAngle(centre, 30f);
            yield return PlaceTestPiece(player, "piece_chest_wood", OnGround(centre, farAngle, 30f), farAngle + 180f, pieces);
            yield return new WaitForSeconds(1f);

            var boxes = pieces.Select(p => p != null ? p.GetComponentInChildren<Container>() : null).ToList();
            Check(boxes.All(b => b != null && b.GetInventory() != null), "three wood chests stand, each with an inventory");
            if (!boxes.All(b => b != null && b.GetInventory() != null))
            {
                RemoveOldTestBuildings(player);
                yield break;
            }

            var at = pieces.Select(p => Vector3.Distance(player.transform.position, p.transform.position)).ToList();
            Log($"chests at {at[0]:0.00} m, {at[1]:0.00} m and {at[2]:0.00} m (the far one at {farAngle:0} degrees)");
            Check(at[0] < 10f && at[1] > 10f && at[1] < 20f && at[2] > 20f,
                $"the chests stand at 5, 15 and 30 m: {at[0]:0.0}, {at[1]:0.0}, {at[2]:0.0}");

            yield return SourcesTakeOrder(player, boxes[0], boxes[1], boxes[2]);
            yield return SourcesRules(player, pieces[0]);

            // ---- the kit's cost, split over the inventory and the chests ----
            var shares = new List<(string Name, int Need, int Bag, int Near, int Middle)>();
            foreach (var cost in resolved.TotalCost)
            {
                var prefab = cost.m_resItem.gameObject.name;
                var need = cost.m_amount;
                var bag = Mathf.RoundToInt(need * 0.4f);
                var near = Mathf.RoundToInt(need * 0.4f);
                var middle = need - bag - near;
                shares.Add((cost.m_resItem.m_itemData.m_shared.m_name, need, bag, near, middle));
                AddTo(player.GetInventory(), prefab, bag);
                AddTo(boxes[0].GetInventory(), prefab, near);
                AddTo(boxes[1].GetInventory(), prefab, middle + SourcesSpare);
                AddTo(boxes[2].GetInventory(), prefab, need);
                Log($"{cost.m_resItem.m_itemData.m_shared.m_name}: needs {need}, bag {bag}, 5 m {near}, 15 m {middle} + {SourcesSpare} spare, 30 m {need}");
            }

            int Bag(string item) => player.GetInventory().CountItems(item);
            int In(int chest, string item) => boxes[chest].GetInventory().CountItems(item);

            var sources = MaterialSources.Around(player);
            foreach (var s in shares)
            {
                Check(Bag(s.Name) == s.Bag && In(0, s.Name) == s.Near && In(1, s.Name) == s.Middle + SourcesSpare && In(2, s.Name) == s.Need,
                    $"{s.Name} put in as planned: bag {Bag(s.Name)}, 5 m {In(0, s.Name)}, 15 m {In(1, s.Name)}, 30 m {In(2, s.Name)}");
                var want = Bag(s.Name) + In(0, s.Name) + In(1, s.Name);
                Check(sources.Count(s.Name) == want,
                    $"Count({s.Name}) is {sources.Count(s.Name)}: bag + 5 m + 15 m = {want}, the 30 m chest's {In(2, s.Name)} left out");
            }

            Check(sources.ChestCount == 2 && sources.Chests[0] == boxes[0] && sources.Chests[1] == boxes[1]
                && Mathf.Approximately(sources.Range, 20f),
                $"the sources are the bag and 2 chests within 20 m, nearest first: {sources.ChestCount} chests within {sources.Range:0} m");
            var rule = BlueprintRules.CheckCanBuild(player, resolved);
            Check(rule == null, "the rules say the kit can be built from bag and chests: " + (rule ?? "ok"));

            // ---- a shorter range drops the 15 m chest, chests off leaves the bag ----
            BuildConfig.ChestRange.Value = 10f;
            var near10 = MaterialSources.Around(player);
            Check(near10.ChestCount == 1 && near10.Chests[0] == boxes[0]
                && shares.All(s => near10.Count(s.Name) == Bag(s.Name) + In(0, s.Name)),
                $"ChestRange 10: only the 5 m chest counts ({near10.ChestCount} chests), "
                + string.Join(", ", shares.Select(s => $"{s.Name} {near10.Count(s.Name)}")));
            Default(BuildConfig.ChestRange);

            BuildConfig.UseChests.Value = false;
            var bagOnly = MaterialSources.Around(player);
            var bagShort = PartialBuild.Missing(resolved, null, bagOnly);
            Check(bagOnly.ChestCount == 0 && bagOnly.Range == 0f && shares.All(s => bagOnly.Count(s.Name) == Bag(s.Name)),
                "UseChests off: the bag only, " + string.Join(", ", shares.Select(s => $"{s.Name} {bagOnly.Count(s.Name)}")));
            Check(bagShort.Count > 0 && BlueprintRules.CheckCanBuild(player, resolved) == null,
                "and the bag alone is short (" + PartialBuild.MissingText(bagShort) + "), which no longer refuses the kit");

            // ---- the click: the whole kit, paid bag first, then nearest chest first ----
            player.m_lookYaw = BuildFacing;
            player.transform.rotation = BuildFacing;
            player.m_body.rotation = BuildFacing;
            yield return new WaitForSeconds(0.3f);
            BlueprintMode.Select(player, resolved);
            yield return AimAtGround(player);
            Check(BlueprintMode.HasTarget && BlueprintMode.Blocked == null, "the preview stands on a free spot: " + (BlueprintMode.Blocked ?? "ok"));
            var root = BlueprintMode.PreviewRoot;
            var middleOfKit = root != null ? root.position : centre;

            // No click here: it would build what the bag pays for and leave the chests too little.
            var bagPlan = root != null
                ? PartialBuild.Plan(resolved, root.position, root.eulerAngles.y, null, bagOnly, false).Count
                : -1;
            Check(bagPlan >= 0 && bagPlan < resolved.Parts.Count,
                $"with chests off a click would build only part of the kit: {bagPlan} of {resolved.Parts.Count} pieces");
            Default(BuildConfig.UseChests);

            player.m_lastToolUseTime = 0f;
            var existing = new HashSet<Piece>(PiecesAround(player, middleOfKit));
            var ruleBefore = BlueprintRules.CheckCanBuild(player, resolved);
            Check(BlueprintMode.TryBuild(player), "chests on again: the click builds the kit: " + (ruleBefore ?? "ok"));
            var built = PiecesAround(player, middleOfKit).Where(p => !existing.Contains(p)).ToList();
            Check(built.Count == resolved.Parts.Count, $"the whole kit stands: {built.Count} of {resolved.Parts.Count} pieces");

            foreach (var s in shares)
            {
                Check(Bag(s.Name) == 0, $"{s.Name}: none left in the inventory ({s.Bag} -> {Bag(s.Name)})");
                Check(In(0, s.Name) == 0 && In(1, s.Name) == SourcesSpare,
                    $"{s.Name}: the 5 m chest was emptied ({s.Near} -> {In(0, s.Name)}) before the 15 m one was touched"
                    + $" ({s.Middle + SourcesSpare} -> {In(1, s.Name)}, its {SourcesSpare} spare kept)");
                Check(In(2, s.Name) == s.Need, $"{s.Name}: the 30 m chest is untouched ({In(2, s.Name)} of {s.Need})");
            }

            // Long enough for the build smoke to clear.
            yield return new WaitForSeconds(3f);
            player.m_lookPitch = 10f;
            yield return Screenshot("build-sources-built");

            // ---- what the chests saved, read back the way the game loads it ----
            foreach (var s in shares)
            {
                var saved = boxes.Select(b => WoodIn(b.m_nview.GetZDO().GetByteArray(ZDOVars.s_items), b, s.Name)).ToList();
                Check(saved[0] == In(0, s.Name) && saved[0] == 0,
                    $"{s.Name}: the 5 m chest's saved items hold the new count ({saved[0]})");
                Check(saved[1] == SourcesSpare && saved[2] == s.Need,
                    $"{s.Name}: saved in the 15 m chest {saved[1]}, in the 30 m chest {saved[2]}");
            }

            BlueprintMode.Exit();
            yield return SourcesWard(player, boxes[0], boxes[1]);

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(1f);
            Check(pieces.All(p => p == null), "every test chest is removed again");
            var left = new List<Piece>();
            Piece.GetAllPiecesInRadius(player.transform.position, 60f, left);
            var mine = left.Count(p => p != null && p.GetCreator() == player.GetPlayerID());
            Check(mine == 0, $"nothing of the test is left standing: {mine}");
        }

        /// <summary>
        /// Take goes the inventory first, then nearest first, one source at a time, and takes nothing
        /// when all of them together are short. A cart between the two near chests proves a hold on
        /// a child object counts. Stone, which the kit does not use.
        /// </summary>
        private static IEnumerator SourcesTakeOrder(Player player, Container near, Container middle, Container far)
        {
            var carts = new List<Piece>();
            yield return PlaceTestPiece(player, "Cart", OnGround(player.transform.position, 200f, 9f) + (Vector3.up * 0.3f), 20f, carts);
            yield return new WaitForSeconds(1f);
            var cart = carts[0] != null ? carts[0].GetComponentInChildren<Container>() : null;
            Check(cart != null && cart.GetComponent<Piece>() == null, "a cart stands at 9 m, its hold on a child object");
            if (cart == null)
            {
                yield break;
            }

            var stone = ObjectDB.instance.GetItemPrefab("Stone").GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            var holders = new[] { player.GetInventory(), near.GetInventory(), cart.GetInventory(), middle.GetInventory(), far.GetInventory() };
            foreach (var inventory in holders)
            {
                AddTo(inventory, "Stone", 3);
            }

            string Left() => string.Join(",", holders.Select(i => i.CountItems(stone)));
            var sources = MaterialSources.Around(player);
            Check(sources.ChestCount == 3 && sources.Chests[0] == near && sources.Chests[1] == cart && sources.Chests[2] == middle,
                $"the chests nearest first: 5 m, the cart, 15 m, and the 30 m one out ({sources.ChestCount} chests)");
            Check(sources.Count(stone) == 12, $"Count(stone) is {sources.Count(stone)}: 3 each in the bag, 5 m, cart, 15 m");
            Check(sources.Take(stone, 7) && Left() == "0,0,2,3,3",
                $"Take 7 stone: the bag, then the 5 m chest, then 2 from the cart. Left bag,5 m,cart,15 m,30 m: {Left()}");
            Check(!sources.Take(stone, 100) && Left() == "0,0,2,3,3", $"Take 100 stone says no and takes nothing: {Left()}");

            foreach (var inventory in holders)
            {
                inventory.RemoveItem(stone, inventory.CountItems(stone));
            }

            ZNetScene.instance.Destroy(carts[0].gameObject);
            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>
        /// A chest counts only when the player could open it: placed by a player, and not private to
        /// someone else. The owner is changed in memory only, and put back in the same frame.
        /// </summary>
        private static IEnumerator SourcesRules(Player player, Piece near)
        {
            var box = near.GetComponentInChildren<Container>();
            var me = player.GetPlayerID();
            Check(MaterialSources.Around(player).Chests.Contains(box), "the 5 m chest counts as it is");

            near.m_creator = 0L;
            var unowned = MaterialSources.Around(player).Chests.Contains(box);
            near.m_creator = me;
            Check(!unowned, "a chest no player placed does not count");

            var privates = new List<Piece>();
            yield return PlaceTestPiece(player, "piece_chest_private", OnGround(player.transform.position, 215f, 6f), 35f, privates);
            yield return new WaitForSeconds(0.5f);
            var personal = privates[0] != null ? privates[0].GetComponentInChildren<Container>() : null;
            Check(personal != null && personal.m_privacy == Container.PrivacySetting.Private, "a private chest stands at 6 m");
            if (personal == null)
            {
                yield break;
            }

            var own = MaterialSources.Around(player).Chests.Contains(personal);
            privates[0].m_creator = me + 1;
            var foreign = MaterialSources.Around(player).Chests.Contains(personal);
            privates[0].m_creator = me;
            Check(own, "the player's own private chest counts");
            Check(!foreign, "another player's private chest does not count");

            ZNetScene.instance.Destroy(privates[0].gameObject);
            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>
        /// A wood chest checks the ward, as the game does when it is opened. Under a ward the player
        /// placed, it counts. When that ward belongs to someone else (in memory, put back in the same
        /// frame), it does not.
        /// </summary>
        private static IEnumerator SourcesWard(Player player, Container near, Container middle)
        {
            var wards = new List<Piece>();
            yield return PlaceTestPiece(player, "guard_stone", OnGround(player.transform.position, 240f, 4f), 60f, wards);
            yield return new WaitForSeconds(0.5f);
            var ward = wards[0] != null ? wards[0].GetComponent<PrivateArea>() : null;
            Check(ward != null, "a ward stands at 4 m");
            if (ward == null)
            {
                yield break;
            }

            if (!ward.IsEnabled())
            {
                Log("the ward came up switched off, switching it on");
                ward.SetEnabled(true);
            }

            var mine = MaterialSources.Around(player);
            Check(near.m_checkGuardStone && mine.Chests.Contains(near) && mine.Chests.Contains(middle),
                "under the player's own ward both wood chests count");

            var me = player.GetPlayerID();
            wards[0].m_creator = me + 1;
            var open = PrivateArea.CheckAccess(near.transform.position, 0f, flash: false);
            var theirs = MaterialSources.Around(player);
            wards[0].m_creator = me;
            Check(!open && !theirs.Chests.Contains(near) && !theirs.Chests.Contains(middle),
                $"under another player's ward neither counts ({theirs.ChestCount} chests)");

            ZNetScene.instance.Destroy(wards[0].gameObject);
            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>Puts a piece down the game's way and adds the new one to the list (null when it is not there).</summary>
        private static IEnumerator PlaceTestPiece(Player player, string prefab, Vector3 spot, float yaw, List<Piece> into)
        {
            var piece = PiecePrefab(prefab);
            if (piece == null)
            {
                Check(false, $"the {prefab} prefab exists");
                into.Add(null);
                yield break;
            }

            var before = new List<Piece>();
            Piece.GetAllPiecesInRadius(spot, 2f, before);
            player.PlacePiece(piece, spot, Quaternion.Euler(0f, yaw, 0f), false);
            yield return null;
            var after = new List<Piece>();
            Piece.GetAllPiecesInRadius(spot, 2f, after);
            into.Add(after.FirstOrDefault(p => !before.Contains(p) && p.GetCreator() == player.GetPlayerID()));
        }

        /// <summary>A way out from the centre with dry ground at this distance, behind the player first.</summary>
        private static float DryAngle(Vector3 centre, float distance)
        {
            foreach (var angle in new[] { 200f, 180f, 220f, 160f, 240f, 140f, 260f, 120f })
            {
                if (OnGround(centre, angle, distance).y > ZoneSystem.instance.m_waterLevel + 0.5f)
                {
                    return angle;
                }
            }

            return 200f;
        }

        private static void AddTo(Inventory inventory, string prefab, int amount)
        {
            if (amount > 0)
            {
                inventory.AddItem(prefab, amount, 1, 0, 0L, "", false);
            }
        }

        // ---------- scenario: build_partial ----------

        /// <summary>Solve on the kit repeated 5 x 5 (400 pieces), from Phase 0 (probe-build.txt).</summary>
        private const double GridSolveMs = 10.76;

        /// <summary>
        /// A click builds every piece the materials pay for and that would stand, bottom to top. The
        /// Workshop with half its wood, a made-up two-storey house with wood for one and a half
        /// storeys, nothing at all, everything, costs off, and the planner's time on 400 pieces.
        /// First: the hammer preview's own workbench copy is not a crafting station.
        /// </summary>
        private static IEnumerator TestBuildPartial(Player player)
        {
            var stationErrors = new List<string>();
            void OnLog(string message, string stack, LogType type)
            {
                if ((type == LogType.Exception || type == LogType.Error) && (message + stack).Contains("CraftingStation"))
                {
                    stationErrors.Add(message);
                }
            }

            Application.logMessageReceived += OnLog;

            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(0.5f);
            PieceCatalog.Ensure();
            Check(PieceCatalog.Ready, "the piece catalog is built");

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop");
            ResolvedBlueprint resolved = null;
            var error = "no Workshop kit";
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out error))
            {
                Check(false, "the workshop kit resolves: " + error);
                Application.logMessageReceived -= OnLog;
                yield break;
            }

            Unlock(player, resolved);
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);
            Log("the kit's parts: " + string.Join(", ", resolved.Parts.Select((p, i) =>
                $"{i} {p.Prefab.name} y {p.Source.Position.y:0.00} costs "
                + string.Join("+", PartialBuild.CostOf(p.Piece).Select(c => $"{c.Value} {c.Key}")))));

            yield return PartialStationCopy(player, resolved);
            yield return PartialNothing(player, resolved);
            yield return PartialHalfWood(player, resolved);
            yield return PartialTwoStoreys(player);
            yield return PartialEverything(player, resolved, false);
            yield return PartialEverything(player, resolved, true);
            PartialTiming(player, resolved);

            BlueprintMode.Exit();
            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(1f);
            Application.logMessageReceived -= OnLog;
            Check(stationErrors.Count == 0, stationErrors.Count == 0
                ? "no CraftingStation error in the log during the whole scenario"
                : $"{stationErrors.Count} CraftingStation errors in the log: {stationErrors[0]}");
            var left = new List<Piece>();
            Piece.GetAllPiecesInRadius(player.transform.position, 60f, left);
            var mine = left.Count(p => p != null && p.GetCreator() == player.GetPlayerID());
            Check(mine == 0, $"nothing of the test is left standing: {mine}");
        }

        private static void Unlock(Player player, ResolvedBlueprint blueprint)
        {
            foreach (var part in blueprint.Parts)
            {
                player.m_knownRecipes.Add(part.Piece.m_name);
            }

            player.UpdateAvailablePiecesList();
        }

        /// <summary>The blueprint in the hammer, the player facing the build way (or <paramref name="facing"/>), the aim on free ground.</summary>
        private static IEnumerator AimBlueprint(Player player, ResolvedBlueprint blueprint, Quaternion? facing = null)
        {
            var face = facing ?? BuildFacing;
            player.m_lookYaw = face;
            player.transform.rotation = face;
            player.m_body.rotation = face;
            yield return new WaitForSeconds(0.3f);
            BlueprintMode.Select(player, blueprint);
            yield return AimAtGround(player);
            var ok = BlueprintMode.HasTarget && BlueprintMode.Blocked == null && BlueprintMode.PreviewRoot != null;
            if (!ok)
            {
                var tool = player.GetRightItem();
                Log($"aim failed: active={BlueprintMode.Active} placeMode={player.InPlaceMode()} dead={player.IsDead()}"
                    + $" tool={(tool != null ? tool.m_shared.m_name + " " + tool.m_durability.ToString("0.#") : "none")}"
                    + $" target={BlueprintMode.HasTarget} blocked={BlueprintMode.Blocked ?? "none"} at {V(player.transform.position)}"
                    + $" teleporting={player.IsTeleporting()} menu={Hud.IsPieceSelectionVisible()}");
            }

            Check(ok, $"the {blueprint.Name} preview stands on a free spot: " + (BlueprintMode.Blocked ?? "ok"));
            player.m_lastToolUseTime = 0f;
        }

        /// <summary>
        /// The world pieces the click put down, matched to the blueprint's parts: same prefab, pivot
        /// within 0.05 m of where the root puts the part.
        /// </summary>
        private static Dictionary<int, Piece> MatchParts(ResolvedBlueprint blueprint, Vector3 rootPos, Quaternion rootRot, List<Piece> built)
        {
            var matched = new Dictionary<int, Piece>();
            var free = new List<Piece>(built);
            for (var i = 0; i < blueprint.Parts.Count; i++)
            {
                var part = blueprint.Parts[i];
                var at = rootPos + (rootRot * part.Source.Position);
                var piece = free.FirstOrDefault(p => p != null && Utils.GetPrefabName(p.gameObject) == part.Prefab.name
                    && Vector3.Distance(p.transform.position, at) < 0.05f);
                if (piece != null)
                {
                    matched[i] = piece;
                    free.Remove(piece);
                }
            }

            return matched;
        }

        /// <summary>The support model on pieces standing in the world, in the blueprint's space, on the real ground.</summary>
        private static SupportMap ModelOf(IEnumerable<Piece> pieces, Vector3 rootPos, Quaternion rootRot)
        {
            var back = Quaternion.Inverse(rootRot);
            var scene = pieces.Where(p => p != null).Select((p, i) => new ScenePiece
            {
                Id = i,
                Prefab = Utils.GetPrefabName(p.gameObject),
                Pos = back * (p.transform.position - rootPos),
                Rot = back * p.transform.rotation,
                Entry = PieceCatalog.Find(Utils.GetPrefabName(p.gameObject)),
            }).ToList();
            return Support.Solve(scene, PartialBuild.GroundUnder(rootPos, rootRot.eulerAngles.y));
        }

        private static int Held(Inventory inventory, string item)
        {
            return inventory != null ? inventory.CountItems(item) : 0;
        }

        /// <summary>
        /// Phase 0 found the hammer preview's workbench copy counted as a real workbench, and threw in
        /// CraftingStation.GetExtensions on its first range check. The copies no longer carry a station.
        /// </summary>
        private static IEnumerator PartialStationCopy(Player player, ResolvedBlueprint kit)
        {
            yield return AimBlueprint(player, kit);
            for (var frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            var root = BlueprintMode.PreviewRoot;
            if (root == null)
            {
                yield break;
            }

            var real = CraftingStation.m_allStations.Count(s => s != null && !s.transform.IsChildOf(root)
                && s.m_name == "$piece_workbench" && s.transform.position.y > -1000f && Flat(s.transform.position, root.position) < 50f);
            var bench = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "piece_workbench");
            Check(real == 0 && bench != null && bench.GetComponentsInChildren<Renderer>(true).Length > 0,
                $"set-up: the preview shows a workbench, and no real one stands within 50 m ({real})");

            var copies = root.GetComponentsInChildren<CraftingStation>(true).Length;
            var listed = CraftingStation.m_allStations.Count(s => s != null && s.transform.IsChildOf(root));
            var atPreview = StationInRange("$piece_workbench", root.position, out var throws, out var why);
            var atPlayer = StationInRange("$piece_workbench", player.transform.position, out var playerThrows, out _);
            Check(copies == 0 && listed == 0, $"the preview carries no crafting station: {copies} on its copies, {listed} listed by the game");
            Check(atPreview == null && atPlayer == null && throws == 0 && playerThrows == 0,
                "HaveBuildStationInRange(\"$piece_workbench\") finds nothing at the preview or the player, and does not throw"
                + (throws + playerThrows > 0 ? $" ({why})" : ""));
            var rule = BlueprintRules.CheckCanBuild(player, kit);
            Check(rule == null, "the kit brings its own workbench, so the rules let it be built: " + (rule ?? "ok"));
            BlueprintMode.Exit();
        }

        /// <summary>Check 6: one wood and one resin pay for no piece. Nothing is built, nothing taken, the message says what is missing.</summary>
        private static IEnumerator PartialNothing(Player player, ResolvedBlueprint kit)
        {
            ClearInventoryExceptHammer(player);
            var items = kit.TotalCost.Select(c => (Prefab: c.m_resItem.gameObject.name, Name: c.m_resItem.m_itemData.m_shared.m_name, Need: c.m_amount)).ToList();
            foreach (var item in items)
            {
                AddTo(player.GetInventory(), item.Prefab, 1);
            }

            yield return AimBlueprint(player, kit);
            var root = BlueprintMode.PreviewRoot;
            var centre = root != null ? root.position : player.transform.position;
            var before = PiecesAround(player, centre).Count;
            var sites = SiteStore.All.Count;
            var built = BlueprintMode.TryBuild(player);
            yield return null;
            Check(!built && PiecesAround(player, centre).Count == before, "nothing affordable: the click builds nothing");
            Check(items.All(i => Held(player.GetInventory(), i.Name) == 1),
                "and takes nothing: " + string.Join(", ", items.Select(i => $"{i.Name} {Held(player.GetInventory(), i.Name)}")));
            var said = BlueprintMode.LastMessage ?? "";
            var named = items.All(i => said.Contains($"{i.Need - 1} {Localization.instance.Localize(i.Name)}"));
            Check(said.StartsWith("Workshop planned, nothing built yet. Missing:") && named,
                $"the message names every missing item with its amount: '{said}'");
            yield return CheckContinuesAfterClick(sites, "nothing affordable");
            BlueprintMode.Exit();
            ClearInventoryExceptHammer(player);
        }

        /// <summary>
        /// Checks 1 to 4: half the kit's wood (half of it in a chest), the rest in full. Part of the
        /// kit goes up, paid exactly, it stands in the game and in the model, and it is the lower part.
        /// </summary>
        private static IEnumerator PartialHalfWood(Player player, ResolvedBlueprint kit)
        {
            var chests = new List<Piece>();
            yield return PlaceTestPiece(player, "piece_chest_wood", OnGround(player.transform.position, 180f, 5f), 0f, chests);
            yield return new WaitForSeconds(0.5f);
            var chest = chests[0] != null ? chests[0].GetComponentInChildren<Container>() : null;
            Check(chest != null, "a wood chest stands 5 m behind the player");
            if (chest == null)
            {
                yield break;
            }

            // Half the wood, split between the bag and the chest. Everything else in full, split too.
            var budget = new Dictionary<string, int>();
            foreach (var cost in kit.TotalCost)
            {
                var name = cost.m_resItem.m_itemData.m_shared.m_name;
                var have = name == "$item_wood" ? cost.m_amount / 2 : cost.m_amount;
                budget[name] = have;
                AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, have - (have / 2));
                AddTo(chest.GetInventory(), cost.m_resItem.gameObject.name, have / 2);
            }

            Check(budget.ContainsKey("$item_wood") && budget["$item_wood"] == 20,
                "the kit needs 40 wood, 20 are there: " + string.Join(", ", budget.Select(b => $"{b.Key} {b.Value}")));
            int Both(string item) => Held(player.GetInventory(), item) + Held(chest.GetInventory(), item);
            var before = budget.Keys.ToDictionary(k => k, Both);

            yield return AimBlueprint(player, kit);
            var root = BlueprintMode.PreviewRoot;
            if (root == null)
            {
                yield break;
            }

            var rootPos = root.position;
            var rootRot = root.rotation;
            var existing = new HashSet<Piece>(PiecesAround(player, rootPos));
            var sites = SiteStore.All.Count;
            var clicked = BlueprintMode.TryBuild(player);
            var said = BlueprintMode.LastMessage ?? "";
            yield return null;
            var built = PiecesAround(player, rootPos).Where(p => !existing.Contains(p)).ToList();
            var total = kit.Parts.Count;
            Check(clicked && built.Count > 0 && built.Count < total, $"half the wood builds part of the kit: {built.Count} of {total} pieces");
            Check(said.StartsWith($"Built {built.Count} of {total} pieces of Workshop. Still missing: "),
                $"and says so: '{said}'");
            yield return CheckContinuesAfterClick(sites, "half the wood");
            BlueprintMode.Exit();

            // 1. What left the bag and the chest is what the placed pieces cost, item by item.
            var paid = new Dictionary<string, int>();
            foreach (var piece in built)
            {
                foreach (var item in PartialBuild.CostOf(piece))
                {
                    paid.TryGetValue(item.Key, out var had);
                    paid[item.Key] = had + item.Value;
                }
            }

            foreach (var item in budget.Keys)
            {
                paid.TryGetValue(item, out var want);
                var gone = before[item] - Both(item);
                Check(gone == want, $"{item}: {gone} left the bag and the chest, the placed pieces cost {want}");
            }

            Check(paid.Keys.All(budget.ContainsKey), "the placed pieces cost nothing the kit does not list");

            var matched = MatchParts(kit, rootPos, rootRot, built);
            Check(matched.Count == built.Count, $"every placed piece is a part of the kit, where the preview showed it: {matched.Count} of {built.Count}");
            Log("built parts: " + string.Join(", ", matched.Keys.OrderBy(i => i).Select(i => $"{i} {kit.Parts[i].Prefab.name}")));

            // 3. The model, on the built pieces alone, on the real ground.
            var model = ModelOf(built, rootPos, rootRot);
            Check(model.Fallen.Count == 0, $"the support model on the {built.Count} built pieces alone: none falls ({model.Fallen.Count})");

            // 4. Bottom to top: no built piece is higher than the lowest unbuilt one the budget paid
            //    for on its own and that would stand on what was built.
            var ground = PartialBuild.GroundUnder(rootPos, rootRot.eulerAngles.y);
            var builtScene = matched.Keys.Select(i => new ScenePiece
            {
                Id = i,
                Prefab = kit.Parts[i].Prefab.name,
                Pos = kit.Parts[i].Source.Position,
                Rot = kit.Parts[i].Source.Rotation,
                Entry = PieceCatalog.Find(kit.Parts[i].Prefab.name),
            }).ToList();
            var builtMap = Support.Solve(builtScene, ground);
            var lowest = float.MaxValue;
            var lowestName = "none";
            for (var i = 0; i < kit.Parts.Count; i++)
            {
                if (matched.ContainsKey(i))
                {
                    continue;
                }

                var part = kit.Parts[i];
                var alone = PartialBuild.CostOf(part.Piece).All(c => budget.TryGetValue(c.Key, out var b) && b >= c.Value);
                var placed = new[]
                {
                    new PlacedPiece { Id = 1000 + i, Prefab = part.Prefab.name, Pos = part.Source.Position, Rot = part.Source.Rotation, Entry = PieceCatalog.Find(part.Prefab.name) },
                };
                var falls = new bool[1];
                Support.Evaluate(builtMap, placed, new float[1], falls);
                if (alone && !falls[0] && part.Source.Position.y < lowest)
                {
                    lowest = part.Source.Position.y;
                    lowestName = $"{i} {part.Prefab.name}";
                }
            }

            var highest = matched.Keys.Select(i => kit.Parts[i].Source.Position.y).DefaultIfEmpty(0f).Max();
            Check(highest <= lowest + 0.01f,
                $"bottom to top: the highest built piece is at {highest:0.00} m, the lowest unbuilt one that was paid for on its own"
                + $" and would stand is at {(lowest == float.MaxValue ? "none" : lowest.ToString("0.00"))} m ({lowestName})");

            // 2. The game agrees: after 15 s, everything still stands.
            yield return new WaitForSeconds(15f);
            var standing = built.Count(p => p != null);
            Check(standing == built.Count, $"after 15 s every built piece still stands: {standing} of {built.Count}");

            player.m_lookPitch = 10f;
            yield return new WaitForSeconds(0.5f);
            yield return Screenshot("build-partial-1");

            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// A made-up house, 4 x 4 m: floors on the ground, walls, a second floor on the walls, walls.
        /// Where a player snapping pieces would put them.
        /// </summary>
        private static Blueprint TwoStoreys()
        {
            var house = new Blueprint { Name = "Two storeys", IconPrefab = "woodwall" };
            void Add(string prefab, float x, float y, float z, float yaw)
            {
                house.Pieces.Add(new BlueprintPiece { PrefabName = prefab, Position = new Vector3(x, y, z), Rotation = Quaternion.Euler(0f, yaw, 0f) });
            }

            foreach (var floorY in new[] { 0f, 2f })
            {
                foreach (var x in new[] { -1f, 1f })
                {
                    foreach (var z in new[] { -1f, 1f })
                    {
                        Add("wood_floor", x, floorY, z, 0f);
                    }
                }

                var wallY = floorY + 1f;
                foreach (var along in new[] { -1f, 1f })
                {
                    Add("woodwall", along, wallY, -2f, 0f);
                    Add("woodwall", along, wallY, 2f, 0f);
                    Add("woodwall", -2f, wallY, along, -90f);
                    Add("woodwall", 2f, wallY, along, 90f);
                }
            }

            return house;
        }

        /// <summary>Check 5: wood for one and a half storeys. Nothing floats, and after 15 s nothing fell.</summary>
        private static IEnumerator PartialTwoStoreys(Player player)
        {
            if (!ResolvedBlueprint.TryResolve(TwoStoreys(), out var house, out var error))
            {
                Check(false, "the two-storey house resolves: " + error);
                yield break;
            }

            Unlock(player, house);
            ClearInventoryExceptHammer(player);
            var wood = house.TotalCost.First(c => c.m_resItem.m_itemData.m_shared.m_name == "$item_wood");
            var storey = wood.m_amount / 2;
            AddTo(player.GetInventory(), wood.m_resItem.gameObject.name, storey + (storey / 2));

            // Its walls and floors need a workbench, which the house does not bring.
            var benches = new List<Piece>();
            yield return PlaceTestPiece(player, "piece_workbench", OnGround(player.transform.position, 180f, 4f), 0f, benches);
            yield return new WaitForSeconds(0.5f);

            yield return AimBlueprint(player, house);
            var root = BlueprintMode.PreviewRoot;
            if (root == null)
            {
                yield break;
            }

            var rootPos = root.position;
            var rootRot = root.rotation;
            var existing = new HashSet<Piece>(PiecesAround(player, rootPos));
            var clicked = BlueprintMode.TryBuild(player);
            var said = BlueprintMode.LastMessage ?? "";
            BlueprintMode.Exit();
            yield return null;
            var built = PiecesAround(player, rootPos).Where(p => !existing.Contains(p)).ToList();
            var matched = MatchParts(house, rootPos, rootRot, built);
            var upper = matched.Keys.Count(i => house.Parts[i].Source.Position.y > 1.5f);
            Check(clicked && built.Count > 12 && built.Count < house.Parts.Count && matched.Count == built.Count,
                $"wood for {storey + (storey / 2)} of {wood.m_amount}: {built.Count} of {house.Parts.Count} pieces, {upper} of them upstairs: '{said}'");
            var model = ModelOf(built, rootPos, rootRot);
            Check(model.Fallen.Count == 0, $"nothing floats: the support model on the built pieces has {model.Fallen.Count} falling");

            yield return new WaitForSeconds(15f);
            var standing = built.Count(p => p != null);
            Check(standing == built.Count, $"after 15 s nothing fell: {standing} of {built.Count}");
            player.m_lookPitch = 10f;
            yield return new WaitForSeconds(0.5f);
            yield return Screenshot("build-partial-2-storeys");

            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>Checks 7 and 8: with every material, or with costs off, the whole kit goes up, as before.</summary>
        private static IEnumerator PartialEverything(Player player, ResolvedBlueprint kit, bool costsOff)
        {
            ClearInventoryExceptHammer(player);
            if (costsOff)
            {
                player.m_noPlacementCost = true;
            }
            else
            {
                foreach (var cost in kit.TotalCost)
                {
                    AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, cost.m_amount);
                }
            }

            if (!costsOff)
            {
                // A piece with a ghost of its own in the hand, so the check below sees the game's ghost come back.
                var wall = PiecePrefab("woodwall");
                Check(wall != null && player.SetSelectedPiece(wall), "set-up: the hammer holds the wood wall");
            }

            yield return AimBlueprint(player, kit);
            var root = BlueprintMode.PreviewRoot;
            var centre = root != null ? root.position : player.transform.position;
            var existing = new HashSet<Piece>(PiecesAround(player, centre));
            var clicked = BlueprintMode.TryBuild(player);
            var said = BlueprintMode.LastMessage ?? "";
            yield return CheckBackToPiece(player, costsOff ? "costs off, built in full" : "every material, built in full");
            BlueprintMode.Exit();
            player.m_noPlacementCost = false;
            yield return null;
            var built = PiecesAround(player, centre).Where(p => !existing.Contains(p)).ToList();
            var left = kit.TotalCost.Sum(c => Held(player.GetInventory(), c.m_resItem.m_itemData.m_shared.m_name));
            Check(clicked && built.Count == kit.Parts.Count && left == 0 && said == "Built Workshop",
                (costsOff ? "costs off, an empty bag" : "every material there")
                + $": every piece built, {built.Count} of {kit.Parts.Count}, {left} left over: '{said}'");

            yield return new WaitForSeconds(2f);
            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Check 9: Plan on the kit repeated 5 x 5 (400 pieces). The limit is 3 x the Solve time
        /// Phase 0 measured on it, times the rounds the plan took. The JIT is warmed first.
        /// </summary>
        private static void PartialTiming(Player player, ResolvedBlueprint kit)
        {
            var grid = new Blueprint { Name = "Workshop grid" };
            for (var gx = 0; gx < 5; gx++)
            {
                for (var gz = 0; gz < 5; gz++)
                {
                    var offset = new Vector3((gx - 2) * 8f, 0f, (gz - 2) * 8f);
                    foreach (var part in kit.Parts)
                    {
                        grid.Pieces.Add(new BlueprintPiece { PrefabName = part.Prefab.name, Position = part.Source.Position + offset, Rotation = part.Source.Rotation });
                    }
                }
            }

            if (!ResolvedBlueprint.TryResolve(grid, out var big, out var error))
            {
                Check(false, "the 400-piece grid resolves: " + error);
                return;
            }

            var spot = player.transform.position;
            spot.y = ZoneSystem.instance.GetGroundHeight(spot);
            var full = big.TotalCost.ToDictionary(c => c.m_resItem.m_itemData.m_shared.m_name, c => c.m_amount);

            // All but one torch's resin: everything is looked at, round after round. And half the wood.
            var budgets = new List<(string Name, Dictionary<string, int> Budget)>
            {
                ("all but one torch's resin", full.ToDictionary(p => p.Key, p => p.Key == "$item_resin" ? p.Value - 2 : p.Value)),
                ("half the wood", full.ToDictionary(p => p.Key, p => p.Key == "$item_wood" ? p.Value / 2 : p.Value)),
            };

            PartialBuild.Plan(big, spot, 0f, null, budgets[0].Budget, false, null);
            foreach (var (name, budget) in budgets)
            {
                var stats = new PlanStats();
                var chosen = PartialBuild.Plan(big, spot, 0f, null, budget, false, stats);
                var limit = 3.0 * GridSolveMs * Math.Max(1, stats.Passes);
                Log($"plan on {big.Parts.Count} pieces, {name}: {chosen.Count} chosen in {stats.Milliseconds:0.0} ms, route {stats.Route},"
                    + $" {stats.Passes} rounds, {stats.Solves} solves, {stats.Evaluates} evaluates, {stats.Exempt} exempt");
                Check(stats.Route == "passes" && chosen.Count > 0 && chosen.Count < big.Parts.Count && stats.Milliseconds < limit,
                    $"Plan on {big.Parts.Count} pieces with {name}: {chosen.Count} chosen in {stats.Milliseconds:0.0} ms,"
                    + $" under 3 x {GridSolveMs} ms x {stats.Passes} rounds = {limit:0} ms");
            }

            var all = new PlanStats();
            var every = PartialBuild.Plan(big, spot, 0f, null, full, false, all);
            Check(all.Route == "all paid" && every.Count == big.Parts.Count, $"with all of it paid, Plan takes every piece: {every.Count}, {all.Milliseconds:0.00} ms");
        }

        // ---------- scenario: build_sites ----------

        private static string SitesFolder => Path.Combine(OutDir, "sites");

        private static void DeleteSitesFolder()
        {
            try
            {
                if (Directory.Exists(SitesFolder))
                {
                    Directory.Delete(SitesFolder, true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log($"cannot delete {SitesFolder}: {e.Message}");
            }
        }

        /// <summary>Every unfinished build forgotten, ghosts included, and the test's sites folder gone.</summary>
        private static void ClearSites()
        {
            // The art guard runs at the end of the chain, when this folder is long gone. Walk it now.
            NoteWrittenFiles();
            SiteStore.Clear();
            DeleteSitesFolder();
        }

        /// <summary>
        /// A partial or empty click keeps the rest as an unfinished build: a file in the world's folder,
        /// ghosts for its missing parts while the hammer is out nearby, and what is built read from the
        /// world. Half the Workshop's wood, then an empty click somewhere else.
        /// </summary>
        private static IEnumerator TestBuildSites(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            ClearSites();
            yield return new WaitForSeconds(0.5f);
            PieceCatalog.Ensure();

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop");
            ResolvedBlueprint resolved = null;
            var error = "no Workshop kit";
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out error))
            {
                Check(false, "the workshop kit resolves: " + error);
                yield break;
            }

            Unlock(player, resolved);
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);
            BlueprintLibrary.Reload();
            var library = BlueprintLibrary.All.Count;
            var deepBefore = DeepObjects(out _);
            var spot = player.transform.position;

            // ---- 1. half the wood: part is built, the rest is kept in a file ----
            foreach (var cost in resolved.TotalCost)
            {
                var wood = cost.m_resItem.m_itemData.m_shared.m_name == "$item_wood";
                AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, wood ? cost.m_amount / 2 : cost.m_amount);
            }

            yield return AimBlueprint(player, resolved);
            var root = BlueprintMode.PreviewRoot;
            if (root == null)
            {
                yield break;
            }

            var plain = new string(root.GetComponentsInChildren<Piece>(true).Select(TintOf).ToArray());
            Check(plain.Length == resolved.Parts.Count && plain.All(t => t == '-'),
                $"the hammer's preview of a new blueprint keeps the plain ghost look, no part tinted: {plain}");

            var rootPos = root.position;
            var rootYaw = root.eulerAngles.y;
            var existing = new HashSet<Piece>(PiecesAround(player, rootPos));
            var refreshes = SiteTracker.Refreshes;
            var clicked = BlueprintMode.TryBuild(player);
            var said = BlueprintMode.LastMessage ?? "";
            BlueprintMode.Exit();
            yield return null;
            yield return null;
            var placed = PiecesAround(player, rootPos).Where(p => !existing.Contains(p)).ToList();
            Check(clicked && placed.Count > 0 && placed.Count < resolved.Parts.Count,
                $"half the wood builds part of the kit: {placed.Count} of {resolved.Parts.Count}: '{said}'");
            Check(SiteStore.All.Count == 1, $"the click kept one unfinished build: {SiteStore.All.Count}");
            var site = SiteStore.All.FirstOrDefault();
            if (site == null)
            {
                yield break;
            }

            Check(SiteTracker.Refreshes > refreshes, $"the tracker refreshed right after the build ({SiteTracker.Refreshes - refreshes} times)");
            var world = ZNet.World;
            var folder = Path.Combine(OutDir, "sites", world.m_name.ToLowerInvariant() + "_" + world.m_uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Check(site.Path != null && File.Exists(site.Path) && Path.GetDirectoryName(site.Path) == folder
                && Path.GetFileName(site.Path).StartsWith("workshop_"),
                $"the file is in the world's folder: {site.Path}, wanted under {folder}");
            Check(!Directory.GetFiles(folder).Any(f => f.EndsWith(".tmp")), "no temp file is left next to it");

            var lines = File.Exists(site.Path) ? File.ReadAllLines(site.Path) : new string[0];
            var poseLine = lines.FirstOrDefault(l => l.StartsWith("#Site:")) ?? "";
            var fields = poseLine.Length > 6 ? poseLine.Substring(6).Split(';') : new string[0];
            var filePos = Vector3.zero;
            var fileYaw = float.NaN;
            if (fields.Length == 4)
            {
                float F(string t) => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
                filePos = new Vector3(F(fields[0]), F(fields[1]), F(fields[2]));
                fileYaw = F(fields[3]);
            }

            Check(fields.Length == 4 && Vector3.Distance(filePos, rootPos) < 0.001f && Mathf.Abs(Mathf.DeltaAngle(fileYaw, rootYaw)) < 0.1f,
                $"its '{poseLine}' is the preview's pose {V4(rootPos)} turned {rootYaw:0.###}: off by {Vector3.Distance(filePos, rootPos):0.#####} m,"
                + $" {Mathf.Abs(Mathf.DeltaAngle(fileYaw, rootYaw)):0.###} degrees");
            Check(lines.Contains("#SiteSource:workshop") && lines.Contains("#Name:Workshop"),
                "and it names the blueprint it came from: " + string.Join(" | ", lines.Where(l => l.StartsWith("#"))));
            var parsed = BlueprintFormat.ParseBlueprint("x", lines);
            Check(parsed.Pieces.Count == resolved.Parts.Count
                && parsed.Pieces.Select(p => p.PrefabName).SequenceEqual(kit.Pieces.Select(p => p.PrefabName)),
                $"the file holds the whole blueprint, in the kit's order: {parsed.Pieces.Count} pieces");
            Check(!lines.Any(l => l.StartsWith("#Built", StringComparison.OrdinalIgnoreCase)), "and nothing about what is built");
            BlueprintLibrary.Reload();
            Check(BlueprintLibrary.All.Count == library && BlueprintLibrary.All.All(b => b.Name != "x"),
                $"the blueprint library does not read the site: {library} before, {BlueprintLibrary.All.Count} after");

            // ---- 2. the ghost: built parts hidden, the rest red (the bag is empty now) ----
            yield return WaitForGhosts(1);
            var ghost = site.Ghost;
            Check(site.BuiltCount == placed.Count, $"the tracker reads {site.BuiltCount} built parts from the world, the click placed {placed.Count}");
            Check(ghost != null && ghost.Done && ghost.Visible, "the site's ghost stands, with the hammer out");
            if (ghost == null || !ghost.Done)
            {
                yield break;
            }

            Check(ghost.VisibleParts == site.Total - site.BuiltCount,
                $"visible ghost parts: {ghost.VisibleParts} = {site.Total} - {site.BuiltCount}");
            Check(Vector3.Distance(ghost.Root.position, site.RootPosition) < 0.001f && Quaternion.Angle(ghost.Root.rotation, site.RootRotation) < 0.1f,
                $"the ghost stands at the site's pose: {V4(ghost.Root.position)}, turned {ghost.Root.eulerAngles.y:0.#}");
            var probe = BlueprintPreview.Create(resolved);
            var box = probe.LocalBounds;
            probe.Destroy();
            Check(Vector3.Distance(ghost.LocalBounds.center, box.center) < 0.01f && Vector3.Distance(ghost.LocalBounds.size, box.size) < 0.01f,
                $"built over several frames with its drawing held, its box is the hammer preview's: {Box(ghost.LocalBounds)} vs {Box(box)}");
            var looksOk = Enumerable.Range(0, site.Total).All(i => ghost.LookOf(i) == (site.Built[i] ? PartLook.Hidden : PartLook.Waiting));
            Check(site.ReadyCount == 0 && looksOk,
                $"with the bag empty nothing is ready: {site.ReadyCount}; built parts hidden, the rest red: {Looks(site)}");

            // Four more wood: the next click would build two more walls. Those look like the normal ghost.
            var runs = SiteTracker.PlanRuns;
            AddTo(player.GetInventory(), "Wood", 4);
            SiteTracker.Refresh(player);
            var ready = Enumerable.Range(0, site.Total).Where(i => site.Ready != null && site.Ready[i]).ToList();
            Check(SiteTracker.PlanRuns == runs + 1 && ready.Count > 0 && ready.Count < site.Total - site.BuiltCount,
                $"with 4 wood the plan ran again and {ready.Count} parts are ready: {string.Join(", ", ready.Select(i => $"{i} {resolved.Parts[i].Prefab.name}"))}");
            Check(Enumerable.Range(0, site.Total).All(i => ghost.LookOf(i) == (site.Built[i] ? PartLook.Hidden : ready.Contains(i) ? PartLook.Ready : PartLook.Waiting)),
                "ready parts are Ready, the others Waiting: " + Looks(site));
            SiteTracker.Refresh(player);
            SiteTracker.Refresh(player);
            Check(SiteTracker.PlanRuns == runs + 1, $"two more refreshes with nothing changed do not plan again ({SiteTracker.PlanRuns - runs - 1} extra)");

            yield return null;
            yield return null;
            var tints = Tints(site);
            var waiting = Enumerable.Range(0, site.Total).Count(i => ghost.LookOf(i) == PartLook.Waiting);
            Check(tints == Wanted(site) && ready.Count > 0 && waiting > 0,
                $"each part wears its look's tint through MaterialMan, _Color and the glow in _EmissionColor: the {ready.Count} ready ones"
                + $" light blue, the {waiting} waiting ones red, the built ones none: {tints} (b blue, r red, - none), wanted {Wanted(site)}");

            // The screenshot: from the front left corner, 11 m off, so the side wall that is ready shows too.
            var centre = site.WorldBox.center;
            var view = centre + new Vector3(-8f, 0f, -8f);
            view.y = ZoneSystem.instance.GetGroundHeight(view) + 1f;
            var look = Quaternion.LookRotation(new Vector3(centre.x - view.x, 0f, centre.z - view.z));
            yield return TeleportNear(player, view, look, "to the site's front left corner, to look at it");
            player.m_lookPitch = 6f;
            SiteTracker.Refresh(player);
            yield return new WaitForSeconds(1f);
            yield return Screenshot("build-sites-1-ghosts");

            // The 4 wood gone again: the parts that were ready turn red, and no blue is left on any of them.
            ClearInventoryExceptHammer(player);
            SiteTracker.Refresh(player);
            yield return null;
            yield return null;
            tints = Tints(site);
            Check(site.ReadyCount == 0 && ready.All(i => ghost.LookOf(i) == PartLook.Waiting && tints[i] == 'r') && tints.IndexOf('b') < 0,
                $"the wood taken away: the parts that were ready are red now, nothing stays light blue: {tints}");

            // ---- 3. loaded again from disk: the same site, what is built read from the world ----
            var was = site;
            SiteStore.Load();
            site = SiteStore.All.FirstOrDefault();
            Check(SiteStore.All.Count == 1 && site != null && site != was && site.Name == was.Name && site.Path == was.Path
                && Vector3.Distance(site.RootPosition, was.RootPosition) < 0.001f && Mathf.Abs(Mathf.DeltaAngle(site.RootYaw, was.RootYaw)) < 0.1f
                && site.Total == was.Total,
                $"Load reads the same site back: {SiteStore.All.Count}, {site?.Name} at {(site != null ? V4(site.RootPosition) : "none")}");
            Check(was.Ghost == null, "and the old one's ghost is gone");
            if (site == null)
            {
                yield break;
            }

            Check(site.BuiltCount == 0, $"nothing built is stored: right after Load it knows {site.BuiltCount} built");
            SiteTracker.Refresh(player);
            Check(site.BuiltCount == was.BuiltCount, $"after a refresh the world says {site.BuiltCount} built, as before ({was.BuiltCount})");
            yield return WaitForGhosts(1);

            // ---- 4. an empty click somewhere else: a second site with nothing built ----
            yield return TeleportNear(player, spot + Vector3.up, BuildFacing, "back to the build spot");
            ClearInventoryExceptHammer(player);
            yield return AimBlueprint(player, resolved, Quaternion.Euler(0f, 180f, 0f));
            var secondRoot = BlueprintMode.PreviewRoot;
            var secondPos = secondRoot != null ? secondRoot.position : Vector3.zero;
            var empty = BlueprintMode.TryBuild(player);
            said = BlueprintMode.LastMessage ?? "";
            BlueprintMode.Exit();
            yield return null;
            var second = SiteStore.All.FirstOrDefault(s => s != site);
            Check(!empty && said.StartsWith("Workshop planned, nothing built yet.") && SiteStore.All.Count == 2 && second != null,
                $"an empty click behind the player keeps a second site: {SiteStore.All.Count}: '{said}'");
            if (second == null)
            {
                yield break;
            }

            Check(Vector3.Distance(second.RootPosition, secondPos) < 0.001f && Flat(second.RootPosition, site.RootPosition) > 5f,
                $"it stands where that preview stood, {Flat(second.RootPosition, site.RootPosition):0.0} m from the first");
            SiteTracker.Refresh(player);
            Check(second.BuiltCount == 0, $"with nothing built: {second.BuiltCount}");
            yield return WaitForGhosts(2);
            Check(second.Ghost != null && second.Ghost.VisibleParts == second.Total,
                $"its ghost shows every part: {(second.Ghost != null ? second.Ghost.VisibleParts : -1)} of {second.Total}");

            // ---- 5. the hammer away: no ghost. Out: ghosts. 100 m away: none ----
            var hammer = player.GetRightItem();
            player.UnequipItem(hammer);
            yield return new WaitForSeconds(0.3f);
            SiteTracker.Refresh(player);
            Check(!player.InPlaceMode() && SiteStore.All.All(s => s.Ghost == null || !s.Ghost.Visible),
                "hammer put away: no ghost is visible");
            yield return EquipHammer(player);
            SiteTracker.Refresh(player);
            Check(SiteStore.All.All(s => s.Ghost != null && s.Ghost.Visible), "hammer out: both ghosts are visible");

            var far = spot + (Vector3.right * 100f);
            far.y = WorldGenerator.instance.GetHeight(far.x, far.z) + 1f;
            yield return TeleportNear(player, far, BuildFacing, "100 m away");
            SiteTracker.Refresh(player);
            var nearest = SiteStore.All.Min(s => Mathf.Sqrt(s.WorldBox.SqrDistance(player.transform.position)));
            Check(nearest > SiteTracker.ShowRange && SiteStore.All.All(s => s.Ghost == null || !s.Ghost.Visible),
                $"100 m away (the nearest site is {nearest:0} m off) no ghost is visible");
            yield return TeleportNear(player, spot + Vector3.up, BuildFacing, "back to the build spot");
            SiteTracker.Refresh(player);
            Check(SiteStore.All.All(s => s.Ghost != null && s.Ghost.Visible), "back near them, the ghosts show again");

            // ---- 6. a built piece of the first site destroyed: its ghost comes back on the next refresh ----
            var standing = MatchParts(site.Resolved, site.RootPosition, site.RootRotation, PiecesAround(player, site.RootPosition));
            var gone = standing.Keys.Where(i => site.Resolved.Parts[i].Prefab.name == "piece_groundtorch_wood").DefaultIfEmpty(standing.Keys.LastOrDefault()).First();
            var builtBefore = site.BuiltCount;
            if (!standing.ContainsKey(gone))
            {
                Check(false, "a built part of the first site stands in the world");
                yield break;
            }

            ZNetScene.instance.Destroy(standing[gone].gameObject);
            var waited = 0f;
            while (site.BuiltCount == builtBefore && waited < SiteTracker.Period + 1f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(site.BuiltCount == builtBefore - 1 && !site.Built[gone],
                $"{site.Resolved.Parts[gone].Prefab.name} destroyed: {site.BuiltCount} built after {waited:0.0} s, was {builtBefore}");
            Check(site.Ghost != null && site.Ghost.LookOf(gone) != PartLook.Hidden && site.Ghost.Part(gone).gameObject.activeSelf
                && site.Ghost.VisibleParts == site.Total - site.BuiltCount,
                $"and its ghost shows again: {(site.Ghost != null ? site.Ghost.LookOf(gone).ToString() : "no ghost")}, {(site.Ghost != null ? site.Ghost.VisibleParts : -1)} visible");

            // ---- the second site built in full, piece by piece: it is finished and its file is gone ----
            var secondPath = second.Path;
            for (var i = 0; i < second.Total; i++)
            {
                player.PlacePiece(second.Resolved.Parts[i].Piece, second.WorldPosition(i), second.WorldRotation(i), false);
            }

            yield return null;
            SiteTracker.Refresh(player);
            Check(!SiteStore.All.Contains(second) && !File.Exists(secondPath) && SiteTracker.LastMessage == "Workshop finished.",
                $"every part of the second site standing: '{SiteTracker.LastMessage}', {SiteStore.All.Count} sites left, file there: {File.Exists(secondPath)}");
            Check(SiteStore.All.Count == 1 && SiteStore.All[0] == site && File.Exists(site.Path), "the first site is still kept");

            // ---- 7. no world object under -1000 m ----
            var deepAfter = DeepObjects(out var sample);
            Check(deepAfter == deepBefore, $"the ghosts left nothing in the world: {deepBefore} deep objects before, {deepAfter} after"
                + (deepAfter != deepBefore ? " | " + sample : ""));

            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            ClearSites();
            yield return new WaitForSeconds(1f);
        }

        /// <summary>Waits until this many sites have a ghost that is done, at most 3 s.</summary>
        private static IEnumerator WaitForGhosts(int count)
        {
            var waited = 0f;
            while (SiteStore.All.Count(s => s.Ghost != null && s.Ghost.Done && s.Ghost.Visible) < count && waited < 3f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Log($"{count} site ghosts done after {waited:0.00} s");
        }

        /// <summary>One letter per part: H hidden, R ready, W waiting.</summary>
        private static string Looks(Site site)
        {
            if (site.Ghost == null)
            {
                return "no ghost";
            }

            return new string(Enumerable.Range(0, site.Total).Select(i =>
                site.Ghost.LookOf(i) == PartLook.Hidden ? 'H' : site.Ghost.LookOf(i) == PartLook.Ready ? 'R' : 'W').ToArray());
        }

        /// <summary>
        /// The tint MaterialMan put on a ghost copy, read back from every mesh renderer's property block,
        /// switched-off ones too: 'b' the light blue, 'r' the red (each with its glow in _EmissionColor),
        /// '-' none, '?' anything else or renderers that disagree.
        /// </summary>
        private static char TintOf(Piece copy)
        {
            if (copy == null)
            {
                return '?';
            }

            bool Wears(MaterialPropertyBlock b, Color colour)
            {
                bool Near(Color a, Color c) => Mathf.Abs(a.r - c.r) < 0.01f && Mathf.Abs(a.g - c.g) < 0.01f && Mathf.Abs(a.b - c.b) < 0.01f;
                return b.HasColor(ShaderProps._Color) && b.HasColor(ShaderProps._EmissionColor)
                    && Near(b.GetColor(ShaderProps._Color), colour) && Near(b.GetColor(ShaderProps._EmissionColor), colour * BlueprintPreview.TintGlow);
            }

            var block = new MaterialPropertyBlock();
            var found = ' ';
            foreach (var renderer in copy.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.GetPropertyBlock(block);
                var tint = block.isEmpty || (!block.HasColor(ShaderProps._Color) && !block.HasColor(ShaderProps._EmissionColor)) ? '-'
                    : Wears(block, BlueprintPreview.ReadyTint) ? 'b'
                    : Wears(block, BlueprintPreview.RedTint) ? 'r'
                    : '?';
                if (found != ' ' && found != tint)
                {
                    return '?';
                }

                found = tint;
            }

            return found == ' ' ? '-' : found;
        }

        /// <summary>One letter per part of a site's ghost: its tint (<see cref="TintOf"/>).</summary>
        private static string Tints(Site site)
        {
            return site.Ghost == null ? "no ghost" : new string(Enumerable.Range(0, site.Total).Select(i => TintOf(site.Ghost.Part(i))).ToArray());
        }

        /// <summary>The tint each part's look asks for: hidden none, ready light blue, waiting red.</summary>
        private static string Wanted(Site site)
        {
            return site.Ghost == null ? "no ghost" : new string(Enumerable.Range(0, site.Total).Select(i =>
                site.Ghost.LookOf(i) == PartLook.Hidden ? '-' : site.Ghost.LookOf(i) == PartLook.Ready ? 'b' : 'r').ToArray());
        }

        /// <summary>A short teleport the game's way: waits out the 2 s cooldown, then faces the given way.</summary>
        private static IEnumerator TeleportNear(Player player, Vector3 to, Quaternion facing, string what)
        {
            var waited = 0f;
            while (player.m_teleportCooldown < 2.1f && waited < 5f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(player.TeleportTo(to, facing, false), "teleport " + what);
            while (player.IsTeleporting())
            {
                yield return null;
            }

            player.m_lookYaw = facing;
            player.transform.rotation = facing;
            player.m_body.rotation = facing;
            yield return new WaitForSeconds(0.5f);
        }

        // ---------- scenario: build_continue ----------

        /// <summary>
        /// Near an unfinished build the key offers "Continue: Workshop (6/16)" first. The preview is the
        /// site's own ghost and stays put, a click builds what the materials pay for now, the last one
        /// finishes it, Remove removes it (at once, or through the window on the keyboard, the mouse and
        /// the pad), and a part the player stands in is left out.
        /// </summary>
        private static IEnumerator TestBuildContinue(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            ClearSites();
            yield return new WaitForSeconds(0.5f);
            PieceCatalog.Ensure();

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop");
            ResolvedBlueprint resolved = null;
            var error = "no Workshop kit";
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out error))
            {
                Check(false, "the workshop kit resolves: " + error);
                yield break;
            }

            Unlock(player, resolved);
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);
            BlueprintLibrary.Reload();
            var deepBefore = DeepObjects(out _);
            var spot = player.transform.position;
            Log("the kit's parts: " + string.Join(", ", resolved.Parts.Select((p, i) =>
                $"{i} {p.Prefab.name} costs " + string.Join("+", PartialBuild.CostOf(p.Piece).Select(c => $"{c.Value} {c.Key}")))));

            yield return ContinueToTheEnd(player, resolved);
            yield return ContinueInThirds(player, resolved);
            yield return ContinueRemoveNothing(player, resolved);
            yield return ContinueRemoveWindow(player, resolved);
            yield return ContinueRemovePad(player, resolved);
            yield return ContinueRemoveRefused(player, resolved, spot);
            yield return ContinueFarAway(player, resolved, spot);
            yield return ContinueStandingInside(player);

            BlueprintMode.Exit();
            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            ClearSites();
            player.m_lastToolUseTime = 0f;
            yield return new WaitForSeconds(1f);
            var deepAfter = DeepObjects(out var sample);
            Check(deepAfter == deepBefore, $"the ghosts left nothing in the world: {deepBefore} deep objects before, {deepAfter} after"
                + (deepAfter != deepBefore ? " | " + sample : ""));
            var left = new List<Piece>();
            Piece.GetAllPiecesInRadius(player.transform.position, 60f, left);
            var mine = left.Count(p => p != null && p.GetCreator() == player.GetPlayerID());
            Check(mine == 0, $"nothing of the test is left standing: {mine}");
        }

        /// <summary>
        /// A new unfinished build in front of the player: these materials, one click (the pad's R2 when
        /// <paramref name="pad"/>), which goes straight to Continue on it; then blueprint mode off.
        /// </summary>
        private static IEnumerator StartSite(Player player, ResolvedBlueprint kit, Func<string, int, int> give, Quaternion? facing = null, bool pad = false)
        {
            ClearInventoryExceptHammer(player);
            foreach (var cost in kit.TotalCost)
            {
                AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, give(cost.m_resItem.m_itemData.m_shared.m_name, cost.m_amount));
            }

            yield return AimBlueprint(player, kit, facing);
            var sites = SiteStore.All.Count;
            if (pad)
            {
                WorldPadOn();
                yield return PadPlace();
                yield return WorldPadOff();
            }
            else
            {
                BlueprintMode.TryBuild(player);
            }

            var said = BlueprintMode.LastMessage ?? "";
            yield return CheckContinuesAfterClick(sites, pad ? "set-up through the pad's R2" : "set-up");
            BlueprintMode.Exit();
            yield return null;
            yield return null;
            var site = BlueprintMode.LastSite;
            Check(SiteStore.All.Count == sites + 1 && site != null && SiteStore.All.Contains(site),
                $"set-up: the click kept an unfinished build, {(site != null ? site.BuiltCount : -1)} of {kit.Parts.Count} built: '{said}'");
            yield return WaitForGhosts(SiteStore.All.Count);
        }

        /// <summary>The key pressed once from normal building: the first entry.</summary>
        private static void FirstEntry(Player player)
        {
            BlueprintMode.Exit();
            BlueprintMode.Cycle(player);
        }

        /// <summary>
        /// After a click that left parts unbuilt (even all of them): blueprint mode went straight to
        /// Continue on the build that click kept, as if the key had picked it. Its label is the key's,
        /// the preview is that build's ghost (no aim preview left), and the card says Continue.
        /// </summary>
        private static IEnumerator CheckContinuesAfterClick(int sitesBefore, string what)
        {
            var site = BlueprintMode.LastSite;
            var kept = site != null && SiteStore.All.Count == sitesBefore + 1 && SiteStore.All.Contains(site);
            var entry = BlueprintMode.EntryName ?? "";
            Check(kept && BlueprintMode.Active && BlueprintMode.CurrentSite == site && BlueprintMode.Current == site.Resolved
                && entry.StartsWith($"Continue: {site.Name} ({site.BuiltCount}/{site.Total})"),
                $"{what}: the click went straight to Continue on the build it kept: '{entry}'");
            if (!kept)
            {
                yield break;
            }

            var waited = 0f;
            while ((site.Ghost == null || !site.Ghost.Done || !site.Ghost.Visible) && waited < 3f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return CardRefreshed();
            var root = BlueprintMode.PreviewRoot;
            Check(BlueprintMode.CurrentSite == site && root != null && site.Ghost != null && root == site.Ghost.Root && site.Ghost.Visible
                && GameObject.Find("ValheimTomrer_Blueprint_" + site.Name) == null,
                $"{what}: the preview is that build's ghost ({waited:0.00} s to show), no aim preview is left");
            var title = Hud.instance.m_buildSelection.text;
            var list = BlueprintInfoCard.List;
            var footer = list != null ? list.Footer.text : "";
            Check(title == $"Continue: {site.Name}" && BlueprintInfoCard.Visible && footer.StartsWith($"Built {site.BuiltCount} of {site.Total}."),
                $"{what}: the card reads '{title}', its list '{footer}'");
        }

        /// <summary>
        /// After a click that put the last part up: blueprint mode is off and the hammer is back on its
        /// normal piece, as picking a piece from the build menu leaves it. The game's own ghost of that
        /// piece shows where the player aims (Repair and Remove have none in the game), and the card
        /// shows that piece with no materials list.
        /// </summary>
        private static IEnumerator CheckBackToPiece(Player player, string what)
        {
            var off = !BlueprintMode.Active && BlueprintMode.CurrentSite == null && BlueprintMode.EntryName == null;
            yield return Frames(3);
            var piece = player.GetSelectedPiece();
            var noGhost = piece != null && (piece.m_repairPiece || piece.m_removePiece);
            var ghost = player.m_placementGhost;
            for (var pitch = 15f; !noGhost && pitch <= 60f && !(ghost != null && ghost.activeSelf); pitch += 3f)
            {
                player.m_lookPitch = pitch;
                yield return Frames(2);
                ghost = player.m_placementGhost;
            }

            var same = piece != null && (noGhost ? ghost == null
                : ghost != null && ghost.activeSelf && Utils.GetPrefabName(ghost) == piece.gameObject.name);
            Check(off && !BlueprintMode.Active && BlueprintMode.PreviewRoot == null && same,
                $"{what}: blueprint mode is off at once ({off}) and the hammer shows its own piece again:"
                + $" selected {(piece != null ? piece.gameObject.name : "none")}, the game's ghost"
                + $" {(ghost != null ? Utils.GetPrefabName(ghost) + (ghost.activeSelf ? " shown" : " hidden") : "none")}{(noGhost ? " (the game draws none for it)" : "")}");
            var title = Hud.instance.m_buildSelection.text;
            Check(piece != null && title == Localization.instance.Localize(piece.m_name) && !BlueprintInfoCard.Visible,
                $"{what}: the card shows that piece ('{title}') and no materials list (list shown: {BlueprintInfoCard.Visible})");
        }

        /// <summary>
        /// The game's build button (JoyPlace) on the world controller, once. R2 in the game's layouts:
        /// a trigger, so an axis pushed all the way, not a button.
        /// </summary>
        private static IEnumerator PadPlace()
        {
            var place = WorldPad.ButtonOf("JoyPlace") ?? PadButton.R2;
            Log($"the game's JoyPlace is {place} on the pad");
            if (place != PadButton.R2 && place != PadButton.L2)
            {
                yield return PadPress(false, PadKeyOf(place));
                yield break;
            }

            var state = new UnityEngine.InputSystem.LowLevel.GamepadState();
            if (place == PadButton.R2)
            {
                state.rightTrigger = 1f;
            }
            else
            {
                state.leftTrigger = 1f;
            }

            UnityEngine.InputSystem.InputSystem.QueueStateEvent(_worldPad, state);
            yield return Frames(3);
            PadDown(false);
            yield return Frames(2);
        }

        /// <summary>The site's parts standing in the world now, found the tracker's way.</summary>
        private static int Standing(Player player, Site site)
        {
            return MatchParts(site.Resolved, site.RootPosition, site.RootRotation, PiecesAround(player, site.RootPosition)).Count;
        }

        /// <summary>
        /// Checks 1 and 2: a site from half the wood, the rest in a chest 5 m away. The key offers
        /// Continue first, the ghost stays put when the camera turns and the wheel turns, and one click
        /// finishes it from the chest.
        /// </summary>
        private static IEnumerator ContinueToTheEnd(Player player, ResolvedBlueprint kit)
        {
            yield return StartSite(player, kit, (item, need) => item == "$item_wood" ? need / 2 : need);
            var site = BlueprintMode.LastSite;
            if (site == null || !SiteStore.All.Contains(site))
            {
                yield break;
            }

            Check(site.BuiltCount > 0 && site.BuiltCount < site.Total, $"half the wood built {site.BuiltCount} of {site.Total}");
            var chests = new List<Piece>();
            yield return PlaceTestPiece(player, "piece_chest_wood", OnGround(player.transform.position, 180f, 5f), 0f, chests);
            yield return new WaitForSeconds(0.5f);
            var chest = chests[0] != null ? chests[0].GetComponentInChildren<Container>() : null;
            Check(chest != null, "a wood chest stands 5 m behind the player");
            if (chest == null)
            {
                yield break;
            }

            // Exactly what the rest costs goes into the chest; the bag is empty.
            var rest = PartialBuild.Missing(site.Resolved, site.Built, MaterialSources.Around(player));
            foreach (var item in rest)
            {
                AddTo(chest.GetInventory(), kit.TotalCost.First(c => c.m_resItem.m_itemData.m_shared.m_name == item.Key).m_resItem.gameObject.name, item.Value);
            }

            Check(rest.Count > 0, "the rest goes into the chest: " + PartialBuild.MissingText(rest));

            // ---- 1. the key: Continue first, locked onto the site's own ghost ----
            FirstEntry(player);
            yield return null;
            yield return null;
            var entry = BlueprintMode.EntryName ?? "";
            Check(BlueprintMode.CurrentSite == site && entry.StartsWith("Continue:") && entry == $"Continue: Workshop ({site.BuiltCount}/{site.Total})",
                $"the key's first entry continues the site: '{entry}'");

            // Square on the pad offers the same first entry.
            BlueprintMode.Exit();
            WorldPadOn();
            yield return PadPress(false, PadKey.West);
            yield return WorldPadOff();
            Check(BlueprintMode.CurrentSite == site && BlueprintMode.EntryName == entry,
                $"square offers the same first entry: '{BlueprintMode.EntryName}'");
            var root = BlueprintMode.PreviewRoot;
            Check(root != null && site.Ghost != null && root == site.Ghost.Root && GameObject.Find("ValheimTomrer_Blueprint_Workshop") == null,
                "the preview is the site's ghost, no second copy is made");
            if (root == null)
            {
                yield break;
            }

            var pos = root.position;
            var rot = root.rotation;
            Check(Vector3.Distance(pos, site.RootPosition) < 0.001f && Quaternion.Angle(rot, site.RootRotation) < 0.1f,
                $"it stands at the site's pose: {V4(pos)}, turned {rot.eulerAngles.y:0.#}");

            var face = Quaternion.Euler(0f, player.m_lookYaw.eulerAngles.y + 90f, 0f);
            player.m_lookYaw = face;
            player.transform.rotation = face;
            player.m_body.rotation = face;
            for (var frame = 0; frame < 10; frame++)
            {
                yield return null;
            }

            Check(BlueprintMode.CurrentSite == site && Vector3.Distance(root.position, pos) < 0.001f && Quaternion.Angle(root.rotation, rot) < 0.01f,
                $"the camera turned 90 degrees, the ghost did not move: {Vector3.Distance(root.position, pos):0.####} m, {Quaternion.Angle(root.rotation, rot):0.##} degrees");
            var steps = BlueprintMode.RotationSteps;
            yield return WheelNotch(1f);
            Check(BlueprintMode.RotationSteps == steps && Quaternion.Angle(root.rotation, rot) < 0.01f && Vector3.Distance(root.position, pos) < 0.001f,
                $"a wheel notch does not turn it: steps {steps} -> {BlueprintMode.RotationSteps}, yaw {rot.eulerAngles.y:0.#} -> {root.eulerAngles.y:0.#}");

            player.m_lookYaw = BuildFacing;
            player.transform.rotation = BuildFacing;
            player.m_body.rotation = BuildFacing;
            player.m_lookPitch = 10f;
            SiteTracker.Refresh(player);
            yield return new WaitForSeconds(1f);
            yield return Screenshot("build-continue-1-locked");

            // ---- 2. the click finishes it from the chest ----
            var path = site.Path;
            player.m_lastToolUseTime = 0f;
            var clicked = BlueprintMode.TryBuild(player);
            var said = BlueprintMode.LastMessage ?? "";
            yield return null;
            var standing = Standing(player, site);
            Check(clicked && standing == site.Total, $"the click builds every piece left: {standing} of {site.Total} stand");
            Check(!File.Exists(path) && SiteStore.All.Count == 0, $"the file is gone ({File.Exists(path)}) and no site is kept ({SiteStore.All.Count})");
            Check(said == "Workshop finished." && !BlueprintMode.Active, $"'{said}', and Continue ended: active {BlueprintMode.Active}");
            var inChest = kit.TotalCost.Sum(c => Held(chest.GetInventory(), c.m_resItem.m_itemData.m_shared.m_name));
            var inBag = kit.TotalCost.Sum(c => Held(player.GetInventory(), c.m_resItem.m_itemData.m_shared.m_name));
            Check(inChest == 0 && inBag == 0, $"the chest is empty ({inChest} left) and so is the bag ({inBag})");
            yield return CheckBackToPiece(player, "Continue's last click");

            yield return new WaitForSeconds(15f);
            standing = Standing(player, site);
            Check(standing == site.Total, $"after 15 s nothing fell: {standing} of {site.Total}");
            player.m_lookPitch = 10f;
            yield return Screenshot("build-continue-2-finished");

            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>Check 3: the wood in three parts, three clicks. Each one builds more, the third (the pad's R2) finishes it.</summary>
        private static IEnumerator ContinueInThirds(Player player, ResolvedBlueprint kit)
        {
            var wood = kit.TotalCost.First(c => c.m_resItem.m_itemData.m_shared.m_name == "$item_wood");
            var third = wood.m_amount / 3;
            var first = wood.m_amount - (2 * third);
            yield return StartSite(player, kit, (item, need) => item == "$item_wood" ? first : need);
            var site = BlueprintMode.LastSite;
            if (site == null || !SiteStore.All.Contains(site))
            {
                yield break;
            }

            var path = site.Path;
            var counts = new List<int> { site.BuiltCount };
            Check(site.BuiltCount > 0, $"{first} of {wood.m_amount} wood: the first click built {site.BuiltCount}");
            FirstEntry(player);
            Check(BlueprintMode.CurrentSite == site, "the key continues it: " + BlueprintMode.EntryName);
            for (var click = 2; click <= 3; click++)
            {
                AddTo(player.GetInventory(), wood.m_resItem.gameObject.name, third);
                player.m_lastToolUseTime = 0f;
                bool clicked;
                string said;
                if (click == 2)
                {
                    clicked = BlueprintMode.TryBuild(player);
                    said = BlueprintMode.LastMessage ?? "";
                    yield return null;
                }
                else
                {
                    // The last one through the pad's R2: the same click, the same end.
                    WorldPadOn();
                    yield return PadPlace();
                    said = BlueprintMode.LastMessage ?? "";
                    clicked = said == "Workshop finished.";
                    yield return WorldPadOff();
                }

                counts.Add(site.BuiltCount);
                Check(clicked && counts[click - 1] > counts[click - 2],
                    $"{third} more wood, click {click}: {counts[click - 2]} -> {counts[click - 1]} built: '{said}'");
                if (click == 2)
                {
                    Check(BlueprintMode.CurrentSite == site && SiteStore.All.Contains(site), "still in Continue after the second click");
                }

                yield return new WaitForSeconds(0.5f);
            }

            Check(counts.Last() == site.Total && !SiteStore.All.Contains(site) && !File.Exists(path) && !BlueprintMode.Active
                && Standing(player, site) == site.Total,
                $"done after the third (R2 on the pad): {string.Join(" -> ", counts)} of {site.Total}, site kept: {SiteStore.All.Contains(site)}");
            yield return CheckBackToPiece(player, "Continue's last click through R2");

            yield return new WaitForSeconds(1f);
            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(1f);
        }

        // ---------- build_continue: removing an unfinished build ----------

        /// <summary>Check 4a: none of the build stands. One Remove forgets the plan at once: no window, file and ghost gone, blueprint mode off.</summary>
        private static IEnumerator ContinueRemoveNothing(Player player, ResolvedBlueprint kit)
        {
            yield return StartSite(player, kit, (item, need) => 0);
            var site = BlueprintMode.LastSite;
            if (site == null || !SiteStore.All.Contains(site))
            {
                yield break;
            }

            var path = site.Path;
            FirstEntry(player);
            Check(BlueprintMode.CurrentSite == site && Standing(player, site) == 0, $"the key continues a build with nothing built: {BlueprintMode.EntryName}");
            var opened = SiteRemovePopup.Opened;
            yield return PressBound("Remove");
            yield return Frames(2);
            var said = BlueprintMode.LastMessage ?? "";
            Check(SiteRemovePopup.Opened == opened && !SiteRemovePopup.IsOpen && !UnifiedPopup.IsVisible(), "nothing built: one Remove opens no window");
            Check(!SiteStore.All.Contains(site) && !File.Exists(path) && site.Ghost == null && !BlueprintMode.Active && BlueprintMode.CurrentSite == null,
                $"and the plan is gone at once: site kept {SiteStore.All.Contains(site)}, file {File.Exists(path)}, ghost {site.Ghost != null}, blueprint mode {BlueprintMode.Active}");
            Check(said == "Plan for Workshop removed." && !said.Contains("again"), $"it says so, no \"press again\": '{said}'");
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>
        /// Check 4b, keyboard and mouse: part of it stands, so Remove opens the game's popup with three
        /// buttons. While it is up the game holds the player; Cancel (the button, then Esc) changes
        /// nothing; Unbuilt parts forgets the plan and leaves what stands.
        /// </summary>
        private static IEnumerator ContinueRemoveWindow(Player player, ResolvedBlueprint kit)
        {
            yield return StartSite(player, kit, (item, need) => item == "$item_wood" ? need / 2 : need);
            var site = BlueprintMode.LastSite;
            if (site == null || !SiteStore.All.Contains(site))
            {
                yield break;
            }

            var path = site.Path;
            var built = Standing(player, site);
            FirstEntry(player);
            Check(BlueprintMode.CurrentSite == site && built > 0 && built < site.Total, $"the key continues it, {built} of {site.Total} built");

            // ---- Remove: the window, and the press did nothing else ----
            yield return PressBound("Remove");
            yield return Frames(2);
            CheckRemoveWindow(player, site, built, "Remove (keyboard)");
            Check(SiteRemovePopup.Selected() == null, "with the mouse no button is picked at first");
            yield return Screenshot("remove-window-keys");

            // ---- while it is up the game holds the player ----
            var at = player.transform.position;
            var entry = BlueprintMode.EntryName;
            var bag = player.GetInventory().GetAllItems().Sum(i => i.m_stack);
            yield return HoldKey(UnityEngine.InputSystem.Key.W, 0.6f);
            yield return PressKey(UnityEngine.InputSystem.Key.B);
            yield return ClickScreen(new Vector2(Screen.width * 0.12f, Screen.height * 0.5f));
            yield return PressKey(UnityEngine.InputSystem.Key.Tab);
            yield return PressKey(UnityEngine.InputSystem.Key.M);
            yield return PressKey(EditorConfig.Key.Value == KeyCode.F7 ? UnityEngine.InputSystem.Key.F7 : UnityEngine.InputSystem.Key.F8);
            yield return Frames(3);
            var moved = Vector3.Distance(player.transform.position, at);
            Check(moved < 0.05f && BlueprintMode.CurrentSite == site && BlueprintMode.EntryName == entry && Standing(player, site) == built
                && player.GetInventory().GetAllItems().Sum(i => i.m_stack) == bag && !InventoryGui.IsVisible()
                && Minimap.instance.m_mode != Minimap.MapMode.Large && !ModUi.Open && SiteRemovePopup.Showing,
                $"while it is up W, B, a click beside it, Tab, M and F7 do nothing: moved {moved:0.###} m, entry '{BlueprintMode.EntryName}',"
                + $" {Standing(player, site)} standing, inventory {InventoryGui.IsVisible()}, map {Minimap.instance.m_mode}, editor {ModUi.Open}, still up {SiteRemovePopup.Showing}");

            // ---- Cancel: the button, then Esc ----
            yield return ClickButton(SiteRemovePopup.ButtonFor(SiteRemovePopup.Choice.Cancel));
            yield return CheckRemoveCancelled(player, site, path, built, "a click on Cancel");

            yield return PressBound("Remove");
            yield return Frames(2);
            Check(SiteRemovePopup.Showing, "Remove opens it again");
            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return CheckRemoveCancelled(player, site, path, built, "Esc");

            // ---- Unbuilt parts: the plan goes, what stands stays ----
            yield return PressBound("Remove");
            yield return Frames(2);
            Check(SiteRemovePopup.Showing, "Remove opens it a third time");
            yield return ClickButton(SiteRemovePopup.ButtonFor(SiteRemovePopup.Choice.UnbuiltParts));
            yield return Frames(3);
            var said = BlueprintMode.LastMessage ?? "";
            Check(!SiteRemovePopup.IsOpen && !UnifiedPopup.IsVisible() && SiteRemovePopup.LastChoice == SiteRemovePopup.Choice.UnbuiltParts,
                $"a click on Unbuilt parts closes it: {SiteRemovePopup.LastChoice}");
            Check(!SiteStore.All.Contains(site) && !File.Exists(path) && site.Ghost == null && !BlueprintMode.Active,
                $"Unbuilt parts: plan kept {SiteStore.All.Contains(site)}, file {File.Exists(path)}, ghost {site.Ghost != null}, blueprint mode {BlueprintMode.Active}");
            Check(Standing(player, site) == built, $"the built pieces are still in the world: {Standing(player, site)} of {built}");
            Check(said == $"Plan for Workshop removed. The {built} built pieces stay.", $"and it says so: '{said}'");

            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Check 4c, the pad alone: R2 builds part of it, square continues it, R1 opens the window on
        /// Cancel. Cross there and circle cancel; the stick, R2, square and triangle do nothing while it
        /// is up; the D-pad walks the three buttons; cross on Whole structure takes every built piece
        /// down and the materials come back as the hammer's Remove gives them.
        /// </summary>
        private static IEnumerator ContinueRemovePad(Player player, ResolvedBlueprint kit)
        {
            yield return StartSite(player, kit, (item, need) => item == "$item_wood" ? need / 2 : need, pad: true);
            var site = BlueprintMode.LastSite;
            if (site == null || !SiteStore.All.Contains(site))
            {
                yield break;
            }

            var path = site.Path;
            var built = Standing(player, site);
            var remove = WorldPad.ButtonOf("JoyRemove") ?? PadButton.R1;
            WorldPadOn();
            yield return PadPress(false, PadKey.West);
            Check(BlueprintMode.CurrentSite == site, "square continues it: " + BlueprintMode.EntryName);

            // ---- R1: the window, on Cancel. Cross there cancels, and the player does not jump ----
            yield return PadPress(false, PadKeyOf(remove));
            yield return Frames(3);
            CheckRemoveWindow(player, site, built, $"{remove} on the pad");
            var cancel = SiteRemovePopup.ButtonFor(SiteRemovePopup.Choice.Cancel);
            var unbuilt = SiteRemovePopup.ButtonFor(SiteRemovePopup.Choice.UnbuiltParts);
            var whole = SiteRemovePopup.ButtonFor(SiteRemovePopup.Choice.WholeStructure);
            Check(SiteRemovePopup.Selected() == cancel.gameObject, $"the pad starts on Cancel: {Name(SiteRemovePopup.Selected())}");
            string Hint(UnityEngine.UI.Button b)
            {
                var hint = b.transform.Find("gamepad_hint");
                var label = hint != null ? hint.GetComponentInChildren<TMPro.TMP_Text>(true) : null;
                return hint != null && hint.gameObject.activeInHierarchy && label != null ? label.text : "-";
            }

            var circle = Localization.instance.Localize("$KEY_JoyButtonB");
            var crossGlyph = Localization.instance.Localize("$KEY_JoyButtonA");
            Check(circle.StartsWith("<sprite") && Hint(cancel) == circle && Hint(unbuilt) == "-" && Hint(whole) == "-",
                $"the game's circle icon on Cancel's corner, none on the other two: '{Hint(cancel)}', '{Hint(unbuilt)}', '{Hint(whole)}'");
            var rise = 0f;
            PadDown(false, PadKey.South);
            yield return Frames(3);
            PadDown(false);
            yield return Rise(player, 1f, r => rise = r);
            yield return CheckRemoveCancelled(player, site, path, built, "cross on Cancel");
            Check(rise < 0.2f, $"cross closed it and did not also jump: rose {rise:0.##} m");

            yield return PadPress(false, PadKeyOf(remove));
            yield return Frames(3);
            Check(SiteRemovePopup.Showing, $"{remove} opens it again");
            PadDown(false, PadKey.East);
            yield return Frames(3);
            PadDown(false);
            yield return Rise(player, 1f, r => rise = r);
            yield return CheckRemoveCancelled(player, site, path, built, "circle");
            Check(rise < 0.2f, $"circle did nothing else: rose {rise:0.##} m");

            // ---- up again: the stick, R2, square and triangle do nothing ----
            yield return PadPress(false, PadKeyOf(remove));
            yield return Frames(3);
            Check(SiteRemovePopup.Showing && SiteRemovePopup.Selected() == cancel.gameObject, $"{remove} opens it a third time, on Cancel");
            var at = player.transform.position;
            var entry = BlueprintMode.EntryName;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(_worldPad, new UnityEngine.InputSystem.LowLevel.GamepadState { leftStick = new Vector2(0f, 1f) });
            yield return new WaitForSeconds(0.6f);
            PadDown(false);
            yield return Frames(2);
            yield return PadPlace();
            yield return PadPress(false, PadKey.West);
            yield return PadPress(false, PadKey.North);
            yield return Frames(3);
            var moved = Vector3.Distance(player.transform.position, at);
            Check(moved < 0.05f && BlueprintMode.CurrentSite == site && BlueprintMode.EntryName == entry && Standing(player, site) == built
                && !InventoryGui.IsVisible() && SiteRemovePopup.Showing && SiteRemovePopup.Selected() == cancel.gameObject,
                $"while it is up the stick, R2, square and triangle do nothing: moved {moved:0.###} m, entry '{BlueprintMode.EntryName}',"
                + $" {Standing(player, site)} standing, inventory {InventoryGui.IsVisible()}, on {Name(SiteRemovePopup.Selected())}");

            // ---- the D-pad walks the row, and stops at its ends ----
            var walk = new List<string> { Name(SiteRemovePopup.Selected()) };
            foreach (var step in new[] { PadKey.DpadRight, PadKey.DpadRight, PadKey.DpadRight, PadKey.DpadLeft, PadKey.DpadRight })
            {
                yield return PadPress(false, step);
                yield return Frames(2);
                walk.Add(Name(SiteRemovePopup.Selected()));
            }

            var names = new[] { cancel, unbuilt, whole, whole, unbuilt, whole }.Select(b => b.name).ToList();
            Check(walk.SequenceEqual(names), $"right, right, right, left, right: {string.Join(" -> ", walk)}");
            Check(crossGlyph.StartsWith("<sprite") && Hint(whole) == crossGlyph && Hint(unbuilt) == "-" && Hint(cancel) == circle,
                $"on Whole structure the cross icon moved to it, circle stays on Cancel: '{Hint(cancel)}', '{Hint(unbuilt)}', '{Hint(whole)}'");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("remove-window-pad");

            // ---- cross on Whole structure ----
            var pieces = MatchParts(site.Resolved, site.RootPosition, site.RootRotation, PiecesAround(player, site.RootPosition)).Values.ToList();
            var back = Recoverable(pieces);
            var centre = site.WorldBox.center;
            var before = back.Keys.ToDictionary(k => k, k => Around(player, centre, k));
            PadDown(false, PadKey.South);
            yield return Frames(3);
            PadDown(false);
            var said = BlueprintMode.LastMessage ?? "";
            yield return Rise(player, 1f, r => rise = r);
            Check(!SiteRemovePopup.IsOpen && SiteRemovePopup.LastChoice == SiteRemovePopup.Choice.WholeStructure && rise < 0.2f,
                $"cross on Whole structure closes it, no jump: {SiteRemovePopup.LastChoice}, rose {rise:0.##} m");
            yield return new WaitForSeconds(2f);
            var standing = Standing(player, site);
            Check(standing == 0 && pieces.All(p => p == null), $"Whole structure: none of the {built} built pieces is left in the world: {standing}");
            Check(!SiteStore.All.Contains(site) && !File.Exists(path) && site.Ghost == null && !BlueprintMode.Active,
                $"the plan went too: kept {SiteStore.All.Contains(site)}, file {File.Exists(path)}, ghost {site.Ghost != null}, blueprint mode {BlueprintMode.Active}");
            var got = back.Keys.ToDictionary(k => k, k => Around(player, centre, k) - before[k]);
            Check(back.Count > 0 && back.All(b => got[b.Key] == b.Value),
                "the materials came back as the hammer's Remove gives them: "
                + string.Join(", ", back.Select(b => $"{Localization.instance.Localize(b.Key)} {got[b.Key]} of {b.Value}")));
            Check(said == $"Workshop removed: {built} pieces taken down.", $"and it says so: '{said}'");
            yield return WorldPadOff();

            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            ClearDrops(centre);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>
        /// Check 4d: a piece the game will not let go is left standing and counted, with the plan
        /// removed all the same. First the top piece made unremovable (the pieces under it stay too, so
        /// it does not fall), then the station rule: 28 m from the only workbench.
        /// </summary>
        private static IEnumerator ContinueRemoveRefused(Player player, ResolvedBlueprint kit, Vector3 spot)
        {
            // ---- the top piece cannot be removed: it stays, with what holds it up ----
            yield return StartSite(player, kit, (item, need) => item == "$item_wood" ? need - 2 : need);
            var site = BlueprintMode.LastSite;
            if (site == null || !SiteStore.All.Contains(site))
            {
                yield break;
            }

            var path = site.Path;
            var parts = MatchParts(site.Resolved, site.RootPosition, site.RootRotation, PiecesAround(player, site.RootPosition));
            var built = parts.Count;
            var top = parts.OrderByDescending(p => p.Value.transform.position.y).First();
            top.Value.m_canBeRemoved = false;
            Log($"{built} of {site.Total} built; the top one, {top.Key} {top.Value.name}, made unremovable");
            FirstEntry(player);
            yield return PressBound("Remove");
            yield return Frames(2);
            Check(SiteRemovePopup.Showing, "Remove opens the window");
            yield return ClickButton(SiteRemovePopup.ButtonFor(SiteRemovePopup.Choice.WholeStructure));
            yield return Frames(3);
            var said = BlueprintMode.LastMessage ?? "";
            var last = SiteRemoval.Last;
            var left = parts.Values.Where(p => p != null).ToList();
            Check(last != null && top.Value != null && last.LeftCount == 1 && last.HoldingUp > 0
                && left.Count == 1 + last.HoldingUp && last.Removed == built - left.Count,
                $"the unremovable top piece stays with the {last?.HoldingUp} under it: {left.Count} of {built} left, {last?.Removed} taken down");
            Check(said == $"Workshop removed: {built - left.Count} of {built} pieces taken down. Left standing: 1 can't be removed, {last?.HoldingUp} hold it up.",
                $"the message counts them: '{said}'");
            Check(!SiteStore.All.Contains(site) && !File.Exists(path) && site.Ghost == null && !BlueprintMode.Active,
                $"the plan is removed all the same: kept {SiteStore.All.Contains(site)}, file {File.Exists(path)}, blueprint mode {BlueprintMode.Active}");
            yield return new WaitForSeconds(5f);
            var still = left.Count(p => p != null);
            Check(still == left.Count, $"5 s later nothing fell: {still} of {left.Count} still stand");
            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            ClearDrops(site.WorldBox.center);
            yield return new WaitForSeconds(1f);

            // ---- the station rule: 28 m from the only workbench ----
            yield return StartSite(player, kit, (item, need) => item == "$item_wood" ? need / 2 : need);
            site = BlueprintMode.LastSite;
            if (site == null || !SiteStore.All.Contains(site))
            {
                yield break;
            }

            path = site.Path;
            parts = MatchParts(site.Resolved, site.RootPosition, site.RootRotation, PiecesAround(player, site.RootPosition));
            built = parts.Count;
            var away = site.RootPosition;
            foreach (var angle in new[] { 180f, 90f, 270f, 135f, 225f, 45f, 315f, 0f })
            {
                away = site.RootPosition + (Quaternion.Euler(0f, angle, 0f) * Vector3.forward * 28f);
                if (WorldGenerator.instance.GetHeight(away.x, away.z) > ZoneSystem.instance.m_waterLevel + 1f)
                {
                    break;
                }
            }

            away.y = WorldGenerator.instance.GetHeight(away.x, away.z) + 1f;
            yield return TeleportNear(player, away, BuildFacing, "28 m from the build");
            yield return EquipHammer(player);
            var needBench = parts.Values.Count(p => p.m_craftingStation != null
                && CraftingStation.HaveBuildStationInRange(p.m_craftingStation.m_name, player.transform.position) == null);
            var distance = Mathf.Sqrt(site.WorldBox.SqrDistance(player.transform.position));
            Log($"{built} of {site.Total} built, {distance:0} m from the build's box, {needBench} need a station the game finds none of");
            FirstEntry(player);
            Check(BlueprintMode.CurrentSite == site && needBench > 0 && needBench < built,
                $"{distance:0} m away the key still continues it, and {needBench} of its {built} pieces need a workbench the player has not near");
            yield return PressBound("Remove");
            yield return Frames(2);
            Check(SiteRemovePopup.Showing, "Remove opens the window");
            yield return ClickButton(SiteRemovePopup.ButtonFor(SiteRemovePopup.Choice.WholeStructure));
            yield return Frames(3);
            said = BlueprintMode.LastMessage ?? "";
            left = parts.Values.Where(p => p != null).ToList();
            Check(left.Count == needBench && left.All(p => p.m_craftingStation != null),
                $"the pieces that need a workbench stay, the rest came down: {left.Count} left of {built}");
            Check(said == $"Workshop removed: {built - needBench} of {built} pieces taken down. Left standing: {needBench} need a workbench nearby.",
                $"the message says why: '{said}'");
            Check(!SiteStore.All.Contains(site) && !File.Exists(path) && site.Ghost == null && !BlueprintMode.Active,
                $"the plan is removed all the same: kept {SiteStore.All.Contains(site)}, file {File.Exists(path)}, blueprint mode {BlueprintMode.Active}");

            yield return TeleportNear(player, spot + Vector3.up, BuildFacing, "back to the build spot");
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            ClearDrops(site.WorldBox.center);
            yield return new WaitForSeconds(1f);
        }

        /// <summary>The remove window is up as the game's popup, with its three buttons, and the press that opened it did nothing else.</summary>
        private static void CheckRemoveWindow(Player player, Site site, int built, string what)
        {
            var controller = player.GetComponent<PlayerController>();
            Check(SiteRemovePopup.Showing && UnifiedPopup.IsVisible() && Menu.IsVisible() && !player.TakeInput() && !controller.TakeInput(false),
                $"{what}: the window is up and the game counts it as a popup: popup {UnifiedPopup.IsVisible()}, Menu.IsVisible {Menu.IsVisible()},"
                + $" player input {player.TakeInput()}, walking {controller.TakeInput(false)}");
            var header = SiteRemovePopup.HeaderShown ?? "";
            var text = SiteRemovePopup.TextShown ?? "";
            Check(header == $"Remove {site.Name}?" && text.StartsWith($"{built} of {site.Total} pieces are built."),
                $"{what}: '{header}' / '{text.Replace("\n", " | ")}'");

            var buttons = new[] { SiteRemovePopup.Choice.Cancel, SiteRemovePopup.Choice.UnbuiltParts, SiteRemovePopup.Choice.WholeStructure }
                .Select(SiteRemovePopup.ButtonFor).ToList();
            var labels = buttons.Select(b => b != null ? b.transform.Find("Text")?.GetComponent<TMPro.TMP_Text>() : null).ToList();
            var boxes = buttons.Select(b => b != null ? ScreenBox((RectTransform)b.transform) : Rect.zero).ToList();
            var panel = buttons[0] != null ? ScreenBox((RectTransform)buttons[0].transform.parent) : Rect.zero;
            var inRow = buttons.All(b => b != null && b.gameObject.activeInHierarchy && b.interactable)
                && boxes[0].xMax < boxes[1].xMin && boxes[1].xMax < boxes[2].xMin
                && Mathf.Abs(boxes[0].y - boxes[2].y) < 0.5f
                && boxes.All(b => panel.Contains(b.min) && panel.Contains(b.max));
            var words = string.Join(", ", labels.Select(l => l != null ? l.text : "?"));
            var fit = labels.All(l => l != null && l.GetPreferredValues(l.text).x <= l.rectTransform.rect.width + 0.5f);
            Check(inRow && fit && words == "Cancel, Unbuilt parts, Whole structure",
                $"{what}: three buttons in a row inside the panel, left to right '{words}', every label fits: "
                + string.Join(" ", boxes.Select(b => $"[{b.xMin:0}-{b.xMax:0}]")) + $" panel [{panel.xMin:0}-{panel.xMax:0}]");
            Check(SiteStore.All.Contains(site) && BlueprintMode.CurrentSite == site && Standing(player, site) == built
                && !Menu.instance.m_root.gameObject.activeSelf,
                $"{what}: the press did nothing else: still Continue, {Standing(player, site)} of {built} standing, pause menu {Menu.instance.m_root.gameObject.activeSelf}");
        }

        /// <summary>Cancel in any of its ways: the window is gone, nothing changed, still Continue, the pause menu shut, the controls back.</summary>
        private static IEnumerator CheckRemoveCancelled(Player player, Site site, string path, int built, string what)
        {
            yield return Frames(3);
            Check(!SiteRemovePopup.IsOpen && !UnifiedPopup.IsVisible() && SiteRemovePopup.LastChoice == SiteRemovePopup.Choice.Cancel,
                $"{what} closes the window as Cancel: {SiteRemovePopup.LastChoice}");
            Check(SiteStore.All.Contains(site) && File.Exists(path) && site.Ghost != null && BlueprintMode.CurrentSite == site
                && Standing(player, site) == built,
                $"{what}: nothing changed and Continue stays on: kept {SiteStore.All.Contains(site)}, file {File.Exists(path)},"
                + $" ghost {site.Ghost != null}, on '{BlueprintMode.EntryName}', {Standing(player, site)} of {built} standing");
            Check(!Menu.instance.m_root.gameObject.activeSelf && player.TakeInput(),
                $"{what}: the pause menu did not open ({Menu.instance.m_root.gameObject.activeSelf}), the player has the controls back ({player.TakeInput()})");
        }

        /// <summary>A real left click through the input system, where the button shows on screen.</summary>
        private static IEnumerator ClickButton(UnityEngine.UI.Button button)
        {
            if (button == null)
            {
                Check(false, "no button to click");
                yield break;
            }

            yield return ClickScreen(ScreenBox((RectTransform)button.transform).center);
        }

        private static IEnumerator ClickScreen(Vector2 at)
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null)
            {
                Check(false, "no mouse device to click with");
                yield break;
            }

            // WithButton changes the struct it is called on, so the press gets a state of its own.
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse, new UnityEngine.InputSystem.LowLevel.MouseState { position = at });
            yield return Frames(2);
            var down = new UnityEngine.InputSystem.LowLevel.MouseState { position = at }.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse, down);
            yield return Frames(2);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse, new UnityEngine.InputSystem.LowLevel.MouseState { position = at });
            yield return Frames(2);
        }

        /// <summary>A UI rectangle in screen pixels. Every canvas the game draws its popups on is an overlay.</summary>
        private static Rect ScreenBox(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        private static string Name(GameObject go)
        {
            return go != null ? go.name : "nothing";
        }

        /// <summary>
        /// What the hammer's Remove gives back for these pieces (Piece.DropResources): every material it
        /// marks as recovered, in full for a piece a player placed, a third for any other, none when
        /// the world makes it free.
        /// </summary>
        private static Dictionary<string, int> Recoverable(IEnumerable<Piece> pieces)
        {
            var back = new Dictionary<string, int>();
            foreach (var piece in pieces)
            {
                if (ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
                {
                    continue;
                }

                foreach (var requirement in piece.m_resources)
                {
                    if (requirement.m_resItem == null || !requirement.m_recover || requirement.m_amount <= 0)
                    {
                        continue;
                    }

                    var amount = piece.IsPlacedByPlayer() ? requirement.m_amount : Mathf.Max(1, requirement.m_amount / 3);
                    var item = requirement.m_resItem.m_itemData.m_shared.m_name;
                    back.TryGetValue(item, out var had);
                    back[item] = had + amount;
                }
            }

            return back;
        }

        /// <summary>How many of an item the player has, in the bag and lying on the ground within 30 m of a point.</summary>
        private static int Around(Player player, Vector3 centre, string item)
        {
            var ground = ItemDrop.s_instances
                .Where(d => d != null && d.m_itemData.m_shared.m_name == item && Vector3.Distance(d.transform.position, centre) < 30f)
                .Sum(d => d.m_itemData.m_stack);
            return player.GetInventory().CountItems(item) + ground;
        }

        /// <summary>Items lying on the ground within 30 m go, so the next check starts clean.</summary>
        private static void ClearDrops(Vector3 centre)
        {
            foreach (var drop in ItemDrop.s_instances.Where(d => d != null && Vector3.Distance(d.transform.position, centre) < 30f).ToList())
            {
                ZNetScene.instance.Destroy(drop.gameObject);
            }
        }

        /// <summary>The input system's button for one of the pad's buttons. The triggers are axes, not buttons.</summary>
        private static PadKey PadKeyOf(PadButton button)
        {
            switch (button)
            {
                case PadButton.Cross: return PadKey.South;
                case PadButton.Circle: return PadKey.East;
                case PadButton.Square: return PadKey.West;
                case PadButton.Triangle: return PadKey.North;
                case PadButton.L1: return PadKey.LeftShoulder;
                case PadButton.R1: return PadKey.RightShoulder;
                case PadButton.Options: return PadKey.Start;
                case PadButton.L3: return PadKey.LeftStick;
                case PadButton.R3: return PadKey.RightStick;
                case PadButton.Up: return PadKey.DpadUp;
                case PadButton.Down: return PadKey.DpadDown;
                case PadButton.Left: return PadKey.DpadLeft;
                default: return PadKey.DpadRight;
            }
        }

        /// <summary>
        /// Presses and lets go of whatever the game binds to this button (the player may have changed
        /// it), through the input system, so the game's own ZInput reads it.
        /// </summary>
        private static IEnumerator PressBound(string button)
        {
            var def = ZInput.instance != null ? ZInput.instance.GetButtonDef(button) : null;
            var path = def != null && def.ButtonAction.bindings.Count > 0 ? def.ButtonAction.bindings[0].effectivePath : null;
            var control = path != null ? UnityEngine.InputSystem.InputSystem.FindControl(path) as UnityEngine.InputSystem.Controls.ButtonControl : null;
            if (control == null)
            {
                Check(false, $"a control is bound to {button}: {path ?? "none"}");
                yield break;
            }

            QueueButton(control, 1f);
            for (var frame = 0; frame < 4; frame++)
            {
                yield return null;
            }

            QueueButton(control, 0f);
            for (var frame = 0; frame < 3; frame++)
            {
                yield return null;
            }

            Log($"pressed {button} ({path})");
        }

        private static void QueueButton(UnityEngine.InputSystem.Controls.ButtonControl control, float value)
        {
            using (UnityEngine.InputSystem.LowLevel.StateEvent.From(control.device, out var eventPtr))
            {
                UnityEngine.InputSystem.InputControlExtensions.WriteValueIntoEvent(control, value, eventPtr);
                UnityEngine.InputSystem.InputSystem.QueueEvent(eventPtr);
            }
        }

        // ---------- a controller in the world ----------

        /// <summary>
        /// A controller of its own on the input system, so the game's ZInput reads its buttons exactly
        /// the way it reads a real one's: the world side of the pad (the hammer, the editor's open
        /// combo, the capture) goes through the game's named buttons, which the editor's fake pad
        /// (<see cref="PadReader.Fake"/>) never reaches. Named like a DualSense, so the pad reader
        /// calls it a PlayStation pad.
        /// </summary>
        private static UnityEngine.InputSystem.Gamepad _worldPad;

        private static ZInput.InputSource _worldPadSource;

        /// <summary>Every time the game asked the inventory to open, whatever stopped it after.</summary>
        private static int _inventoryShows;

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show), new[] { typeof(Container), typeof(int) })]
        private static class InventoryGuiShowCountPatch
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix()
            {
                _inventoryShows++;
            }
        }

        private static void WorldPadOn()
        {
            if (_worldPad != null)
            {
                return;
            }

            _worldPadSource = ZInput.m_inputSource;
            _worldPad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>("AutoTestPad DualSense");
            Log($"world pad added: '{_worldPad.name}', the game's modifier is {WorldPad.ModifierButton}, layout {ZInput.InputLayout}");
        }

        /// <summary>Lets go of every button, takes the controller away and gives the game back the input it had.</summary>
        private static IEnumerator WorldPadOff()
        {
            if (_worldPad == null)
            {
                yield break;
            }

            PadDown(false);
            yield return null;
            yield return null;
            UnityEngine.InputSystem.InputSystem.RemoveDevice(_worldPad);
            _worldPad = null;
            ZInput.instance?.OnInput(_worldPadSource, true);
            yield return null;
        }

        /// <summary>The world controller's buttons from now on: the game's modifier (L2) when asked, these, nothing else.</summary>
        private static void PadDown(bool modifier, params PadKey[] buttons)
        {
            var state = new UnityEngine.InputSystem.LowLevel.GamepadState();
            foreach (var button in buttons)
            {
                state = state.WithButton(button);
            }

            if (modifier)
            {
                if (WorldPad.ModifierButton == PadButton.L1)
                {
                    state = state.WithButton(PadKey.LeftShoulder);
                }
                else
                {
                    state.leftTrigger = 1f;
                }
            }

            UnityEngine.InputSystem.InputSystem.QueueStateEvent(_worldPad, state);
        }

        /// <summary>
        /// One press on the world controller: the modifier first when asked, then the buttons for three
        /// frames (the D-pad's own repeat starts only after 0.3 s), then all of it let go.
        /// </summary>
        private static IEnumerator PadPress(bool modifier, params PadKey[] buttons)
        {
            if (modifier)
            {
                PadDown(true);
                yield return Frames(2);
            }

            PadDown(modifier, buttons);
            yield return Frames(3);
            PadDown(modifier);
            yield return Frames(2);
            if (modifier)
            {
                PadDown(false);
                yield return Frames(2);
            }
        }

        private static IEnumerator Frames(int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                yield return null;
            }
        }

        /// <summary>The game's own count of forsaken power uses: it goes up the moment the power starts.</summary>
        private static float PowerUses()
        {
            var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            return profile != null && profile.m_playerStats[0].m_stats.TryGetValue(PlayerStatType.UseGuardianPower, out var uses) ? uses : -1f;
        }

        /// <summary>The highest the player's feet got over where they stand now, while the frames run.</summary>
        private static IEnumerator Rise(Player player, float seconds, Action<float> result)
        {
            var from = player.transform.position.y;
            var top = from;
            var until = Time.time + seconds;
            while (Time.time < until)
            {
                top = Mathf.Max(top, player.transform.position.y);
                yield return null;
            }

            result(top - from);
        }

        /// <summary>
        /// Check 5: two builds of the same blueprint are told apart by distance; 60 m away the key offers
        /// only normal blueprints; picking a normal piece ends Continue. Leaves one site for check 6.
        /// </summary>
        private static IEnumerator ContinueFarAway(Player player, ResolvedBlueprint kit, Vector3 spot)
        {
            yield return StartSite(player, kit, (item, need) => item == "$item_wood" ? need / 2 : need);
            var near = BlueprintMode.LastSite;
            yield return StartSite(player, kit, (item, need) => 0, Quaternion.Euler(0f, 180f, 0f));
            var behind = BlueprintMode.LastSite;
            if (near == null || behind == null || near == behind || SiteStore.All.Count != 2)
            {
                Check(false, $"set-up: two sites of the Workshop: {SiteStore.All.Count}");
                yield break;
            }

            player.m_lookYaw = BuildFacing;
            player.transform.rotation = BuildFacing;
            player.m_body.rotation = BuildFacing;
            FirstEntry(player);
            var one = (BlueprintMode.CurrentSite, BlueprintMode.EntryName ?? "");
            BlueprintMode.Cycle(player);
            var two = (BlueprintMode.CurrentSite, BlueprintMode.EntryName ?? "");
            BlueprintMode.Cycle(player);
            var three = (BlueprintMode.CurrentSite, BlueprintMode.EntryName ?? "", BlueprintMode.Active);
            float Away(Site s) => Mathf.Sqrt(s.WorldBox.SqrDistance(player.transform.position));
            Check(one.Item1 != null && two.Item1 != null && one.Item1 != two.Item1 && Away(one.Item1) <= Away(two.Item1)
                && one.Item2.StartsWith("Continue: Workshop") && one.Item2.Contains(" m ")
                && two.Item2.StartsWith("Continue: Workshop") && two.Item2.Contains(" m ") && one.Item2 != two.Item2,
                $"two Workshop sites, nearest first, told apart by distance and way: '{one.Item2}', '{two.Item2}'");
            Check(three.Item1 == null && three.Item3 && !three.Item2.StartsWith("Continue:"), $"then the normal blueprints: '{three.Item2}'");
            BlueprintMode.Exit();
            SiteStore.Delete(behind);

            // ---- 5. 60 m away: only the normal blueprints. Dry land: swimming puts the hammer away ----
            var far = spot + (Vector3.right * 60f);
            foreach (var angle in new[] { 90f, 270f, 180f, 135f, 225f, 45f, 315f })
            {
                far = spot + (Quaternion.Euler(0f, angle, 0f) * Vector3.forward * 60f);
                if (WorldGenerator.instance.GetHeight(far.x, far.z) > ZoneSystem.instance.m_waterLevel + 1f)
                {
                    break;
                }
            }

            far.y = WorldGenerator.instance.GetHeight(far.x, far.z) + 1f;
            yield return TeleportNear(player, far, BuildFacing, "60 m away");
            yield return EquipHammer(player);
            var away = Away(near);
            var names = new List<string>();
            FirstEntry(player);
            for (var i = 0; i < 20 && BlueprintMode.Active; i++)
            {
                names.Add(BlueprintMode.EntryName ?? "");
                BlueprintMode.Cycle(player);
            }

            Check(away > BlueprintMode.ContinueRange && names.Count > 0 && names.All(n => !n.StartsWith("Continue:")),
                $"{away:0} m from the site the key offers only blueprints: {string.Join(", ", names)}");
            BlueprintMode.Exit();

            yield return TeleportNear(player, spot + Vector3.up, BuildFacing, "back to the build spot");
            yield return EquipHammer(player);
            FirstEntry(player);
            Check(BlueprintMode.CurrentSite == near, "back near it, Continue comes first again: " + BlueprintMode.EntryName);

            // Picking a normal piece ends blueprint mode, Continue too. The site stays.
            var wall = PiecePrefab("woodwall");
            var picked = wall != null && player.SetSelectedPiece(wall);
            yield return null;
            Check(picked && !BlueprintMode.Active && BlueprintMode.CurrentSite == null && SiteStore.All.Contains(near) && near.Ghost != null,
                $"picking the wood wall ends Continue ({BlueprintMode.Active}), the site and its ghost stay");
        }

        /// <summary>
        /// Check 6: the player stands where a ready part goes. The click builds every other ready part,
        /// leaves that one out, and says so. It stays ready.
        /// </summary>
        private static IEnumerator ContinueStandingInside(Player player)
        {
            var site = SiteStore.All.FirstOrDefault();
            if (site == null || site.Ghost == null)
            {
                Check(false, "set-up: a site with its ghost is kept");
                yield break;
            }

            ClearInventoryExceptHammer(player);
            AddTo(player.GetInventory(), "Wood", 4);
            SiteTracker.Refresh(player);
            var ready = site.ReadyOrder != null ? site.ReadyOrder.ToList() : new List<int>();
            Check(ready.Count >= 2, $"4 wood make {ready.Count} parts ready: {string.Join(", ", ready.Select(i => $"{i} {site.Resolved.Parts[i].Prefab.name}"))}");
            if (ready.Count < 2)
            {
                yield break;
            }

            // The test's own box: the ghost copy's drawn box in the world, grown 0.6 m in x and z.
            bool Holds(int part, Vector3 at)
            {
                var copy = site.Ghost.Part(part);
                var renderers = copy != null ? copy.GetComponentsInChildren<Renderer>().Where(r => r.enabled && !(r is ParticleSystemRenderer)).ToList() : null;
                if (renderers == null || renderers.Count == 0)
                {
                    return false;
                }

                var box = renderers[0].bounds;
                foreach (var renderer in renderers)
                {
                    box.Encapsulate(renderer.bounds);
                }

                box.Expand(new Vector3(0.6f, 0f, 0.6f));
                return box.Contains(at + (Vector3.up * 0.5f));
            }

            // A spot 0.2 m inside from one ready part's pivot, on the ground, that no other ready part holds.
            var centre = site.WorldBox.center;
            var target = -1;
            var stand = Vector3.zero;
            foreach (var part in ready)
            {
                var pivot = site.WorldPosition(part);
                var inward = new Vector3(centre.x - pivot.x, 0f, centre.z - pivot.z).normalized;
                var at = pivot + (inward * 0.2f);
                at.y = ZoneSystem.instance.GetGroundHeight(at);
                if (Holds(part, at) && ready.Count(p => Holds(p, at)) == 1)
                {
                    target = part;
                    stand = at;
                    break;
                }
            }

            Check(target >= 0, $"set-up: a spot where only part {target} is held: {V4(stand)}");
            if (target < 0)
            {
                yield break;
            }

            var look = Quaternion.LookRotation(new Vector3(centre.x - stand.x, 0f, centre.z - stand.z));
            yield return TeleportNear(player, stand + (Vector3.up * 0.3f), look, "into the half-built house");
            yield return EquipHammer(player);
            var standing = player.transform.position;
            var held = ready.Where(p => Holds(p, standing)).ToList();
            Check(held.Count == 1 && held[0] == target && !site.Built[target],
                $"the player stands at {V4(standing)}, in part {string.Join(", ", held)} ({site.Resolved.Parts[target].Prefab.name})");

            FirstEntry(player);
            Check(BlueprintMode.CurrentSite == site, "the key continues it: " + BlueprintMode.EntryName);
            var tintsBefore = Tints(site);
            var before = (bool[])site.Built.Clone();
            player.m_lastToolUseTime = 0f;
            var clicked = BlueprintMode.TryBuild(player);
            var said = BlueprintMode.LastMessage ?? "";
            yield return null;
            var added = Enumerable.Range(0, site.Total).Where(i => site.Built[i] && !before[i]).OrderBy(i => i).ToList();
            var wanted = ready.Where(p => !held.Contains(p)).OrderBy(i => i).ToList();
            Check(clicked && added.SequenceEqual(wanted),
                $"the click builds every ready part but the one the player stands in: built {string.Join(", ", added)}, wanted {string.Join(", ", wanted)}");
            Check(!site.Built[target] && said.Contains("1 left out"), $"that one is left out, and it says so: '{said}'");
            SiteTracker.Refresh(player);
            Check(site.Ready != null && site.Ready[target] && SiteStore.All.Contains(site), $"it stays ready: {Looks(site)}");
            yield return null;
            yield return null;
            var tints = Tints(site);
            var waiting = Enumerable.Range(0, site.Total).Where(i => site.Ghost.LookOf(i) == PartLook.Waiting).DefaultIfEmpty(-1).First();
            Check(added.Count > 0 && added.All(i => tintsBefore[i] == 'b' && site.Ghost.LookOf(i) == PartLook.Hidden && tints[i] == '-')
                && tints[target] == 'b' && waiting >= 0 && tints[waiting] == 'r' && tints == Wanted(site),
                $"the parts the click built were light blue and are hidden now with no tint left ({string.Join(", ", added)}),"
                + $" the one left out is still light blue ({target}), a waiting one red ({waiting}): before {tintsBefore}, after {tints}");
            player.m_lookPitch = 20f;
            yield return new WaitForSeconds(0.5f);
            yield return Screenshot("build-continue-3-inside");
            BlueprintMode.Exit();
        }

        // ---------- scenario: card_materials ----------

        /// <summary>
        /// The hammer card's materials list: a readable row per item and station, have / need, a bar,
        /// and a footer. The Workshop with half its wood, then Continue on the build that click left,
        /// then a made-up blueprint with 12 items in two columns, then a normal piece: the list goes
        /// and the game's own slots come back.
        /// </summary>
        private static IEnumerator TestCardMaterials(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            ClearSites();
            yield return new WaitForSeconds(0.5f);
            PieceCatalog.Ensure();

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop");
            ResolvedBlueprint resolved = null;
            var error = "no Workshop kit";
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out error))
            {
                Check(false, "the workshop kit resolves: " + error);
                yield break;
            }

            Unlock(player, resolved);
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);
            Check(MaterialList.Short(9999) == "9999" && MaterialList.Short(10000) == "10k" && MaterialList.Short(12345) == "12.3k",
                $"big numbers: 9999 -> {MaterialList.Short(9999)}, 10000 -> {MaterialList.Short(10000)}, 12345 -> {MaterialList.Short(12345)}");

            var chests = new List<Piece>();
            yield return PlaceTestPiece(player, "piece_chest_wood", OnGround(player.transform.position, 180f, 5f), 0f, chests);
            yield return new WaitForSeconds(0.5f);
            var chest = chests[0] != null ? chests[0].GetComponentInChildren<Container>() : null;
            Check(chest != null, "a wood chest stands 5 m behind the player");
            if (chest == null)
            {
                yield break;
            }

            yield return CardHalfWood(player, resolved, chest);
            yield return CardContinue(player, resolved);
            RemoveOldTestBuildings(player);
            ClearSites();
            ClearInventoryExceptHammer(player);
            yield return new WaitForSeconds(0.5f);
            yield return CardTwelveItems(player);
            yield return CardBigBlueprint(player, resolved);
            yield return CardNormalPiece(player);

            BlueprintMode.Exit();
            RemoveOldTestBuildings(player);
            ClearInventoryExceptHammer(player);
            ClearSites();
            player.m_lastToolUseTime = 0f;
            yield return new WaitForSeconds(1f);
            var left = new List<Piece>();
            Piece.GetAllPiecesInRadius(player.transform.position, 60f, left);
            var mine = left.Count(p => p != null && p.GetCreator() == player.GetPlayerID());
            Check(mine == 0 && SiteStore.All.Count == 0, $"nothing of the test is left standing ({mine}) and no site is kept ({SiteStore.All.Count})");
        }

        /// <summary>Waits for the card's next refresh, then two frames so the texts are drawn.</summary>
        private static IEnumerator CardRefreshed()
        {
            BlueprintInfoCard.RefreshSoon();
            var before = BlueprintInfoCard.Refreshes;
            var waited = 0f;
            while (BlueprintInfoCard.Refreshes == before && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return null;
            yield return null;
        }

        /// <summary>
        /// Check 1: half the wood in the bag, the resin in the chest. One row per item and station, the
        /// texts are MaterialTally's numbers, wood in Warn and resin in Good, the footer's count is the
        /// plan's. Then the click: the list shows what is left in the very next frames.
        /// </summary>
        private static IEnumerator CardHalfWood(Player player, ResolvedBlueprint kit, Container chest)
        {
            ClearInventoryExceptHammer(player);
            foreach (var cost in kit.TotalCost)
            {
                var name = cost.m_resItem.m_itemData.m_shared.m_name;
                if (name == "$item_wood")
                {
                    AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, cost.m_amount / 2);
                }
                else
                {
                    AddTo(chest.GetInventory(), cost.m_resItem.gameObject.name, cost.m_amount);
                }
            }

            yield return AimBlueprint(player, kit);
            yield return CardRefreshed();
            var hud = Hud.instance;
            LogCard(hud);
            var list = BlueprintInfoCard.List;
            Check(BlueprintInfoCard.Visible && list != null && list.Visible, "blueprint mode: the materials list is on the card");
            if (list == null)
            {
                yield break;
            }

            var slotsOn = hud.m_requirementItems.Count(s => s != null && s.activeInHierarchy);
            Check(slotsOn == 0, $"the game's requirement slots are hidden: {slotsOn} on");

            var sources = MaterialSources.Around(player);
            var tally = MaterialTally.For(kit, null, sources);
            var rows = list.ShownRows.ToList();
            var want = kit.TotalCost.Count + kit.Stations.Count;
            Check(rows.Count == want && tally.Rows.Count == kit.TotalCost.Count && tally.Stations.Count == kit.Stations.Count,
                $"one row per item and station: {rows.Count} rows, the kit has {kit.TotalCost.Count} items and {kit.Stations.Count} stations");
            CheckRowsMatch(rows, tally, "the Workshop");

            var wood = rows.FirstOrDefault(r => r.Key == "$item_wood");
            var full = rows.FirstOrDefault(r => !r.IsStation && r.Key != "$item_wood");
            Check(wood != null && wood.Have.color == UiTheme.Warn && wood.Fill.color == UiTheme.Warn,
                $"the wood row is short: its number and bar are Warn ({(wood != null ? wood.Have.text + wood.Need.text : "no row")})");
            Check(full != null && full.Have.color == UiTheme.Good && full.Fill.color == UiTheme.Good,
                $"a full item is Good: {(full != null ? full.Name.text + " " + full.Have.text + full.Need.text : "no row")}");
            var bench = rows.FirstOrDefault(r => r.IsStation);
            Check(bench != null && bench.State.text == "in blueprint" && bench.State.color == UiTheme.Accent,
                $"the workbench row says the kit brings its own: '{(bench != null ? bench.State.text : "no row")}'");

            var root = BlueprintMode.PreviewRoot;
            var plan = root != null ? PartialBuild.Plan(kit, root.position, root.eulerAngles.y, null, sources, false).Count : -1;
            var footer = list.Footer.text;
            Check(plan > 0 && plan < kit.Parts.Count && footer == $"Can build now: {plan} of {kit.Parts.Count} pieces"
                && list.Footer.color == UiTheme.Accent, $"the footer counts what the plan builds ({plan}): '{footer}'");
            var texts = BlueprintInfoCard.Panel.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t => t.text).ToList();
            Check(sources.ChestCount >= 1 && texts.Count > 0 && texts.All(t => !t.Contains("From:")),
                $"with a chest in range the list still says nothing about where things come from: no 'From:' in its {texts.Count} texts");
            CheckFooterAtBottom(list, "the Workshop's list");
            CheckLabels(list);
            CheckOnScreen("the Workshop's list");

            var description = hud.m_pieceDescription.text;
            Check(description.EndsWith($"{kit.Parts.Count} pieces.") && NoControls(description),
                $"the card counts the pieces and lists no controls (they are in the game's hint row): '{description.Replace("\n", " | ")}'");
            yield return CardPadHint(kit, false);

            // Still: a refresh every 0.5 s, and the plan is not worked out again.
            var refreshes = BlueprintInfoCard.Refreshes;
            var plans = BlueprintInfoCard.PlanRuns;
            var planAt = BlueprintInfoCard.LastPlanAt;
            var previewAt = BlueprintMode.PreviewRoot != null ? BlueprintMode.PreviewRoot.position : Vector3.zero;
            yield return new WaitForSeconds(1.6f);
            var made = BlueprintInfoCard.Refreshes - refreshes;
            Check(made >= 2 && made <= 4 && BlueprintInfoCard.PlanRuns == plans,
                $"held still for 1.6 s: {made} refreshes, the plan ran {BlueprintInfoCard.PlanRuns - plans} more times"
                + $" (last one {BlueprintInfoCard.LastPlanMs:0.00} ms, because: {BlueprintInfoCard.LastPlanReason}; made at {V4(planAt)}, the preview at {V4(previewAt)})");

            // Ctrl+F3 hides the game's HUD, and the list with it.
            hud.m_userHidden = true;
            yield return null;
            yield return null;
            var hidden = !OnScreen(BlueprintInfoCard.Panel);
            hud.m_userHidden = false;
            yield return null;
            yield return null;
            Check(hidden && OnScreen(BlueprintInfoCard.Panel), $"the list hides with the HUD (Ctrl+F3) and comes back: hidden {hidden}");

            yield return new WaitForSeconds(0.6f);
            yield return Screenshot("card-materials-1");

            // The click: part of it goes up, and the list says what is left in the next frames.
            yield return AimBlueprint(player, kit);
            refreshes = BlueprintInfoCard.Refreshes;
            var sites = SiteStore.All.Count;
            var clicked = BlueprintMode.TryBuild(player);
            yield return null;
            yield return null;
            var after = MaterialSources.Around(player);
            wood = list.ShownRows.FirstOrDefault(r => r.Key == "$item_wood");
            Check(clicked && BlueprintInfoCard.Refreshes > refreshes && wood != null
                && wood.Have.text == MaterialList.Short(after.Count("$item_wood")),
                $"right after the click the list shows the wood left: '{(wood != null ? wood.Have.text : "no row")}', {after.Count("$item_wood")} in the bag");

            // And the card is on Continue already: the click went there on its own.
            yield return CheckContinuesAfterClick(sites, "the card's click");
        }

        /// <summary>
        /// Check 2: Continue on the build the click left. The need column is what the missing pieces
        /// cost, the footer starts "Built", and the hint line says what Continue does.
        /// </summary>
        private static IEnumerator CardContinue(Player player, ResolvedBlueprint kit)
        {
            var site = BlueprintMode.LastSite;
            BlueprintMode.Exit();
            yield return null;
            if (site == null || !SiteStore.All.Contains(site))
            {
                Check(false, "the half-wood click kept an unfinished build");
                yield break;
            }

            // Some wood, so the next click can build a little: the footer shows the "some" colour.
            AddTo(player.GetInventory(), "Wood", 6);
            SiteTracker.Refresh(player);
            yield return WaitForGhosts(1);
            FirstEntry(player);
            yield return CardRefreshed();
            var list = BlueprintInfoCard.List;
            Check(BlueprintMode.CurrentSite == site && list != null && list.Visible, "Continue on it: the list is on the card");
            var title = Hud.instance.m_buildSelection.text;
            Check(title == "Continue: Workshop", $"the key's Continue has the same card title: '{title}'");
            if (list == null || BlueprintMode.CurrentSite != site)
            {
                yield break;
            }

            // What the parts not built yet cost, worked out here without MaterialTally.
            var missing = new Dictionary<string, int>();
            var stations = new HashSet<string>();
            for (var i = 0; i < site.Total; i++)
            {
                if (site.Built[i])
                {
                    continue;
                }

                foreach (var cost in PartialBuild.CostOf(site.Resolved.Parts[i].Piece))
                {
                    missing.TryGetValue(cost.Key, out var had);
                    missing[cost.Key] = had + cost.Value;
                }

                var station = site.Resolved.Parts[i].Piece.m_craftingStation;
                if (station != null)
                {
                    stations.Add(station.m_name);
                }
            }

            var rows = list.ShownRows.ToList();
            var items = rows.Where(r => !r.IsStation).ToList();
            Check(items.Count == missing.Count && rows.Count(r => r.IsStation) == stations.Count,
                $"one row per item the missing pieces need: {items.Count} (want {missing.Count}), stations {rows.Count(r => r.IsStation)} (want {stations.Count})");
            foreach (var row in items)
            {
                missing.TryGetValue(row.Key, out var need);
                Check(row.Need.text == " / " + MaterialList.Short(need),
                    $"{row.Name.text}: need is the missing pieces' cost, {need}: '{row.Need.text.Trim()}' (the whole kit: {kit.TotalCost.First(c => c.m_resItem.m_itemData.m_shared.m_name == row.Key).m_amount})");
            }

            CheckRowsMatch(rows, MaterialTally.For(site.Resolved, site.Built, MaterialSources.Around(player)), "Continue");
            var footer = list.Footer.text;
            Check(footer.StartsWith($"Built {site.BuiltCount} of {site.Total}.") && footer.EndsWith($"Can build now: {site.ReadyCount} more."),
                $"the footer starts \"Built\" and counts the next click ({site.ReadyCount}): '{footer}'");
            var description = Hud.instance.m_pieceDescription.text;
            Check(description == (site.Resolved.Blueprint.Description ?? "") && NoControls(description),
                $"the Continue card keeps only the blueprint's own text, no controls: '{description.Replace("\n", " | ")}'");
            yield return CardPadHint(kit, true);
            CheckFooterAtBottom(list, "the Continue list");
            CheckLabels(list);
            CheckOnScreen("the Continue list");

            player.m_lookPitch = 10f;
            yield return new WaitForSeconds(0.6f);
            yield return Screenshot("card-materials-2");
            BlueprintMode.Exit();
        }

        /// <summary>
        /// With the pad in use (a press on a controller the game reads) the card still lists no
        /// controls, and the game's hint row under it has switched to the pad set. Back on the
        /// keyboard, the keyboard set.
        /// </summary>
        private static IEnumerator CardPadHint(ResolvedBlueprint kit, bool continuing)
        {
            var mode = continuing ? HintRow.Mode.Continue : HintRow.Mode.Blueprint;
            yield return HintsToPad();
            yield return Frames(2);
            var text = Hud.instance.m_pieceDescription.text;
            Check(ZInput.IsGamepadActive() && NoControls(text),
                $"with the pad in use the {(continuing ? "Continue" : "normal")} card lists no controls: '{text.Replace("\n", " | ")}'");
            Check(HintRow.Shown == mode && HintRow.GamepadRow != null && HintRow.GamepadRow.gameObject.activeInHierarchy
                && HintRow.ShownEntries.Count > 0
                && HintRow.ShownEntries[0].GamepadEntry.text.Contains(Localization.instance.GetBoundKeyString("JoyPlace", true)),
                $"and the game's hint row under it shows the {mode} pad set: "
                + string.Join(", ", HintRow.ShownEntries.Select(e => e.GamepadEntry.text)));
            if (!continuing)
            {
                yield return new WaitForSeconds(0.6f);
                yield return Screenshot("card-materials-pad");
            }

            yield return WorldPadOff();
            yield return Frames(3);
            text = Hud.instance.m_pieceDescription.text;
            Check(!ZInput.IsGamepadActive() && NoControls(text) && HintRow.Shown == mode && HintRow.KeyboardRow.gameObject.activeInHierarchy,
                $"back on the keyboard: still no controls on the card, the row shows the keyboard set: '{text.Replace("\n", " | ")}'");
        }

        /// <summary>A made-up blueprint whose four pieces need 12 different items, all from the workbench.</summary>
        private static Blueprint TwelveItems()
        {
            var twelve = new Blueprint { Name = "Twelve items", IconPrefab = "piece_bed02" };
            void Add(string prefab, float x, float z, float yaw)
            {
                twelve.Pieces.Add(new BlueprintPiece { PrefabName = prefab, Position = new Vector3(x, 0f, z), Rotation = Quaternion.Euler(0f, yaw, 0f) });
            }

            Add("piece_bed02", -2.5f, 0f, 0f);
            Add("piece_bathtub", 2f, 0f, 0f);
            Add("piece_banner01", 0f, 2.5f, 0f);
            Add("piece_groundtorch_wood", 0f, -1.5f, 0f);
            return twelve;
        }

        /// <summary>Check 3: 12 items in two columns, every row on the screen and inside the list.</summary>
        private static IEnumerator CardTwelveItems(Player player)
        {
            if (!ResolvedBlueprint.TryResolve(TwelveItems(), out var twelve, out var error))
            {
                Check(false, "the twelve-item blueprint resolves: " + error);
                yield break;
            }

            Check(twelve.TotalCost.Count == 12, $"set-up: the made-up blueprint needs 12 items: {twelve.TotalCost.Count}");

            // A third in full, a third half, a third none: all three looks at once.
            ClearInventoryExceptHammer(player);
            for (var i = 0; i < twelve.TotalCost.Count; i++)
            {
                var cost = twelve.TotalCost[i];
                AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, i % 3 == 0 ? cost.m_amount : i % 3 == 1 ? cost.m_amount / 2 : 0);
            }

            yield return AimBlueprint(player, twelve);
            yield return CardRefreshed();
            var list = BlueprintInfoCard.List;
            if (list == null)
            {
                Check(false, "the list exists");
                yield break;
            }

            var rows = list.ShownRows.ToList();
            var items = rows.Where(r => !r.IsStation).ToList();
            Check(items.Count == 12 && rows.Count == 12 + twelve.Stations.Count && list.Columns == 2,
                $"12 item rows and {twelve.Stations.Count} station rows, in {list.Columns} columns");
            Check(rows.All(r => r.Rect.gameObject.activeInHierarchy) && rows.Count(r => r.Column == 0) > 0 && rows.Count(r => r.Column == 1) > 0,
                $"every row is on: {rows.Count(r => r.Column == 0)} in the left column, {rows.Count(r => r.Column == 1)} in the right");
            CheckRowsMatch(rows, MaterialTally.For(twelve, null, MaterialSources.Around(player)), "twelve items");

            // Inside the list's background, not on top of each other, and not cut.
            var panel = BlueprintInfoCard.Panel;
            var inside = rows.All(r => Contains(panel, r.Rect));
            var overlaps = 0;
            for (var a = 0; a < rows.Count; a++)
            {
                for (var b = a + 1; b < rows.Count; b++)
                {
                    if (ScreenRect(rows[a].Rect).Overlaps(ScreenRect(rows[b].Rect)))
                    {
                        overlaps++;
                    }
                }
            }

            Check(inside && overlaps == 0, $"every row sits inside the list ({inside}) and none overlaps another ({overlaps})");
            var cut = rows.Where(r => r.Name.isTextTruncated || (!r.IsStation && (r.Have.isTextTruncated || r.Need.isTextTruncated))).Select(r => r.Name.text).ToList();
            Check(cut.Count == 0, "no name or number is cut short: " + (cut.Count == 0 ? "none" : string.Join(", ", cut)));
            var footer = list.Footer;
            Check(Contains(panel, footer.rectTransform), $"the footer sits inside the list: '{footer.text}'");
            CheckFooterAtBottom(list, "the twelve-item list");
            CheckLabels(list);
            CheckOnScreen("the twelve-item list");
            Log("twelve items: " + string.Join(", ", rows.Select(r => $"{r.Name.text} {(r.IsStation ? r.State.text : r.Have.text + r.Need.text)}")));

            yield return new WaitForSeconds(0.6f);
            yield return Screenshot("card-materials-3");
        }

        /// <summary>
        /// Not in the plan's list, but the choice it asks for: the Workshop 5 x 5 (400 pieces) with half
        /// its wood. Its plan is slow, so while the preview moves it is not worked out again, and once the
        /// preview holds still it is, once.
        /// </summary>
        private static IEnumerator CardBigBlueprint(Player player, ResolvedBlueprint kit)
        {
            var grid = new Blueprint { Name = "Workshop grid", IconPrefab = "piece_workbench" };
            for (var gx = 0; gx < 5; gx++)
            {
                for (var gz = 0; gz < 5; gz++)
                {
                    var offset = new Vector3((gx - 2) * 8f, 0f, (gz - 2) * 8f);
                    foreach (var part in kit.Parts)
                    {
                        grid.Pieces.Add(new BlueprintPiece { PrefabName = part.Prefab.name, Position = part.Source.Position + offset, Rotation = part.Source.Rotation });
                    }
                }
            }

            if (!ResolvedBlueprint.TryResolve(grid, out var big, out var error))
            {
                Check(false, "the 400-piece grid resolves: " + error);
                yield break;
            }

            ClearInventoryExceptHammer(player);
            foreach (var cost in big.TotalCost)
            {
                var name = cost.m_resItem.m_itemData.m_shared.m_name;
                AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, name == "$item_wood" ? cost.m_amount / 2 : cost.m_amount);
            }

            yield return AimBlueprint(player, big);
            yield return CardRefreshed();
            yield return new WaitForSeconds(1.2f);
            var first = BlueprintInfoCard.LastPlanMs;
            var list = BlueprintInfoCard.List;
            Log($"400-piece plan for the card: {first:0.0} ms ({BlueprintInfoCard.LastPlanReason}), footer '{(list != null ? list.Footer.text : "none")}'");

            // Turn the view 20 degrees over 1.5 s: the preview slides across the ground to a new spot.
            var runs = BlueprintInfoCard.PlanRuns;
            var start = player.m_lookYaw.eulerAngles.y;
            var moving = 0f;
            var travelled = 0f;
            var root = BlueprintMode.PreviewRoot;
            var was = root != null ? root.position : Vector3.zero;
            while (moving < 1.5f)
            {
                moving += Time.deltaTime;
                var yaw = Quaternion.Euler(0f, start + (Mathf.Min(moving / 1.5f, 1f) * 20f), 0f);
                player.m_lookYaw = yaw;
                player.transform.rotation = yaw;
                player.m_body.rotation = yaw;
                yield return null;
                root = BlueprintMode.PreviewRoot;
                if (root != null)
                {
                    travelled += (root.position - was).magnitude;
                    was = root.position;
                }
            }

            var whileMoving = BlueprintInfoCard.PlanRuns - runs;
            yield return new WaitForSeconds(1.5f);
            var afterStill = BlueprintInfoCard.PlanRuns - runs - whileMoving;
            Check(first > 5.0 && travelled > 2f && whileMoving == 0 && afterStill == 1 && BlueprintInfoCard.LastPlanReason == "moved",
                $"a slow plan ({first:0.0} ms) waits while the preview moves ({travelled:0.0} m in 1.5 s: {whileMoving} runs),"
                + $" then runs once when it holds still ({afterStill}, because: {BlueprintInfoCard.LastPlanReason}, {BlueprintInfoCard.LastPlanMs:0.0} ms)");
            ClearInventoryExceptHammer(player);
        }

        /// <summary>Check 4: a normal piece picked. The list goes, and the game fills its own slots again.</summary>
        private static IEnumerator CardNormalPiece(Player player)
        {
            var before = BlueprintMode.Active && BlueprintInfoCard.Visible;
            var wall = PiecePrefab("woodwall");
            var picked = wall != null && player.SetSelectedPiece(wall);
            yield return null;
            yield return null;
            var hud = Hud.instance;
            var slotsOn = hud.m_requirementItems.Count(s => s != null && s.activeInHierarchy);
            var want = wall != null ? wall.m_resources.Length + (wall.m_craftingStation != null ? 1 : 0) : -1;
            Check(before && picked && !BlueprintMode.Active && !BlueprintInfoCard.Visible,
                $"from blueprint mode with the list up ({before}), picking the wood wall leaves it and hides the list ({BlueprintInfoCard.Visible})");
            Check(slotsOn == want && hud.m_buildSelection.text == Localization.instance.Localize(wall.m_name),
                $"the game's own slots are back: {slotsOn} on, the wall has {wall.m_resources.Length} resources and a station; card '{hud.m_buildSelection.text}'");
        }

        /// <summary>Every shown row's texts are the tally's numbers, row by row.</summary>
        private static void CheckRowsMatch(List<MaterialList.Row> rows, Tally tally, string what)
        {
            var wrong = new List<string>();
            foreach (var item in tally.Rows)
            {
                var row = rows.FirstOrDefault(r => !r.IsStation && r.Key == item.Item);
                if (row == null || row.Have.text != MaterialList.Short(item.Have) || row.Need.text != " / " + MaterialList.Short(item.Need)
                    || row.Name.text != item.Name || row.Icon.sprite != item.Icon || row.Icon.sprite == null)
                {
                    wrong.Add(row == null ? item.Name + " has no row" : $"{item.Name}: '{row.Have.text}{row.Need.text}' want {item.Have} / {item.Need}");
                }
            }

            foreach (var station in tally.Stations)
            {
                var row = rows.FirstOrDefault(r => r.IsStation && r.Key == station.Station);
                if (row == null || row.Icon.sprite != station.Icon || row.Name.text != station.Name)
                {
                    wrong.Add(station.Name + (row == null ? " has no row" : " shows the wrong icon or name"));
                }
            }

            Check(wrong.Count == 0, $"{what}: every row's icon, name, have and need are MaterialTally's"
                + (wrong.Count == 0 ? $" ({tally.Rows.Count} items, {tally.Stations.Count} stations)" : ": " + string.Join("; ", wrong)));
        }

        /// <summary>Every label of the list is on the mod's own text material, never the shared vanilla one.</summary>
        private static void CheckLabels(MaterialList list)
        {
            var labels = list.Root.GetComponentsInChildren<TMPro.TMP_Text>(true);
            var wrong = labels.Count(l => l.fontSharedMaterial != UiTheme.FontMaterial);
            Check(labels.Length > 0 && wrong == 0, $"all {labels.Length} labels use UiTheme.FontMaterial ({wrong} do not)");
        }

        /// <summary>
        /// The footer is the list's last line, at the bottom of its background: under it only the
        /// padding, the same as over the first row. Nothing empty is left where a second line was.
        /// </summary>
        private static void CheckFooterAtBottom(MaterialList list, string what)
        {
            var panel = ScreenRect(BlueprintInfoCard.Panel);
            var footer = ScreenRect(list.Footer.rectTransform);
            var first = list.ShownRows.FirstOrDefault();
            var over = first != null ? panel.yMax - ScreenRect(first.Rect).yMax : -1f;
            var under = footer.yMin - panel.yMin;
            var rowsOver = list.ShownRows.All(r => ScreenRect(r.Rect).yMin >= footer.yMax - 0.5f);
            Check(first != null && Mathf.Abs(under - over) < 1.5f && rowsOver,
                $"{what}: the footer is the last line, {under:0} px over the bottom edge, as the first row is {over:0} px under the top");
        }

        private static void CheckOnScreen(string what)
        {
            var rect = ScreenRect(BlueprintInfoCard.Panel);
            var fits = rect.xMin >= 0f && rect.yMin >= 0f && rect.xMax <= Screen.width && rect.yMax <= Screen.height;
            Check(fits, $"{what} fits on the screen: pixels ({rect.xMin:0},{rect.yMin:0})-({rect.xMax:0},{rect.yMax:0}) of {Screen.width}x{Screen.height}");
        }

        private static bool OnScreen(RectTransform rect)
        {
            return rect != null && ScreenRect(rect).Overlaps(new Rect(0f, 0f, Screen.width, Screen.height));
        }

        /// <summary>A rect's box in screen pixels. The HUD is an overlay canvas, so world corners are pixels.</summary>
        private static Rect ScreenRect(RectTransform rect)
        {
            if (rect == null)
            {
                return new Rect(-1f, -1f, 0f, 0f);
            }

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        private static bool Contains(RectTransform outer, RectTransform inner)
        {
            var a = ScreenRect(outer);
            var b = ScreenRect(inner);
            return b.xMin >= a.xMin - 0.5f && b.yMin >= a.yMin - 0.5f && b.xMax <= a.xMax + 0.5f && b.yMax <= a.yMax + 0.5f;
        }

        /// <summary>The build card's children, for the log: where the name, the description and the slots sit.</summary>
        private static void LogCard(Hud hud)
        {
            var panel = BlueprintInfoCard.Panel;
            var card = panel != null ? panel.parent as RectTransform : null;
            if (card == null)
            {
                return;
            }

            var text = new StringBuilder("card children:");
            foreach (RectTransform child in card)
            {
                var image = child.GetComponent<UnityEngine.UI.Image>();
                var label = child.GetComponent<TMPro.TMP_Text>();
                text.Append($" | '{child.name}' on={child.gameObject.activeSelf} pos {V2(child.anchoredPosition)} size {V2(child.rect.size)}"
                    + $" anchors {V2(child.anchorMin)}-{V2(child.anchorMax)} pivot {V2(child.pivot)}"
                    + (image != null ? $" image '{(image.sprite != null ? image.sprite.name : "none")}' {image.color}" : "")
                    + (label != null ? $" text {label.fontSize}pt '{label.text.Replace("\n", "\\n")}'" : ""));
            }

            var own = card.GetComponent<UnityEngine.UI.Image>();
            text.Append($" | card image: {(own != null ? (own.sprite != null ? own.sprite.name : "no sprite") + " " + own.color : "none")}");
            var screen = ScreenRect(card);
            text.Append($" | card pixels ({screen.xMin:0},{screen.yMin:0})-({screen.xMax:0},{screen.yMax:0}), list pixels {ScreenRect(panel)}");
            Log(text.ToString());
        }

        // ---------- scenario: hint_row ----------

        /// <summary>Counts how often the object it sits on is switched on.</summary>
        private sealed class EnableCounter : MonoBehaviour
        {
            public int Count;

            private void OnEnable()
            {
                Count++;
            }
        }

        /// <summary>What the test expects one hint to show, worked out here from the game's own names.</summary>
        private sealed class WantHint
        {
            public string Label;

            /// <summary>The keyboard side, "Shift + wheel".</summary>
            public string Keys;

            /// <summary>The pad side, the game's icon tags.</summary>
            public string Pad;
        }

        /// <summary>The game's own row as it shows it, before a mode: to compare with after.</summary>
        private sealed class VanillaRow
        {
            public TMPro.TMP_Text KbLabel;
            public TMPro.TMP_Text KbKey;
            public UnityEngine.UI.Image KbCap;
            public TMPro.TMP_Text KbPlus;
            public UnityEngine.UI.Image KbWheel;
            public TMPro.TMP_Text GpText;
            public float KbSpacing;
            public float GpSpacing;
            public float EntrySpacing;
            public float KbLabelSize;
            public float GpSize;
            public float KbCentreY;
            public float GpCentreY;
        }

        /// <summary>
        /// The controls of blueprint mode, Continue and the capture sit in the game's own hint row, in
        /// its own look, on the keyboard and on a pad the game reads, and switch when the input does.
        /// The game's own row comes back unchanged after each. The card and the capture's status line
        /// carry no controls any more. Screenshots of every state.
        /// </summary>
        private static IEnumerator TestHintRow(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            ClearSites();
            yield return new WaitForSeconds(0.5f);
            PieceCatalog.Ensure();

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Name == "Workshop");
            ResolvedBlueprint resolved = null;
            var error = "no Workshop kit";
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out error))
            {
                Check(false, "the workshop kit resolves: " + error);
                yield break;
            }

            Unlock(player, resolved);
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);
            var hints = KeyHints.instance;
            Check(hints != null, "the game's hint row is there");
            if (hints == null)
            {
                yield break;
            }

            // ---- 1. the game's own row with the hammer out, keyboard then pad ----
            BlueprintMode.Exit();
            player.m_lookPitch = 20f;
            yield return new WaitForSeconds(0.5f);
            var vanilla = ReadVanilla(hints);
            Check(vanilla != null && HintRow.Shown == HintRow.Mode.None && hints.m_buildHints.activeSelf,
                "a normal hammer piece: the game's build hints show, none of ours");
            if (vanilla == null)
            {
                yield break;
            }

            var gameRow = GameRow(hints);
            Log("the game's row, keyboard: " + gameRow);
            yield return Screenshot("hints-vanilla-keys");
            yield return HintsToPad();
            var gameRowPad = GameRow(hints);
            vanilla.GpCentreY = ScreenRect(vanilla.GpText.rectTransform).center.y;
            vanilla.GpSize = vanilla.GpText.fontSize;
            Log("the game's row, pad: " + gameRowPad);
            yield return Screenshot("hints-vanilla-pad");
            yield return HintsToKeyboard();

            // ---- 2. a blueprint in hand ----
            yield return AimBlueprint(player, resolved);
            player.m_lookPitch = 20f;
            yield return Frames(4);
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Blueprint, "a blueprint in hand");
            var counter = hints.m_buildHints.AddComponent<EnableCounter>();
            yield return Frames(30);
            Check(counter.Count == 0 && !hints.m_buildHints.activeSelf,
                $"the game's build hints stay off while ours show, not switched on and off each frame: on {counter.Count} times in 30 frames");
            UnityEngine.Object.Destroy(counter);
            var description = Hud.instance.m_pieceDescription.text;
            var count = $"{resolved.Parts.Count} pieces.";
            Check(description == (string.IsNullOrEmpty(kit.Description) ? count : kit.Description + "\n" + count) && NoControls(description),
                $"the card keeps its own text and the piece count, no controls: '{description.Replace("\n", " | ")}'");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-blueprint-keys");
            yield return HintsToPad();
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Blueprint, "a blueprint in hand, pad");
            description = Hud.instance.m_pieceDescription.text;
            Check(NoControls(description), $"with the pad in use the card still has no controls: '{description.Replace("\n", " | ")}'");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-blueprint-pad");
            yield return HintsToKeyboard();
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Blueprint, "a blueprint in hand, back on the keyboard");

            BlueprintMode.Exit();
            yield return Frames(4);
            Check(HintRow.Shown == HintRow.Mode.None && GameRow(hints) == gameRow,
                "blueprint mode off: the game's row is back exactly as it was: " + GameRow(hints));

            // ---- 3. Continue on an unfinished build ----
            yield return StartSite(player, resolved, (name, amount) => name == "$item_wood" ? amount / 2 : amount);
            FirstEntry(player);
            player.m_lookPitch = 20f;
            yield return Frames(4);
            Check(BlueprintMode.CurrentSite != null, "set-up: the key offers Continue on it");
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Continue, "Continue");
            description = Hud.instance.m_pieceDescription.text;
            Check(description == (kit.Description ?? "") && NoControls(description),
                $"the Continue card keeps only the blueprint's own text: '{description.Replace("\n", " | ")}'");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-continue-keys");
            yield return HintsToPad();
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Continue, "Continue, pad");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-continue-pad");
            yield return HintsToKeyboard();
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Continue, "Continue, back on the keyboard");
            BlueprintMode.Exit();
            yield return Frames(4);
            Check(HintRow.Shown == HintRow.Mode.None && GameRow(hints) == gameRow,
                "Continue off: the game's row is back exactly as it was: " + GameRow(hints));
            ClearSites();
            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(0.5f);

            // ---- 4. a capture with the hammer out ----
            player.m_lookPitch = 35f;
            yield return new WaitForSeconds(0.3f);
            WorldCapture.Begin(player.transform.position + (player.transform.forward * 8f));
            WorldCapture.Pin(player.transform.position + (player.transform.forward * 8f));
            yield return Frames(4);
            Check(WorldCapture.Active && WorldCapture.LastMessage == "", $"a capture starts with no \"press the key\" message: '{WorldCapture.LastMessage}'");
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Capture, "a capture, hammer out");
            var status = CaptureHud.Text;
            Check(CaptureHud.Visible && status.StartsWith("Capture  ") && !status.Contains("\n") && NoControls(status),
                $"the capture's status is one line with no controls: '{status.Replace("\n", " | ")}'");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-capture-hammer-keys");
            yield return HintsToPad();
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Capture, "a capture, hammer out, pad");
            status = CaptureHud.Text;
            Check(!status.Contains("\n") && NoControls(status), $"with the pad, still one line with no controls: '{status}'");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-capture-hammer-pad");
            WorldCapture.Cancel();
            yield return Frames(4);
            Check(HintRow.Shown == HintRow.Mode.None && GameRow(hints) == gameRowPad,
                "the capture stopped on the pad: the game's pad row is back exactly as it was: " + GameRow(hints));
            yield return HintsToKeyboard();
            yield return Frames(2);
            Check(GameRow(hints) == gameRow, "and back on the keyboard, the game's keyboard row as it was: " + GameRow(hints));

            // ---- 5. a capture with the hammer away: the game showed nothing ----
            var hammer = player.GetRightItem();
            player.UnequipItem(hammer);
            yield return new WaitForSeconds(0.5f);
            var bare = GameRow(hints);
            Log("the game's row, hammer away: " + bare);
            WorldCapture.Begin(player.transform.position + (player.transform.forward * 8f));
            WorldCapture.Pin(player.transform.position + (player.transform.forward * 8f));
            yield return Frames(4);
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Capture, "a capture, hammer away");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-capture-nohammer-keys");
            yield return HintsToPad();
            yield return CheckHintRow(hints, vanilla, HintRow.Mode.Capture, "a capture, hammer away, pad");
            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("hints-capture-nohammer-pad");
            yield return HintsToKeyboard();
            WorldCapture.Cancel();
            yield return Frames(4);
            Check(HintRow.Shown == HintRow.Mode.None && GameRow(hints) == bare,
                "the capture stopped with the hammer away: the game's row is as it was: " + GameRow(hints));

            // ---- 6. the same with a club: the game showed its combat hints ----
            var club = player.GetInventory().AddItem("Club", 1, 1, 0, 0L, "", false);
            if (club != null)
            {
                player.EquipItem(club);
                yield return new WaitForSeconds(0.5f);
                var combat = GameRow(hints);
                Check(hints.m_combatHints.activeSelf, "set-up: with a club the game shows its combat hints");
                WorldCapture.Begin(player.transform.position + (player.transform.forward * 8f));
                WorldCapture.Pin(player.transform.position + (player.transform.forward * 8f));
                yield return Frames(4);
                Check(HintRow.Shown == HintRow.Mode.Capture && !hints.m_combatHints.activeSelf,
                    "a capture with a club: our capture hints in place of the combat hints");
                WorldCapture.Cancel();
                yield return Frames(4);
                Check(HintRow.Shown == HintRow.Mode.None && GameRow(hints) == combat,
                    "the capture stopped: the combat hints are back as they were: " + GameRow(hints));
                player.UnequipItem(club);
                player.GetInventory().RemoveItem(club);
            }
            else
            {
                Check(false, "set-up: a club to hold");
            }

            yield return WorldPadOff();
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            ClearSites();
        }

        /// <summary>
        /// The controls must be in the hint row, nowhere else: no key, button or wheel names and no
        /// "do this: that" hints in a text.
        /// </summary>
        private static bool NoControls(string text)
        {
            var words = new[]
            {
                "Wheel", "wheel", "rotate", "next", "edit", "Click", "click", "Esc", "cancel", "R1", "R2", "L2", "RT", "RB", "LT",
                "□", "△", "○", "D-pad", "<sprite", "Shift", "Alt", "F7", "F8", "B:", "Remove",
            };
            return words.All(w => !text.Contains(w));
        }

        /// <summary>The game's own row as it shows now: which of its groups are on, and every text and picture on screen in them. Ours left out.</summary>
        private static string GameRow(KeyHints hints)
        {
            var text = new StringBuilder();
            foreach (Transform group in hints.transform)
            {
                if (HintRow.Root != null && group == HintRow.Root)
                {
                    continue;
                }

                text.Append(group.name).Append(group.gameObject.activeSelf ? " on" : " off");
                if (group.gameObject.activeInHierarchy)
                {
                    text.Append(" [");
                    foreach (var label in group.GetComponentsInChildren<TMPro.TMP_Text>(false))
                    {
                        text.Append(label.transform.parent.name).Append('/').Append(label.name).Append('=').Append(label.text).Append("; ");
                    }

                    foreach (var image in group.GetComponentsInChildren<UnityEngine.UI.Image>(false))
                    {
                        text.Append(image.name).Append("; ");
                    }

                    text.Append(']');
                }

                text.Append(" | ");
            }

            return text.ToString();
        }

        /// <summary>The game's own pieces of its build row, and where they sit, read while it shows on the keyboard.</summary>
        private static VanillaRow ReadVanilla(KeyHints hints)
        {
            var build = hints.m_buildHints.transform;
            var kb = build.Find("Keyboard");
            var gp = build.Find("Gamepad");
            var place = kb != null ? kb.Find("Place") : null;
            var row = new VanillaRow
            {
                KbLabel = place != null ? place.Find("Text")?.GetComponent<TMPro.TMP_Text>() : null,
                KbCap = place != null ? place.Find("key_bkg")?.GetComponent<UnityEngine.UI.Image>() : null,
                KbKey = place != null ? place.Find("key_bkg/Key")?.GetComponent<TMPro.TMP_Text>() : null,
                KbPlus = kb != null ? kb.Find("Copy/Text (1)")?.GetComponent<TMPro.TMP_Text>() : null,
                KbWheel = kb != null ? kb.Find("rotate/mousew_icon")?.GetComponent<UnityEngine.UI.Image>() : null,
                GpText = gp != null ? gp.Find("Text - Place")?.GetComponent<TMPro.TMP_Text>() : null,
            };
            if (row.KbLabel == null || row.KbCap == null || row.KbKey == null || row.KbPlus == null || row.KbWheel == null || row.GpText == null)
            {
                Check(false, "the game's build row has its Place, Copy, rotate and pad Place entries");
                return null;
            }

            row.KbSpacing = kb.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>().spacing;
            row.GpSpacing = gp.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>().spacing;
            row.EntrySpacing = place.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>().spacing;
            row.KbLabelSize = row.KbLabel.fontSize;
            row.GpSize = row.GpText.fontSize;
            row.KbCentreY = ScreenRect((RectTransform)place).center.y;
            Log($"the game's row: label {row.KbLabel.font.name} {row.KbLabelSize} {row.KbLabel.color} '{row.KbLabel.fontSharedMaterial.name}',"
                + $" key {row.KbKey.font.name} {row.KbKey.fontSize} {row.KbKey.fontStyle}, cap '{row.KbCap.sprite.name}' {row.KbCap.color},"
                + $" spacing {row.KbSpacing} / {row.GpSpacing} / {row.EntrySpacing}, row centre y {row.KbCentreY:0.#}");
            Check(ZInput.IsMouseActive() && !ZInput.IsGamepadActive() && row.KbLabel.gameObject.activeInHierarchy,
                "the game's keyboard row shows");
            return row;
        }

        /// <summary>What each mode must show, from the game's own button names and the mod's keys.</summary>
        private static List<WantHint> WantHints(HintRow.Mode mode)
        {
            string G(string button) => Localization.instance.GetBoundKeyString(button, true);
            string K(KeyCode key) => ZInput.KeyCodeToDisplayName(key);
            string Plus(string a, string b) => a + " + " + b;
            string Or(string a, string b) => a + " / " + b;
            WantHint W(string label, string keys, string pad) => new WantHint { Label = label, Keys = keys, Pad = pad };

            var edit = W("Edit", K(EditorConfig.Key.Value), Plus(G("JoyAltKeys"), G("JoyButtonX")));
            var menu = W("Build Menu", G("BuildMenu"), G(ZInput.IsNonClassicFunctionality() ? "JoyBuildMenu" : "JoyUse"));
            var rotate = ZInput.InputLayout == InputLayout.Default ? Plus(G("JoyRotate"), G("JoyRStick")) : Or(G("JoyRotate"), G("JoyRotateRight"));
            switch (mode)
            {
                case HintRow.Mode.Blueprint:
                    return new List<WantHint>
                    {
                        W("Build", G("Attack"), G("JoyPlace")),
                        W("Next blueprint", K(ValheimTomrerPlugin.BlueprintKey.Value), G("JoyButtonX")),
                        edit,
                        menu,
                        W("Rotate", "wheel", rotate),
                    };
                case HintRow.Mode.Continue:
                    return new List<WantHint>
                    {
                        W("Build", G("Attack"), G("JoyPlace")),
                        W("Remove", G("Remove"), G("JoyRemove")),
                        W("Next", K(ValheimTomrerPlugin.BlueprintKey.Value), G("JoyButtonX")),
                        edit,
                        menu,
                    };
                case HintRow.Mode.Capture:
                    var leftRight = Or(G("JoyDPadLeft"), G("JoyDPadRight"));
                    var upDown = Or(G("JoyDPadUp"), G("JoyDPadDown"));
                    return new List<WantHint>
                    {
                        W("Capture", K(EditorConfig.CaptureKey.Value), Plus(G("JoyAltKeys"), G("JoyButtonY"))),
                        W("Turn", "wheel", leftRight),
                        W("Width", "Shift + wheel", Plus(G("JoyAltKeys"), leftRight)),
                        W("Depth", "Alt + wheel", Plus(G("JoyAltKeys"), upDown)),
                        W("Both sides", "Shift + Alt + wheel", upDown),
                        W("Stop", K(KeyCode.Escape), G("JoyButtonB")),
                    };
                default:
                    return new List<WantHint>();
            }
        }

        /// <summary>A keyboard entry as it shows: its label, then its caps' texts, "+" and "wheel", in order.</summary>
        private static string ShownKeys(RectTransform entry, out string label)
        {
            label = null;
            var parts = new List<string>();
            foreach (Transform child in entry)
            {
                if (!child.gameObject.activeSelf)
                {
                    continue;
                }

                var image = child.GetComponent<UnityEngine.UI.Image>();
                var text = child.GetComponent<TMPro.TMP_Text>();
                if (image != null && image.sprite != null && image.sprite.name == "mousew_icon")
                {
                    parts.Add("wheel");
                }
                else if (image != null)
                {
                    var key = child.GetComponentInChildren<TMPro.TMP_Text>();
                    parts.Add(key != null ? key.text : "?");
                }
                else if (text != null && label == null)
                {
                    label = text.text;
                }
                else if (text != null)
                {
                    parts.Add(text.text);
                }
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// One mode's row: exactly its set, in the game's row and look, on the input in use. The game's
        /// groups are off, ours on; the entries are the game's own font, size, colour, material, caps,
        /// icons and spacing; they sit on the game's line, on screen, left to right, with no overlap.
        /// </summary>
        private static IEnumerator CheckHintRow(KeyHints hints, VanillaRow vanilla, HintRow.Mode mode, string what)
        {
            yield return Frames(2);
            var pad = ZInput.IsGamepadActive();
            var want = WantHints(mode);
            var entries = HintRow.ShownEntries;
            var gameOff = !hints.m_buildHints.activeSelf && !hints.m_combatHints.activeSelf && !hints.m_fishingHints.activeSelf
                && !hints.m_inventoryHints.activeSelf && !hints.m_radialHints.activeSelf;
            Check(HintRow.Shown == mode && HintRow.Root != null && HintRow.Root.gameObject.activeInHierarchy && gameOff,
                $"{what}: our {mode} hints show in the game's row, every group of the game's is off (shown {HintRow.Shown})");
            Check(pad ? HintRow.GamepadRow.gameObject.activeInHierarchy && !HintRow.KeyboardRow.gameObject.activeSelf
                    : HintRow.KeyboardRow.gameObject.activeInHierarchy && !HintRow.GamepadRow.gameObject.activeSelf,
                $"{what}: the {(pad ? "pad" : "keyboard")} row shows, the other one not (the game says pad {pad})");
            if (HintRow.Shown != mode || HintRow.Root == null)
            {
                yield break;
            }

            // Exactly the set.
            var shown = new List<string>();
            var ok = entries.Count == want.Count;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var keys = ShownKeys(entry.KeyboardEntry, out var label);
                var padText = entry.GamepadEntry.text;
                shown.Add(pad ? padText : $"{label} [{keys}]");
                if (i >= want.Count)
                {
                    continue;
                }

                var w = want[i];
                ok &= label == w.Label && keys == w.Keys && !string.IsNullOrEmpty(w.Pad)
                    && padText == $"{w.Label} <mspace=0.6em> {w.Pad}</mspace>";
            }

            Check(ok, $"{what}: exactly {want.Count} entries, "
                + string.Join(", ", want.Select(w => pad ? w.Label + " " + w.Pad : $"{w.Label} [{w.Keys}]"))
                + " | shown: " + string.Join(", ", shown));

            // The same buttons, in the same family, as the game's own entries next to them.
            var vanillaPlace = vanilla.GpText.text;
            var place = entries.Count > 0 ? entries[0].GamepadEntry.text : "";
            var placeGlyph = Localization.instance.GetBoundKeyString("JoyPlace", true);
            Check(!pad || mode == HintRow.Mode.Capture || (vanillaPlace.Contains(placeGlyph) && place.Contains(placeGlyph)),
                $"{what}: the build button is the game's own Place icon: ours '{place}', the game's '{vanillaPlace}'");

            // The game's look.
            var style = new List<string>();
            var gpRow = HintRow.GamepadRow.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            var kbRow = HintRow.KeyboardRow.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            if (gpRow == null || kbRow == null || gpRow.spacing != vanilla.GpSpacing || kbRow.spacing != vanilla.KbSpacing
                || gpRow.childAlignment != TextAnchor.MiddleRight || kbRow.childAlignment != TextAnchor.MiddleRight)
            {
                style.Add("row layout");
            }

            foreach (var entry in entries)
            {
                var g = entry.GamepadEntry;
                if (g.font != vanilla.GpText.font || g.fontSize != vanilla.GpSize || g.color != vanilla.GpText.color
                    || g.fontSharedMaterial != vanilla.GpText.fontSharedMaterial || g.fontStyle != vanilla.GpText.fontStyle)
                {
                    style.Add($"pad '{entry.Label}' {g.font.name} {g.fontSize} {g.color} {g.fontSharedMaterial.name}");
                }

                var kb = entry.KeyboardEntry;
                var entryLayout = kb.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                if (entryLayout == null || entryLayout.spacing != vanilla.EntrySpacing)
                {
                    style.Add($"'{entry.Label}' entry spacing");
                }

                foreach (Transform child in kb)
                {
                    var image = child.GetComponent<UnityEngine.UI.Image>();
                    var text = child.GetComponent<TMPro.TMP_Text>();
                    if (image != null && image.sprite != null && image.sprite.name == "mousew_icon")
                    {
                        if (image.sprite != vanilla.KbWheel.sprite || ((RectTransform)child).rect.size != vanilla.KbWheel.rectTransform.rect.size)
                        {
                            style.Add($"'{entry.Label}' wheel {((RectTransform)child).rect.size}");
                        }
                    }
                    else if (image != null)
                    {
                        var key = child.GetComponentInChildren<TMPro.TMP_Text>();
                        if (image.sprite != vanilla.KbCap.sprite || image.color != vanilla.KbCap.color || image.type != vanilla.KbCap.type
                            || key == null || key.font != vanilla.KbKey.font || key.fontSize != vanilla.KbKey.fontSize
                            || key.fontStyle != vanilla.KbKey.fontStyle || key.color != vanilla.KbKey.color
                            || key.fontSharedMaterial != vanilla.KbKey.fontSharedMaterial)
                        {
                            style.Add($"'{entry.Label}' cap '{(key != null ? key.text : "?")}'");
                        }
                    }
                    else if (text != null)
                    {
                        var like = text.text == "+" ? vanilla.KbPlus : vanilla.KbLabel;
                        var size = text.text == "+" ? vanilla.KbPlus.fontSizeMax : vanilla.KbLabelSize;
                        if (text.font != like.font || text.fontSize != size || text.color != like.color
                            || text.fontSharedMaterial != like.fontSharedMaterial || text.fontStyle != like.fontStyle)
                        {
                            style.Add($"'{entry.Label}' text '{text.text}' {text.font.name} {text.fontSize} {text.color} {text.fontSharedMaterial.name}");
                        }
                    }
                }
            }

            Check(style.Count == 0, $"{what}: every entry is in the game's look (font, size, colour, material, caps, wheel, spacing)"
                + (style.Count > 0 ? ": differs " + string.Join(", ", style) : ""));

            // Where: the game's row, on screen, left to right, no overlap, the game's gaps, right-aligned.
            var row = ScreenRect(HintRow.Root);
            var build = ScreenRect((RectTransform)hints.m_buildHints.transform);
            var rects = entries.Select(e => ScreenRect(pad ? e.GamepadEntry.rectTransform : e.KeyboardEntry)).ToList();
            var centre = pad ? vanilla.GpCentreY : vanilla.KbCentreY;
            var gap = (pad ? vanilla.GpSpacing : vanilla.KbSpacing) * HintRow.Root.lossyScale.x;
            var where = new List<string>();
            if (Mathf.Abs(row.xMin - build.xMin) > 0.5f || Mathf.Abs(row.xMax - build.xMax) > 0.5f
                || Mathf.Abs(row.yMin - build.yMin) > 0.5f || Mathf.Abs(row.yMax - build.yMax) > 0.5f)
            {
                where.Add($"row {row} vs the game's {build}");
            }

            for (var i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                if (r.xMin < 0f || r.yMin < 0f || r.xMax > Screen.width || r.yMax > Screen.height)
                {
                    where.Add($"'{entries[i].Label}' off screen {r}");
                }

                if (Mathf.Abs(r.center.y - centre) > 1.5f)
                {
                    where.Add($"'{entries[i].Label}' centre y {r.center.y:0.#}, the game's {centre:0.#}");
                }

                if (i > 0 && (r.xMin < rects[i - 1].xMax - 0.5f || Mathf.Abs(r.xMin - rects[i - 1].xMax - gap) > 1.5f))
                {
                    where.Add($"'{entries[i].Label}' starts {r.xMin - rects[i - 1].xMax:0.#} px after the one before, the game's gap is {gap:0.#}");
                }
            }

            if (rects.Count > 0 && Mathf.Abs(rects[rects.Count - 1].xMax - row.xMax) > 1.5f)
            {
                where.Add($"the last entry ends at {rects[rects.Count - 1].xMax:0.#}, the row at {row.xMax:0.#}");
            }

            // Nothing drawn of an entry on the build card or the materials list, whichever shows.
            var buildCard = Hud.instance != null && Hud.instance.m_buildHud != null ? Hud.instance.m_buildHud.transform.Find("SelectedInfo") as RectTransform : null;
            var above = new List<RectTransform>();
            if (buildCard != null && buildCard.gameObject.activeInHierarchy)
            {
                foreach (RectTransform child in buildCard)
                {
                    var image = child.GetComponent<UnityEngine.UI.Image>();
                    if (image != null && image.enabled && child.gameObject.activeInHierarchy && child.anchorMin == Vector2.zero && child.anchorMax == Vector2.one)
                    {
                        above.Add(child);
                    }
                }
            }

            if (BlueprintInfoCard.Visible)
            {
                above.Add(BlueprintInfoCard.Panel);
            }

            var drawn = entries.SelectMany(e => Drawn(pad ? e.GamepadEntry.rectTransform : e.KeyboardEntry)).ToList();
            foreach (var box in above)
            {
                var r = ScreenRect(box);
                var hit = drawn.FirstOrDefault(d => d.Overlaps(r));
                if (hit != default(Rect))
                {
                    where.Add($"something drawn at {hit} overlaps '{box.name}' {r}");
                }
            }

            if (above.Count > 0)
            {
                Log($"{what}: over the row: " + string.Join(", ", above.Select(a => $"'{a.name}' from y {ScreenRect(a).yMin:0}"))
                    + $"; the row's drawn parts reach y {(drawn.Count > 0 ? drawn.Max(d => d.yMax) : 0f):0}");
            }

            Check(where.Count == 0, $"{what}: the entries sit in the game's row, on screen, in order, with the game's gaps and no overlap, "
                + $"x {(rects.Count > 0 ? rects[0].xMin : 0f):0} to {(rects.Count > 0 ? rects[rects.Count - 1].xMax : 0f):0} of {Screen.width}"
                + (where.Count > 0 ? ": " + string.Join("; ", where) : ""));
        }

        private static readonly Dictionary<Sprite, Rect> VisibleParts = new Dictionary<Sprite, Rect>();

        /// <summary>
        /// What an entry draws, in screen pixels: each text, cap and icon. A picture that keeps its
        /// shape (the wheel) draws only the middle of its box, and only its visible pixels count.
        /// </summary>
        private static List<Rect> Drawn(RectTransform entry)
        {
            var parts = new List<Rect>();
            foreach (var graphic in entry.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
            {
                var r = ScreenRect(graphic.rectTransform);
                if (graphic is UnityEngine.UI.Image image && image.preserveAspect && image.sprite != null && r.height > 0f)
                {
                    var aspect = image.sprite.rect.width / image.sprite.rect.height;
                    var w = Mathf.Min(r.width, r.height * aspect);
                    var h = w / aspect;
                    r = new Rect(r.center.x - (w / 2f), r.center.y - (h / 2f), w, h);
                    var part = VisiblePart(image.sprite);
                    r = new Rect(r.x + (part.x * r.width), r.y + (part.y * r.height), part.width * r.width, part.height * r.height);
                }

                parts.Add(r);
            }

            return parts;
        }

        /// <summary>
        /// The part of a sprite that is not see-through, as fractions of its rect (0 to 1, from the
        /// bottom left). Read back through a temporary render texture, because the game's textures
        /// cannot be read directly. Nothing is written to disk.
        /// </summary>
        private static Rect VisiblePart(Sprite sprite)
        {
            if (VisibleParts.TryGetValue(sprite, out var known))
            {
                return known;
            }

            var whole = new Rect(0f, 0f, 1f, 1f);
            var texture = sprite.texture;
            var area = sprite.textureRect;
            if (texture == null || area.width < 1f || area.height < 1f)
            {
                VisibleParts[sprite] = whole;
                return whole;
            }

            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
            var before = RenderTexture.active;
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            var read = new Texture2D((int)area.width, (int)area.height, TextureFormat.ARGB32, false);
            read.ReadPixels(new Rect(area.x, area.y, area.width, area.height), 0, 0);
            read.Apply();
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(target);

            var pixels = read.GetPixels32();
            int width = read.width, height = read.height;
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels[(y * width) + x].a > 25)
                    {
                        minX = Mathf.Min(minX, x);
                        maxX = Mathf.Max(maxX, x);
                        minY = Mathf.Min(minY, y);
                        maxY = Mathf.Max(maxY, y);
                    }
                }
            }

            UnityEngine.Object.Destroy(read);
            var offset = sprite.textureRectOffset;
            var full = sprite.rect;
            var part = maxX < 0 ? whole : new Rect(
                (offset.x + minX) / full.width,
                (offset.y + minY) / full.height,
                (maxX - minX + 1) / full.width,
                (maxY - minY + 1) / full.height);
            Log($"sprite '{sprite.name}' {full.width}x{full.height}: visible part {part}");
            VisibleParts[sprite] = part;
            return part;
        }

        /// <summary>A press on the world controller, the game's modifier alone: the game says the pad is in use.</summary>
        private static IEnumerator HintsToPad()
        {
            WorldPadOn();
            yield return PadPress(true);
            yield return Frames(3);
            Check(ZInput.IsGamepadActive(), "a press on the controller: the game says the pad is in use");
        }

        /// <summary>The mouse moves a little: the game says the keyboard and mouse are in use again.</summary>
        private static IEnumerator HintsToKeyboard()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            for (var i = 0; i < 4 && mouse != null && ZInput.IsGamepadActive(); i++)
            {
                var at = mouse.position.ReadValue();
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse, new UnityEngine.InputSystem.LowLevel.MouseState { position = at, delta = new Vector2(2f, 0f) });
                yield return Frames(2);
            }

            yield return Frames(2);
            Check(!ZInput.IsGamepadActive() && ZInput.IsMouseActive(), "the mouse moves: the game says the keyboard and mouse are in use");
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

            yield return EditorOpenPad(player);
        }

        /// <summary>
        /// The pad: L2 + square in the world opens the editor, on the blueprint in the hammer's hand,
        /// and nothing else happens (no inventory, no sitting). Inside, the same two buttons close it.
        /// The world side goes through the game's own buttons (a controller the game reads), the
        /// editor side through the editor's fake pad, as every editor pad test does.
        /// </summary>
        private static IEnumerator EditorOpenPad(Player player)
        {
            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Pieces.Count > 2);
            ResolvedBlueprint resolved = null;
            if (kit == null || !ResolvedBlueprint.TryResolve(kit, out resolved, out _))
            {
                Check(false, "a kit to put in the hammer");
                yield break;
            }

            EditorSession.Forget();
            Unlock(player, resolved);
            yield return EquipHammer(player);
            BlueprintMode.Select(player, resolved);
            yield return Frames(2);
            Check(BlueprintMode.Current == resolved, $"'{kit.Name}' is in the hammer's hand");

            WorldPadOn();
            var shows = _inventoryShows;
            yield return PadPress(true, PadKey.West);
            yield return new WaitForSeconds(0.5f);
            var document = EditorSession.Document;
            Check(ModUi.Open && EditorWindow.Visible && !player.TakeInput(),
                "L2 + square opened the editor, and the game stopped taking input");
            Check(document != null && document.Name == kit.Name && document.Pieces.Count == kit.Pieces.Count && !BlueprintMode.Active,
                $"on the blueprint in hand, which the hammer let go of: '{(document != null ? document.Name : "nothing")}'");
            Check(_inventoryShows == shows && !player.IsSitting() && !player.InEmote(),
                $"nothing else: no inventory ({_inventoryShows - shows} tries), not sitting");
            yield return WorldPadOff();

            // Inside: the editor reads the pad itself, the same two buttons close it.
            _pad = new PadState();
            PadReader.Fake = _pad;
            yield return Frames(3);
            // A piece selected: square alone would pick it up to move it.
            var modifier = WorldPad.ModifierButton;
            if (document != null && document.Pieces.Count > 0)
            {
                EditorState.Select(document.Pieces[0].Id);
            }

            yield return Tap(modifier, PadButton.Square);
            yield return new WaitForSeconds(0.4f);
            Check(!ModUi.Open && !ModUi.Blocking && player.TakeInput() && EditorSession.Kept,
                $"{modifier} + square closed it again and kept the blueprint");
            Check(EditorState.Mode == EditMode.Idle && EditorState.SelectionCount == 1 && !player.IsSitting(),
                $"and did not pick the selected piece up to move it (mode {EditorState.Mode}), nor sit the player down");
            PadReader.Fake = null;
            _pad = null;
            EditorSession.Forget();
            BlueprintMode.Exit();
        }

        // ---------- scenario: editor_view ----------

        /// <summary>
        /// The 3D pane: a kit standing on the grid, the camera framing it, turning, zooming and
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

            // A quarter turn, then six wheel notches closer.
            var camera = ViewportHost.Camera;
            var before = camera.Position;
            var distance = camera.Distance;
            camera.Turn(90f, -8f);
            for (var notch = 0; notch < 6; notch++)
            {
                camera.Zoom(-100f, new Vector2(0.5f, 0.5f));
            }

            yield return null;
            yield return null;
            Check(camera.Distance < distance * 0.9f, $"the wheel came closer: {distance:0.0} m -> {camera.Distance:0.0} m");
            Check(Vector3.Distance(camera.Position, before) > 1f,
                $"turning and zooming moved the camera {Vector3.Distance(camera.Position, before):0.0} m");
            var turned = SampleView("turned");
            Check(Difference(framed, turned) > 0.05f,
                $"the view changed after turning ({Difference(framed, turned) * 100f:0} % of the pixels)");
            yield return Screenshot("editor-view-2-turned");

            // C hands the mouse to the pane: the cursor is held, the mouse turns the view.
            yield return PressKey(UnityEngine.InputSystem.Key.C);
            ViewportHost.Frame();
            yield return new WaitForSeconds(0.3f);
            Check(ViewportHost.Captured, "C gave the mouse to the pane");
            Check(Cursor.lockState == CursorLockMode.Locked, $"the cursor is held (is {Cursor.lockState})");

            // The game's own mouse look (PlayerController.LateUpdate): 0.05 degrees a pixel times
            // its Mouse sensitivity, and its invert setting.
            var sens = ZInput.IsGamepadMouseActive() ? PlayerController.m_switchMouseSens : PlayerController.m_mouseSens;
            var yaw = camera.Yaw;
            var pitch = camera.Pitch;
            camera.MouseLook(new Vector2(100f, 20f));
            var lookTurn = Mathf.DeltaAngle(yaw, camera.Yaw);
            var raised = camera.Pitch - pitch;
            Check(Mathf.Abs(lookTurn - 5f * sens) < 0.01f && Mathf.Abs(raised - sens) < 0.01f,
                $"100 by 20 mouse pixels turned the view {lookTurn:0.00} and up {raised:0.00} degrees,"
                + $" the game's {5f * sens:0.00} and {sens:0.00} (0.05 a pixel x its Mouse sensitivity {sens})");

            var wasSens = PlayerController.m_mouseSens;
            var wasInvert = PlayerController.m_invertMouse;
            var wasSwitch = PlayerController.m_switchMouseSens;
            PlayerController.m_mouseSens = wasSens * 2f;
            PlayerController.m_switchMouseSens = wasSwitch * 2f;
            PlayerController.m_invertMouse = true;
            yaw = camera.Yaw;
            pitch = camera.Pitch;
            camera.MouseLook(new Vector2(100f, 20f));
            var fast = Mathf.DeltaAngle(yaw, camera.Yaw);
            var lowered = camera.Pitch - pitch;
            PlayerController.m_mouseSens = wasSens;
            PlayerController.m_switchMouseSens = wasSwitch;
            PlayerController.m_invertMouse = wasInvert;
            Check(Mathf.Abs(fast - 2f * lookTurn) < 0.01f && Mathf.Abs(lowered + 2f * raised) < 0.01f,
                $"the game's settings count: sensitivity x2 turns {fast:0.00} degrees, invert mouse turns up into down ({lowered:0.00})");

            var flyFrom = camera.Position;
            yield return HoldKey(UnityEngine.InputSystem.Key.W, 0.8f);
            var flew = Vector3.Distance(camera.Position, flyFrom);
            Check(flew > 1f, $"W flew the camera forward {flew:0.0} m");
            yield return null;
            var flown = SampleView("flown");
            Check(Difference(turned, flown) > 0.05f,
                $"the view changed after flying ({Difference(turned, flown) * 100f:0} % of the pixels)");
            yield return Screenshot("editor-view-3-captured");

            // Esc gives the cursor back without closing; the second one closes.
            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.4f);
            Check(ModUi.Open, "Esc on the captured pane keeps the window open");
            Check(!ViewportHost.Captured, "Esc gave the pane back");
            Check(Cursor.lockState == CursorLockMode.None, $"Esc gave the cursor back (is {Cursor.lockState})");

            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open, "the second Esc closed the editor");
            Check(ViewportHost.Scene != null && ViewportHost.Scene.IsAlive
                    && !ViewportHost.Scene.Root.gameObject.activeSelf,
                "the pane kept its scene for the next open, switched off");
            Check(GameObject.Find("ValheimTomrer_EditorScene") == null, "so no search in the world finds it");
            Check(ViewportHost.Preview != null && !ViewportHost.Preview.TextureAlive, "and it let go of its texture");

            EditorSession.Forget();
            yield return new WaitForSeconds(0.5f);
            Check(ViewportHost.Scene == null && ViewportHost.Preview == null, "Forget let go of the scene and the camera");
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
                Check(!DocumentStore.Save(document, out var error),
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
                CheckEmptyIsSaved(temp);
            }
            finally
            {
                BlueprintLibrary.UserFolder = real;
                BlueprintLibrary.Reload();
            }
        }

        /// <summary>
        /// New, then Save as, before a single piece is placed: that is the first thing the editor
        /// does, so an empty blueprint has to write, read back and stay out of the build tool.
        /// </summary>
        private static void CheckEmptyIsSaved(string folder)
        {
            var blank = DocumentStore.New("Empty start");
            Check(DocumentStore.SaveAs(blank, "Empty start", false, out var error),
                $"a blueprint with no pieces is saved: {error}");

            var path = Path.Combine(folder, "empty-start.blueprint");
            Check(File.Exists(path) && blank.SourcePath == path && !blank.Dirty,
                "empty-start.blueprint is on disk and the document is clean");
            Check(DocumentStore.Open(path, out var reopened, out error)
                && reopened.Pieces.Count == 0 && reopened.Name == "Empty start",
                $"and it reads back empty, still named: {error}");
            Check(!BlueprintLibrary.All.Any(b =>
                    string.Equals(b.SourcePath, path, StringComparison.OrdinalIgnoreCase)),
                $"but the build tool's list leaves it out: {BlueprintLibrary.All.Count} blueprints");

            var rows = Checks.Run(reopened);
            Check(rows.Count == 1 && rows[0].Level == CheckLevel.Warning,
                $"and the problem list calls it a warning, not an error: {Row(rows, 0)}");

            Check(DocumentStore.Delete(path, out error), "delete the empty one: " + error);
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

            // The aim follows the mouse, which the test cannot point. Give it a moment, then take
            // the pane, where the aim is always the middle of the picture.
            var tries = 0f;
            while ((EditorState.Aimed == null || !ViewportHost.Ghost.Visible) && tries < 2f)
            {
                tries += Time.deltaTime;
                if (tries > 0.6f && !ViewportHost.Captured)
                {
                    ViewportHost.Capture();
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

        /// <summary>The rows the fixture cannot show: a line that cannot be read, empty, too big, and no card row for 12 items.</summary>
        private static void CheckOtherProblems(PieceEntry normal)
        {
            var empty = DocumentStore.New("Empty");
            var rows = Checks.Run(empty);
            Check(rows.Count == 1 && rows[0].Level == CheckLevel.Warning
                && rows[0].Message == "No pieces yet. The build tool skips an empty blueprint.",
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

            // Twelve items and a station: the card used to show only six squares and warned about
            // the rest. The list shows them all now, so there is no such row any more.
            var twelve = TwelveItemsDocument();
            rows = Checks.Run(twelve);
            var cardRows = rows.Where(c => c.Message.Contains("card") || c.Message.Contains("cost items")).ToList();
            Check(twelve.Pieces.Count == 4 && cardRows.Count == 0,
                $"a 12-item blueprint has no card-slots problem: {cardRows.Count} rows about the card"
                + (cardRows.Count > 0 ? $" ('{cardRows[0].Message}')" : ""));
        }

        /// <summary>The four pieces of <see cref="TwelveItems"/> as an editor document: 12 items, all from the workbench.</summary>
        private static BlueprintDocument TwelveItemsDocument()
        {
            var document = DocumentStore.New("Twelve items");
            foreach (var piece in TwelveItems().Pieces)
            {
                document.AddPiece(piece.PrefabName, piece.Position, piece.Rotation);
            }

            return document;
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
            Check(BlueprintPanel.CardText == $"A blueprint with problems\n{document.Pieces.Count} pieces."
                && NoControls(BlueprintPanel.CardText),
                $"the card text is the description and the count, no controls: '{BlueprintPanel.CardText.Replace("\n", " / ")}'");
            CheckEditorMaterials(document, "the problem blueprint");

            // A short list: the region grows to show all of the card, nothing to scroll.
            yield return new WaitForSeconds(0.2f);
            var view = BlueprintPanel.Scroll.viewport;
            Check(Contains(view, BlueprintPanel.CardPanel) && BlueprintPanel.Scroll.content.rect.height <= view.rect.height + 0.5f,
                $"the whole card is in sight without scrolling: region {EditorWindow.BlueprintBand:0} high, content "
                + $"{BlueprintPanel.Scroll.content.rect.height:0}, the problem list keeps {EditorWindow.ChecksPane.rect.height:0}");

            var refreshes = BlueprintPanel.MaterialRefreshes;
            BlueprintPanel.NameField.text = "Panel tes";
            BlueprintPanel.NameField.text = "Panel test 2";
            yield return null;
            yield return null;
            Check(BlueprintPanel.MaterialRefreshes > refreshes,
                $"a change to the blueprint works the list out again at once: {BlueprintPanel.MaterialRefreshes - refreshes} times");
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

            yield return PanelsMaterials();

            // Esc steps back one thing at a time, so the selection has to go before the window.
            EditorState.Select(Array.Empty<int>());
            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open, "Esc closed the editor");
        }

        /// <summary>
        /// The materials list in the editor's card, worked out here without the panel: one row per
        /// item the document's hammer pieces cost and one per station, have = what
        /// <see cref="MaterialSources.Around"/> counts where the player stands, a station in range of
        /// the player (not of the blueprint) or in the blueprint itself. No footer, own text material,
        /// and nothing in the list the panel walk could step into.
        /// </summary>
        private static void CheckEditorMaterials(BlueprintDocument document, string what)
        {
            var player = Player.m_localPlayer;
            var list = BlueprintPanel.List;
            if (list == null || player == null)
            {
                Check(false, $"{what}: the editor's card has a materials list");
                return;
            }

            var items = new HashSet<string>();
            var stations = new HashSet<string>();
            var own = new HashSet<string>();
            foreach (var piece in document.Pieces)
            {
                var entry = PieceCatalog.Find(piece.PrefabName);
                if (entry == null)
                {
                    continue;
                }

                foreach (var cost in entry.Cost.Where(c => c.Amount > 0))
                {
                    items.Add(cost.Token);
                }

                if (!string.IsNullOrEmpty(entry.StationToken))
                {
                    stations.Add(entry.StationToken);
                }

                if (!string.IsNullOrEmpty(entry.OwnStationToken))
                {
                    own.Add(entry.OwnStationToken);
                }
            }

            var rows = list.ShownRows.ToList();
            Check(rows.Count == items.Count + stations.Count && rows.Count(r => !r.IsStation) == items.Count,
                $"{what}: one row per item and station, {rows.Count} rows for {items.Count} items and {stations.Count} stations");

            var sources = MaterialSources.Around(player);
            var wrongHave = rows.Where(r => !r.IsStation && r.Have.text != MaterialList.Short(sources.Count(r.Key)))
                .Select(r => $"{r.Name.text} shows {r.Have.text}, the world has {sources.Count(r.Key)}").ToList();
            Check(wrongHave.Count == 0, $"{what}: every have is MaterialSources.Around(player).Count"
                + (wrongHave.Count == 0 ? $" ({string.Join(", ", rows.Where(r => !r.IsStation).Select(r => r.Name.text + " " + r.Have.text + r.Need.text))})"
                    : ": " + string.Join("; ", wrongHave)));
            CheckRowsMatch(rows, BlueprintCard.Materials(document, sources, false), what);

            var noStations = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench);
            var wrongState = new List<string>();
            foreach (var row in rows.Where(r => r.IsStation))
            {
                var want = noStations ? "not needed"
                    : own.Contains(row.Key) ? "in blueprint"
                    : CraftingStation.HaveBuildStationInRange(row.Key, player.transform.position) != null ? "in range"
                    : "not in range";
                if (row.State.text != want)
                {
                    wrongState.Add($"{row.Name.text} says '{row.State.text}', want '{want}'");
                }
            }

            Check(wrongState.Count == 0, $"{what}: a station is in range of the player or in the blueprint: "
                + (wrongState.Count == 0 ? string.Join(", ", rows.Where(r => r.IsStation).Select(r => r.Name.text + " " + r.State.text)) : string.Join("; ", wrongState)));
            Check(!list.Footer.gameObject.activeSelf && list.Columns == 1,
                $"{what}: no footer in the editor, one column (panel {list.Width:0} wide)");
            CheckLabels(list);

            var texts = BlueprintPanel.CardPanel.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(t => t.text).ToList();
            Check(texts.All(t => !t.Contains("Not shown") && !t.Contains("slots")),
                $"{what}: no \"Not shown\" or slots text in the card's {texts.Count} texts");
            var selectables = list.Root.GetComponentsInChildren<UnityEngine.UI.Selectable>(true).Length;
            Check(selectables == 0, $"{what}: the list holds nothing the panel walk could step into ({selectables} selectables)");
        }

        /// <summary>
        /// A 12-item blueprint in the open window: 12 item rows and the workbench, none cut, none
        /// overlapping, all inside the card; the region scrolls to show the last one. The list follows
        /// the bag within a second while the window is open, and reads the chests once a second at most.
        /// </summary>
        private static IEnumerator PanelsMaterials()
        {
            var player = Player.m_localPlayer;
            if (!ResolvedBlueprint.TryResolve(TwelveItems(), out var twelve, out var error))
            {
                Check(false, "the twelve-item blueprint resolves: " + error);
                yield break;
            }

            // A third in full, a third half, a third none: all three looks at once. Taken back at the end.
            var added = new List<KeyValuePair<string, int>>();
            for (var i = 0; i < twelve.TotalCost.Count; i++)
            {
                var cost = twelve.TotalCost[i];
                var amount = i % 3 == 0 ? cost.m_amount : i % 3 == 1 ? cost.m_amount / 2 : 0;
                var before = player.GetInventory().CountItems(cost.m_resItem.m_itemData.m_shared.m_name);
                AddTo(player.GetInventory(), cost.m_resItem.gameObject.name, amount);
                added.Add(new KeyValuePair<string, int>(
                    cost.m_resItem.m_itemData.m_shared.m_name,
                    player.GetInventory().CountItems(cost.m_resItem.m_itemData.m_shared.m_name) - before));
            }

            var document = TwelveItemsDocument();
            EditorState.Select(Array.Empty<int>());
            EditorSession.Replace(document);
            yield return null;
            yield return null;
            yield return null;
            Check(EditorSession.Document == document && twelve.TotalCost.Count == 12,
                $"the window shows the 12-item blueprint ({twelve.TotalCost.Count} items, {twelve.Stations.Count} station)");
            CheckEditorMaterials(document, "twelve items");

            var list = BlueprintPanel.List;
            var rows = list.ShownRows.ToList();
            Check(rows.Count(r => !r.IsStation) == 12 && rows.Count == 12 + twelve.Stations.Count,
                $"12 item rows and {rows.Count(r => r.IsStation)} station row");
            Check(!Checks.Run(document).Any(c => c.Message.Contains("card")), "and no card-slots problem in the problem list");

            var card = BlueprintPanel.CardPanel;
            var inside = rows.All(r => Contains(card, r.Rect));
            var overlaps = 0;
            for (var a = 0; a < rows.Count; a++)
            {
                for (var b = a + 1; b < rows.Count; b++)
                {
                    overlaps += ScreenRect(rows[a].Rect).Overlaps(ScreenRect(rows[b].Rect)) ? 1 : 0;
                }
            }

            var cut = rows.Where(r => r.Name.isTextTruncated || (!r.IsStation && (r.Have.isTextTruncated || r.Need.isTextTruncated))
                || (r.IsStation && r.State.isTextTruncated)).Select(r => r.Name.text).ToList();
            Check(inside && overlaps == 0 && cut.Count == 0,
                $"every row inside the card ({inside}), none overlapping ({overlaps}), none cut short ({(cut.Count == 0 ? "none" : string.Join(", ", cut))})");

            // Taller than the window allows: the region grows until the problem list keeps its least,
            // then scrolls, and at the bottom the last row is in sight.
            yield return new WaitForSeconds(0.2f);
            var scroll = BlueprintPanel.Scroll;
            var view = scroll.viewport;
            var checksLeft = EditorWindow.ChecksPane.rect.height;
            var grown = scroll.content.rect.height > view.rect.height + 0.5f
                ? checksLeft >= 159.5f && checksLeft <= 161f
                : Contains(view, card);
            Check(grown && view.rect.height > 360f,
                $"the region grew to {EditorWindow.BlueprintBand:0} (view {view.rect.height:0}, content {scroll.content.rect.height:0}, "
                + $"card {card.rect.height:0}, list {list.Height:0}); the problem list keeps {checksLeft:0}");
            scroll.verticalNormalizedPosition = 0f;
            yield return null;
            yield return null;
            var last = rows[rows.Count - 1];
            Check(Contains(view, last.Rect),
                $"scrolled to the bottom, the last row ('{last.Name.text} {last.State.text}') is in sight");
            // The items above teach the cut-down recipe list "new" recipes, and the game's popups for
            // them would cover the left panel in the picture. A test artefact: clear them.
            if (MessageHud.instance != null)
            {
                MessageHud.instance.ClearUnlockQueue();
                MessageHud.instance.HideAll();
            }

            yield return new WaitForSeconds(0.3f);
            yield return Screenshot("editor-panels-card");

            // The pad scrolls it too: the right stick, while the panel walk is in this panel.
            yield return PanelsPadScroll(scroll);

            // The bag changes with the window open: the list follows within a second.
            var first = rows.First(r => !r.IsStation);
            var firstCost = twelve.TotalCost.First(c => c.m_resItem.m_itemData.m_shared.m_name == first.Key);
            var wasText = first.Have.text;
            AddTo(player.GetInventory(), firstCost.m_resItem.gameObject.name, 1);
            added.Add(new KeyValuePair<string, int>(first.Key, 1));
            var refreshes = BlueprintPanel.MaterialRefreshes;
            var reads = BlueprintPanel.SourceReads;
            var waited = 0f;
            var want = MaterialList.Short(MaterialSources.Around(player).Count(first.Key));
            while (first.Have.text != want && waited < 3f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            Check(first.Have.text == want && waited <= BlueprintPanel.MaterialsPeriod + 0.2f,
                $"one more {first.Name.text} in the bag shows in {waited:0.00} s: '{wasText}' -> '{first.Have.text}'");

            // Held still for 2.5 s: two or three refreshes, never one a frame, and one read of the chests each.
            refreshes = BlueprintPanel.MaterialRefreshes;
            reads = BlueprintPanel.SourceReads;
            var frames = 0;
            waited = 0f;
            while (waited < 2.5f)
            {
                waited += Time.unscaledDeltaTime;
                frames++;
                yield return null;
            }

            var made = BlueprintPanel.MaterialRefreshes - refreshes;
            var read = BlueprintPanel.SourceReads - reads;
            Check(made >= 2 && made <= 3 && read <= made,
                $"with nothing changing, {made} refreshes in {frames} frames over 2.5 s, {read} reads of the chests");

            foreach (var item in added.Where(a => a.Value > 0))
            {
                player.GetInventory().RemoveItem(item.Key, item.Value);
            }

            scroll.verticalNormalizedPosition = 1f;
        }

        /// <summary>
        /// L3 walks into the panels, R1 on to the right one, and the right stick scrolls it: up to the
        /// top, down to the bottom. The ring hides while its widget is scrolled out of sight.
        /// </summary>
        private static IEnumerator PanelsPadScroll(UnityEngine.UI.ScrollRect scroll)
        {
            _pad = new PadState();
            PadReader.Fake = _pad;
            yield return Frames(3);
            EditorState.CancelMode();
            yield return Tap(PadButton.L3);
            yield return Tap(PadButton.R1);
            Check(FocusNav.Active && FocusNav.Current == FocusRegion.Right && FocusNav.ScrollTarget() == scroll,
                $"L3 and R1 put the walk in the right panel ({FocusNav.Current}), on '{WidgetName(FocusNav.Focused)}', "
                + $"and its list to scroll is the blueprint region: {FocusNav.ScrollTarget() == scroll}");

            // Stepping in scrolled the name box into sight; start from the bottom again.
            scroll.verticalNormalizedPosition = 0f;
            yield return Frames(2);
            var start = scroll.verticalNormalizedPosition;
            Check(RingMatchesSight(), $"at the bottom the ring shows only when its widget is in sight (shown: {FocusNav.Ring.gameObject.activeSelf})");
            _pad.Rs = new Vector2(0f, 1f);
            yield return Wait(1f);
            _pad.Rs = Vector2.zero;
            yield return Frames(2);
            var top = scroll.verticalNormalizedPosition;
            Check(start < 0.01f && top > 0.99f, $"right stick up scrolls the panel to the top: {start:0.00} -> {top:0.00}");
            Check(RingMatchesSight(), "the ring shows exactly when its widget is in sight");

            _pad.Rs = new Vector2(0f, -1f);
            yield return Wait(1f);
            _pad.Rs = Vector2.zero;
            yield return Frames(2);
            var bottom = scroll.verticalNormalizedPosition;
            Check(bottom < 0.01f, $"right stick down scrolls it back down: {top:0.00} -> {bottom:0.00}");
            Check(RingMatchesSight(), $"and the ring still shows exactly when its widget is in sight (shown: {FocusNav.Ring.gameObject.activeSelf})");

            yield return Tap(PadButton.Circle);
            PadReader.Fake = null;
            _pad = null;
            Check(!FocusNav.Active && ModUi.Open, "circle left the walk, the window stays");
        }

        /// <summary>The focus ring is up when its widget's middle is inside its list's view, and down when not.</summary>
        private static bool RingMatchesSight()
        {
            var widget = FocusNav.Focused;
            var ring = FocusNav.Ring;
            if (widget == null || ring == null)
            {
                return false;
            }

            var list = widget.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            var inSight = list == null || list.viewport == null
                || ScreenRect(list.viewport).Contains(ScreenRect((RectTransform)widget.transform).center);
            return ring.gameObject.activeSelf == inSight;
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

        /// <summary>C takes the pane, Esc gives it back, W A S D, Space, Ctrl, Shift and F.</summary>
        private static IEnumerator CameraKeys()
        {
            var camera = ViewportHost.Camera;

            Check(!ViewportHost.Captured, "the window opens with the mouse on the panels");
            yield return PressKey(UnityEngine.InputSystem.Key.C);
            var took = ViewportHost.Captured && ModUi.LockCursor;
            var gave = Bindings.Cancel();
            Check(took && gave && !ViewportHost.Captured && !ModUi.LockCursor,
                "C takes the pane and Esc gives it back");

            // A click selects. It must never take the mouse: that used to hide the cursor and
            // swing the view on the click that was meant to pick a piece.
            ViewportHost.ClickAt(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), false);
            yield return null;
            Check(!ViewportHost.Captured && !ModUi.LockCursor,
                "a click on the pane leaves the cursor alone");

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
            ViewportHost.Capture();
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
            ViewportHost.Release();
            yield return null;

            // Save always reads "Save": the problem list counts the errors, not the button.
            // A piece this game does not have is one error.
            document.AddPiece("valheimtomrer_no_such_piece", Vector3.zero, Quaternion.identity);
            ChecksPanel.Refresh();
            TopBar.Tick();
            Check(TopBar.SaveText == "Save",
                $"with an error the Save button still reads '{TopBar.SaveText}'");
            EditorState.Undo();
            ChecksPanel.Refresh();
            TopBar.Tick();

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
                && Dialogs.RowText(mine).Contains(" pieces")
                && Dialogs.RowText(mine).Contains(DateTime.Now.Year.ToString())),
                $"a row carries path, date and pieces: '{(mine >= 0 ? Dialogs.RowText(mine) : "none")}'");
            Dialogs.Close();

            // ---- save as ----
            // A key down first, so the keyboard is what was used last.
            yield return PressKey(UnityEngine.InputSystem.Key.LeftShift);
            Dialogs.SaveAs("Keys test");
            yield return null;
            Check(Dialogs.Kind == "saveAs" && Dialogs.NameField != null, "Save as opened with a name box");
            yield return SaveAsDeleteKeys();
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

            yield return DeleteKeys();

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

        /// <summary>
        /// Deleting one of the player's blueprints with the mouse and the keys: a real click on
        /// its Delete button asks first, Esc goes back to the list, the right arrow and Enter
        /// answer. Kits have no Delete button. The file is the test's own, in the test's folder.
        /// </summary>
        private static IEnumerator DeleteKeys()
        {
            EditorCommands.OpenDialog();
            yield return null;
            yield return null;
            var own = -1;
            var kitsWithout = true;
            for (var i = 0; i < Dialogs.RowCount; i++)
            {
                if (Dialogs.Row(i).IsKit)
                {
                    kitsWithout &= Dialogs.DeleteButton(i) == null;
                }
                else if (own < 0)
                {
                    own = i;
                }
            }

            var path = own >= 0 ? Dialogs.Row(own).Path : null;
            Check(Dialogs.TitleText == "Blueprints" && own >= 0 && Dialogs.DeleteButton(own) != null && kitsWithout,
                $"the list is called '{Dialogs.TitleText}', your own row {own} has a Delete button, the kits have none");
            if (own < 0)
            {
                Dialogs.Close();
                yield break;
            }

            yield return ClickScreen(ScreenBox((RectTransform)Dialogs.DeleteButton(own).transform).center);
            Check(Dialogs.Kind == "confirm" && Dialogs.TitleText == "Delete the blueprint?"
                && WidgetName(FocusNav.Focused) == "Left",
                $"a mouse click on Delete asks first ('{Dialogs.TitleText}'), on '{WidgetName(FocusNav.Focused)}'");
            Check(Bindings.Cancel() && Dialogs.Kind == "open" && File.Exists(path)
                && Dialogs.Row(own) != null && Dialogs.Row(own).Path == path,
                "Esc goes back to the list, and the file is still there");

            yield return null;
            yield return ClickScreen(ScreenBox((RectTransform)Dialogs.DeleteButton(own).transform).center);
            var asked = Dialogs.Kind == "confirm";
            Bindings.Press(KeyCode.RightArrow, KeyMods.None);
            var on = WidgetName(FocusNav.Focused);
            Bindings.Press(KeyCode.Return, KeyMods.None);
            yield return null;
            var gone = true;
            for (var i = 0; i < Dialogs.RowCount; i++)
            {
                gone &= Dialogs.Row(i).Path != path;
            }

            Check(asked && on == "Right" && !File.Exists(path) && Dialogs.Kind == "open" && gone,
                $"asked again, the right arrow went to '{on}', Enter deleted {Path.GetFileName(path)} "
                + $"and the list came back without it: {Dialogs.RowCount} rows");
            Check(Toasts.Count > 0 && Toasts.TextOf(0).StartsWith("Deleted"),
                $"a message says so: '{(Toasts.Count > 0 ? Toasts.TextOf(0) : "none")}'");
            Dialogs.Close();
        }

        /// <summary>
        /// Save as with the keyboard: it opens typing, and a real mouse click on the name box
        /// followed by Delete and Backspace saves nothing and closes nothing. "Keys test" is taken,
        /// so a save would ask "Replace the file?" and the dialog would read "confirm". The keys go
        /// through the input system, so the editor and the game's UI see them. The box itself
        /// reads its keys another way the test cannot reach, which is why its text stays.
        /// </summary>
        private static IEnumerator SaveAsDeleteKeys()
        {
            var waited = 0f;
            while (!ModUi.Typing && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(!EditorInput.PadInUse && ModUi.Typing,
                $"with the keyboard in use, Save as opens typing in the name box after {waited:0.00} s");

            var box = Dialogs.NameField;
            var file = Path.Combine(BlueprintLibrary.UserFolder, BlueprintFormat.FileNameFor("Keys test"));
            var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            yield return ClickScreen(ScreenBox((RectTransform)box.transform).center);
            yield return PressKey(UnityEngine.InputSystem.Key.Delete);
            yield return PressKey(UnityEngine.InputSystem.Key.Backspace);
            yield return Frames(2);
            var same = File.Exists(file) && File.GetLastWriteTimeUtc(file) == stamp;
            Check(Dialogs.Kind == "saveAs" && Dialogs.NameField == box && same && ModUi.Typing,
                $"a click on the name box, then Delete and Backspace: still '{Dialogs.Kind}', typing={ModUi.Typing}, "
                + $"the file untouched={same}");

            // Enter in the box on a name another file has asks "Replace the file?". The box reads
            // Enter before the editor's tick, as here: it submits and lets go, then the editor sees
            // the same Enter. (The open blueprint is keys-test.blueprint itself, which saves unasked.)
            var other = BlueprintDocument.New("Taken name");
            Check(DocumentStore.SaveAs(other, "Taken name", true, out var takenError), $"a second file: {takenError ?? "ok"}");
            var taken = other.SourcePath;
            var takenStamp = File.GetLastWriteTimeUtc(taken);
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(
                keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.Enter));
            box.text = "Taken name";
            box.onSubmit.Invoke(box.text);
            box.DeactivateInputField();
            yield return Frames(3);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return Frames(2);
            var untouched = File.GetLastWriteTimeUtc(taken) == takenStamp;
            Check(Dialogs.Kind == "confirm" && Dialogs.TitleText == "Replace the file?" && untouched,
                $"Enter in the box on a taken name asks first ('{Dialogs.TitleText}'), and the same Enter "
                + $"does not answer it: {Path.GetFileName(taken)} untouched={untouched}");
            File.Delete(taken);

            Dialogs.Close();
            Dialogs.SaveAs("Keys test");
            yield return null;
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
                _pad = new PadState();
                PadReader.Fake = _pad;
                yield return null;
                yield return null;

                yield return PadWakes();
                yield return PadDialogs(document);
                yield return PadMenu();
                yield return PadCamera();
                yield return PadLook();
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
            Check(rows.Length == 22, $"the help table has the whole controller half: {rows.Length} rows");
            Check(Array.Exists(rows, r => r.Keys == "×") && Array.Exists(rows, r => r.Keys == "L2 + R2")
                && Array.Exists(rows, r => r.Keys == $"{ps.Of(WorldPad.ModifierButton)} + □" && r.What.StartsWith("Close the editor")),
                "and the rows are written in those names, the close row too");

            _pad.Ps = false;
            yield return null;
            yield return null;
            var xbox = EditorInput.Glyphs;
            Check(xbox.Of(PadButton.Cross) == "A" && xbox.Of(PadButton.L1) == "LB" && xbox.Of(PadButton.Options) == "Menu",
                $"an Xbox pad switches them to {xbox.Of(PadButton.Cross)} {xbox.Of(PadButton.Circle)} "
                + $"{xbox.Of(PadButton.Square)} {xbox.Of(PadButton.Triangle)}");
            Check(Array.Exists(Bindings.Pad, r => r.Keys == "A") && Array.Exists(Bindings.Pad, r => r.Keys == "LT + RT"),
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

            var yaw = camera.Yaw;
            var from = Time.unscaledTime;
            _pad.Rs = new Vector2(1f, 0f);
            yield return Wait(0.3f);
            _pad.Rs = Vector2.zero;
            var speed = Mathf.Abs(Mathf.DeltaAngle(yaw, camera.Yaw)) / Mathf.Max(Time.unscaledTime - from, 1e-3f);
            yield return null;
            var want = 110f * PlayerController.m_gamepadSens;
            Check(speed > want * 0.8f && speed < want * 1.2f,
                $"the right stick turns the camera at the game's {want:0} degrees a second: {speed:0}");

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

        /// <summary>
        /// The right stick looks around the way the game's does (PlayerController.LateUpdate): the
        /// same stick numbers as <c>ZInput.GetJoyRightStick</c>, 110 degrees a second times the
        /// game's own Gamepad sensitivity, the same step every frame, and the game's invert
        /// settings. Driven through the made-up DualSense, so the reader's real device path runs.
        /// </summary>
        private static IEnumerator PadLook()
        {
            var camera = ViewportHost.Camera;
            var settings = UnityEngine.InputSystem.InputSystem.settings;
            var game = GameCamera.instance != null ? GameCamera.instance.m_camera : null;
            Log($"pad look: input update {settings.updateMode}, Unity's stick dead zone {settings.defaultDeadzoneMin}"
                + $" to {settings.defaultDeadzoneMax}, game Gamepad sensitivity {PlayerController.m_gamepadSens}, invert"
                + $" x {PlayerController.m_invertCameraX} y {PlayerController.m_invertCameraY}, field of view: game"
                + $" {(game != null ? game.fieldOfView : 0f):0}, pane {camera.FieldOfView:0}");

            PadReader.Fake = null;
            WorldPadOn();

            // The dead zone and the ramp: every stick position reads what the game reads.
            var worst = 0f;
            var table = new List<string>();
            foreach (var raw in new[]
            {
                new Vector2(0.1f, 0f), new Vector2(0.22f, 0f), new Vector2(0.3f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.7f, 0f), new Vector2(0.9f, 0f), new Vector2(1f, 0f), new Vector2(0.4f, 0.4f),
                new Vector2(-0.35f, 0.6f),
            })
            {
                PadSticks(raw, -raw);
                yield return Frames(2);
                var mine = EditorInput.Pad != null ? EditorInput.Pad.Rs : new Vector2(9f, 9f);
                var theirs = ZInput.GetJoyRightStick();
                var left = EditorInput.Pad != null ? EditorInput.Pad.Ls : new Vector2(9f, 9f);
                worst = Mathf.Max(worst, Mathf.Max(Vector2.Distance(mine, theirs), Vector2.Distance(left, ZInput.GetJoyLeftStick())));
                table.Add($"{raw.x:0.00},{raw.y:0.00} -> {mine.x:0.000},{mine.y:0.000} (game {theirs.x:0.000},{theirs.y:0.000})");
            }

            Log("pad look: stick table " + string.Join("; ", table));
            Check(worst < 0.002f, $"both sticks read what the game reads at every position: off by at most {worst:0.0000}");

            // The speed, and the same step every frame.
            PadSticks(new Vector2(0.6f, 0f), Vector2.zero);
            yield return Frames(3);
            var stick = ZInput.GetJoyRightStick().x;
            var want = 110f * PlayerController.m_gamepadSens * stick;
            var rates = new List<float>();
            var times = new List<float>();
            var yaw = camera.Yaw;
            var until = Time.unscaledTime + 0.6f;
            while (Time.unscaledTime < until)
            {
                yield return null;
                rates.Add(Mathf.DeltaAngle(yaw, camera.Yaw) / Mathf.Max(EditorInput.Dt, 1e-5f));
                times.Add(Time.unscaledDeltaTime * 1000f);
                yaw = camera.Yaw;
            }

            PadSticks(Vector2.zero, Vector2.zero);
            yield return Frames(2);
            rates.Sort();
            times.Sort();
            var mean = 0f;
            foreach (var rate in rates)
            {
                mean += rate / rates.Count;
            }

            Log($"pad look: {rates.Count} frames, {times[0]:0.0} / {times[times.Count / 2]:0.0} / {times[times.Count - 1]:0.0} ms"
                + $" (fastest / middle / slowest), degrees a second per frame {rates[0]:0.0} to {rates[rates.Count - 1]:0.0}");
            Check(Mathf.Abs(mean - want) < want * 0.02f,
                $"the stick at {stick:0.00} turns {mean:0.0} degrees a second, the game's {want:0.0} (110 x {stick:0.00} x {PlayerController.m_gamepadSens})");
            Check(rates[rates.Count - 1] - rates[0] < want * 0.01f,
                $"the same turn every frame, no smoothing or jumps: {rates[0]:0.0} to {rates[rates.Count - 1]:0.0}");

            // The game's own settings: twice the sensitivity turns twice as fast, invert Y looks down.
            var wasSens = PlayerController.m_gamepadSens;
            var wasInvertY = PlayerController.m_invertCameraY;
            PlayerController.m_gamepadSens = wasSens * 2f;
            PlayerController.m_invertCameraY = true;
            PadSticks(new Vector2(0.6f, 0.6f), Vector2.zero);
            yield return Frames(4);
            var step = 0f;
            var turned = 0f;
            var looked = 0f;
            for (var frame = 0; frame < 10; frame++)
            {
                var before = camera.Yaw;
                var beforePitch = camera.Pitch;
                yield return null;
                step += EditorInput.Dt;
                turned += Mathf.DeltaAngle(before, camera.Yaw);
                looked += camera.Pitch - beforePitch;
            }

            PadSticks(Vector2.zero, Vector2.zero);
            PlayerController.m_gamepadSens = wasSens;
            PlayerController.m_invertCameraY = wasInvertY;
            yield return Frames(2);
            var diagonal = ZInputStick(new Vector2(0.6f, 0.6f));
            var fast = 110f * wasSens * 2f * diagonal.x;
            Check(Mathf.Abs(turned / step - fast) < fast * 0.02f && looked < 0f,
                $"the game's settings count: sensitivity x2 turns {turned / step:0.0} degrees a second (want {fast:0.0}),"
                + $" invert Y turns the stick's up into looking down ({looked:0.0} degrees)");

            yield return WorldPadOff();
            PadReader.Fake = _pad;
            yield return Frames(2);
        }

        /// <summary>Both sticks of the made-up DualSense, raw, nothing pressed.</summary>
        private static void PadSticks(Vector2 right, Vector2 left)
        {
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(
                _worldPad, new UnityEngine.InputSystem.LowLevel.GamepadState { rightStick = right, leftStick = left });
        }

        /// <summary>ZInput's own dead zone on a raw stick, the numbers the game turns by.</summary>
        private static Vector2 ZInputStick(Vector2 raw)
        {
            return ZInput.m_instance.ApplyDeadzoneVector(raw);
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

            // Square and triangle work off the crosshair on their own: nothing selected first,
            // no second button held. They used to need R2 first, and copy needed L2 + R1.
            EditorState.Select(Array.Empty<int>());
            yield return Tap(PadButton.Square);
            var moving = EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Move
                && EditorState.Moving != null && EditorState.Moving.Count == 1;
            yield return Tap(PadButton.Cross);
            Check(moving && !PiecePicker.IsOpen,
                "square moves the aimed piece with nothing selected, and cross is silent while it is in hand");
            EditorState.CancelMode();

            // The piece was hidden while it was in hand, so the crosshair was on what stands
            // behind it. The pad reads the aim of the frame before: wait until it is back.
            EditorState.Select(Array.Empty<int>());
            yield return AimBackOn(aimed.Id);
            yield return Tap(PadButton.Triangle);
            Check(EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Duplicate
                && EditorState.Moving != null && EditorState.Moving.Count == 1,
                "triangle copies the aimed piece with nothing selected");
            yield return Tap(PadButton.Circle);
            Check(EditorState.Mode == EditMode.Idle, "circle stops placing");
            yield return Tap(PadButton.Circle);
            Check(EditorState.SelectionCount == 0, "and the next one clears the selection");

            var count = document.Pieces.Count;
            yield return AimBackOn(aimed.Id);
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

            // Undo rebuilt the deleted piece's copy, so the aim needs a frame to find it again.
            yield return AimBackOn(aimed.Id);
            EditorState.Select(ids);
            count = document.Pieces.Count;
            yield return Tap(PadButton.R1);
            var both = document.Pieces.Count;
            EditorState.Undo();
            yield return null;
            yield return null;
            Check(both == count - 2,
                $"and the whole selection when the aimed piece is in it: {count} -> {both}");

            // And they take the whole selection when the aimed piece is part of it.
            yield return AimBackOn(aimed.Id);
            EditorState.Select(ids);
            yield return Tap(PadButton.Triangle);
            Check(EditorState.Mode == EditMode.Place && EditorState.Action == PlaceAction.Duplicate
                && EditorState.Moving != null && EditorState.Moving.Count == ids.Count,
                $"triangle copies the whole selection when the aimed piece is in it: {ids.Count} pieces");
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

        /// <summary>
        /// Waits until the crosshair is on the piece again, at most a second. The pad acts on the
        /// aim of the frame before its press, so after the piece was hidden (in hand) or rebuilt
        /// (undo) a press straight away would see what stood behind it.
        /// </summary>
        private static IEnumerator AimBackOn(int id)
        {
            var waited = 0f;
            while (ViewportHost.AimPiece != id && waited < 1f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return null;
        }

        /// <summary>A real mouse move over the pane takes the aim back from the crosshair.</summary>
        private static IEnumerator PadMouse()
        {
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


        // ---------- scenario: editor_focus ----------

        /// <summary>How many presses left one of our widgets selected in the EventSystem.</summary>
        private static int _focusSelected;

        /// <summary>
        /// The panel walk: L3 opens it, the D-pad and L1/R1 move it, cross presses, circle gives
        /// the pane back. Driven through the made-up pad, against the counts the focus probe
        /// measured. It opens the same blueprint as that probe, one piece and one undo step, so
        /// the numbers line up.
        ///
        /// The one thing it cannot prove: a real controller's cross also reaches whatever the
        /// EventSystem has selected. The fake pad is invisible to the game's input module, so the
        /// double press has to be checked by hand. What is checked here is the rule that prevents
        /// it: nothing of ours is ever selected while the ring is on a button.
        /// </summary>
        private static IEnumerator TestEditorFocus(Player player)
        {
            yield return new WaitForSeconds(1f);
            _focusSelected = 0;

            PieceCatalog.Ensure();
            var wall = PieceCatalog.Find("woodwall") ?? (PieceCatalog.All.Count > 0 ? PieceCatalog.All[0] : null);
            Check(PieceCatalog.Ready && wall != null, "the catalog is built and has a piece for the blueprint");
            if (wall == null)
            {
                yield break;
            }

            var document = BlueprintDocument.New("focus test");
            document.AddPiece(wall.PrefabName, Vector3.zero, Quaternion.identity);
            EditorSession.OpenDocument(document);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open && ViewportHost.Ready,
                $"the window is open on a blueprint of {document.Pieces.Count} piece, undo={document.CanUndo}");

            _pad = new PadState();
            PadReader.Fake = _pad;
            yield return null;
            yield return null;

            yield return FocusEnter();
            yield return FocusRegions(document);
            yield return FocusPress(document);
            yield return FocusDialog();
            yield return FocusDelete(wall);
            yield return FocusLeave();

            Check(_focusSelected == 0,
                $"the EventSystem never held one of our buttons: {_focusSelected} presses left one selected");

            PadReader.Fake = null;
            _pad = null;
            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            Check(!ModUi.Open && !FocusNav.Active, "the editor closed and the walk went with it");
        }

        /// <summary>L3 opens the walk on the top bar, and a pad wake does not kill it.</summary>
        private static IEnumerator FocusEnter()
        {
            Check(!FocusNav.Active, "the walk is off while the 3D pane has the focus");
            yield return FocusTap(PadButton.L3);
            Check(FocusNav.Active && FocusNav.Current == FocusRegion.TopBar,
                $"L3 with an empty hand opens the walk on the {FocusNav.Current} region, "
                + $"widget '{WidgetName(FocusNav.Focused)}'");

            var live = Interactable(EditorWindow.TopBar);
            Check(FocusNav.Count == live && FocusNav.Count == 10,
                $"the top bar's walk holds every button that can be pressed and no more: "
                + $"{FocusNav.Count} of {live} interactable (the probe measured 10, Redo is off)");

            // The L3 press itself woke the pad, so start from the mouse having the aim again.
            ViewportHost.GiveAimBack();
            yield return null;
            ViewportHost.TakeAim();
            yield return null;
            Check(FocusNav.Active && !ViewportHost.PadAim,
                "a pad wake does nothing at all while the walk is on: it still holds "
                + $"'{WidgetName(FocusNav.Focused)}'");

            var gap = RingGap();
            Check(gap < 4f, $"the ring sits on the focused widget: {gap:0.0} px between the two centres");

            // The top bar is one row: right walks it in screen order and stops at the end.
            var steps = new List<int>();
            var widgets = FocusNav.Count;
            var ordered = true;
            for (var i = 0; i < widgets; i++)
            {
                var x = Middle((RectTransform)FocusNav.Focused.transform).x;
                yield return FocusTap(PadButton.Right);
                steps.Add(FocusNav.Index);
                ordered &= i == widgets - 1 || Middle((RectTransform)FocusNav.Focused.transform).x > x;
            }

            Check(steps.Count > 1 && steps[0] == 1 && steps[steps.Count - 2] == widgets - 1
                && FocusNav.Index == widgets - 1 && FocusNav.Current == FocusRegion.TopBar && ordered,
                $"D-pad right walks the bar one button at a time, left to right, and stops at the end: "
                + $"0 -> {steps[0]} -> ... -> {steps[steps.Count - 2]} -> {FocusNav.Index} of {widgets}");

            var moved = RingGap();
            Check(moved < 4f, $"and the ring follows it: {moved:0.0} px on '{WidgetName(FocusNav.Focused)}'");

            // The left stick goes sideways too, and a slanted push is one step on its bigger half.
            yield return FocusStick(new Vector2(-1f, 0f));
            var back = FocusNav.Index;
            yield return FocusStick(new Vector2(1f, 0f));
            var on = FocusNav.Index;
            yield return FocusStick(new Vector2(-0.8f, 0.6f));
            Check(back == widgets - 2 && on == widgets - 1 && FocusNav.Index == widgets - 2
                && FocusNav.Current == FocusRegion.TopBar,
                $"the left stick walks the bar: left -> {back}, right -> {on}, slanted up-left -> "
                + $"{FocusNav.Index} on the {FocusNav.Current}");

            yield return FocusTap(PadButton.Up);
            Check(FocusNav.Index == widgets - 2 && FocusNav.Current == FocusRegion.TopBar,
                "up on the top bar does nothing, there is nothing above it");

            // The bar's last buttons sit over the right panel: down goes into it, up comes back
            // to the same button.
            var top = FocusNav.Focused;
            yield return FocusTap(PadButton.Down);
            var under = FocusNav.Current;
            var landed = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Up);
            Check(under == FocusRegion.Right && FocusNav.Current == FocusRegion.TopBar && FocusNav.Focused == top,
                $"down from '{WidgetName(top)}' goes into the {under} panel, on '{landed}', and up comes "
                + $"back to '{WidgetName(FocusNav.Focused)}'");

            for (var i = 0; i < widgets; i++)
            {
                yield return FocusTap(PadButton.Left);
            }

            Check(FocusNav.Index == 0 && FocusNav.Current == FocusRegion.TopBar,
                $"left walks back to the first button and stops there: {FocusNav.Index}");
        }

        /// <summary>L1 and R1 walk the three regions, and the left panel's list follows its tab.</summary>
        private static IEnumerator FocusRegions(BlueprintDocument document)
        {
            // The regions sit left, top, right on screen. R1 goes on to the right and wraps,
            // L1 goes back the same way.
            yield return FocusTap(PadButton.R1);
            var first = FocusNav.Current;
            yield return FocusTap(PadButton.R1);
            var second = FocusNav.Current;
            yield return FocusTap(PadButton.R1);
            Check(first == FocusRegion.Right && second == FocusRegion.Left
                && FocusNav.Current == FocusRegion.TopBar,
                $"R1 walks left to right and wraps: TopBar -> {first} -> {second} -> {FocusNav.Current}");

            yield return FocusTap(PadButton.L1);
            Check(FocusNav.Current == FocusRegion.Left,
                $"L1 walks them the other way: TopBar -> {FocusNav.Current}");

            // L1 again wraps round to the right panel: the name, the description and the two icons.
            yield return FocusTap(PadButton.L1);
            var rightCount = FocusNav.Count;
            Check(FocusNav.Current == FocusRegion.Right
                && rightCount == Interactable(EditorWindow.RightPanel) && rightCount == 4,
                $"the right panel's walk with nothing selected: {rightCount} widgets (the probe measured 4)");

            // R1 wraps on to the left panel.
            yield return FocusTap(PadButton.R1);
            EditorWindow.SetLeftTab(0);
            yield return null;
            yield return null;
            var tab0 = FocusNav.Count;
            var tabs = FocusNav.Count >= 3
                && WidgetName(FocusNav.Widgets[0]) == "Pieces"
                && WidgetName(FocusNav.Widgets[1]) == "In blueprint"
                && FocusNav.Widgets[2] is TMPro.TMP_InputField;
            Check(tab0 == Interactable(EditorWindow.LeftPanel) && tabs && tab0 >= 20,
                $"the left panel's walk is the two tabs, the search box and the chips: {tab0} widgets "
                + $"({Palette.TagChipCount} tag chips, the probe measured 22)");

            // The two tabs are one row, the search box sits under them, the chips flow in rows
            // under that. The pad moves by what is on screen.
            var pieces = FocusNav.Focused;
            yield return FocusTap(PadButton.Right);
            var blueprintTab = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Right);
            var across = FocusNav.Current;
            yield return FocusTap(PadButton.Left);
            var backTab = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Left);
            Check(blueprintTab == "In blueprint" && across == FocusRegion.Right && backTab == "In blueprint"
                && FocusNav.Focused == pieces,
                $"right goes Pieces -> '{blueprintTab}' -> the {across} panel across the 3D pane, left comes "
                + $"back -> '{backTab}' -> '{WidgetName(FocusNav.Focused)}'");

            // Up from the tabs goes into the top bar, down comes back to the same tab.
            yield return FocusTap(PadButton.Up);
            var over = FocusNav.Current;
            var overName = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Down);
            Check(over == FocusRegion.TopBar && FocusNav.Current == FocusRegion.Left && FocusNav.Focused == pieces,
                $"up from Pieces goes into the {over}, on '{overName}', and down comes back to "
                + $"'{WidgetName(FocusNav.Focused)}'");

            yield return FocusTap(PadButton.Down);
            var search = FocusNav.Focused is TMPro.TMP_InputField;
            yield return FocusTap(PadButton.Down);
            var firstChip = FocusNav.Focused;
            Check(search && WidgetName(firstChip) == "Chip All",
                $"down goes to the search box, then to the first chip: '{WidgetName(firstChip)}'");

            yield return FocusTap(PadButton.Right);
            var secondChip = FocusNav.Focused;
            var along = secondChip != firstChip && Mathf.Abs(Mid(secondChip).y - Mid(firstChip).y) < 1f
                && Mid(secondChip).x > Mid(firstChip).x;
            yield return FocusTap(PadButton.Down);
            var below = FocusNav.Focused;
            var lower = below != null && WidgetName(below).StartsWith("Chip ")
                && Mid(below).y < Mid(secondChip).y - 10f;
            yield return FocusTap(PadButton.Up);
            var returned = FocusNav.Focused == secondChip;
            Check(along && lower && returned,
                $"right goes along the chip row to '{WidgetName(secondChip)}', down to the row under it "
                + $"('{WidgetName(below)}'), up back to '{WidgetName(FocusNav.Focused)}'");

            yield return FocusStick(new Vector2(0f, -1f));
            var stickDown = FocusNav.Focused;
            yield return FocusStick(new Vector2(0f, 1f));
            Check(stickDown == below && FocusNav.Focused == secondChip,
                $"the left stick does the same: down to '{WidgetName(stickDown)}', up to "
                + $"'{WidgetName(FocusNav.Focused)}'");

            yield return FocusTap(PadButton.Down);
            Check(RingGap() < 4f, $"the ring follows it into the chips, on '{WidgetName(FocusNav.Focused)}'");
            yield return Screenshot("editor-focus-1-ring");

            EditorWindow.SetLeftTab(1);
            yield return null;
            yield return null;
            var tab1 = FocusNav.Count;
            var hidden = 0;
            foreach (var widget in FocusNav.Widgets)
            {
                if (EditorWindow.PalettePane != null && widget.transform.IsChildOf(EditorWindow.PalettePane))
                {
                    hidden++;
                }
            }

            Check(tab1 == 2 && tab1 != tab0 && hidden == 0,
                $"the In blueprint tab changes the walk: {tab0} -> {tab1} widgets, {hidden} of them "
                + "belong to the hidden pane");

            EditorWindow.SetLeftTab(0);
            yield return null;
            yield return null;
        }

        /// <summary>Cross on a button fires it, cross on a text box starts typing.</summary>
        private static IEnumerator FocusPress(BlueprintDocument document)
        {
            yield return FocusTap(PadButton.R1);
            Check(FocusNav.Current == FocusRegion.TopBar,
                $"R1 from the left panel is the {FocusNav.Current}");

            var steps = 0;
            while (WidgetName(FocusNav.Focused) != "Undo" && steps < 20)
            {
                yield return FocusTap(PadButton.Right);
                steps++;
            }

            Check(WidgetName(FocusNav.Focused) == "Undo", $"the walk reaches the Undo button in {steps} steps");
            var before = document.Pieces.Count;
            yield return FocusTap(PadButton.Cross);
            Check(document.CanRedo && document.Pieces.Count == before - 1,
                $"cross on Undo really undoes: {before} -> {document.Pieces.Count} pieces, redo is now on");
            EditorState.Redo();
            yield return null;
            yield return null;

            // The right panel's first widget is the blueprint's name box.
            yield return FocusTap(PadButton.R1);
            var field = FocusNav.Focused as TMPro.TMP_InputField;
            Check(FocusNav.Current == FocusRegion.Right && field != null,
                $"R1 lands on the right panel's first widget, the '{WidgetName(FocusNav.Focused)}' box");
            if (field == null)
            {
                yield break;
            }

            yield return FocusTap(PadButton.Cross);
            var waited = 0f;
            while (!ModUi.Typing && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(ModUi.Typing, $"cross on the box starts typing after {waited:0.00} s");
            var at = FocusNav.Index;
            var text = field.text;
            yield return FocusTap(PadButton.R1);
            Check(FocusNav.Index == at && ModUi.Typing && field.text == text,
                $"and R1 does nothing while it types: still widget {FocusNav.Index} of {FocusNav.Count}");

            // The pad has no keys to type with: the D-pad walks on out of the box.
            yield return FocusTap(PadButton.Down);
            var below = FocusNav.Focused;
            Check(!ModUi.Typing && below != field && FocusNav.Current == FocusRegion.Right && !ModUi.HasSelection,
                $"the D-pad leaves the box and walks on down, to '{WidgetName(below)}', nothing selected in the UI");
            yield return FocusTap(PadButton.Up);
            Check(FocusNav.Focused == field && !ModUi.Typing, "up comes back to the box, not typing");

            // Circle, straight on a box that types: it lets go, the walk keeps it.
            yield return FocusTap(PadButton.Cross);
            waited = 0f;
            while (!ModUi.Typing && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return FocusTap(PadButton.Circle);
            yield return null;
            Check(ModUi.Typing == false && FocusNav.Active && FocusNav.Focused == field && field.text == text,
                $"circle stops the typing and the walk stays on the box: '{WidgetName(FocusNav.Focused)}'");

            // Esc's job, straight on the box: it stops typing but the walk keeps it.
            yield return FocusTap(PadButton.Cross);
            waited = 0f;
            while (!ModUi.Typing && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            field.DeactivateInputField();
            yield return null;
            yield return null;
            Check(FocusNav.Active && FocusNav.Focused == field && !ModUi.Typing && !ModUi.HasSelection,
                "the box lets go of the keyboard, the walk keeps it, and nothing is selected in the UI");
        }

        /// <summary>
        /// A dialog takes the walk on its own while it is up: the D-pad moves through what it
        /// holds, L1 and R1 cannot walk out of it, and closing it puts the walk back where it was.
        /// </summary>
        private static IEnumerator FocusDialog()
        {
            var before = FocusNav.Current;
            EditorCommands.OpenDialog();
            yield return null;
            yield return null;

            Check(Dialogs.IsOpen && FocusNav.InDialog,
                $"Open takes the walk into the dialog: region {FocusNav.Current}, "
                + $"widget '{WidgetName(FocusNav.Focused)}'");
            var live = Interactable(Dialogs.Modal);
            Check(FocusNav.Count == live && live > 1,
                $"the walk holds everything the dialog can press: {FocusNav.Count} of {live}");
            Check(FocusNav.Focused == Dialogs.FocusStart && Dialogs.RowCount > 0,
                $"and it starts on the first of {Dialogs.RowCount} blueprints");
            Check(RingGap() < 4f, $"the ring is on it, over the dialog: {RingGap():0.0} px");

            var first = FocusNav.Focused;
            yield return FocusTap(PadButton.Down);
            Check(FocusNav.Focused != first && FocusNav.InDialog,
                $"the D-pad moves on inside it, to '{WidgetName(FocusNav.Focused)}'");

            var at = FocusNav.Focused;
            yield return FocusTap(PadButton.R1);
            Check(FocusNav.InDialog && FocusNav.Focused == at,
                $"R1 cannot walk out of it: still '{WidgetName(FocusNav.Focused)}'");

            // Down runs the list, scrolling it, to the Close button under it, and no further.
            var taps = 0;
            var inSight = true;
            while (WidgetName(FocusNav.Focused) != "Left" && taps < 60)
            {
                yield return FocusTap(PadButton.Down);
                inSight &= RingGap() < 4f && InModal(FocusNav.Focused);
                taps++;
            }

            var close = FocusNav.Focused;
            yield return FocusTap(PadButton.Down);
            Check(WidgetName(close) == "Left" && FocusNav.Focused == close && FocusNav.InDialog && inSight,
                $"down walks the {Dialogs.RowCount} rows to the Close button in {taps} steps, every one "
                + $"scrolled into sight, and stops there: '{WidgetName(FocusNav.Focused)}'");

            yield return FocusTap(PadButton.Up);
            Check(FocusNav.InDialog && FocusNav.Focused != close && InModal(FocusNav.Focused),
                $"up goes back into the list: '{WidgetName(FocusNav.Focused)}'");

            yield return FocusTap(PadButton.Circle);
            yield return null;
            yield return null;
            Check(!Dialogs.IsOpen, "circle closes the dialog");
            Check(FocusNav.Active && FocusNav.Current == before,
                $"and the walk is back on the {FocusNav.Current} region it came from");

            yield return FocusSaveAs();
            yield return FocusQuestion();
            yield return FocusHelp();
            Check(FocusNav.Active && FocusNav.Current == before && !Dialogs.IsOpen,
                $"after the three dialogs the walk is back on the {FocusNav.Current} region");
        }

        /// <summary>Save as: the name box, and under it Cancel and Save side by side.</summary>
        private static IEnumerator FocusSaveAs()
        {
            var padInUse = EditorInput.PadInUse;
            Dialogs.SaveAs("focus test");
            yield return null;
            yield return null;
            var box = FocusNav.Focused as TMPro.TMP_InputField;

            // The pad has no keys: the ring waits on the box, not typing, so the D-pad walks at once.
            Check(padInUse && Dialogs.Kind == "saveAs" && box != null && !ModUi.Typing && FocusNav.InDialog,
                $"with the pad in use, Save as opens with the ring on the name box, not typing: "
                + $"'{WidgetName(FocusNav.Focused)}', pad in use={padInUse}");

            yield return FocusTap(PadButton.Down);
            var down = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Right);
            var right = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Left);
            var left = WidgetName(FocusNav.Focused);
            yield return FocusStick(new Vector2(0f, 1f));
            Check(down == "Left" && right == "Right" && left == "Left" && FocusNav.Focused == box
                && FocusNav.InDialog,
                $"down goes to Cancel ('{down}'), right to Save ('{right}'), left back ('{left}'), "
                + $"the stick up to the name box ('{WidgetName(FocusNav.Focused)}')");
            if (box == null)
            {
                Dialogs.Close();
                yield break;
            }

            yield return FocusSaveAsTyping(box);

            yield return FocusTap(PadButton.Circle);
            yield return null;
            yield return null;
            Check(!Dialogs.IsOpen, "circle closes Save as");
        }

        /// <summary>
        /// The name box while it types, with a real pad the game's own UI reads too (its module
        /// sends cross as Submit and circle as Cancel to the box): cross must not save the first
        /// name and close, circle must stop the typing and keep the text and the dialog, and the
        /// D-pad walks on out of the box. The made-up pad drives the editor at the same time.
        /// </summary>
        private static IEnumerator FocusSaveAsTyping(TMPro.TMP_InputField box)
        {
            // A folder of its own, so a save that should not happen cannot touch the player's files.
            var folder = Path.Combine(OutDir, "focus-saveas");
            var wasFolder = BlueprintLibrary.UserFolder;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            Directory.CreateDirectory(folder);
            BlueprintLibrary.UserFolder = folder;

            yield return FocusTap(PadButton.Cross);
            var waited = 0f;
            while (!ModUi.Typing && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check(ModUi.Typing && FocusNav.Focused == box, $"cross on the box starts typing after {waited:0.00} s");
            box.text = "focus typed";

            WorldPadOn();
            yield return PadPress(false, PadKey.South);
            yield return Frames(3);
            var written = Directory.GetFiles(folder).Length;
            Check(Dialogs.Kind == "saveAs" && ModUi.Typing && box.text == "focus typed" && written == 0,
                $"a real pad's cross, which the game's UI sends to the box, saves nothing and the box keeps "
                + $"typing: dialog '{Dialogs.Kind}', text '{box.text}', {written} files written");

            // Circle reaches the box through the game's UI and the editor in the same frames.
            PadDown(false, PadKey.East);
            _pad.Down.Add(PadButton.Circle);
            yield return Frames(2);
            PadDown(false);
            _pad.Down.Remove(PadButton.Circle);
            yield return Frames(3);
            Check(Dialogs.Kind == "saveAs" && !ModUi.Typing && box.text == "focus typed"
                && FocusNav.Focused == box && !ModUi.HasSelection,
                $"circle from both stops the typing, keeps the text and the dialog: dialog '{Dialogs.Kind}', "
                + $"text '{box.text}', on '{WidgetName(FocusNav.Focused)}'");

            // Typing again, the D-pad leaves the box for the buttons under it.
            yield return FocusTap(PadButton.Cross);
            waited = 0f;
            while (!ModUi.Typing && waited < 2f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return FocusTap(PadButton.Down);
            Check(Dialogs.Kind == "saveAs" && !ModUi.Typing && WidgetName(FocusNav.Focused) == "Left" && !ModUi.HasSelection,
                $"while it types, the D-pad leaves the box and walks down to '{WidgetName(FocusNav.Focused)}'");
            yield return FocusStick(new Vector2(0f, 1f));
            yield return WorldPadOff();
            BlueprintLibrary.UserFolder = wasFolder;
            Directory.Delete(folder, true);
        }

        /// <summary>A yes or no question: the two buttons side by side, and the X over them.</summary>
        private static IEnumerator FocusQuestion()
        {
            Dialogs.Confirm("Focus test", "A question for the walk.", "Discard", () => { });
            yield return null;
            yield return null;
            var start = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Left);
            var left = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Right);
            var right = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Up);
            var up = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Down);
            Check(Dialogs.Kind == "confirm" && start == "Right" && left == "Left" && right == "Right"
                && up == "Close" && WidgetName(FocusNav.Focused) == "Right" && FocusNav.InDialog,
                $"the question starts on its answer ('{start}'), left goes to Cancel ('{left}'), right "
                + $"back ('{right}'), up to the X ('{up}'), down back ('{WidgetName(FocusNav.Focused)}')");

            yield return FocusTap(PadButton.Circle);
            yield return null;
            yield return null;
            Check(!Dialogs.IsOpen, "circle closes the question and nothing was answered");
        }

        /// <summary>
        /// Deleting one of the player's blueprints with the pad alone, in a folder of the test's
        /// own: right from its row to Delete, cross asks first on Cancel, circle and Cancel both
        /// come back to the list on the same row, the answer deletes the file and the walk lands
        /// on the row that took its place, or the one above after the last. The blueprint open
        /// from that file stays open, as not saved.
        /// </summary>
        private static IEnumerator FocusDelete(PieceEntry wall)
        {
            var before = FocusNav.Current;
            var folder = Path.Combine(OutDir, "focus-delete");
            var wasFolder = BlueprintLibrary.UserFolder;
            var wasDocument = EditorSession.Document;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            Directory.CreateDirectory(folder);
            BlueprintLibrary.UserFolder = folder;
            foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
            {
                var made = BlueprintDocument.New(name);
                made.AddPiece(wall.PrefabName, Vector3.zero, Quaternion.identity);
                Check(DocumentStore.SaveAs(made, name, true, out var error), $"wrote {name}: {error ?? "ok"}");
            }

            var beta = Path.Combine(folder, BlueprintFormat.FileNameFor("Beta"));
            Check(DocumentStore.Open(beta, out var opened, out var openError), $"opened Beta: {openError ?? "ok"}");
            EditorSession.Replace(opened);

            EditorCommands.OpenDialog();
            yield return null;
            yield return null;
            var rows = Dialogs.RowCount;
            var at = -1;
            for (var i = 0; i < rows; i++)
            {
                if (Dialogs.Row(i).Path == beta)
                {
                    at = i;
                }
            }

            var taps = 0;
            while (at >= 0 && FocusNav.Focused != Dialogs.RowWidget(at) && taps < 30)
            {
                yield return FocusTap(PadButton.Down);
                taps++;
            }

            yield return FocusTap(PadButton.Right);
            Check(at > 0 && FocusNav.InDialog && FocusNav.Focused == Dialogs.DeleteButton(at),
                $"down {taps} times to Beta's row, right to its Delete button: '{WidgetName(FocusNav.Focused)}'");
            yield return FocusTap(PadButton.Left);
            var back = FocusNav.Focused == Dialogs.RowWidget(at);
            yield return FocusTap(PadButton.Right);
            yield return FocusTap(PadButton.Down);
            var down = FocusNav.Focused == Dialogs.DeleteButton(at + 1);
            yield return FocusTap(PadButton.Up);
            Check(back && down && FocusNav.Focused == Dialogs.DeleteButton(at),
                "left goes back to the row, down goes to the Delete button under it, up comes back");

            yield return FocusTap(PadButton.Cross);
            var text = ModalText();
            Check(Dialogs.Kind == "confirm" && Dialogs.TitleText == "Delete the blueprint?"
                && WidgetName(FocusNav.Focused) == "Left" && text.Contains("Beta") && text.Contains("stays open"),
                $"cross asks first, on Cancel ('{WidgetName(FocusNav.Focused)}'): '{text}'");

            yield return FocusTap(PadButton.Circle);
            yield return null;
            Check(Dialogs.Kind == "open" && File.Exists(beta) && FocusNav.Focused == Dialogs.DeleteButton(at)
                && Dialogs.Row(at).Path == beta,
                $"circle goes back to the list, on Beta's Delete button, and the file stays: '{WidgetName(FocusNav.Focused)}'");

            yield return FocusTap(PadButton.Cross);
            yield return FocusTap(PadButton.Cross);
            yield return null;
            Check(Dialogs.Kind == "open" && File.Exists(beta) && FocusNav.Focused == Dialogs.DeleteButton(at),
                "cross twice (ask, then Cancel) comes back the same way and deletes nothing");

            yield return FocusTap(PadButton.Cross);
            yield return FocusTap(PadButton.Right);
            var answer = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Cross);
            yield return null;
            Check(answer == "Right" && !File.Exists(beta) && Dialogs.Kind == "open" && Dialogs.RowCount == rows - 1
                && FocusNav.Focused == Dialogs.DeleteButton(at) && Dialogs.Row(at).Name == "Gamma",
                $"right to Delete ('{answer}') and cross: the file is gone, {Dialogs.RowCount} of {rows} rows, "
                + $"the walk is on the Delete button of '{(Dialogs.Row(at) != null ? Dialogs.Row(at).Name : "none")}'");
            Check(opened.SourcePath == null && opened.Dirty && TopBar.FileText.Contains("not saved yet"),
                $"the blueprint from that file stays open, as not saved: '{Strip(TopBar.FileText)}'");

            yield return FocusTap(PadButton.Cross);
            yield return FocusTap(PadButton.Right);
            yield return FocusTap(PadButton.Cross);
            yield return null;
            Check(Dialogs.RowCount == rows - 2 && FocusNav.Focused == Dialogs.DeleteButton(at - 1)
                && Dialogs.Row(at - 1).Name == "Alpha",
                $"deleting the last row puts the walk on the one above: "
                + $"'{(Dialogs.Row(at - 1) != null ? Dialogs.Row(at - 1).Name : "none")}'");
            yield return Screenshot("editor-focus-delete");

            yield return FocusTap(PadButton.Circle);
            yield return null;
            yield return null;
            Check(!Dialogs.IsOpen && FocusNav.Active && FocusNav.Current == before,
                $"circle closes the list, the walk is back on the {FocusNav.Current} region");

            BlueprintLibrary.UserFolder = wasFolder;
            EditorSession.Replace(wasDocument);
            Directory.Delete(folder, true);
        }

        /// <summary>Every text the dialog shows, in one line.</summary>
        private static string ModalText()
        {
            return Dialogs.Modal == null
                ? ""
                : string.Join(" | ", Dialogs.Modal.GetComponentsInChildren<TMPro.TextMeshProUGUI>(false).Select(t => t.text));
        }

        /// <summary>The help: the Close button at the bottom and the X at the top.</summary>
        private static IEnumerator FocusHelp()
        {
            yield return FocusTap(PadButton.Options);
            yield return null;
            var start = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Up);
            var up = WidgetName(FocusNav.Focused);
            yield return FocusTap(PadButton.Down);
            Check(Dialogs.Kind == "help" && start == "Left" && up == "Close"
                && WidgetName(FocusNav.Focused) == "Left" && FocusNav.InDialog,
                $"the help starts on Close ('{start}'), up goes to the X ('{up}'), down back "
                + $"('{WidgetName(FocusNav.Focused)}')");

            yield return FocusTap(PadButton.Circle);
            yield return null;
            yield return null;
            Check(!Dialogs.IsOpen, "circle closes the help");
        }

        /// <summary>Circle gives the pane back, and the sticks fly again.</summary>
        private static IEnumerator FocusLeave()
        {
            var camera = ViewportHost.Camera;
            var from = camera.Position;
            yield return FocusTap(PadButton.Circle);
            Check(!FocusNav.Active && FocusNav.Focused == null, "circle leaves the walk");
            Check(FocusNav.Ring == null || !FocusNav.Ring.gameObject.activeSelf, "and the ring goes with it");

            _pad.Ls = new Vector2(1f, 0f);
            yield return Wait(0.25f);
            _pad.Ls = Vector2.zero;
            yield return null;
            var moved = Vector3.Distance(camera.Position, from);
            Check(moved > 0.5f, $"the left stick flies the camera again: {moved:0.0} m");
        }

        /// <summary>A tap that also watches the rule the double-press trap hangs on.</summary>
        private static IEnumerator FocusTap(PadButton button)
        {
            yield return Tap(button);
            if (ModUi.HasSelection && !ModUi.Typing)
            {
                _focusSelected++;
            }
        }

        /// <summary>The left stick pushed one way for one read, then let go, watched like a tap.</summary>
        private static IEnumerator FocusStick(Vector2 stick)
        {
            _pad.Ls = stick;
            yield return null;
            yield return null;
            _pad.Ls = Vector2.zero;
            yield return null;
            if (ModUi.HasSelection && !ModUi.Typing)
            {
                _focusSelected++;
            }
        }

        private static Vector3 Mid(UnityEngine.UI.Selectable widget)
        {
            return widget != null ? Middle((RectTransform)widget.transform) : Vector3.zero;
        }

        /// <summary>The widget's middle is inside the dialog, and inside its scroll list's window if it has one.</summary>
        private static bool InModal(UnityEngine.UI.Selectable widget)
        {
            if (widget == null || Dialogs.Modal == null)
            {
                return false;
            }

            var middle = Mid(widget);
            var scroll = widget.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            var inList = scroll == null || scroll.viewport == null || Inside(scroll.viewport, middle);
            return inList && Inside(Dialogs.Modal, middle);
        }

        private static bool Inside(RectTransform rect, Vector3 point)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return point.x >= corners[0].x && point.x <= corners[2].x
                && point.y >= corners[0].y && point.y <= corners[2].y;
        }

        private static string WidgetName(UnityEngine.UI.Selectable widget)
        {
            return widget != null ? widget.name : "none";
        }

        private static int Interactable(RectTransform region)
        {
            return region == null
                ? 0
                : region.GetComponentsInChildren<UnityEngine.UI.Selectable>(false).Count(s => s.interactable);
        }

        /// <summary>How far the ring's middle is from the focused widget's, in canvas pixels.</summary>
        private static float RingGap()
        {
            var ring = FocusNav.Ring;
            var target = FocusNav.Focused != null ? (RectTransform)FocusNav.Focused.transform : null;
            if (ring == null || target == null || !ring.gameObject.activeInHierarchy)
            {
                return 999f;
            }

            return Vector3.Distance(Middle(ring), Middle(target));
        }

        private static Vector3 Middle(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return (corners[0] + corners[2]) * 0.5f;
        }

        // ---------- scenario: editor_keep ----------

        /// <summary>
        /// Closing the editor keeps everything, and the key comes back to it: the blueprint with
        /// its unsaved change and its undo, the selection, the piece in hand and its turn, the
        /// camera, the left tab, the search, the piece menu, a dialog and the walk inside it. A
        /// blueprint in the build tool's hand that is not the one left open asks first when there
        /// are unsaved changes. A dead pane (what a world change does) is built again on the same
        /// view. Forget leaves nothing behind and the next open starts fresh.
        /// </summary>
        private static IEnumerator TestEditorKeep(Player player)
        {
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open && !EditorSession.Kept, "the editor starts closed, with nothing kept");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return WaitPaneReady();
            var document = EditorSession.Document;
            Check(ModUi.Open && ViewportHost.Ready && document != null && document.Pieces.Count >= 3,
                $"the key opened the editor on a kit: '{(document != null ? document.Name : "nothing")}'");
            var wall = PieceCatalog.Find("woodwall");
            if (!ModUi.Open || document == null || document.Pieces.Count < 3 || wall == null)
            {
                yield break;
            }

            yield return KeepEverything(document, wall);
            yield return KeepPad(document);
            yield return KeepDialog();
            yield return KeepAgainstHand(player, wall);
            yield return RebuildDeadPane();

            // ---- Forget: nothing left, and the next open is a fresh one ----
            var kept = EditorSession.Document;
            EditorSession.Forget();
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open && !EditorSession.Kept && EditorSession.Document == null, "Forget dropped the blueprint");
            Check(!Dialogs.IsOpen && !PiecePicker.IsOpen && !FocusNav.Active, "and the dialog, the piece menu and the walk");
            Check(ViewportHost.Scene == null && ViewportHost.Leaked() == 0,
                $"and the pane, with nothing left behind: {ViewportHost.Leaked()} objects still alive");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return WaitPaneReady();
            var fresh = EditorSession.Document;
            Check(ModUi.Open && fresh != null && fresh != kept && !fresh.Dirty
                    && EditorState.SelectionCount == 0 && EditorState.Mode == EditMode.Idle,
                "after Forget the key opens a fresh copy of the kit, nothing selected, nothing in hand");
        }

        /// <summary>Changes one of everything, closes with the key, opens with the key, finds it all.</summary>
        private static IEnumerator KeepEverything(BlueprintDocument document, PieceEntry wall)
        {
            var first = document.Pieces[0].Id;
            var second = document.Pieces[1].Id;
            EditorState.Select(document.Pieces[2].Id);
            EditorState.DeleteSelection();
            EditorState.Select(new[] { first, second });
            EditorSession.StartAdd(wall);
            EditorState.SetPlaceSteps(3);
            EditorWindow.SetLeftTab(1);
            Palette.SetSearch("wood");
            PiecePicker.Open();
            var camera = ViewportHost.Camera;
            camera.Turn(40f, -10f);
            camera.Zoom(-300f, new Vector2(0.5f, 0.5f));
            yield return null;
            yield return null;

            var position = camera.Position;
            var yaw = camera.Yaw;
            var pitch = camera.Pitch;
            var distance = camera.Distance;
            var undo = document.UndoDepth;
            var count = document.Pieces.Count;
            var scene = ViewportHost.Scene;
            Check(document.Dirty && undo > 0, $"the blueprint has an unsaved change, {undo} undo step(s)");
            yield return Screenshot("editor-keep-1-before");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open && EditorSession.Kept, "F7 closed the editor and kept the blueprint");
            Check(!ModUi.Blocking && Player.m_localPlayer.TakeInput(), "the game has its input back");
            Check(ViewportHost.Scene == scene && scene.IsAlive && !scene.Root.gameObject.activeSelf,
                "the pane's scene is kept, switched off");
            Check(ViewportHost.Preview != null && !ViewportHost.Preview.TextureAlive, "the pane's texture was let go");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open, "F7 opened it again");
            Check(EditorSession.Document == document && document.Dirty && document.UndoDepth == undo
                    && document.Pieces.Count == count,
                $"on the same blueprint, change and undo kept: {document.Pieces.Count} pieces, {document.UndoDepth} undo step(s)");
            Check(EditorState.SelectionCount == 2 && EditorState.IsSelected(first) && EditorState.IsSelected(second),
                $"the same two pieces are selected ({EditorState.SelectionCount})");
            Check(EditorState.Mode == EditMode.Place && EditorState.Held == wall && EditorState.Steps == 3,
                $"the wall is still in hand, turned {EditorState.Steps} steps");
            Check(Palette.Selected == wall, "and its tile is still marked in the palette");
            Check(EditorWindow.LeftTab == 1, "the In blueprint tab is still open");
            Check(Palette.Search == "wood", $"the search still says '{Palette.Search}'");
            Check(PiecePicker.IsOpen, "the piece menu is still up");
            Check(ViewportHost.Scene == scene && scene.Root.gameObject.activeSelf, "the same scene, switched on again");

            camera = ViewportHost.Camera;
            Check(Vector3.Distance(camera.Position, position) < 1e-3f && Mathf.Abs(Mathf.DeltaAngle(camera.Yaw, yaw)) < 1e-3f
                    && Mathf.Abs(camera.Pitch - pitch) < 1e-3f && Mathf.Abs(camera.Distance - distance) < 1e-3f,
                $"the camera looks from where it did: {V4(camera.Position)} yaw {camera.Yaw:0.0} pitch {camera.Pitch:0.0}");

            yield return null;
            yield return null;
            Check(ViewportHost.Pieces != null && ViewportHost.Pieces.Count == document.Pieces.Count,
                $"every piece stands in the pane: {(ViewportHost.Pieces != null ? ViewportHost.Pieces.Count : 0)} of {document.Pieces.Count}");
            var picture = SampleView("opened again");
            Check(Painted(picture) > 0.2f, $"the pane draws again: {Painted(picture) * 100f:0} % of it is not background");
            yield return Screenshot("editor-keep-2-opened-again");

            PiecePicker.Close();
            EditorState.CancelMode();
            Palette.SetSearch("");
            EditorWindow.SetLeftTab(0);
        }

        /// <summary>
        /// The pad's L2 + square closes and opens it the way F7 does: the same blueprint, its unsaved
        /// change and its undo, the same selection. Closed through the editor's fake pad, opened
        /// through a controller the game reads.
        /// </summary>
        private static IEnumerator KeepPad(BlueprintDocument document)
        {
            var undo = document.UndoDepth;
            var count = document.Pieces.Count;
            var selected = EditorState.Selection.ToArray();
            _pad = new PadState();
            PadReader.Fake = _pad;
            yield return Frames(3);
            yield return Tap(WorldPad.ModifierButton, PadButton.Square);
            yield return new WaitForSeconds(0.4f);
            PadReader.Fake = null;
            _pad = null;
            Check(!ModUi.Open && EditorSession.Kept && EditorSession.Document == document,
                "L2 + square closed the editor and kept the blueprint");

            WorldPadOn();
            yield return PadPress(true, PadKey.West);
            yield return new WaitForSeconds(0.5f);
            yield return WorldPadOff();
            Check(ModUi.Open && EditorSession.Document == document && document.Dirty && document.UndoDepth == undo
                    && document.Pieces.Count == count,
                $"L2 + square opened it again on the same blueprint, change and undo kept: {document.UndoDepth} undo step(s)");
            Check(EditorState.SelectionCount == selected.Length && selected.All(EditorState.IsSelected),
                $"the same selection ({EditorState.SelectionCount})");
        }

        /// <summary>A dialog up, with the walk inside it, is still up after closing and opening.</summary>
        private static IEnumerator KeepDialog()
        {
            EditorCommands.Help();
            yield return null;
            var focused = FocusNav.Focused;
            Check(Dialogs.Kind == "help" && FocusNav.InDialog && focused != null, "the help is up, the walk is in it");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(!ModUi.Open && Dialogs.IsOpen, "F7 closed the window with the help still up");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open && Dialogs.Kind == "help" && FocusNav.InDialog && FocusNav.Focused == focused,
                "it opened again on the help, the walk on the same button");
            Check(FocusNav.Ring != null && FocusNav.Ring.gameObject.activeInHierarchy, "the ring shows");
            yield return Screenshot("editor-keep-3-dialog");

            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.3f);
            Check(ModUi.Open && !Dialogs.IsOpen && !FocusNav.Active, "Esc shut the help, not the window, and the walk left");
        }

        /// <summary>
        /// The key with a blueprint in the build tool's hand. The one left open: it comes back as it
        /// was. Another one, over unsaved changes: the window asks first.
        /// </summary>
        private static IEnumerator KeepAgainstHand(Player player, PieceEntry wall)
        {
            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.ReadOnly && string.IsNullOrEmpty(b.SourcePath));
            ResolvedBlueprint resolved = null;
            Check(kit != null && ResolvedBlueprint.TryResolve(kit, out resolved, out _), "a kit to put in the hammer");
            if (resolved == null)
            {
                yield break;
            }

            var document = EditorSession.Document;
            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            yield return EquipHammer(player);

            // The same kit that is open, with its unsaved change: no question, the same blueprint.
            BlueprintMode.Select(player, resolved);
            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open && EditorSession.Document == document && document.Dirty && !Dialogs.IsOpen,
                "the same kit in hand: the key came back to the blueprint as it was, nothing asked");
            Check(!BlueprintMode.Active, "the build tool let go of it");

            // A new blueprint with a piece in it and no file, then the kit in hand: the window asks.
            EditorSession.Replace(DocumentStore.New("Keep test"));
            var mine = EditorSession.Document;
            mine.AddPiece(wall.PrefabName, Vector3.zero, Quaternion.identity);
            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            BlueprintMode.Select(player, resolved);
            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return new WaitForSeconds(0.5f);
            Check(ModUi.Open && EditorSession.Document == mine && Dialogs.Kind == "confirm",
                $"another blueprint in hand over unsaved changes: the window asks first ({Dialogs.TitleText})");
            yield return Screenshot("editor-keep-4-asks");

            Dialogs.Submit();
            yield return WaitPaneReady();
            var taken = EditorSession.Document;
            Check(!Dialogs.IsOpen && taken != mine && taken != null && taken.Name == kit.Name && !taken.Dirty,
                $"Discard opened the kit from the hammer: '{(taken != null ? taken.Name : "nothing")}'");
            Check(ViewportHost.Pieces != null && ViewportHost.Pieces.Count == taken.Pieces.Count,
                $"and the pane stands it: {(ViewportHost.Pieces != null ? ViewportHost.Pieces.Count : 0)} of {taken.Pieces.Count}");
        }

        /// <summary>A world change kills the pane while the window is closed. The next open builds it again, same view.</summary>
        private static IEnumerator RebuildDeadPane()
        {
            var document = EditorSession.Document;
            EditorState.Select(document.Pieces[0].Id);
            var camera = ViewportHost.Camera;
            camera.Turn(-30f, 5f);
            yield return null;
            var position = camera.Position;
            var yaw = camera.Yaw;
            var pitch = camera.Pitch;

            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            var dead = ViewportHost.Scene;
            UnityEngine.Object.Destroy(dead.Root.gameObject);
            yield return null;
            yield return null;
            Check(!dead.IsAlive, "the kept scene is gone, the way a world change takes it");

            yield return PressKey(UnityEngine.InputSystem.Key.F7);
            yield return WaitPaneReady();
            yield return null;
            yield return null;
            var scene = ViewportHost.Scene;
            Check(ModUi.Open && scene != null && scene != dead && scene.IsAlive, "the key built a new pane");
            Check(EditorSession.Document == document && EditorState.IsSelected(document.Pieces[0].Id),
                "on the same blueprint and selection");
            camera = ViewportHost.Camera;
            Check(Vector3.Distance(camera.Position, position) < 1e-3f && Mathf.Abs(Mathf.DeltaAngle(camera.Yaw, yaw)) < 1e-3f
                    && Mathf.Abs(camera.Pitch - pitch) < 1e-3f,
                $"the new camera looks from where the old one did: {V4(camera.Position)} yaw {camera.Yaw:0.0}");
            Check(ViewportHost.Pieces != null && ViewportHost.Pieces.Count == document.Pieces.Count,
                $"every piece stands again: {(ViewportHost.Pieces != null ? ViewportHost.Pieces.Count : 0)} of {document.Pieces.Count}");
            var picture = SampleView("built again");
            Check(Painted(picture) > 0.2f, $"the pane draws: {Painted(picture) * 100f:0} % of it is not background");
            yield return Screenshot("editor-keep-5-rebuilt");
        }

        /// <summary>Waits until every piece of the open blueprint stands in the pane.</summary>
        private static IEnumerator WaitPaneReady()
        {
            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return null;
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
            yield return CheckBackToPiece(player, "the editor's blueprint built in full");

            yield return new WaitForSeconds(2f);
            player.m_lookPitch = 12f;
            yield return Screenshot("editor-build-1-built");

            // Support checks start at once; give them time to break it if they will.
            yield return new WaitForSeconds(15f);
            var standing = built.Count(p => p != null);
            Check(standing == wanted.Count, $"still standing after 15 s: {standing} of {wanted.Count}");
            yield return Screenshot("editor-build-2-after-15s");
        }

        /// <summary>
        /// The way back: a build in full left blueprint mode, so the blueprint key takes it again, and
        /// the editor key edits the blueprint the hammer is holding.
        /// </summary>
        private static IEnumerator EditItAgain(Player player, string path)
        {
            Check(!BlueprintMode.Active, "the build in full left blueprint mode");
            for (var i = 0; i < 12 && (BlueprintMode.Current == null || BlueprintMode.Current.Blueprint.SourcePath != path); i++)
            {
                BlueprintMode.Cycle(player);
            }

            Check(BlueprintMode.Active && BlueprintMode.Current.Blueprint.SourcePath == path,
                $"the blueprint key puts it back in the hammer: '{BlueprintMode.EntryName}'");
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

        // ---------- scenario: editor_capture ----------

        /// <summary>
        /// Capture: the workshop kit is built in the world turned 45 degrees, a rectangle is put
        /// over it, turned and sized with real wheel events, and what comes back has to be the kit
        /// file as it is, not turned. A piece of another build tool stands inside (left out and
        /// counted), and one wall stands across the edge with its pivot outside (glows orange).
        /// Then the kept-document question, Esc, and the glow's cost on a few hundred pieces.
        /// </summary>
        private static IEnumerator TestEditorCapture(Player player)
        {
            yield return MoveToBuildSpot(player);
            yield return EquipHammer(player);
            RemoveOldTestBuildings(player);
            WorldCapture.ResetShape();
            PieceCatalog.Ensure();
            Check(PieceCatalog.Ready, "the piece catalog is built");

            // 1. On bare ground.
            yield return CaptureBareGround(player);

            var kit = BlueprintLibrary.All.FirstOrDefault(b => b.Pieces.Count > 2);
            if (kit == null)
            {
                Check(false, "no kit to stand in the world");
                yield break;
            }

            if (!ResolvedBlueprint.TryResolve(kit, out var resolved, out var error))
            {
                Check(false, error);
                yield break;
            }

            Check(true, $"kit to capture: '{kit.Name}', {kit.Pieces.Count} pieces");
            foreach (var part in resolved.Parts)
            {
                player.m_knownRecipes.Add(part.Piece.m_name);
            }

            player.UpdateAvailablePiecesList();
            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);
            foreach (var cost in resolved.TotalCost)
            {
                player.GetInventory().AddItem(cost.m_resItem.gameObject.name, cost.m_amount, 1, 0, 0L, "", false);
            }

            // 2. The kit, turned 45 degrees. Facing 180 the hammer starts the kit at 0, and two
            // notches of the real wheel turn it on to 45. The body turns slowly on its own, so it
            // is set straight away.
            var facing = Quaternion.Euler(0f, 180f, 0f);
            player.m_lookYaw = facing;
            player.m_lookPitch = 20f;
            player.transform.rotation = facing;
            player.m_body.rotation = facing;
            yield return new WaitForSeconds(0.5f);
            BlueprintMode.Select(player, resolved);
            yield return null;
            var start = BlueprintMode.RotationSteps;
            Log($"facing {player.transform.eulerAngles.y:0.#}, the kit starts at {start * BlueprintMode.RotationStep:0.#}");
            yield return WheelNotch(1f);
            yield return WheelNotch(1f);
            var kitYaw = Mathf.Repeat(BlueprintMode.RotationSteps * BlueprintMode.RotationStep, 360f);
            Check(BlueprintMode.RotationSteps == start + 2 && Mathf.Abs(Mathf.DeltaAngle(kitYaw, 45f)) < 0.01f,
                $"two wheel notches turn the kit to 45 degrees: steps {start} -> {BlueprintMode.RotationSteps}, yaw {kitYaw:0.#}");

            yield return AimAtGround(player);
            var root = BlueprintMode.PreviewRoot;
            Check(root != null && BlueprintMode.HasTarget && BlueprintMode.Blocked == null,
                "the kit's preview stands on a free spot: " + (BlueprintMode.Blocked ?? "ok"));
            if (root == null)
            {
                yield break;
            }

            var rootPos = root.position;
            var rootTurn = root.rotation;
            player.m_lastToolUseTime = 0f;
            var existing = new HashSet<Piece>(PiecesAround(player, rootPos));
            Check(BlueprintMode.TryBuild(player), "the kit is built in the world");
            BlueprintMode.Exit();
            var built = PiecesAround(player, rootPos).Where(p => !existing.Contains(p)).ToList();
            Check(built.Count == resolved.Parts.Count,
                $"every piece of it stands: {built.Count} of {resolved.Parts.Count}");
            Check(Mathf.Abs(Mathf.DeltaAngle(rootTurn.eulerAngles.y, 45f)) < 0.01f,
                $"and it stands turned 45 degrees: {rootTurn.eulerAngles.y:0.##}");
            if (built.Count != resolved.Parts.Count)
            {
                yield break;
            }

            // The rectangle that covers it: the pivots' footprint in the kit's own frame, half a
            // metre spare on each side, rounded up to the 2 m grid of the sides.
            var turn45 = Quaternion.Euler(0f, 45f, 0f);
            var back45 = Quaternion.Inverse(turn45);
            var lo = new Vector3(float.MaxValue, 0f, float.MaxValue);
            var hi = new Vector3(float.MinValue, 0f, float.MinValue);
            foreach (var piece in built)
            {
                var local = back45 * (piece.transform.position - rootPos);
                lo = Vector3.Min(lo, new Vector3(local.x, 0f, local.z));
                hi = Vector3.Max(hi, new Vector3(local.x, 0f, local.z));
            }

            var middle = (lo + hi) * 0.5f;
            var centre = rootPos + (turn45 * middle);
            centre.y = ZoneSystem.instance.GetGroundHeight(centre);
            var wantWidth = Mathf.Ceil((hi.x - lo.x + 0.999f) / WorldCapture.SideStep) * WorldCapture.SideStep;
            var wantDepth = Mathf.Ceil((hi.z - lo.z + 0.999f) / WorldCapture.SideStep) * WorldCapture.SideStep;
            Log($"capture rectangle: centre {V4(centre)}, {wantWidth} x {wantDepth} m, pivots {V4(lo)} to {V4(hi)}");

            // A piece the hammer cannot build, in the middle.
            var stray = OtherToolPrefab();
            Check(stray != null && PieceCatalog.OtherTool(stray.gameObject.name) != null,
                $"a piece of another tool to leave out: {(stray != null ? stray.gameObject.name : "none")}");
            if (stray != null)
            {
                player.PlacePiece(stray, new Vector3(centre.x, rootPos.y, centre.z), Quaternion.identity, false, true);
            }

            // A wall lying across the +x edge: pivot 0.5 m outside, its 2 m body from 0.5 m inside
            // to 1.5 m outside. Same height as the kit's own walls.
            var wallPrefab = PiecePrefab("woodwall");
            Check(wallPrefab != null, "the woodwall prefab exists");
            var wallAt = centre + (turn45 * new Vector3((wantWidth * 0.5f) + 0.5f, 0f, 0f));
            wallAt.y = rootPos.y + 1.0477f;
            if (wallPrefab != null)
            {
                player.PlacePiece(wallPrefab, wallAt, turn45, false, true);
            }

            yield return new WaitForSeconds(1f);
            var extraWall = PiecesAround(player, wallAt)
                .FirstOrDefault(p => Utils.GetPrefabName(p.gameObject) == "woodwall"
                    && Vector3.Distance(p.transform.position, wallAt) < 0.01f);
            Check(extraWall != null, $"the extra wall stands across the edge at {V4(wallAt)}");

            // 3. The rectangle, over the kit, turned and sized with the real wheel.
            yield return CaptureShape(player, centre, wantWidth, wantDepth);

            // 4. The glow.
            yield return CaptureGlow(player, built, extraWall, wantWidth, wantDepth);

            // 5 and 6. The key again: the editor opens on it with Save as up, and it is the kit.
            yield return CaptureIntoEditor(player, kit);

            // 7. A kept blueprint with unsaved changes is asked about first.
            yield return CaptureOverKept(player, centre, kit);

            // 8. Esc takes everything off again.
            yield return CaptureEscape(player, centre, built, extraWall);

            // 9. The same on the pad alone, and the game sees none of the presses.
            yield return CapturePad(player, centre, kit);

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(0.5f);

            // What a glow refresh costs on a few hundred pieces.
            yield return CaptureGlowCost(player);

            RemoveOldTestBuildings(player);
            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>1. The key twice on ground with nothing on it: a rectangle, then no window and a message.</summary>
        private static IEnumerator CaptureBareGround(Player player)
        {
            player.m_lookPitch = 35f;
            yield return new WaitForSeconds(0.5f);
            yield return PressKey(UnityEngine.InputSystem.Key.F8);
            yield return new WaitForSeconds(0.3f);
            Check(WorldCapture.Active && WorldCapture.Drawn && CaptureHud.Visible,
                $"the capture key puts a rectangle on the ground, with its status lines: active {WorldCapture.Active}, "
                + $"drawn {WorldCapture.Drawn}, lines {CaptureHud.Visible}");
            Check(!ModUi.Blocking, "and the game keeps its input");
            if (!WorldCapture.Active)
            {
                yield break;
            }

            yield return PressKey(UnityEngine.InputSystem.Key.F8);
            yield return new WaitForSeconds(0.3f);
            Check(!WorldCapture.Active && !WorldCapture.Drawn && !CaptureHud.Visible && CaptureTint.Count == 0,
                "the second press ends it: no rectangle, no lines, nothing glows");
            Check(WorldCapture.LastCount == 0 && !ModUi.Open && WorldCapture.LastMessage.StartsWith("Nothing"),
                $"on bare ground it opens no window and says so: '{WorldCapture.LastMessage}'");
        }

        /// <summary>
        /// 3. The key starts it over the kit, the test pins it on the footprint's centre, and the
        /// wheel does the rest: two notches alone, then Shift, Alt and Shift + Alt, each checked.
        /// </summary>
        private static IEnumerator CaptureShape(Player player, Vector3 centre, float wantWidth, float wantDepth)
        {
            var zoom = GameCamera.instance != null ? GameCamera.instance.m_distance : float.NaN;
            var placeTurn = player.m_placeRotation;
            yield return PressKey(UnityEngine.InputSystem.Key.F8);
            yield return new WaitForSeconds(0.2f);
            Check(WorldCapture.Active, "the key starts a capture over the kit");
            if (!WorldCapture.Active)
            {
                yield break;
            }

            WorldCapture.Pin(centre);
            Check(Vector3.Distance(WorldCapture.Centre, centre) < 0.01f,
                $"pinned on the kit's footprint centre: {V4(WorldCapture.Centre)}");

            var shift = UnityEngine.InputSystem.Key.LeftShift;
            var alt = UnityEngine.InputSystem.Key.LeftAlt;
            yield return Notch(1f, 22.5f, 0f, 0f, "the wheel alone turns it");
            yield return Notch(1f, 22.5f, 0f, 0f, "and again");
            yield return Notch(1f, 0f, 2f, 0f, "Shift + wheel widens it", shift);
            yield return Notch(1f, 0f, 0f, 2f, "Alt + wheel deepens it", alt);
            yield return Notch(1f, 0f, 2f, 2f, "Shift + Alt + wheel grows both sides", shift, alt);
            while (WorldCapture.Width > wantWidth && WorldCapture.Depth > wantDepth)
            {
                yield return Notch(-1f, 0f, -2f, -2f, "Shift + Alt + wheel down shrinks both", shift, alt);
            }

            while (WorldCapture.Width > wantWidth)
            {
                yield return Notch(-1f, 0f, -2f, 0f, "Shift + wheel down narrows it", shift);
            }

            while (WorldCapture.Depth > wantDepth)
            {
                yield return Notch(-1f, 0f, 0f, -2f, "Alt + wheel down makes it shallower", alt);
            }

            Check(Mathf.Abs(Mathf.DeltaAngle(WorldCapture.Yaw, 45f)) < 0.01f
                && Mathf.Approximately(WorldCapture.Width, wantWidth) && Mathf.Approximately(WorldCapture.Depth, wantDepth),
                $"it ends turned 45 and {wantWidth} x {wantDepth} m: {WorldCapture.Yaw:0.#}, "
                + $"{WorldCapture.Width} x {WorldCapture.Depth}");

            var zoomAfter = GameCamera.instance != null ? GameCamera.instance.m_distance : float.NaN;
            Check(Mathf.Abs(zoomAfter - zoom) < 1e-4f && player.m_placeRotation == placeTurn,
                $"the wheel did not zoom the camera or turn the hammer's piece: distance {zoom:0.###} -> {zoomAfter:0.###}, "
                + $"piece turn {placeTurn} -> {player.m_placeRotation}");
        }

        /// <summary>One notch of the real wheel, checked against what it should do to the rectangle.</summary>
        private static IEnumerator Notch(
            float direction, float turn, float width, float depth, string what, params UnityEngine.InputSystem.Key[] held)
        {
            var yaw = WorldCapture.Yaw;
            var w = WorldCapture.Width;
            var d = WorldCapture.Depth;
            yield return WheelNotch(direction, held);
            var turned = Mathf.DeltaAngle(yaw, WorldCapture.Yaw);
            var ok = Mathf.Abs(turned - turn) < 0.01f
                && Mathf.Abs(WorldCapture.Width - w - width) < 0.01f
                && Mathf.Abs(WorldCapture.Depth - d - depth) < 0.01f;
            Check(ok, $"{what}: turn {yaw:0.#} -> {WorldCapture.Yaw:0.#}, {w} x {d} -> {WorldCapture.Width} x {WorldCapture.Depth}");
        }

        /// <summary>A notch of the real wheel through the input system, with these keys held while it turns.</summary>
        private static IEnumerator WheelNotch(float direction, params UnityEngine.InputSystem.Key[] held)
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (mouse == null || keyboard == null)
            {
                Check(false, "no mouse or keyboard device for the wheel");
                yield break;
            }

            if (held.Length > 0)
            {
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(
                    keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(held));
                yield return null;
                yield return null;
            }

            UnityEngine.InputSystem.InputSystem.QueueDeltaStateEvent(mouse.scroll, new Vector2(0f, direction));
            for (var frame = 0; frame < 4; frame++)
            {
                yield return null;
            }

            if (held.Length > 0)
            {
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(
                    keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState());
                yield return null;
                yield return null;
            }
        }

        /// <summary>4. Yellow on the kit, orange on the wall across the edge, nothing on the stray piece.</summary>
        private static IEnumerator CaptureGlow(Player player, List<Piece> built, Piece extraWall, float width, float depth)
        {
            yield return new WaitForSeconds(0.5f);
            Check(WorldCapture.InsideNow == built.Count && WorldCapture.EdgeNow == 1 && WorldCapture.SkippedNow == 1,
                $"inside {WorldCapture.InsideNow} (want {built.Count}), across the edge {WorldCapture.EdgeNow} (want 1), "
                + $"not buildable {WorldCapture.SkippedNow} (want 1)");
            Check(CaptureTint.InsideCount == built.Count && CaptureTint.LeftOutCount == 1,
                $"the glow set: {CaptureTint.InsideCount} yellow (want {built.Count}), {CaptureTint.LeftOutCount} orange (want 1)");
            Check(built.All(p => CaptureTint.Glows(p.gameObject, out var inside) && inside),
                "every kit piece is in the yellow set");
            Check(extraWall != null && CaptureTint.Glows(extraWall.gameObject, out var yellow) && !yellow,
                "the wall across the edge is the orange one");

            // What the renderers carry. The piece under the crosshair wears the game's own
            // highlight, so one may differ.
            var hovered = player.GetHoveringPiece();
            var lit = built.Count(p => HasColor(p.gameObject, CaptureTint.Inside));
            var orange = extraWall != null && HasColor(extraWall.gameObject, CaptureTint.LeftOut);
            Log($"glow on the renderers: {lit} of {built.Count} yellow, wall orange {orange}, "
                + $"hovered {(hovered != null ? hovered.name : "none")}");
            Check(lit >= built.Count - 1 && (orange || hovered == extraWall),
                $"and the renderers carry it: {lit} of {built.Count} yellow, the wall orange {orange}");

            var boxPoints = 2 * (CaptureBox.Segments(width) + CaptureBox.Segments(depth));
            Check(WorldCapture.Drawn, "the outline is on the ground");
            var text = CaptureHud.Text;
            Check(CaptureHud.Visible && text.Contains($"{width:0} x {depth:0} m") && text.Contains("turned 45°")
                && text.Contains($"{built.Count} pieces") && text.Contains("2 left out") && !text.Contains("\n") && NoControls(text),
                "the status line says it, on one line, with no controls: " + text.Replace("\n", " | "));
            Check(HintRow.Shown == HintRow.Mode.Capture && HintRow.ShownEntries.Any(e => e.Keys == "Shift + Alt + wheel"),
                "the controls are in the game's hint row: " + string.Join(", ", HintRow.ShownEntries.Select(e => $"{e.Label} [{e.Keys}]")));
            Log($"outline points expected {boxPoints}");

            // The lines must not sit on the game's own top-left message ("Built Workshop" and the like).
            var hudRoot = Hud.instance != null ? Hud.instance.m_rootObject.transform : null;
            var message = MessageHud.instance != null ? MessageHud.instance.m_messageText : null;
            if (hudRoot != null && message != null && CaptureHud.Rect != null)
            {
                var theirs = RectIn(hudRoot, message.rectTransform);
                var ours = RectIn(hudRoot, CaptureHud.Rect);
                Log($"top-left message box {theirs}, capture lines {ours}");
                Check(!theirs.Overlaps(ours), $"the status lines clear the game's top-left message: {ours} vs {theirs}");
            }

            yield return Screenshot("editor-capture-1-rect");
        }

        /// <summary>5 and 6. The key captures: the editor opens on it, Save as up, and it is the kit, not turned.</summary>
        private static IEnumerator CaptureIntoEditor(Player player, Blueprint kit)
        {
            var zoom = GameCamera.instance != null ? GameCamera.instance.m_distance : float.NaN;
            yield return PressKey(UnityEngine.InputSystem.Key.F8);
            yield return new WaitForSeconds(0.5f);
            Check(!WorldCapture.Active && !WorldCapture.Drawn && !CaptureHud.Visible && CaptureTint.Count == 0,
                "the capture ended: no rectangle, no lines, nothing glows");
            Check(ModUi.Open, "the editor opened on what came back");
            var document = EditorSession.Document;
            Check(document != null && document.Pieces.Count == kit.Pieces.Count,
                $"it holds the kit's pieces: {(document != null ? document.Pieces.Count : 0)} of {kit.Pieces.Count}");
            Check(WorldCapture.LastSkipped == 1,
                $"and left the other tool's piece out, counted: {WorldCapture.LastSkipped}");
            Check(document != null && string.IsNullOrEmpty(document.SourcePath) && document.Dirty && !document.ReadOnly,
                "it opened as a blueprint with no file of its own, not saved");
            Check(Dialogs.Kind == "saveAs" && Dialogs.NameField != null && Dialogs.NameField.text == WorldCapture.Name,
                $"with Save as up and '{WorldCapture.Name}' in the name box: dialog '{Dialogs.Kind}', "
                + $"name '{(Dialogs.NameField != null ? Dialogs.NameField.text : "none")}'");
            if (document == null || document.Pieces.Count == 0)
            {
                yield break;
            }

            CompareToKit(kit, document, Quaternion.identity);
            var bottom = EditorState.BottomCentre(new List<DocPiece>(document.Pieces));
            Check(bottom.magnitude < 1e-3f, $"its origin is the bottom centre, {V4(bottom)} off");

            var waited = 0f;
            while (!ViewportHost.Ready && waited < 15f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            yield return new WaitForSeconds(1f);
            yield return Screenshot("editor-capture-2-in-editor");

            // Closing keeps it, Save as and all, for the next scenario step.
            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
            Check(!ModUi.Open, "the editor closed");
            var zoomAfter = GameCamera.instance != null ? GameCamera.instance.m_distance : float.NaN;
            Log($"camera distance {zoom:0.###} -> {zoomAfter:0.###}");
        }

        /// <summary>
        /// 7. A capture over a kept blueprint with unsaved changes: the question comes first, and
        /// Save as only after Discard, on the new blueprint.
        /// </summary>
        private static IEnumerator CaptureOverKept(Player player, Vector3 centre, Blueprint kit)
        {
            var kept = EditorState.Document;
            Check(kept != null && kept.Dirty, "the last capture is kept, with unsaved changes");

            yield return PressKey(UnityEngine.InputSystem.Key.F8);
            yield return new WaitForSeconds(0.2f);
            WorldCapture.Pin(centre);
            yield return new WaitForSeconds(0.3f);
            yield return PressKey(UnityEngine.InputSystem.Key.F8);
            yield return new WaitForSeconds(0.5f);

            Check(ModUi.Open && Dialogs.Kind == "confirm" && EditorState.Document == kept,
                $"it asks first: window {ModUi.Open}, dialog '{Dialogs.Kind}' '{Dialogs.TitleText}', "
                + $"still on the kept one {EditorState.Document == kept}");
            if (Dialogs.Kind != "confirm")
            {
                yield break;
            }

            Dialogs.Submit();
            yield return new WaitForSeconds(0.3f);
            var now = EditorState.Document;
            Check(now != null && now != kept && now.Name == WorldCapture.Name && now.Pieces.Count == kit.Pieces.Count,
                $"after Discard the captured one is open: {(now != null ? now.Pieces.Count : 0)} pieces, new {now != kept}");
            Check(Dialogs.Kind == "saveAs" && Dialogs.NameField != null && Dialogs.NameField.text == WorldCapture.Name,
                $"and Save as is up on it: dialog '{Dialogs.Kind}'");

            EditorSession.Close();
            yield return new WaitForSeconds(0.3f);
        }

        /// <summary>8. Esc during a capture: the glow comes off every piece, and the pause menu stays shut.</summary>
        private static IEnumerator CaptureEscape(Player player, Vector3 centre, List<Piece> built, Piece extraWall)
        {
            yield return PressKey(UnityEngine.InputSystem.Key.F8);
            yield return new WaitForSeconds(0.2f);
            WorldCapture.Pin(centre);
            yield return new WaitForSeconds(0.5f);
            Check(WorldCapture.Active && CaptureTint.Count == built.Count + 1,
                $"another capture glows again: {CaptureTint.Count} pieces");

            yield return PressKey(UnityEngine.InputSystem.Key.Escape);
            yield return new WaitForSeconds(0.3f);
            var menu = Menu.IsVisible();
            if (menu)
            {
                Menu.instance.Hide();
            }

            Check(!menu, "Esc stopped the capture without opening the pause menu");
            Check(!WorldCapture.Active && !WorldCapture.Drawn && !CaptureHud.Visible && CaptureTint.Count == 0,
                $"and took everything away: active {WorldCapture.Active}, drawn {WorldCapture.Drawn}, "
                + $"lines {CaptureHud.Visible}, glowing {CaptureTint.Count}");

            // Nothing keeps our colour. Only the piece under the crosshair may wear the game's own.
            var hovered = player.GetHoveringPiece();
            var pieces = new List<Piece>(built);
            if (extraWall != null)
            {
                pieces.Add(extraWall);
            }

            var ours = pieces.Count(p => HasColor(p.gameObject, CaptureTint.Inside) || HasColor(p.gameObject, CaptureTint.LeftOut));
            var coloured = pieces.Count(p => p != hovered && HasAnyColor(p.gameObject));
            Check(ours == 0 && coloured == 0,
                $"no piece keeps a changed colour: {ours} with ours, {coloured} with any (hovered one skipped)");
        }

        /// <summary>
        /// 9. The whole capture on the pad alone, through a controller the game reads like a real one:
        /// L2 + triangle starts it, every D-pad step turns or sizes it (each one checked), and the game
        /// sees none of those presses: the hotbar, the forsaken power, the camera and minimap zoom and
        /// the inventory stay as they were. L2 + triangle takes it into the editor with Save as up. A
        /// second one stops on circle, with no jump. Last, the same presses with no capture up do reach
        /// the game, so every "unchanged" above could have failed.
        /// </summary>
        private static IEnumerator CapturePad(Player player, Vector3 centre, Blueprint kit)
        {
            // The last capture is kept with unsaved changes: forget it, so this one opens straight on Save as.
            EditorSession.Forget();

            // No hammer: in build mode the game itself would not zoom on L2 + D-pad, and the test must
            // see that it could. Two things in the hotbar, so D-pad left and right have somewhere to go.
            var inventory = player.GetInventory();
            var tool = player.GetRightItem();
            if (tool != null)
            {
                player.UnequipItem(tool);
            }

            AddTo(inventory, "Torch", 1);
            AddTo(inventory, "Club", 1);
            player.SetGuardianPower("GP_Eikthyr");
            player.m_guardianPowerCooldown = 0f;
            var map = Minimap.instance;
            if (map != null && map.m_mode != Minimap.MapMode.Small)
            {
                map.SetMapMode(Minimap.MapMode.Small);
            }

            var bar = UnityEngine.Object.FindObjectOfType<HotkeyBar>();
            var camera = GameCamera.instance;
            yield return new WaitForSeconds(0.5f);

            WorldPadOn();
            _pad = new PadState { Ps = true };
            PadReader.Fake = _pad;
            var selected = bar != null ? bar.m_selected : -99;
            var right = player.GetRightItem();
            var uses = PowerUses();
            var distance = camera != null ? camera.m_distance : float.NaN;
            var zoom = map != null ? map.SmallZoom : float.NaN;
            var shows = _inventoryShows;
            Log($"pad capture: hotbar {selected} of {(bar != null ? bar.m_elements.Count : 0)}, right hand "
                + $"{(right != null ? right.m_shared.m_name : "empty")}, power uses {uses}, camera {distance:0.###}, map zoom {zoom:0.####}");

            // Start: L2 + triangle.
            yield return PadPress(true, PadKey.North);
            yield return Frames(2);
            Check(WorldCapture.Active && WorldCapture.Drawn && CaptureHud.Visible && !ModUi.Blocking,
                $"L2 + triangle starts a capture, the game keeps its input: active {WorldCapture.Active}");
            Check(_inventoryShows == shows && !InventoryGui.IsVisible(),
                $"and the inventory did not open: {_inventoryShows - shows} tries");
            if (!WorldCapture.Active)
            {
                yield return CapturePadDone(player, map, zoom, camera, distance);
                yield break;
            }

            WorldCapture.Pin(centre);
            yield return null;
            var wantYaw = WorldCapture.Yaw;
            var wantWidth = WorldCapture.Width;
            var wantDepth = WorldCapture.Depth;
            yield return PadStep(false, PadKey.DpadRight, 22.5f, 0f, 0f, "D-pad right turns it 22.5 degrees");
            yield return PadStep(false, PadKey.DpadLeft, -22.5f, 0f, 0f, "D-pad left turns it back");
            yield return PadStep(false, PadKey.DpadUp, 0f, 2f, 2f, "D-pad up grows both sides 2 m");
            yield return PadStep(false, PadKey.DpadDown, 0f, -2f, -2f, "D-pad down shrinks both sides 2 m");
            yield return PadStep(true, PadKey.DpadRight, 0f, 2f, 0f, "L2 + D-pad right widens it 2 m");
            yield return PadStep(true, PadKey.DpadLeft, 0f, -2f, 0f, "L2 + D-pad left narrows it 2 m");
            yield return PadStep(true, PadKey.DpadUp, 0f, 0f, 2f, "L2 + D-pad up deepens it 2 m");
            yield return PadStep(true, PadKey.DpadDown, 0f, 0f, -2f, "L2 + D-pad down makes it shallower 2 m");
            Check(Mathf.Abs(Mathf.DeltaAngle(WorldCapture.Yaw, wantYaw)) < 0.01f
                && Mathf.Approximately(WorldCapture.Width, wantWidth) && Mathf.Approximately(WorldCapture.Depth, wantDepth),
                $"and it is back on the kit: {WorldCapture.Yaw:0.#}, {WorldCapture.Width} x {WorldCapture.Depth}");

            // The game saw none of it.
            var nowSelected = bar != null ? bar.m_selected : -99;
            var nowRight = player.GetRightItem();
            var nowDistance = camera != null ? camera.m_distance : float.NaN;
            var nowZoom = map != null ? map.SmallZoom : float.NaN;
            Check(nowSelected == selected && nowRight == right,
                $"the hotbar did not move and nothing was used from it: slot {selected} -> {nowSelected}, "
                + $"right hand {(nowRight != null ? nowRight.m_shared.m_name : "empty")}");
            Check(PowerUses() == uses && player.m_guardianPowerCooldown == 0f,
                $"the forsaken power did not start: uses {uses} -> {PowerUses()}, cooldown {player.m_guardianPowerCooldown:0.#}");
            Check(Mathf.Abs(nowDistance - distance) < 1e-4f, $"the camera did not zoom: {distance:0.####} -> {nowDistance:0.####}");
            Check(Mathf.Abs(nowZoom - zoom) < 1e-6f, $"the minimap did not zoom: {zoom:0.####} -> {nowZoom:0.####}");
            Check(_inventoryShows == shows && !InventoryGui.IsVisible() && !player.IsSitting(),
                $"no inventory ({_inventoryShows - shows} tries), not sitting");

            // The status lines speak the pad. The torch and the club made the game queue "New item"
            // popups over the top left; a test artefact, cleared for the picture.
            if (MessageHud.instance != null)
            {
                MessageHud.instance.ClearUnlockQueue();
                MessageHud.instance.HideAll();
            }

            yield return new WaitForSeconds(0.4f);
            var text = CaptureHud.Text;
            var take = Localization.instance.GetBoundKeyString(WorldPad.Modifier, true) + " + " + Localization.instance.GetBoundKeyString(WorldPad.Triangle, true);
            Check(ZInput.IsGamepadActive() && !text.Contains("\n") && NoControls(text),
                "with the pad the status line is still one line with no controls: " + text);
            Check(HintRow.Shown == HintRow.Mode.Capture && HintRow.GamepadRow.gameObject.activeInHierarchy
                && HintRow.ShownEntries.Count > 0 && HintRow.ShownEntries[0].GamepadEntry.text.Contains(take),
                "the game's hint row shows the capture's pad set, the game's own icons: "
                + string.Join(", ", HintRow.ShownEntries.Select(e => e.GamepadEntry.text)));
            yield return Screenshot("editor-capture-3-pad");

            // Take it: L2 + triangle again.
            yield return PadPress(true, PadKey.North);
            yield return new WaitForSeconds(0.5f);
            var document = EditorSession.Document;
            Check(!WorldCapture.Active && ModUi.Open && document != null && document.Pieces.Count == kit.Pieces.Count,
                $"L2 + triangle again takes it into the editor: {(document != null ? document.Pieces.Count : 0)} of {kit.Pieces.Count} pieces");
            Check(Dialogs.Kind == "saveAs" && Dialogs.NameField != null && Dialogs.NameField.text == WorldCapture.Name,
                $"with Save as up, the way F8 does it: dialog '{Dialogs.Kind}'");
            Check(_inventoryShows == shows, $"the inventory never tried to open: {_inventoryShows - shows} tries");
            EditorSession.Close();
            yield return new WaitForSeconds(0.4f);

            // A second one, stopped with circle: no glow left, and circle did not jump.
            yield return PadPress(true, PadKey.North);
            WorldCapture.Pin(centre);
            yield return new WaitForSeconds(0.5f);
            Check(WorldCapture.Active && CaptureTint.Count > 0, $"a second capture glows: {CaptureTint.Count} pieces");
            var rise = 0f;
            yield return PadPress(false, PadKey.East);
            yield return Rise(player, 1.2f, r => rise = r);
            Check(!WorldCapture.Active && !WorldCapture.Drawn && !CaptureHud.Visible && CaptureTint.Count == 0,
                $"circle stopped it and took the glow off: active {WorldCapture.Active}, glowing {CaptureTint.Count}");
            Check(rise < 0.15f && !Menu.IsVisible(), $"and the player did not jump: rose {rise:0.00} m");

            // The same presses with no capture up reach the game, as always.
            yield return PadPress(false, PadKey.DpadRight);
            var moved = bar != null ? bar.m_selected : -99;
            yield return PadPress(false, PadKey.DpadDown);
            yield return Frames(2);
            var used = PowerUses();

            // Held zoom, out then in: one of the two moves it, whichever end it sat at.
            var camera0 = camera != null ? camera.m_distance : float.NaN;
            PadDown(true);
            yield return Frames(2);
            PadDown(true, PadKey.DpadDown);
            yield return Frames(8);
            var camera1 = camera != null ? camera.m_distance : float.NaN;
            PadDown(true, PadKey.DpadUp);
            yield return Frames(8);
            PadDown(false);
            yield return Frames(2);
            var camera2 = camera != null ? camera.m_distance : float.NaN;
            var cameraMoved = Mathf.Abs(camera1 - camera0) > 1e-3f || Mathf.Abs(camera2 - camera1) > 1e-3f;

            var map0 = map != null ? map.SmallZoom : float.NaN;
            yield return PadPress(true, PadKey.DpadLeft);
            var map1 = map != null ? map.SmallZoom : float.NaN;
            yield return PadPress(true, PadKey.DpadRight);
            var map2 = map != null ? map.SmallZoom : float.NaN;
            var mapMoved = Mathf.Abs(map1 - map0) > 1e-6f || Mathf.Abs(map2 - map1) > 1e-6f;

            // The power's animation holds the player a moment; the jump waits for it.
            yield return new WaitForSeconds(3f);
            var jump = 0f;
            yield return PadPress(false, PadKey.East);
            yield return Rise(player, 1.2f, r => jump = r);
            Log($"with no capture: hotbar {selected} -> {moved}, power uses {uses} -> {used}, camera {camera0:0.###} -> "
                + $"{camera1:0.###} -> {camera2:0.###}, map {map0:0.####} -> {map1:0.####} -> {map2:0.####}, jump {jump:0.00} m");
            Check(moved != selected, $"with no capture up the D-pad moves the hotbar again: slot {selected} -> {moved}");
            Check(used > uses, $"and D-pad down starts the forsaken power: uses {uses} -> {used}");
            Check(cameraMoved, $"and L2 + D-pad up, down zooms the camera: {camera0:0.###} -> {camera1:0.###} -> {camera2:0.###}");
            Check(mapMoved, $"and L2 + D-pad left, right zooms the minimap: {map0:0.####} -> {map1:0.####} -> {map2:0.####}");
            Check(jump > 0.3f, $"and circle jumps: {jump:0.00} m");

            yield return CapturePadDone(player, map, zoom, camera, distance);
        }

        /// <summary>One D-pad press during a capture, checked against what it should do to the rectangle.</summary>
        private static IEnumerator PadStep(bool modifier, PadKey button, float turn, float width, float depth, string what)
        {
            var yaw = WorldCapture.Yaw;
            var w = WorldCapture.Width;
            var d = WorldCapture.Depth;
            yield return PadPress(modifier, button);
            var turned = Mathf.DeltaAngle(yaw, WorldCapture.Yaw);
            var ok = Mathf.Abs(turned - turn) < 0.01f
                && Mathf.Abs(WorldCapture.Width - w - width) < 0.01f
                && Mathf.Abs(WorldCapture.Depth - d - depth) < 0.01f;
            Check(ok, $"{what}: turn {yaw:0.#} -> {WorldCapture.Yaw:0.#}, {w} x {d} -> {WorldCapture.Width} x {WorldCapture.Depth}");
        }

        /// <summary>Everything the pad capture changed goes back: the controller, the power, the zoom, the hammer.</summary>
        private static IEnumerator CapturePadDone(Player player, Minimap map, float zoom, GameCamera camera, float distance)
        {
            WorldCapture.Cancel();
            if (ModUi.Open)
            {
                EditorSession.Close();
            }

            yield return WorldPadOff();
            PadReader.Fake = null;
            _pad = null;
            player.SetGuardianPower("");
            player.m_guardianPowerCooldown = 0f;
            if (map != null && !float.IsNaN(zoom))
            {
                map.SmallZoom = zoom;
            }

            if (camera != null && !float.IsNaN(distance))
            {
                camera.m_distance = distance;
            }

            ClearInventoryExceptHammer(player);
            yield return EquipHammer(player);
        }

        /// <summary>
        /// Times the glow on a few hundred floors: every piece set again (what the plan first asked
        /// for) against only what changed (what ships). The numbers go into the handoff.
        /// </summary>
        private static IEnumerator CaptureGlowCost(Player player)
        {
            var floor = PiecePrefab("wood_floor");
            if (floor == null)
            {
                Check(false, "the wood_floor prefab exists");
                yield break;
            }

            var middle = player.transform.position;
            var before = new HashSet<Piece>(PiecesAround(player, middle));
            for (var x = 0; x < 20; x++)
            {
                for (var z = 0; z < 20; z++)
                {
                    var spot = middle + new Vector3((x * 1.5f) - 14.25f, 0f, (z * 1.5f) - 14.25f);
                    spot.y = ZoneSystem.instance.GetGroundHeight(spot);
                    player.PlacePiece(floor, spot, Quaternion.identity, false);
                }
            }

            yield return new WaitForSeconds(1f);
            var floors = PiecesAround(player, middle).Where(p => !before.Contains(p)).ToList();
            Check(floors.Count == 400, $"placed 400 floors to time the glow: {floors.Count}");
            var first100 = floors.Take(100).ToList();
            var fewer = floors.Take(floors.Count - 40).ToList();
            var man = MaterialMan.instance;

            double Timed(System.Action action)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                action();
                man.Update();
                return watch.Elapsed.TotalMilliseconds;
            }

            var firstTime = Timed(() => CaptureTint.Show(floors, null, true));
            var lit = floors.Count(p => HasColor(p.gameObject, CaptureTint.Inside));
            CaptureTint.Clear();
            man.Update();
            var full100 = Timed(() => CaptureTint.Show(first100, null, true));
            var full100Again = Timed(() => CaptureTint.Show(first100, null, true));
            var full400 = Timed(() => CaptureTint.Show(floors, null, true));
            var full400Again = Timed(() => CaptureTint.Show(floors, null, true));
            var still = Timed(() => CaptureTint.Show(floors, null));
            var stillChanged = CaptureTint.LastChanged;
            var moved = Timed(() => CaptureTint.Show(fewer, null));
            var movedChanged = CaptureTint.LastChanged;
            var clear = Timed(CaptureTint.Clear);
            var clean = floors.Count(p => !HasAnyColor(p.gameObject));

            // Sorting the loaded pieces round a rectangle, the other half of a refresh.
            var taken = new List<Piece>();
            var edge = new List<Piece>();
            var sortWatch = System.Diagnostics.Stopwatch.StartNew();
            WorldCapture.Collect(taken, edge, out _);
            var sort = sortWatch.Elapsed.TotalMilliseconds;
            Log($"sorting {Piece.s_allPieces.Count} loaded pieces round a {WorldCapture.Width} x {WorldCapture.Depth} m "
                + $"rectangle: {sort:0.00} ms ({taken.Count} inside, {edge.Count} across the edge)");

            Log($"glow cost: first 400 {firstTime:0.00} ms (lit {lit}); set every piece again: 100 {full100Again:0.00} ms, "
                + $"400 {full400Again:0.00} ms; only what changed: nothing moved {still:0.00} ms ({stillChanged} touched), "
                + $"40 left {moved:0.00} ms ({movedChanged} touched); clear 360 {clear:0.00} ms (clean {clean})");
            Check(lit == floors.Count && clean == floors.Count,
                $"all {floors.Count} floors lit and all clean after: lit {lit}, clean {clean}");
            Check(stillChanged == 0 && movedChanged == 40,
                $"a refresh touches only what changed: {stillChanged} when nothing moved, {movedChanged} when 40 left");
        }

        /// <summary>
        /// Name for name and spot for spot against the kit file. Both sets are measured from their
        /// own middle, so this is about the shape that came back, not about where the origin went.
        /// </summary>
        private static void CompareToKit(Blueprint kit, BlueprintDocument document, Quaternion turn)
        {
            var want = kit.Pieces.Select(p => turn * p.Position).ToList();
            var got = document.Pieces.Select(p => p.Position).ToList();
            var wantMid = Middle(want);
            var gotMid = Middle(got);
            var taken = new bool[got.Count];
            var missing = new List<string>();
            var worst = 0f;
            var worstTurn = 0f;

            for (var i = 0; i < kit.Pieces.Count; i++)
            {
                var wanted = want[i] - wantMid;
                var best = -1;
                var bestOff = float.MaxValue;
                for (var j = 0; j < got.Count; j++)
                {
                    if (taken[j] || document.Pieces[j].PrefabName != kit.Pieces[i].PrefabName)
                    {
                        continue;
                    }

                    var off = Vector3.Distance(got[j] - gotMid, wanted);
                    if (off < bestOff)
                    {
                        bestOff = off;
                        best = j;
                    }
                }

                if (best < 0)
                {
                    missing.Add(kit.Pieces[i].PrefabName);
                    continue;
                }

                taken[best] = true;
                worst = Mathf.Max(worst, bestOff);
                worstTurn = Mathf.Max(
                    worstTurn, Quaternion.Angle(turn * kit.Pieces[i].Rotation, document.Pieces[best].Rotation));
            }

            Check(missing.Count == 0, missing.Count == 0
                ? $"every one of the {kit.Pieces.Count} pieces came back by name"
                : "pieces that did not come back: " + string.Join(", ", missing));
            Check(worst < 0.01f, $"and in the same spot, worst {worst:0.######} m off");
            Check(worstTurn < 0.5f, $"and turned the same way as the file, worst {worstTurn:0.###} degrees off");
        }

        /// <summary>A UI rect in another transform's local space, from its four corners.</summary>
        private static Rect RectIn(Transform space, RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var corner in corners)
            {
                var local = (Vector2)space.InverseTransformPoint(corner);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }

            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        /// <summary>True when any renderer of a piece carries a colour in its property block.</summary>
        private static bool HasAnyColor(GameObject go)
        {
            if (go == null)
            {
                return false;
            }

            var block = new MaterialPropertyBlock();
            foreach (var renderer in go.GetComponentsInChildren<Renderer>())
            {
                renderer.GetPropertyBlock(block);
                if (!block.isEmpty && (block.HasColor(ShaderProps._Color) || block.HasColor(ShaderProps._EmissionColor)))
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector3 Middle(List<Vector3> points)
        {
            var sum = Vector3.zero;
            foreach (var point in points)
            {
                sum += point;
            }

            return points.Count > 0 ? sum / points.Count : Vector3.zero;
        }

        /// <summary>A piece of another build tool, which a capture has to leave out. Terrain ops skipped.</summary>
        private static Piece OtherToolPrefab()
        {
            var db = ObjectDB.instance;
            if (db == null)
            {
                return null;
            }

            foreach (var item in db.m_items)
            {
                var drop = item != null ? item.GetComponent<ItemDrop>() : null;
                var table = drop != null ? drop.m_itemData.m_shared.m_buildPieces : null;
                if (table == null || table == PieceCatalog.Table)
                {
                    continue;
                }

                foreach (var prefab in table.m_pieces)
                {
                    if (prefab == null || PieceCatalog.Find(prefab.name) != null)
                    {
                        continue;
                    }

                    if (prefab.GetComponent<ZNetView>() == null
                        || prefab.GetComponent<TerrainOp>() != null
                        || prefab.GetComponent<TerrainModifier>() != null)
                    {
                        continue;
                    }

                    var piece = prefab.GetComponent<Piece>();
                    if (piece != null)
                    {
                        return piece;
                    }
                }
            }

            return null;
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

            // The test character is saved on quit, so its hammer wears down run after run. At 0 the
            // game will not equip it, and blueprint mode quietly never starts.
            hammer.m_durability = hammer.GetMaxDurability();
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
