using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The pieces standing in the pane, one copy per piece of the open blueprint. The first fill is
    /// done by <see cref="BlueprintPreview"/>; from then on this keeps the copies and the document
    /// in step, building only what changed instead of the whole model.
    ///
    /// Everything it makes hangs under the preview's root, so the preview's own Destroy still
    /// cleans up. The copies are solid, on the editor layer, and keep their colliders so the pane's
    /// ray can pick them.
    /// </summary>
    internal sealed class SceneModel
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private readonly Dictionary<int, Standing> _live = new Dictionary<int, Standing>();
        private readonly HashSet<int> _hidden = new HashSet<int>();
        private readonly MaterialPropertyBlock _tint = new MaterialPropertyBlock();
        private readonly Transform _root;
        private readonly int _layer;
        private int _revision = -1;
        private bool _boxes;
        private GameObject _tinted;
        private Color _tintColor;

        public SceneModel(Transform root, int layer)
        {
            _root = root;
            _layer = layer;
        }

        /// <summary>Copies standing in the pane.</summary>
        public int Count => _live.Count;

        /// <summary>The piece id under a collider the pane's ray hit, or -1.</summary>
        public int IdOf(Transform hit)
        {
            if (hit == null)
            {
                return -1;
            }

            foreach (var pair in _live)
            {
                if (pair.Value.Object != null && hit.IsChildOf(pair.Value.Object.transform))
                {
                    return pair.Key;
                }
            }

            return -1;
        }

        /// <summary>Takes over the copies the first fill built. They stand in the document's order.</summary>
        public void Adopt(BlueprintDocument document, BlueprintPreview model)
        {
            _live.Clear();
            if (document == null || model == null || !model.IsAlive)
            {
                return;
            }

            var root = model.Root;
            var count = Mathf.Min(root.childCount, document.Pieces.Count);
            for (var i = 0; i < count; i++)
            {
                var piece = document.Pieces[i];
                var child = root.GetChild(i);
                _live[piece.Id] = new Standing
                {
                    Object = child.gameObject,
                    Prefab = piece.PrefabName,
                    Position = piece.Position,
                    Rotation = piece.Rotation,
                };
            }

            _revision = document.Revision;
            if (_boxes)
            {
                foreach (var pair in _live)
                {
                    Paint(pair.Value.Object);
                }
            }
        }

        /// <summary>Builds what the document gained, drops what it lost, moves what changed.</summary>
        public void Sync(BlueprintDocument document)
        {
            if (document == null || _root == null)
            {
                return;
            }

            if (document.Revision == _revision)
            {
                return;
            }

            _revision = document.Revision;
            var seen = new HashSet<int>();
            foreach (var piece in document.Pieces)
            {
                seen.Add(piece.Id);
                if (!_live.TryGetValue(piece.Id, out var standing) || standing.Object == null)
                {
                    var built = Create(piece);
                    if (built != null)
                    {
                        _live[piece.Id] = built;
                    }

                    continue;
                }

                if (standing.Prefab != piece.PrefabName)
                {
                    Object.Destroy(standing.Object);
                    var rebuilt = Create(piece);
                    if (rebuilt != null)
                    {
                        _live[piece.Id] = rebuilt;
                    }
                    else
                    {
                        _live.Remove(piece.Id);
                    }

                    continue;
                }

                if (standing.Position != piece.Position || standing.Rotation != piece.Rotation)
                {
                    standing.Object.transform.localPosition = piece.Position;
                    standing.Object.transform.localRotation = piece.Rotation;
                    standing.Position = piece.Position;
                    standing.Rotation = piece.Rotation;
                }
            }

            var gone = new List<int>();
            foreach (var pair in _live)
            {
                if (!seen.Contains(pair.Key))
                {
                    gone.Add(pair.Key);
                }
            }

            foreach (var id in gone)
            {
                if (_live[id].Object != null)
                {
                    Object.Destroy(_live[id].Object);
                }

                _live.Remove(id);
            }
        }

        /// <summary>The piece that wears a tint right now, or -1.</summary>
        public int TintedId => _tinted != null ? IdOf(_tinted.transform) : -1;

        /// <summary>
        /// Tints one piece the way the game's hammer does with the piece it points at
        /// (WearNTear.Highlight): the colour on the surface, and 40 % of it as glow. -1 clears it.
        /// </summary>
        public void Tint(int id, Color color)
        {
            var copy = id >= 0 && _live.TryGetValue(id, out var standing) ? standing.Object : null;
            if (copy == _tinted && (copy == null || color == _tintColor))
            {
                return;
            }

            Paint(_tinted, null);
            _tinted = copy;
            _tintColor = color;
            if (copy == null)
            {
                return;
            }

            _tint.Clear();
            _tint.SetColor(ColorId, color);
            _tint.SetColor(EmissionId, color * 0.4f);
            Paint(copy, _tint);
        }

        private static void Paint(GameObject copy, MaterialPropertyBlock block)
        {
            if (copy == null)
            {
                return;
            }

            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is ParticleSystemRenderer))
                {
                    renderer.SetPropertyBlock(block);
                }
            }
        }

        /// <summary>Pieces being carried are hidden here, because the ghost draws them instead.</summary>
        public void Hide(IEnumerable<int> ids)
        {
            var want = new HashSet<int>();
            if (ids != null)
            {
                foreach (var id in ids)
                {
                    want.Add(id);
                }
            }

            foreach (var id in _hidden)
            {
                if (!want.Contains(id))
                {
                    Show(id, true);
                }
            }

            _hidden.Clear();
            foreach (var id in want)
            {
                _hidden.Add(id);
                Show(id, false);
            }
        }

        /// <summary>
        /// The Boxes view: the models stop drawing and the wire boxes stand in for them. The
        /// copies stay where they are, so the pane's ray still picks the right piece.
        /// </summary>
        public void SetBoxes(bool on)
        {
            if (_boxes == on)
            {
                return;
            }

            _boxes = on;
            foreach (var pair in _live)
            {
                Paint(pair.Value.Object);
            }
        }

        private void Paint(GameObject copy)
        {
            if (copy == null)
            {
                return;
            }

            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = !_boxes;
            }
        }

        public void Clear()
        {
            _tinted = null;
            _live.Clear();
            _hidden.Clear();
            _revision = -1;
        }

        private void Show(int id, bool visible)
        {
            if (_live.TryGetValue(id, out var standing) && standing.Object != null
                && standing.Object.activeSelf != visible)
            {
                standing.Object.SetActive(visible);
            }
        }

        private Standing Create(DocPiece piece)
        {
            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(piece.PrefabName) : null;
            if (prefab == null)
            {
                return null;
            }

            var copy = BlueprintPreview.Build(prefab, _root, PreviewStyle.Solid(_layer), null);
            copy.transform.localPosition = piece.Position;
            copy.transform.localRotation = piece.Rotation;
            Paint(copy);
            return new Standing
            {
                Object = copy,
                Prefab = piece.PrefabName,
                Position = piece.Position,
                Rotation = piece.Rotation,
            };
        }

        private sealed class Standing
        {
            public GameObject Object;
            public string Prefab;
            public Vector3 Position;
            public Quaternion Rotation;
        }
    }
}
