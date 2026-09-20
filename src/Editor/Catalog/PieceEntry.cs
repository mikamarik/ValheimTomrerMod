using System.Collections.Generic;
using UnityEngine;

namespace ValheimTomrer.Editor.Catalog
{
    /// <summary>One material a piece costs.</summary>
    internal sealed class PieceCost
    {
        /// <summary>The item's name token, the key the game counts inventory by.</summary>
        public string Token;

        public string Name;
        public int Amount;
        public Sprite Icon;
    }

    internal enum ColliderKind
    {
        Box,
        Sphere,
        Capsule,
        Mesh,
    }

    /// <summary>
    /// One collider of a piece, measured in piece space. The box is always filled in, even for
    /// spheres, capsules and meshes, so a rough test needs no special cases.
    /// </summary>
    internal sealed class PieceCollider
    {
        public ColliderKind Kind;
        public Vector3 Center;
        public Vector3 Size;            // scale is already in it
        public Quaternion Rotation;
        public float Radius;            // sphere and capsule
        public Vector3 P0;              // capsule segment ends
        public Vector3 P1;
        public bool Convex;             // mesh colliders
        public int Layer;
        public string LayerName;
        public bool Trigger;
        public bool OnRigidbody;        // hangs under a Rigidbody (carts, ships)
        public bool Placed;             // there on the built piece
        public bool Ghost;              // there on the see-through copy while placing
    }

    /// <summary>
    /// Everything the editor needs to know about one building piece, read off the prefab once.
    /// Nothing here is instantiated and nothing is written back: the prefab is only read.
    /// </summary>
    internal sealed class PieceEntry
    {
        /// <summary>Layers the game's placement ray hits (Player.m_placeRayMask).</summary>
        public static readonly string[] PlaceRayLayers =
        {
            "Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle",
        };

        /// <summary>Layers the snap search looks at (Piece.s_pieceRayMask).</summary>
        public static readonly string[] PieceLayers = { "piece", "piece_nonsolid" };

        public GameObject Prefab;
        public Piece Piece;

        /// <summary>Where it sits in the hammer's own list, so the grid can keep the game's order.</summary>
        public int Order;

        public string PrefabName;
        public string NameToken;
        public string DisplayName;
        public string Description;

        public Piece.UsageTagFlags Usage;
        public string[] UsageTags;

        /// <summary>The hammer only offers it in its season (Piece.m_enabled is false).</summary>
        public bool Seasonal;

        public string Dlc;

        public PieceCost[] Cost;

        public string StationToken;
        public string StationName;
        public Sprite StationIcon;

        /// <summary>The station this piece is itself, so a blueprint can bring its own.</summary>
        public string OwnStationToken;

        public Sprite Icon;

        public int Comfort;
        public Piece.ComfortGroup ComfortGroup;

        /// <summary>Box around every visible part, in piece space.</summary>
        public Bounds Bounds;

        public Vector3[] SnapPoints;
        public string[] SnapNames;

        // The placement flags, straight off the piece.
        public bool GroundPiece;
        public bool ClipGround;
        public bool ClipEverything;
        public bool CanRotate;
        public bool AllowRotatedOverlap;
        public bool WaterPiece;
        public bool NoClipping;
        public bool RepairPiece;
        public bool RemovePiece;

        public PieceCollider[] Colliders;

        /// <summary>Colliders the mouse ray can hit on a built piece.</summary>
        public PieceCollider[] RayColliders;

        /// <summary>Colliders the game pushes a new piece against the aimed point with.</summary>
        public PieceCollider[] TouchColliders;

        /// <summary>Colliders that make a built piece count as near enough to snap to.</summary>
        public PieceCollider[] SnapSearchColliders;

        /// <summary>Lower case name and prefab, joined, so the search filter needs no allocation.</summary>
        public string SearchText;

        public bool HasIcon => Icon != null;

        public Vector3 Size => Bounds.size;

