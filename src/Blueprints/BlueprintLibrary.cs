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

        public static string UserFolder => Path.Combine(Paths.ConfigPath, "ValheimTomrer", "blueprints");

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
                    TryAdd(result, resource, () => Parse(name, resource, ReadLines(reader)));
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
                TryAdd(result, file, () => Parse(Path.GetFileNameWithoutExtension(file), file, File.ReadAllLines(file)));
            }
        }

        private static Blueprint Parse(string name, string source, IEnumerable<string> lines)
        {
            return source.EndsWith(".vbuild", StringComparison.OrdinalIgnoreCase)
                ? BlueprintFormat.ParseVBuild(name, lines)
                : BlueprintFormat.ParseBlueprint(name, lines);
        }

        private static void TryAdd(List<Blueprint> result, string source, Func<Blueprint> load)
        {
            try
            {
                result.Add(load());
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
