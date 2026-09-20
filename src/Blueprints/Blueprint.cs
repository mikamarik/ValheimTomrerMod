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
        public Vector3 Position;
        public Quaternion Rotation;
    }

    /// <summary>A structure made of vanilla pieces, as read from a blueprint file.</summary>
    internal sealed class Blueprint
    {
        public string Name;
        public string Description = "";

        /// <summary>Prefab whose icon represents the blueprint. Falls back to the first piece.</summary>
        public string IconPrefab;

        public readonly List<BlueprintPiece> Pieces = new List<BlueprintPiece>();
    }
}