        /// <summary>Reads one prefab. Never instantiates it and never changes it.</summary>
        public static PieceEntry Read(GameObject prefab, int order)
        {
            var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
            if (piece == null)
            {
                return null;
            }

            var entry = new PieceEntry
            {
                Prefab = prefab,
                Piece = piece,
                Order = order,
                PrefabName = prefab.name,
                NameToken = piece.m_name,
                DisplayName = Localize(piece.m_name),
                Description = Localize(piece.m_description),
                Usage = piece.m_usage,
                UsageTags = TagNames(piece.m_usage),
                Seasonal = !piece.m_enabled,
                Dlc = piece.m_dlc ?? "",
                Icon = piece.m_icon,
                Comfort = piece.m_comfort,
                ComfortGroup = piece.m_comfortGroup,
                GroundPiece = piece.m_groundPiece,
                ClipGround = piece.m_clipGround,
                ClipEverything = piece.m_clipEverything,
                CanRotate = piece.m_canRotate,
                AllowRotatedOverlap = piece.m_allowRotatedOverlap,
                WaterPiece = piece.m_waterPiece,
                NoClipping = piece.m_noClipping,
                RepairPiece = piece.m_repairPiece,
                RemovePiece = piece.m_removePiece,
                Cost = ReadCost(piece),
                Bounds = MeshBounds(prefab),
            };

            if (piece.m_craftingStation != null)
            {
                entry.StationToken = piece.m_craftingStation.m_name;
                entry.StationName = Localize(piece.m_craftingStation.m_name);
                entry.StationIcon = piece.m_craftingStation.m_icon;
            }

            var own = prefab.GetComponentInChildren<CraftingStation>(true);
            entry.OwnStationToken = own != null ? own.m_name : null;

            ReadSnapPoints(piece, entry);
            ReadColliders(prefab, entry);
            entry.SearchText = (entry.DisplayName + "\n" + entry.PrefabName).ToLowerInvariant();
            return entry;
        }

        /// <summary>The tag names of a usage mask, in the game's own order.</summary>
        public static string[] TagNames(Piece.UsageTagFlags usage)
        {
            var names = new List<string>();
            foreach (var tag in AllTags)
            {
                if ((usage & tag) != 0)
                {
                    names.Add(tag.ToString());
                }
            }

            return names.ToArray();
        }

        /// <summary>Every tag the game has, lowest bit first. That is the build menu's order.</summary>
        public static readonly Piece.UsageTagFlags[] AllTags =
        {
            Piece.UsageTagFlags.Misc, Piece.UsageTagFlags.Crafting, Piece.UsageTagFlags.Building,
            Piece.UsageTagFlags.Floor, Piece.UsageTagFlags.Wall, Piece.UsageTagFlags.Roof,
            Piece.UsageTagFlags.Architecture, Piece.UsageTagFlags.Furniture, Piece.UsageTagFlags.Lighting,
            Piece.UsageTagFlags.Decor, Piece.UsageTagFlags.Storage, Piece.UsageTagFlags.Transport,
            Piece.UsageTagFlags.Food, Piece.UsageTagFlags.Meads, Piece.UsageTagFlags.Feasts,
            Piece.UsageTagFlags.Defense, Piece.UsageTagFlags.Stacks, Piece.UsageTagFlags.Stairs,
            Piece.UsageTagFlags.Doors, Piece.UsageTagFlags.Seasonal,
        };

        private static string Localize(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return "";
            }

            return Localization.instance != null ? Localization.instance.Localize(token) : token;
        }

        private static PieceCost[] ReadCost(Piece piece)
        {
            var cost = new List<PieceCost>();
            foreach (var need in piece.m_resources)
            {
                if (need == null || need.m_resItem == null || need.m_amount <= 0)
                {
                    continue;
                }

                var shared = need.m_resItem.m_itemData.m_shared;
                cost.Add(new PieceCost
                {
                    Token = shared.m_name,
                    Name = Localize(shared.m_name),
                    Amount = need.m_amount,
                    Icon = shared.m_icons != null && shared.m_icons.Length > 0 ? shared.m_icons[0] : null,
                });
            }

            return cost.ToArray();
        }

        private static void ReadSnapPoints(Piece piece, PieceEntry entry)
        {
            var points = new List<Transform>();
            piece.GetSnapPoints(points);
            entry.SnapPoints = new Vector3[points.Count];
            entry.SnapNames = new string[points.Count];
            for (var i = 0; i < points.Count; i++)
            {
                entry.SnapPoints[i] = points[i].localPosition;
                entry.SnapNames[i] = points[i].name;
            }
        }

        // ---------- colliders ----------

        private static void ReadColliders(GameObject prefab, PieceEntry entry)
        {
            var rayMask = LayerMask.GetMask(PlaceRayLayers);
            var pieceMask = LayerMask.GetMask(PieceLayers);
            var toRoot = prefab.transform.worldToLocalMatrix;

            var all = new List<PieceCollider>();
            var ray = new List<PieceCollider>();
            var touch = new List<PieceCollider>();
            var snap = new List<PieceCollider>();

            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                var read = Measure(collider, toRoot);
                var bit = 1 << read.Layer;

                // The ghost keeps only the colliders the placement ray cares about (SetupPlacementGhost)
                // and loses every Rigidbody with them.
                read.Placed = collider.enabled;
                read.Ghost = collider.enabled && (bit & rayMask) != 0;

                all.Add(read);
                if (read.Placed && !read.Trigger && !read.OnRigidbody && (bit & rayMask) != 0)
                {
                    ray.Add(read);
                }

                if (read.Ghost && !read.Trigger && !(read.Kind == ColliderKind.Mesh && !read.Convex))
                {
                    touch.Add(read);
                }

                if (read.Placed && (bit & pieceMask) != 0)
                {
                    snap.Add(read);
                }
            }

