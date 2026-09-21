using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Placement;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The see-through copy of whatever is in hand, standing where a click would drop it. Each
    /// piece wears the colour the game's hammer would show on it once built: light blue on the
    /// ground, then green, yellow, orange and red as its support runs out. A piece that would fall
    /// down, or land on a piece of the same kind, blinks red: a click does nothing then.
    ///
    /// The copies are the real prefabs with their visuals kept and everything else stripped, the
    /// same way the vanilla placement ghost is built, with every material swapped for one flat
    /// see-through colour. Nothing is loaded from disk and the copies have no colliders, so the
    /// pane's picking ray goes straight through them.
    /// </summary>
    internal sealed class GhostRenderer
    {
        private const float Alpha = 0.6f;

        /// <summary>How see-through the refused colour gets on the off beat of its blink.</summary>
        private const float BlinkAlpha = 0.15f;

        /// <summary>One blink, on and off, in seconds.</summary>
        private const float BlinkPeriod = 0.8f;

        private static readonly Color Valid = Hex(0x7DFF8A);
        private static readonly Color Bad = Hex(0xFF5A4F);
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private readonly Transform _parent;
        private readonly int _layer;
        private readonly List<Renderer> _renderers = new List<Renderer>();

        /// <summary>The renderers of each piece of the set, in the set's order.</summary>
        private readonly List<Renderer[]> _parts = new List<Renderer[]>();

        private readonly List<Color> _shown = new List<Color>();
        private MaterialPropertyBlock _block;

        private GameObject _root;
        private Material _valid;
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

        /// <summary>It is blinking red: a click here does nothing.</summary>
        public bool ShowingBad => _showingBad;

        /// <summary>The colour piece i of the set wears right now, for the test.</summary>
        public Color ColorOf(int i)
        {
            return i >= 0 && i < _shown.Count ? _shown[i] : Color.clear;
        }

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
                    _parts.Add(new Renderer[0]);
                    _shown.Add(Color.clear);
                    continue;
                }

                var copy = BlueprintPreview.Build(prefab, _root.transform, style, null);
                copy.transform.localPosition = piece.Pos;
                copy.transform.localRotation = piece.Rot;
                _parts.Add(copy.GetComponentsInChildren<Renderer>(true));
                _shown.Add(Color.clear);
                _pieces++;
            }

            _valid = NewMaterial();
            _block = new MaterialPropertyBlock();
            Paint(_valid);
        }

        /// <summary>
        /// Puts the ghost where the placing rule says it would land, and colours each piece by the
        /// support it would have there.
        /// </summary>
        public void Place(PlaceResult result)
        {
            if (_root == null || result == null)
            {
                return;
            }

            _root.transform.localPosition = result.Pos;
            _root.transform.localRotation = result.Rot;
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }

            _showingBad = result.Blocked;
            var on = Time.unscaledTime % BlinkPeriod < BlinkPeriod * 0.5f;
            var refused = new Color(Bad.r, Bad.g, Bad.b, on ? Alpha : BlinkAlpha);
            for (var i = 0; i < _parts.Count; i++)
            {
                Color color;
                if (result.Duplicate || (result.Falls != null && i < result.Falls.Length && result.Falls[i]))
                {
                    color = refused;
                }
                else if (result.Support != null && i < result.Support.Length && i < result.World.Length)
                {
                    var entry = result.World[i].Entry;
                    color = WithAlpha(Support.ColorOf(result.Support[i], entry != null ? entry.Support : null));
                }
                else
                {
                    color = WithAlpha(Valid);
                }

                Tint(i, color);
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

            _root = null;
            _valid = null;
            _renderers.Clear();
            _parts.Clear();
            _shown.Clear();
            _showingBad = false;
            _pieces = 0;
        }

        /// <summary>One piece's colour, through a property block so every piece can share the one material.</summary>
        private void Tint(int i, Color color)
        {
            if (_shown[i] == color)
            {
                return;
            }

            _shown[i] = color;
            _block.Clear();
            _block.SetColor(ColorId, color);
            foreach (var renderer in _parts[i])
            {
                if (renderer != null && !(renderer is ParticleSystemRenderer))
                {
                    renderer.SetPropertyBlock(_block);
                }
            }
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
