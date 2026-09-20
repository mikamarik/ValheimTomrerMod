using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimTomrer.Blueprints
{
    /// <summary>How the copies are painted.</summary>
    internal enum PreviewLook
    {
        /// <summary>See-through copies of the materials, no shadows: the vanilla placement preview.</summary>
        Ghost,

        /// <summary>The shared materials untouched, shadows on: the piece as it really looks.</summary>
        Solid,
    }

    /// <summary>What a preview is for: which layer it lives on, how it is painted, whether it can be hit.</summary>
    internal struct PreviewStyle
    {
        public int Layer;
        public PreviewLook Look;
        public bool Colliders;

        /// <summary>Blueprint mode: the game's "ghost" layer, tinted copies, nothing to collide with.</summary>
        public static PreviewStyle Ghost()
        {
            return new PreviewStyle
            {
                Layer = BlueprintPreview.GhostLayer,
                Look = PreviewLook.Ghost,
                Colliders = false,
            };
        }

        /// <summary>The editor pane: the editor's own layer, real materials, clickable.</summary>
        public static PreviewStyle Solid(int layer)
        {
            return new PreviewStyle
            {
                Layer = layer,
                Look = PreviewLook.Solid,
                Colliders = true,
            };
        }
    }

    /// <summary>
    /// A copy of a whole blueprint, built from the real prefabs. Each piece is set up the way
    /// <c>Player.SetupPlacementGhost</c> sets up the vanilla preview: no network object, no physics,
    /// no lights or sounds. Nothing here is saved.
    ///
    /// Two users, told apart by <see cref="PreviewStyle"/>: blueprint mode's see-through preview that
    /// follows the player's aim, and the editor's solid model standing in its own scene.
    /// </summary>
    internal sealed class BlueprintPreview
    {
        /// <summary>How many pieces one <see cref="Fill"/> step builds, so a big kit does not stall a frame.</summary>
        public const int PiecesPerStep = 8;

        private static int _ghostLayer = -1;

        private readonly ResolvedBlueprint _blueprint;
        private readonly PreviewStyle _style;
        private readonly GameObject _root;
        private readonly List<Piece> _pieces = new List<Piece>();
        private readonly List<bool> _invalid = new List<bool>();
        private readonly List<Material> _materials = new List<Material>();
        private bool _filling;

        private BlueprintPreview(ResolvedBlueprint blueprint, PreviewStyle style)
        {
            _blueprint = blueprint;
            _style = style;
            _root = new GameObject("ValheimTomrer_Blueprint_" + blueprint.Name) { layer = style.Layer };
        }

        public static int GhostLayer
        {
            get
            {
                if (_ghostLayer < 0)
                {
                    _ghostLayer = LayerMask.NameToLayer("ghost");
                }

                return _ghostLayer;
            }
        }

        public Transform Root => _root.transform;

        /// <summary>False once Unity destroyed the objects, for example on logout.</summary>
        public bool IsAlive => _root != null;

        /// <summary>Box around all visible parts, relative to the blueprint origin. Final once <see cref="Done"/>.</summary>
        public Bounds LocalBounds { get; private set; }

        /// <summary>Pieces standing so far, and how many there will be.</summary>
        public int Built => _pieces.Count;

        public int Total => _blueprint.Parts.Count;

        public bool Done { get; private set; }

        /// <summary>
        /// The point that goes where the player aims: bottom of the blueprint, middle of its front
        /// edge (+Z faces the player). The building then always stands in front of the player,
        /// wherever the file put its origin.
        /// </summary>
        public Vector3 Anchor => new Vector3(LocalBounds.center.x, LocalBounds.min.y, LocalBounds.max.z);

        /// <summary>Blueprint mode's preview: see-through, on the ghost layer, built in one go.</summary>
        public static BlueprintPreview Create(ResolvedBlueprint blueprint)
        {
            return Create(blueprint, PreviewStyle.Ghost());
        }

        /// <summary>Builds every piece at once. Fine for a kit, use <see cref="Fill"/> for anything big.</summary>
        public static BlueprintPreview Create(ResolvedBlueprint blueprint, PreviewStyle style)
        {
            var preview = new BlueprintPreview(blueprint, style);
            var fill = preview.Fill();
            while (fill.MoveNext())
            {
            }

            return preview;
        }

        /// <summary>An empty preview. Step <see cref="Fill"/> once a frame to build it up.</summary>
        public static BlueprintPreview Empty(ResolvedBlueprint blueprint, PreviewStyle style)
        {
            return new BlueprintPreview(blueprint, style);
        }

        /// <summary>
        /// Builds the copies, <see cref="PiecesPerStep"/> per step. Call MoveNext once a frame;
        /// it ends when everything stands and the bounds are measured.
        /// </summary>
        public IEnumerator Fill()
        {
            if (_filling || Done)
            {
                yield break;
            }

            _filling = true;
            var parts = _blueprint.Parts;
            for (var i = 0; i < parts.Count; i++)
            {
                if (!IsAlive)
                {
                    yield break;
                }

                var part = parts[i];
                var copy = CreateCopy(part.Prefab);
                copy.transform.localPosition = part.Source.Position;
                copy.transform.localRotation = part.Source.Rotation;
                _pieces.Add(copy.GetComponent<Piece>());
                _invalid.Add(false);

                if ((i + 1) % PiecesPerStep == 0 && i + 1 < parts.Count)
                {
                    yield return null;
                }
            }

            LocalBounds = MeasureBounds();
            Done = true;
            _filling = false;
        }

        /// <summary>
        /// The root is never rotated or scaled, so the world box of the renderers is the local box
        /// moved by the root's position.
        /// </summary>
        private Bounds MeasureBounds()
        {
            var origin = _root.transform.position;
            var bounds = new Bounds(origin + _blueprint.Parts[0].Source.Position, Vector3.zero);
            foreach (var renderer in _root.GetComponentsInChildren<Renderer>())
            {
                if (renderer.enabled && !(renderer is ParticleSystemRenderer))
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            bounds.center -= origin;
            return bounds;
        }

        public void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible)
            {
                _root.SetActive(visible);
            }
        }

        /// <summary>Red tint on one piece, same look as the vanilla "can't place here" preview.</summary>
        public void SetInvalid(int index, bool invalid)
        {
            if (index >= _invalid.Count || _invalid[index] == invalid)
            {
                return;
            }

            _invalid[index] = invalid;
            _pieces[index].SetInvalidPlacementHeightlight(invalid);
        }

        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }

            foreach (var material in _materials)
            {
                if (material != null)
                {
                    Object.Destroy(material);
                }
            }

            _materials.Clear();
        }

        private GameObject CreateCopy(GameObject prefab)
        {
            return Build(prefab, _root.transform, _style, _materials);
        }

        /// <summary>
        /// One piece's visuals, stripped the way the vanilla placement ghost is. The caller owns the
        /// object and, when <paramref name="owned"/> is given, the material copies put in it.
        /// </summary>
        public static GameObject Build(GameObject prefab, Transform parent, PreviewStyle style, List<Material> owned)
        {
            // Same guards as the vanilla preview: no ZDO, no terrain edits while instantiating.
            var terrainModifier = prefab.GetComponentInChildren<TerrainModifier>();
            var terrainModifierEnabled = terrainModifier != null && terrainModifier.enabled;
            if (terrainModifier != null)
            {
                terrainModifier.enabled = false;
            }

            GameObject copy;
            ZNetView.m_forceDisableInit = true;
            TerrainOp.m_forceDisableTerrainOps = true;
            try
            {
                copy = Object.Instantiate(prefab, parent, false);
            }
            finally
            {
                ZNetView.m_forceDisableInit = false;
                TerrainOp.m_forceDisableTerrainOps = false;
                if (terrainModifier != null)
                {
                    terrainModifier.enabled = terrainModifierEnabled;
                }
            }

            copy.name = prefab.name;
            StripToVisuals(copy, style);
            if (style.Colliders)
            {
                AddFallbackCollider(copy);
            }

            if (style.Look == PreviewLook.Ghost)
            {
                SetupMaterials<MeshRenderer>(copy, owned);
                SetupMaterials<SkinnedMeshRenderer>(copy, owned);
            }

            return copy;
        }

        private static void StripToVisuals(GameObject copy, PreviewStyle style)
        {
            DestroyAll<Joint>(copy);
            DestroyAll<Rigidbody>(copy);
            DestroyAll<ParticleSystemForceField>(copy);
            DestroyAll<Demister>(copy);
            DestroyAll<TerrainModifier>(copy);
            DestroyAll<GuidePoint>(copy);
            DestroyAll<LightLod>(copy);
            DestroyAll<LightFlicker>(copy);
            DestroyAll<Light>(copy);
            DestroyAll<WispSpawner>(copy);

            // A workbench draws its build area with a CircleProjector, which spawns its 80 ring
            // pieces in Update, after this strip has run. They would keep layer Default and cast
            // shadows in the world. Kill the projector and they are never made.
            DestroyAll<CircleProjector>(copy);

            // The aim preview never needs collisions; the editor keeps them so a piece can be clicked.
            if (!style.Colliders)
            {
                foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
                {
                    collider.enabled = false;
                }
            }

            foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour is AudioSource || behaviour is ZSFX || behaviour is Windmill
                    || behaviour.GetType().Name == "MagicaCloth")
                {
                    behaviour.enabled = false;
                }
            }

            foreach (var particles in copy.GetComponentsInChildren<ParticleSystem>(true))
            {
                particles.gameObject.SetActive(false);
            }

            // Vanilla only runs these for its own preview (Player.IsPlacementGhost), so do it here.
            foreach (var disable in copy.GetComponentsInChildren<DisableInPlacementGhost>(true))
            {
                foreach (var behaviour in disable.m_components)
                {
                    if (behaviour != null)
                    {
                        behaviour.enabled = false;
                    }
                }

                foreach (var go in disable.m_objects)
                {
                    if (go != null)
                    {
                        go.SetActive(false);
                    }
                }
            }

            foreach (var transform in copy.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = style.Layer;
            }

            if (style.Look == PreviewLook.Ghost)
            {
                var ghostOnly = copy.transform.Find("_GhostOnly");
                if (ghostOnly != null)
                {
                    ghostOnly.gameObject.SetActive(true);
                }
            }
        }

        /// <summary>
        /// A piece with no collider of its own (piece_repair) gets a box around its meshes, so the
        /// editor's ray can still find it. Pieces with no mesh either get a small box.
        /// </summary>
        private static void AddFallbackCollider(GameObject copy)
        {
            if (copy.GetComponentInChildren<Collider>(true) != null)
            {
                return;
            }

            var bounds = MeshBounds(copy);
            var box = copy.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = Vector3.Max(bounds.size, new Vector3(0.25f, 0.25f, 0.25f));
        }

        /// <summary>The box around a copy's meshes, in the copy's own space.</summary>
        private static Bounds MeshBounds(GameObject copy)
        {
            var toRoot = copy.transform.worldToLocalMatrix;
            var bounds = new Bounds(new Vector3(0f, 0.25f, 0f), Vector3.zero);
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

            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
            {
                Add(filter.sharedMesh, filter.transform);
            }

            foreach (var skin in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Add(skin.sharedMesh, skin.transform);
            }

            return bounds;
        }

        /// <summary>Copies of the materials with the vanilla preview settings, so textures do not slide.</summary>
        private static void SetupMaterials<T>(GameObject copy, List<Material> owned) where T : Renderer
        {
            foreach (var renderer in copy.GetComponentsInChildren<T>(true))
            {
                if (renderer.sharedMaterial == null)
                {
                    continue;
                }

                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null)
                    {
                        continue;
                    }

                    var material = new Material(materials[i]);
                    material.SetFloat("_ValueNoise", 0f);
                    material.SetFloat("_TriplanarLocalPos", 1f);
                    materials[i] = material;
                    owned?.Add(material);
                }

                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private static void DestroyAll<T>(GameObject go) where T : Component
        {
            foreach (var component in go.GetComponentsInChildren<T>(true))
            {
                Object.Destroy(component);
            }
        }
    }
}
