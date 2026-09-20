using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// All blueprints the mod knows: kits shipped inside the DLL, then the player's own files from
    /// <c>BepInEx/config/ValheimTomrer/blueprints</c>.
    /// </summary>
    internal static class BlueprintLibrary
    {
        private const string KitResourcePrefix = "ValheimTomrer.Kits.";

        private static string _userFolder;

        /// <summary>
        /// Where the player's own blueprints live. Settable so a test can point it at a temp folder.
        /// </summary>
        public static string UserFolder
        {
            get => _userFolder ?? Path.Combine(Paths.ConfigPath, "ValheimTomrer", "blueprints");
            set => _userFolder = value;
        }

        public static List<Blueprint> All { get; private set; } = new List<Blueprint>();

        public static void Reload()
        {
            var result = new List<Blueprint>();
            LoadKits(result);
            LoadUserFiles(result);
            All = result;
            ValheimTomrerPlugin.Log.LogInfo($"blueprints loaded: {result.Count} ({string.Join(", ", result.Select(b => b.Name))})");
        }

        private static void LoadKits(List<Blueprint> result)
        {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(KitResourcePrefix)).OrderBy(n => n))
            {
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream))
                {
                    var name = Path.GetFileNameWithoutExtension(resource.Substring(KitResourcePrefix.Length));
                    TryAdd(result, resource, () => AsKit(Parse(name, resource, ReadLines(reader))));
                }
            }
        }

        private static void LoadUserFiles(List<Blueprint> result)
        {
            try
            {
                Directory.CreateDirectory(UserFolder);
            }
            catch (Exception e)
            {
                ValheimTomrerPlugin.Log.LogWarning($"cannot create blueprint folder {UserFolder}: {e.Message}");
                return;
            }

            var files = Directory.GetFiles(UserFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".blueprint", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                TryAdd(result, file, () => FromFile(Parse(Path.GetFileNameWithoutExtension(file), file, File.ReadAllLines(file)), file));
            }
        }

        private static Blueprint Parse(string name, string source, IEnumerable<string> lines)
        {
            return source.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase)
                ? BlueprintFormat.ParseVBuild(name, lines)
                : BlueprintFormat.ParseBlueprint(name, lines);
        }

        /// <summary>A kit lives inside the DLL, so it can only ever be saved under a new name.</summary>
        private static Blueprint AsKit(Blueprint blueprint)
        {
            blueprint.ReadOnly = true;
            return blueprint;
        }

        /// <summary>A .vbuild or a file with sections we cannot write back is read-only too.</summary>
        private static Blueprint FromFile(Blueprint blueprint, string path)
        {
            blueprint.SourcePath = path;
            blueprint.ReadOnly = blueprint.HasSections
                || path.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase);
            return blueprint;
        }

        private static void TryAdd(List<Blueprint> result, string source, Func<Blueprint> load)
        {
            try
            {
                var blueprint = load();

                // The build tool has nothing to build from an empty blueprint, and resolving one
                // has no piece to take an icon from. The editor still opens the file.
                if (blueprint.Pieces.Count == 0)
                {
                    return;
                }

                result.Add(blueprint);
            }
            catch (Exception e) when (e is FormatException || e is IOException || e is OverflowException)
            {
                ValheimTomrerPlugin.Log.LogWarning($"skipped blueprint {source}: {e.Message}");
            }
        }

        private static IEnumerable<string> ReadLines(TextReader reader)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }
    }
}