            entry.Colliders = all.ToArray();
            entry.RayColliders = ray.ToArray();
            entry.TouchColliders = touch.ToArray();
            entry.SnapSearchColliders = snap.ToArray();
        }

        private static PieceCollider Measure(Collider collider, Matrix4x4 toRoot)
        {
            var matrix = toRoot * collider.transform.localToWorldMatrix;
            var scale = Abs(matrix.lossyScale);
            var read = new PieceCollider
            {
                Layer = collider.gameObject.layer,
                LayerName = LayerMask.LayerToName(collider.gameObject.layer),
                Trigger = collider.isTrigger,
                OnRigidbody = collider.GetComponentInParent<Rigidbody>(true) != null,
                Rotation = matrix.rotation,
            };

            switch (collider)
            {
                case BoxCollider box:
                    read.Kind = ColliderKind.Box;
                    read.Center = matrix.MultiplyPoint3x4(box.center);
                    read.Size = Vector3.Scale(box.size, scale);
                    break;

                case SphereCollider sphere:
                    read.Kind = ColliderKind.Sphere;
                    read.Center = matrix.MultiplyPoint3x4(sphere.center);
                    read.Radius = sphere.radius * Mathf.Max(scale.x, scale.y, scale.z);
                    read.Size = Vector3.one * (read.Radius * 2f);
                    break;

                case CapsuleCollider capsule:
                    read.Kind = ColliderKind.Capsule;
                    MeasureCapsule(capsule, matrix, scale, read);
                    break;

                case MeshCollider mesh:
                    read.Kind = ColliderKind.Mesh;
                    read.Convex = mesh.convex;
                    var local = mesh.sharedMesh != null ? mesh.sharedMesh.bounds : new Bounds();
                    read.Center = matrix.MultiplyPoint3x4(local.center);
                    read.Size = Vector3.Scale(local.size, scale);
                    break;

                default:
                    read.Kind = ColliderKind.Box;
                    read.Center = matrix.MultiplyPoint3x4(Vector3.zero);
                    break;
            }

            return read;
        }

        private static void MeasureCapsule(CapsuleCollider capsule, Matrix4x4 matrix, Vector3 scale, PieceCollider read)
        {
            var axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
            var along = capsule.direction == 0 ? scale.x : capsule.direction == 1 ? scale.y : scale.z;
            var across = capsule.direction == 0
                ? Mathf.Max(scale.y, scale.z)
                : capsule.direction == 1 ? Mathf.Max(scale.x, scale.z) : Mathf.Max(scale.x, scale.y);

            read.Radius = capsule.radius * across;
            var half = Mathf.Max(capsule.height * along * 0.5f, read.Radius);
            var end = Mathf.Max(half - read.Radius, 0f);

            read.Center = matrix.MultiplyPoint3x4(capsule.center);
            read.P0 = matrix.MultiplyPoint3x4(capsule.center - axis * (end / Mathf.Max(along, 1e-5f)));
            read.P1 = matrix.MultiplyPoint3x4(capsule.center + axis * (end / Mathf.Max(along, 1e-5f)));
            read.Size = Vector3.Scale(axis, Vector3.one * (half * 2f))
                + Vector3.Scale(Vector3.one - axis, Vector3.one * (read.Radius * 2f));
        }

        private static Vector3 Abs(Vector3 v)
        {
            return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
        }

        // ---------- bounds ----------

        /// <summary>
        /// The box around every visible part, in the prefab's own space. Measured the same way the
        /// blueprint preview measures a whole building, so the two numbers agree.
        /// </summary>
        private static Bounds MeshBounds(GameObject prefab)
        {
            var toRoot = prefab.transform.worldToLocalMatrix;
            var bounds = new Bounds();
            var first = true;

            void Add(Mesh mesh, Transform at)
            {
                if (mesh == null)
                {
                    return;
                }

                var matrix = toRoot * at.localToWorldMatrix;
                var local = mesh.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var offset = Vector3.Scale(local.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                    var point = matrix.MultiplyPoint3x4(local.center + offset);
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

            foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>())
            {
                Add(filter.sharedMesh, filter.transform);
            }

            foreach (var skin in prefab.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Add(skin.sharedMesh, skin.transform);
            }

            return bounds;
        }
    }
}
