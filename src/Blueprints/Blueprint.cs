using System.Collections.Generic;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// One piece of a blueprint. Position and rotation are relative to the blueprint origin,
    /// which sits on the ground where the player aims.
    /// </summary>
    internal sealed class BlueprintPiece
    {
        public string PrefabName;

        /// <summary>Field 1 of the line, the build category other mods write there. Kept as read.</summary>
        public string Category = "";

        public Vector3 Position;
        public Quaternion Rotation;

        /// <summary>
        /// Fields 9 and later as written (extra info, scale, data), so a save loses nothing.
        /// Null when the line stopped after the rotation.
        /// </summary>
        public string Rest;
    }

    /// <summary>A structure made of vanilla pieces, as read from a blueprint file.</summary>
    internal sealed class Blueprint
    {
        public string Name;
        public string Description = "";

        /// <summary>Prefab whose icon represents the blueprint. Falls back to the first piece.</summary>
        public string IconPrefab;

        public readonly List<BlueprintPiece> Pieces = new List<BlueprintPiece>();

        /// <summary>Headers we do not know, kept raw so a save writes them back.</summary>
        public readonly List<string> ExtraHeaders = new List<string>();

        /// <summary>
        /// The file had a <c>#SnapPoints</c> or <c>#Terrain</c> section. We cannot write those back,
        /// so such a file is only ever saved under a new name.
        /// </summary>
        public bool HasSections { get; set; }

        /// <summary>The file it was read from, or null for a kit inside the DLL or a new blueprint.</summary>
        public string SourcePath { get; set; }

        /// <summary>Can only be saved under a new name: a kit, a .vbuild, or a file with sections.</summary>
        public bool ReadOnly { get; set; }
    }
}
