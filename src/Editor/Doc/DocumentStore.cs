using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ValheimTomrer.Blueprints;

namespace ValheimTomrer.Editor.Doc
{
    /// <summary>One line of the editor's open list: a kit inside the DLL, or a file on disk.</summary>
    internal sealed class BlueprintEntry
    {
        /// <summary>The name to show, from <c>#Name:</c> or the file name.</summary>
        public string Name;

        /// <summary>Full path, or null for a kit.</summary>
        public string Path;

        /// <summary>Resource name inside the DLL, or null for a file.</summary>
        public string Resource;

        public bool IsKit;

        /// <summary>Can only be saved under a new name.</summary>
        public bool ReadOnly;

        public int Pieces;

        /// <summary>When the file last changed. Meaningless for a kit.</summary>
        public DateTime Modified;

        /// <summary>Why the file cannot be opened, or null when it is fine.</summary>
        public string Error;
    }

    /// <summary>
    /// Opening, saving, renaming and deleting blueprints. Kits ship inside the DLL and are read-only;
    /// the player's own files live under <see cref="BlueprintLibrary.UserFolder"/>. Every write goes
    /// to a temp file first, then a rename, so a crash can never leave half a file behind.
    /// </summary>
    internal static class DocumentStore
    {
        private const string KitResourcePrefix = "ValheimTomrer.Kits.";

        private static readonly UTF8Encoding NoBom = new UTF8Encoding(false);

