using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimTomrer.Blueprints.Sites
{
    /// <summary>
    /// One unfinished build: a copy of the blueprint as it was at the first click, where it stands,
    /// and which parts stand in the world now. The file (<see cref="SiteStore"/>) holds the blueprint
    /// and the pose only. <see cref="Built"/> is never saved: <see cref="SiteTracker"/> reads it from
    /// the world, so the file and the world cannot get out of step.
    /// </summary>
    internal sealed class Site
    {
        /// <summary>The box is the parts' pivots grown by this much, enough for a 4 m beam's half.</summary>
        public const float BoxMargin = 2f;

        private Site(Blueprint blueprint, ResolvedBlueprint resolved, Vector3 rootPosition, float rootYaw, string source)
        {
            Blueprint = blueprint;
            Resolved = resolved;
            RootPosition = rootPosition;
            RootYaw = rootYaw;
            Source = source;
            Built = new bool[resolved.Parts.Count];
            WorldBox = MeasureBox();
            Items = resolved.TotalCost.Select(c => c.m_resItem.m_itemData.m_shared.m_name).ToList();
            Kinds = resolved.Parts.Select(p => p.Piece).Distinct().ToList();
        }

        /// <summary>The site's own copy of the blueprint, with its <c>#Site:</c> headers.</summary>
        public Blueprint Blueprint { get; }

        /// <summary><see cref="Blueprint"/> matched to the live prefabs. Part indexes are the file's piece order.</summary>
        public ResolvedBlueprint Resolved { get; }

        /// <summary>Where the blueprint's origin stands in the world.</summary>
        public Vector3 RootPosition { get; }

        /// <summary>The turn around y, in degrees.</summary>
        public float RootYaw { get; }

        public Quaternion RootRotation => Quaternion.Euler(0f, RootYaw, 0f);

        /// <summary>The blueprint it was copied from: its file name, or the kit's name. Only for the reader.</summary>
        public string Source { get; }

        /// <summary>The file, set by <see cref="SiteStore.Add"/> or <see cref="SiteStore.Load"/>.</summary>
        public string Path { get; internal set; }

        /// <summary>One flag per part: it stands in the world now. Filled by <see cref="SiteTracker"/>.</summary>
        public bool[] Built { get; }

        public int BuiltCount { get; internal set; }

        public int Total => Built.Length;

        /// <summary>Goes up each time <see cref="Built"/> changes. Part of the key that decides when to plan again.</summary>
        public int BuiltVersion { get; internal set; }

        /// <summary>Box around every part in the world, turned with the site: pivots grown by <see cref="BoxMargin"/>.</summary>
        public Bounds WorldBox { get; }

        public string Name => Blueprint.Name;

        // ---------- kept by SiteTracker ----------

        /// <summary>The parts the next click would build, by index. Null until planned once.</summary>
        public bool[] Ready { get; internal set; }

        public int ReadyCount { get; internal set; }

        /// <summary>The same parts in the order the click builds them: bottom to top. Null until planned once.</summary>
        public IReadOnlyList<int> ReadyOrder { get; internal set; }

        /// <summary>What the last plan was made from. The plan runs again only when this changes.</summary>
        internal string ReadyKey { get; set; }

        /// <summary>The see-through copy in the world, or null. It dies with the scene on a world change.</summary>
        public BlueprintPreview Ghost { get; internal set; }

        /// <summary>The steps still to run to build <see cref="Ghost"/>, or null once it stands.</summary>
        internal IEnumerator GhostFill { get; set; }

        /// <summary>Item names the blueprint needs, for the plan's key.</summary>
        internal List<string> Items { get; }

        /// <summary>Each kind of piece once, for the free-build keys in the plan's key.</summary>
        internal List<Piece> Kinds { get; }

        /// <summary>
        /// A new site from the blueprint in the hammer, at the preview's pose. The blueprint is copied,
        /// so editing or deleting the source never touches a build in progress. Null when the copy
        /// no longer resolves (it always should: the source just did).
        /// </summary>
        public static Site Start(ResolvedBlueprint from, Vector3 rootPosition, float rootYaw)
        {
            var source = from.Blueprint;
            var copy = new Blueprint
            {
                Name = source.Name,
                Description = source.Description,
                IconPrefab = source.IconPrefab,
            };

            foreach (var piece in source.Pieces)
            {
                copy.Pieces.Add(new BlueprintPiece
                {
                    PrefabName = piece.PrefabName,
                    Category = piece.Category,
                    Position = piece.Position,
                    Rotation = piece.Rotation,
                    Rest = piece.Rest,
                });
            }

            copy.ExtraHeaders.AddRange(source.ExtraHeaders.Where(h => !SiteStore.IsSiteHeader(h)));
            var sourceName = source.SourcePath != null
                ? System.IO.Path.GetFileNameWithoutExtension(source.SourcePath)
                : SiteStore.Slug(source.Name);
            return From(copy, rootPosition, rootYaw, sourceName, out _);
        }

        /// <summary>A site from a blueprint and its pose, as <see cref="SiteStore"/> reads it back.</summary>
        internal static Site From(Blueprint blueprint, Vector3 rootPosition, float rootYaw, string source, out string error)
        {
            if (!ResolvedBlueprint.TryResolve(blueprint, out var resolved, out error))
            {
                return null;
            }

            if (resolved.Parts.Count == 0)
            {
                error = $"{blueprint.Name}: no pieces";
                return null;
            }

            return new Site(blueprint, resolved, rootPosition, rootYaw, source);
        }

        /// <summary>Where a part stands in the world.</summary>
        public Vector3 WorldPosition(int index)
        {
            return RootPosition + (RootRotation * Resolved.Parts[index].Source.Position);
        }

        public Quaternion WorldRotation(int index)
        {
            return RootRotation * Resolved.Parts[index].Source.Rotation;
        }

        /// <summary>Destroys the ghost, if there is one. The next refresh builds a new one when needed.</summary>
        public void DropGhost()
        {
            Ghost?.Destroy();
            Ghost = null;
            GhostFill = null;
        }

        private Bounds MeasureBox()
        {
            var box = new Bounds(WorldPosition(0), Vector3.zero);
            for (var i = 1; i < Resolved.Parts.Count; i++)
            {
                box.Encapsulate(WorldPosition(i));
            }

            box.Expand(BoxMargin * 2f);
            return box;
        }
    }
}
