using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace ValheimTomrer.Blueprints.Sites
{
    /// <summary>
    /// The unfinished builds of the loaded world, one <c>.blueprint</c> file each, in
    /// <c>BepInEx/config/ValheimTomrer/sites/&lt;world&gt;_&lt;uid&gt;/</c>. Nothing goes into the world save.
    ///
    /// A file is the blueprint as it was at the first click, plus two headers the other mods keep as
    /// unknown ones (<see cref="Blueprint.ExtraHeaders"/>):
    ///   <c>#Site:x;y;z;yaw</c>   the root pose: world position, turn in degrees
    ///   <c>#SiteSource:name</c>  the blueprint it was copied from, for the reader only
    /// It is written on <see cref="Add"/> and deleted when the build is finished. What is built is
    /// never written: <see cref="SiteTracker"/> reads it from the world.
    /// </summary>
    internal static class SiteStore
    {
        public const string SiteHeader = "#Site:";
        public const string SourceHeader = "#SiteSource:";

        private static readonly List<Site> Sites = new List<Site>();
        private static readonly UTF8Encoding NoBom = new UTF8Encoding(false);

        /// <summary>Where the per-world folders live. Settable so a test never touches the player's own.</summary>
        public static string RootOverride { get; set; }

        public static string Root => RootOverride ?? Path.Combine(Paths.ConfigPath, "ValheimTomrer", "sites");

        /// <summary>The loaded world's folder, or null when no world is loaded.</summary>
        public static string WorldFolder { get; private set; }

        /// <summary>Every unfinished build of the loaded world.</summary>
        public static IReadOnlyList<Site> All => Sites;

        /// <summary>The folder for a world: its name cleaned like a blueprint file name, then its uid.</summary>
        public static string FolderFor(World world)
        {
            return Path.Combine(Root, Slug(world.m_name) + "_" + world.m_uid.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>"Camp hut" -> "camp-hut", the way <see cref="BlueprintFormat.FileNameFor"/> cleans a name.</summary>
        public static string Slug(string name)
        {
            var file = BlueprintFormat.FileNameFor(name);
            return file.Substring(0, file.Length - ".blueprint".Length);
        }

        /// <summary>
        /// Reads every site of the loaded world from its folder. What was loaded before is dropped
        /// first, ghosts included. Called by <see cref="SiteTracker"/> when a world starts.
        /// </summary>
        public static void Load()
        {
            Clear();
            var world = ZNet.World;
            if (world == null || ZNetScene.instance == null)
            {
                return;
            }

            WorldFolder = FolderFor(world);
            string[] files;
            try
            {
                files = Directory.Exists(WorldFolder) ? Directory.GetFiles(WorldFolder, "*.blueprint") : new string[0];
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ValheimTomrerPlugin.Log.LogWarning($"cannot read the sites folder {WorldFolder}: {e.Message}");
                return;
            }

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var site = Read(file, out var error);
                    if (site == null)
                    {
                        ValheimTomrerPlugin.Log.LogWarning($"skipped site {Path.GetFileName(file)}: {error}");
                        continue;
                    }

                    Sites.Add(site);
                }
                catch (Exception e) when (e is FormatException || e is IOException || e is OverflowException || e is UnauthorizedAccessException)
                {
                    ValheimTomrerPlugin.Log.LogWarning($"skipped site {Path.GetFileName(file)}: {e.Message}");
                }
            }

            ValheimTomrerPlugin.Log.LogInfo($"sites loaded for world {world.m_name}: {Sites.Count}"
                + (Sites.Count > 0 ? " (" + string.Join(", ", Sites.Select(s => s.Name)) + ")" : ""));
        }

        /// <summary>Forgets every site in memory and destroys their ghosts. The files stay.</summary>
        public static void Clear()
        {
            foreach (var site in Sites)
            {
                site.DropGhost();
            }

            Sites.Clear();
            WorldFolder = null;
        }

        /// <summary>
        /// Keeps a new unfinished build: a file named after the blueprint and the time, then the list.
        /// False when there is no world or the file cannot be written.
        /// </summary>
        public static bool Add(Site site)
        {
            var folder = WorldFolder ?? (ZNet.World != null ? FolderFor(ZNet.World) : null);
            if (site == null || folder == null)
            {
                return false;
            }

            site.Path = FreePath(folder, site.Name);
            if (!Save(site))
            {
                return false;
            }

            Sites.Add(site);
            ValheimTomrerPlugin.Log.LogInfo($"site kept: {site.Name} at {site.RootPosition}, turned {site.RootYaw:0.#}, in {site.Path}");
            return true;
        }

        /// <summary>Writes the site's file: temp file, then a rename, so it is never half written.</summary>
        public static bool Save(Site site)
        {
            SetHeaders(site);
            var temp = site.Path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(site.Path));
                File.WriteAllText(temp, BlueprintFormat.Write(site.Blueprint), NoBom);
                if (File.Exists(site.Path))
                {
                    File.Delete(site.Path);
                }

                File.Move(temp, site.Path);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                ValheimTomrerPlugin.Log.LogWarning($"cannot write site {site.Path}: {e.Message}");
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Nothing more to do.
                }

                return false;
            }
        }

        /// <summary>Forgets a site: its ghost, its place in the list, and its file.</summary>
        public static void Delete(Site site)
        {
            if (site == null)
            {
                return;
            }

            site.DropGhost();
            Sites.Remove(site);
            try
            {
                if (site.Path != null && File.Exists(site.Path))
                {
                    File.Delete(site.Path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ValheimTomrerPlugin.Log.LogWarning($"cannot delete site {site.Path}: {e.Message}");
            }
        }

        /// <summary>True for the two headers this store owns.</summary>
        public static bool IsSiteHeader(string line)
        {
            return line.StartsWith(SiteHeader, StringComparison.OrdinalIgnoreCase)
                || line.StartsWith(SourceHeader, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"#Site:x;y;z;yaw" to a pose. False when the line is not one.</summary>
        public static bool TryReadPose(string line, out Vector3 position, out float yaw)
        {
            position = Vector3.zero;
            yaw = 0f;
            if (line == null || !line.StartsWith(SiteHeader, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fields = line.Substring(SiteHeader.Length).Split(';');
            if (fields.Length < 4
                || !Number(fields[0], out var x) || !Number(fields[1], out var y)
                || !Number(fields[2], out var z) || !Number(fields[3], out yaw))
            {
                return false;
            }

            position = new Vector3(x, y, z);
            return true;
        }

        /// <summary>The pose as the header writes it: 4 decimals, dot, no exponent.</summary>
        public static string PoseLine(Vector3 position, float yaw)
        {
            const int decimals = BlueprintFormat.PositionDecimals;
            return SiteHeader
                + BlueprintFormat.FormatNumber(position.x, decimals) + ";"
                + BlueprintFormat.FormatNumber(position.y, decimals) + ";"
                + BlueprintFormat.FormatNumber(position.z, decimals) + ";"
                + BlueprintFormat.FormatNumber(yaw, decimals);
        }

        private static Site Read(string path, out string error)
        {
            var blueprint = BlueprintFormat.ParseBlueprint(Path.GetFileNameWithoutExtension(path), File.ReadAllLines(path));
            Vector3 position = default;
            var yaw = 0f;
            var havePose = false;
            var source = "";
            foreach (var header in blueprint.ExtraHeaders)
            {
                if (TryReadPose(header, out var p, out var y))
                {
                    position = p;
                    yaw = y;
                    havePose = true;
                }
                else if (header.StartsWith(SourceHeader, StringComparison.OrdinalIgnoreCase))
                {
                    source = header.Substring(SourceHeader.Length).Trim();
                }
            }

            if (!havePose)
            {
                error = "no #Site: line";
                return null;
            }

            var site = Site.From(blueprint, position, yaw, source, out error);
            if (site != null)
            {
                site.Path = path;
            }

            return site;
        }

        /// <summary>The two headers, in place of any the blueprint already had.</summary>
        private static void SetHeaders(Site site)
        {
            var headers = site.Blueprint.ExtraHeaders;
            headers.RemoveAll(IsSiteHeader);
            headers.Add(PoseLine(site.RootPosition, site.RootYaw));
            if (!string.IsNullOrEmpty(site.Source))
            {
                headers.Add(SourceHeader + site.Source);
            }
        }

        /// <summary>"workshop_20260921-143005.blueprint", with "_2" and on when two land in the same second.</summary>
        private static string FreePath(string folder, string name)
        {
            var stem = Slug(name) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var path = Path.Combine(folder, stem + ".blueprint");
            for (var n = 2; (File.Exists(path) || Sites.Any(s => s.Path == path)) && n < 1000; n++)
            {
                path = Path.Combine(folder, stem + "_" + n.ToString(CultureInfo.InvariantCulture) + ".blueprint");
            }

            return path;
        }

        private static bool Number(string text, out float value)
        {
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