        /// <summary>The kits built into the DLL.</summary>
        public static List<BlueprintEntry> ListKits()
        {
            var entries = new List<BlueprintEntry>();
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var resource in assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(KitResourcePrefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var fallback = Path.GetFileNameWithoutExtension(resource.Substring(KitResourcePrefix.Length));
                var entry = new BlueprintEntry { Resource = resource, IsKit = true, ReadOnly = true, Name = fallback };
                try
                {
                    var lines = ReadResource(resource);
                    var blueprint = BlueprintFormat.ParseBlueprint(fallback, lines);
                    entry.Name = blueprint.Name;
                    entry.Pieces = blueprint.Pieces.Count;
                }
                catch (Exception e)
                {
                    entry.Error = e.Message;
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>Every blueprint file the player has, including the ones in subfolders.</summary>
        public static List<BlueprintEntry> ListUserFiles()
        {
            var entries = new List<BlueprintEntry>();
            var folder = BlueprintLibrary.UserFolder;
            string[] files;
            try
            {
                Directory.CreateDirectory(folder);
                files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ValheimTomrerPlugin.Log.LogWarning($"cannot read blueprint folder {folder}: {e.Message}");
                return entries;
            }

            foreach (var file in files
                .Where(IsBlueprintFile)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fallback = Path.GetFileNameWithoutExtension(file);
                var entry = new BlueprintEntry { Path = file, Name = fallback };
                try
                {
                    entry.Modified = new FileInfo(file).LastWriteTime;
                    var blueprint = Read(file);
                    entry.Name = blueprint.Name;
                    entry.Pieces = blueprint.Pieces.Count;
                    entry.ReadOnly = blueprint.ReadOnly;
                }
                catch (Exception e)
                {
                    entry.Error = e.Message;
                    entry.ReadOnly = true;
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>An empty blueprint that is not on disk yet.</summary>
        public static BlueprintDocument New(string name = "New blueprint")
        {
            return BlueprintDocument.New(name);
        }

        /// <summary>Opens a file for editing.</summary>
        public static bool Open(string path, out BlueprintDocument document, out string error)
        {
            document = null;
            try
            {
                document = BlueprintDocument.FromBlueprint(Read(path));
                error = null;
                return true;
            }
            catch (Exception e)
            {
                error = $"cannot open {Path.GetFileName(path)}: {e.Message}";
                return false;
            }
        }

        /// <summary>Opens a kit from inside the DLL. It can only be saved under a new name.</summary>
        public static bool OpenKit(BlueprintEntry kit, out BlueprintDocument document, out string error)
        {
            document = null;
            if (kit == null || kit.Resource == null)
            {
                error = "not a blueprint that ships with the mod";
                return false;
            }

            try
            {
                var fallback = Path.GetFileNameWithoutExtension(kit.Resource.Substring(KitResourcePrefix.Length));
                var blueprint = BlueprintFormat.ParseBlueprint(fallback, ReadResource(kit.Resource));
                blueprint.ReadOnly = true;
                document = BlueprintDocument.FromBlueprint(blueprint);
                error = null;
                return true;
            }
            catch (Exception e)
            {
                error = $"cannot open {kit.Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>Writes the document back over the file it came from.</summary>
        public static bool Save(BlueprintDocument document, out string error)
        {
            if (document == null)
            {
                error = "nothing open";
                return false;
            }

            if (document.ReadOnly || string.IsNullOrEmpty(document.SourcePath))
            {
                error = "this blueprint can only be saved under a new name";
                return false;
            }

            if (!WriteFile(document.SourcePath, BlueprintFormat.Write(document.ToBlueprint()), out error))
            {
                return false;
            }

            document.MarkSaved();
            BlueprintLibrary.Reload();
            return true;
        }

        /// <summary>
        /// Writes the document under a new name in the player's folder. The name becomes the
        /// blueprint's <c>#Name:</c> and, slugged, its file name.
        /// </summary>
        public static bool SaveAs(BlueprintDocument document, string name, bool overwrite, out string error)
        {
            if (document == null)
            {
                error = "nothing open";
                return false;
            }

            var wanted = (name ?? "").Trim();
            if (wanted.Length == 0)
            {
                error = "give the blueprint a name";
                return false;
            }

            var path = Path.Combine(BlueprintLibrary.UserFolder, BlueprintFormat.FileNameFor(wanted));
            if (!overwrite && File.Exists(path))
            {
                error = $"{Path.GetFileName(path)} already exists";
                return false;
            }

            var blueprint = document.ToBlueprint();
            blueprint.Name = wanted;
            if (!WriteFile(path, BlueprintFormat.Write(blueprint), out error))
            {
                return false;
            }

            document.Adopt(wanted, path, false);
            BlueprintLibrary.Reload();
            return true;
        }

        /// <summary>Gives a file a new name, which changes both <c>#Name:</c> and the file name.</summary>
        public static bool Rename(string path, string name, out string newPath, out string error)
        {
            newPath = null;
            var wanted = (name ?? "").Trim();
            if (wanted.Length == 0)
            {
                error = "give the blueprint a name";
                return false;
            }

            Blueprint blueprint;
            try
            {
                blueprint = Read(path);
            }
            catch (Exception e)
            {
                error = $"cannot open {Path.GetFileName(path)}: {e.Message}";
                return false;
            }

            if (blueprint.ReadOnly)
            {
                error = "this file has parts the editor cannot write back";
                return false;
            }

            var target = Path.Combine(
                Path.GetDirectoryName(path) ?? BlueprintLibrary.UserFolder,
                BlueprintFormat.FileNameFor(wanted));
            var samePlace = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
            if (!samePlace && File.Exists(target))
            {
                error = $"{Path.GetFileName(target)} already exists";
                return false;
            }

            blueprint.Name = wanted;
            if (!WriteFile(target, BlueprintFormat.Write(blueprint), out error))
            {
                return false;
            }

            if (!samePlace && !Remove(path, out error))
            {
                return false;
            }

            newPath = target;
            BlueprintLibrary.Reload();
            return true;
        }

        public static bool Delete(string path, out string error)
        {
            if (!Remove(path, out error))
            {
                return false;
            }

            BlueprintLibrary.Reload();
            return true;
        }

        /// <summary>Copies a file next to itself, under a free name.</summary>
        public static bool Duplicate(string path, out string newPath, out string error)
        {
            newPath = null;
            Blueprint blueprint;
            try
            {
                blueprint = Read(path);
            }
            catch (Exception e)
            {
                error = $"cannot open {Path.GetFileName(path)}: {e.Message}";
                return false;
            }

            var folder = Path.GetDirectoryName(path) ?? BlueprintLibrary.UserFolder;
            var name = FreeName(folder, blueprint.Name + " copy", out var target);
            blueprint.Name = name;
            blueprint.HasSections = false;
            if (!WriteFile(target, BlueprintFormat.Write(blueprint), out error))
            {
                return false;
            }

            newPath = target;
            BlueprintLibrary.Reload();
            return true;
        }

        // ---------- shared ----------

        private static bool IsBlueprintFile(string path)
        {
            return path.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase);
        }

        private static Blueprint Read(string path)
        {
            var fallback = Path.GetFileNameWithoutExtension(path);
            var lines = File.ReadAllLines(path);
            var vbuild = path.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase);
            var blueprint = vbuild
                ? BlueprintFormat.ParseVBuild(fallback, lines)
                : BlueprintFormat.ParseBlueprint(fallback, lines);
            blueprint.SourcePath = path;
            blueprint.ReadOnly = vbuild || blueprint.HasSections;
            return blueprint;
        }

        private static IEnumerable<string> ReadResource(string resource)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream))
            {
                var lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }

                return lines;
            }
        }

        /// <summary>"Camp hut" -> "Camp hut 2" when the file is taken. Gives back the free path too.</summary>
        private static string FreeName(string folder, string wanted, out string path)
        {
            var name = wanted;
            path = Path.Combine(folder, BlueprintFormat.FileNameFor(name));
            for (var n = 2; File.Exists(path) && n < 1000; n++)
            {
                name = wanted + " " + n.ToString(CultureInfo.InvariantCulture);
                path = Path.Combine(folder, BlueprintFormat.FileNameFor(name));
            }

            return name;
        }

        /// <summary>Temp file, then a rename: the real file is never half written.</summary>
        private static bool WriteFile(string path, string text, out string error)
        {
            var temp = path + ".tmp";
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                File.WriteAllText(temp, text, NoBom);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(temp, path);
                error = null;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                error = $"cannot write {Path.GetFileName(path)}: {e.Message}";
                TryDelete(temp);
                return false;
            }
        }

        private static bool Remove(string path, out string error)
        {
            try
            {
                File.Delete(path);
                error = null;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                error = $"cannot delete {Path.GetFileName(path)}: {e.Message}";
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Nothing to do: the temp file is already the fallback.
            }
        }
    }
}
