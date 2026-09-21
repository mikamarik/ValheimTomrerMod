using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ValheimTomrer.Blueprints;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The box the world capture draws while the player picks its two corners. It stands in the
    /// world, not in the editor's pane, so it goes on the game's "ghost" layer and is painted with
    /// the same see-through green as a piece about to be placed.
    ///
    /// Twelve thin bars, not a solid block: a solid one would hide the building inside it. The mesh
    /// is rebuilt in place every frame, so it costs one draw call however long the box is dragged.
    /// </summary>
    internal sealed class CaptureBox
    {
        /// <summary>Bar thickness. Thicker than the editor's, because this one is seen from metres away.</summary>
        private const float Bar = 0.1f;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();

        private GameObject _object;
        private Mesh _mesh;
        private Material _material;

        /// <summary>The box stands in the world right now.</summary>
        public bool Visible => _object != null && _object.activeSelf;

        /// <summary>Draws the box. The corners are world points, so the object itself never moves.</summary>
        public void Show(Bounds box)
        {
            Build();
            if (_object == null)
            {
                return;
            }

            _vertices.Clear();
            _triangles.Clear();
            SelectionBoxes.WireBox(box, Bar, _vertices, _triangles);
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
            if (!_object.activeSelf)
            {
                _object.SetActive(true);
            }
        }

        public void Hide()
        {
            if (_object != null && _object.activeSelf)
            {
                _mesh.Clear();
                _object.SetActive(false);
            }
        }

        public void Destroy()
        {
            if (_object != null)
            {
                Object.Destroy(_object);
            }

            if (_mesh != null)
            {
                Object.Destroy(_mesh);
            }

            if (_material != null)
            {
                Object.Destroy(_material);
            }

            _object = null;
            _mesh = null;
            _material = null;
        }

        private void Build()
        {
            if (_object != null)
            {
                return;
            }

            _object = new GameObject("ValheimTomrer_CaptureBox") { layer = BlueprintPreview.GhostLayer };
            _mesh = new Mesh { name = "ValheimTomrer_CaptureBox", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _material = GhostRenderer.NewMaterial();
            var renderer = _object.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _object.SetActive(false);
        }
    }
}
