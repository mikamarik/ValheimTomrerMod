using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// The see-through copy of a whole blueprint that follows the player's aim.
    /// Each piece is set up the way <c>Player.SetupPlacementGhost</c> sets up the vanilla preview:
    /// no network object, no physics, no lights or sounds, "ghost" layer. Nothing here is saved.
    /// </summary>
    internal sealed class BlueprintPreview
    {
        private static int _ghostLayer = -1;

        private readonly GameObject _root;
        private readonly List<Piece> _pieces = new List<Piece>();
        private readonly List<bool> _invalid = new List<bool>();
        private readonly List<Material> _materials = new List<Material>();

        private BlueprintPreview(string name)
        {
            _root = new GameObject("ValheimTomrer_Blueprint_" + name);
        }

        public Transform Root => _root.transform;

        /// <summary>False once Unity destroyed the objects, for example on logout.</summary>
        public bool IsAlive => _root != null;

        /// <summary>Box around all visible parts, relative to the blueprint origin.</summary>
        public Bounds LocalBounds { get; private set; }

        /// <summary>
        /// The point that goes where the player aims: bottom of the blueprint, middle of its front
        /// edge (+Z faces the player). The building then always stands in front of the player,
        /// wherever the file put its origin.
        /// </summary>
        public Vector3 Anchor => new Vector3(LocalBounds.center.x, LocalBounds.min.y, LocalBounds.max.z);

        public static BlueprintPreview Create(ResolvedBlueprint blueprint)
        {
            if (_ghostLayer < 0)
            {
                _ghostLayer = LayerMask.NameToLayer("ghost");
            }

            var preview = new BlueprintPreview(blueprint.Name);
            foreach (var part in blueprint.Parts)
            {
                var copy = preview.CreateCopy(part.Prefab);
                copy.transform.localPosition = part.Source.Position;
                copy.transform.localRotation = part.Source.Rotation;
                preview._pieces.Add(copy.GetComponent<Piece>());
                preview._invalid.Add(false);
            }

            preview.LocalBounds = MeasureBounds(preview._root, blueprint);
            return preview;
        }

        /// <summary>
        /// The root sits at the world origin with no rotation right after creation,
        /// so world-space renderer bounds are the local bounds.
        /// </summary>
        private static Bounds MeasureBounds(GameObject root, ResolvedBlueprint blueprint)
        {
            var bounds = new Bounds(blueprint.Parts[0].Source.Position, Vector3.zero);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (renderer.enabled && !(renderer is ParticleSystemRenderer))
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return bounds;
        }

        public void SetVisible(bool visible)
        {
            if (_root.activeSelf != visible)
            {
                _root.SetActive(visible);
            }
        }

        /// <summary>Red tint on one piece, same look as the vanilla "can't place here" preview.</summary>
        public void SetInvalid(int index, bool invalid)
        {
            if (_invalid[index] == invalid)
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
                copy = Object.Instantiate(prefab, _root.transform, false);
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
            StripToVisuals(copy);
            SetupMaterials<MeshRenderer>(copy);
            SetupMaterials<SkinnedMeshRenderer>(copy);
            return copy;
        }

        private static void StripToVisuals(GameObject copy)
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

            // The preview never needs collisions, so switch every collider off.
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
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
                transform.gameObject.layer = _ghostLayer;
            }

            var ghostOnly = copy.transform.Find("_GhostOnly");
            if (ghostOnly != null)
            {
                ghostOnly.gameObject.SetActive(true);
            }
        }

        /// <summary>Copies of the materials with the vanilla preview settings, so textures do not slide.</summary>
        private void SetupMaterials<T>(GameObject copy) where T : Renderer
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
                    _materials.Add(material);
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
