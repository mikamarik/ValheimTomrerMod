using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Placement;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The see-through copy of whatever is in hand, standing where a click would drop it. Green
    /// while it can be placed, red while it would land on a piece of the same kind.
    ///
    /// The copies are the real prefabs with their visuals kept and everything else stripped, the
    /// same way the vanilla placement ghost is built, with every material swapped for one flat
    /// see-through colour. Nothing is loaded from disk and the copies have no colliders, so the
    /// pane's picking ray goes straight through them.
    /// </summary>
    internal sealed class GhostRenderer
    {
        private const float Alpha = 0.6f;

        private static readonly Color Valid = Hex(0x7DFF8A);
        private static readonly Color Bad = Hex(0xFF5A4F);

        private readonly Transform _parent;
        private readonly int _layer;
        private readonly List<Renderer> _renderers = new List<Renderer>();

        private GameObject _root;
        private Material _valid;
        private Material _bad;
        private bool _showingBad;
        private int _pieces;

        public GhostRenderer(Transform parent, int layer)
        {
            _parent = parent;
            _layer = layer;
        }

        /// <summary>The ghost stands in the pane right now.</summary>
        public bool Visible => _root != null && _root.activeSelf;

        /// <summary>How many pieces it carries. Zero when nothing is in hand.</summary>
        public int PieceCount => _pieces;

        /// <summary>Renderers the copies brought with them.</summary>
        public int RendererCount => _renderers.Count;

        /// <summary>It is showing the "same piece is already there" colour.</summary>
        public bool ShowingBad => _showingBad;

        /// <summary>
        /// A new copy of the see-through green the ghost is painted with. The world capture draws
        /// its box with it, so the two read as the same thing. The caller owns it and destroys it.
        /// </summary>
        public static Material NewMaterial()
        {
            return Shading.Overlay(WithAlpha(Valid), false);
        }

        /// <summary>Builds the copies for a new set in hand. Null or empty clears them.</summary>
        public void Show(MovingSet set)
        {
            Clear();
            if (set == null || set.Count == 0 || _parent == null)
            {
                return;
            }

            var scene = ZNetScene.instance;
            if (scene == null)
            {
                return;
            }

            _root = new GameObject("ValheimTomrer_Ghost") { layer = _layer };
            _root.transform.SetParent(_parent, false);
            _root.SetActive(false);

            var style = new PreviewStyle { Layer = _layer, Look = PreviewLook.Solid, Colliders = false };
            foreach (var piece in set.Pieces)
            {
                var prefab = scene.GetPrefab(piece.Prefab);
                if (prefab == null)
                {
                    continue;
                }

                var copy = BlueprintPreview.Build(prefab, _root.transform, style, null);
                copy.transform.localPosition = piece.Pos;
                copy.transform.localRotation = piece.Rot;
                _pieces++;
            }

            _valid = NewMaterial();
            _bad = Shading.Overlay(WithAlpha(Bad), false);
            Paint(_valid);
        }

        /// <summary>Puts the ghost where the placing rule says it would land.</summary>
        public void Place(Vector3 position, Quaternion rotation, bool duplicate)
        {
            if (_root == null)
            {
                return;
            }

            _root.transform.localPosition = position;
            _root.transform.localRotation = rotation;
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }

            if (duplicate != _showingBad)
            {
                _showingBad = duplicate;
                Paint(duplicate ? _bad : _valid);
            }
        }

        /// <summary>Hides it without throwing the copies away: the aim only missed for a frame.</summary>
        public void Hide()
        {
            if (_root != null && _root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        public void Destroy()
        {
            Clear();
        }

        private void Clear()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }

            if (_valid != null)
            {
                Object.Destroy(_valid);
            }

            if (_bad != null)
            {
                Object.Destroy(_bad);
            }

            _root = null;
            _valid = null;
            _bad = null;
            _renderers.Clear();
            _showingBad = false;
            _pieces = 0;
        }

        /// <summary>One flat colour over every part, so the ghost reads as one shape.</summary>
        private void Paint(Material material)
        {
            if (_root == null || material == null)
            {
                return;
            }

            if (_renderers.Count == 0)
            {
                _renderers.AddRange(_root.GetComponentsInChildren<Renderer>(true));
            }

            foreach (var renderer in _renderers)
            {
                if (renderer == null || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                var count = Mathf.Max(1, renderer.sharedMaterials.Length);
                var materials = new Material[count];
                for (var i = 0; i < count; i++)
                {
                    materials[i] = material;
                }

                renderer.sharedMaterials = materials;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private static Color WithAlpha(Color color)
        {
            return new Color(color.r, color.g, color.b, Alpha);
        }

        private static Color Hex(int rgb)
        {
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0xFF);
        }
    }
}
