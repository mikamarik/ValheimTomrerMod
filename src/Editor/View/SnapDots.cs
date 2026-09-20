using System.Collections.Generic;
using UnityEngine;
using ValheimTomrer.Editor.Placement;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The snap points while something is in hand: cyan for the ones on the pieces around the aim,
    /// yellow for the ones the piece in hand carries, orange for the pair that actually snapped.
    ///
    /// All the dots of one colour go in one mesh, rebuilt in place when the set changes, so a scene
    /// with thousands of snap points still costs three draw calls. Depth testing is off, so a dot
    /// behind a wall is still visible, which is the whole point of showing them.
    /// </summary>
    internal sealed class SnapDots
    {
        /// <summary>Caps, so a huge blueprint cannot turn one frame into a stall.</summary>
        public const int NearbyCap = 8192;

        public const int OwnCap = 1024;

        private const float NearbyRadius = 0.06f;
        private const float OwnRadius = 0.07f;
        private const float PairRadius = 0.12f;

        private static readonly Color NearbyColor = Hex(0x7FD4FF);
        private static readonly Color OwnColor = Hex(0xFFE066);
        private static readonly Color PairColor = Hex(0xFF8A3D);

        private static readonly Vector3[] Corners =
        {
            Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
        };

        /// <summary>The eight faces of a ball made of six points. Three pixels wide, so this is plenty.</summary>
        private static readonly int[] Faces =
        {
            0, 4, 3, 0, 3, 5, 0, 5, 2, 0, 2, 4,
            1, 3, 4, 1, 5, 3, 1, 2, 5, 1, 4, 2,
        };

        private readonly Transform _root;
        private readonly int _layer;
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();
        private readonly Vector3[] _pair = new Vector3[2];

        private OverlayMesh _nearby;
        private OverlayMesh _own;
        private OverlayMesh _pairMesh;

        public SnapDots(Transform root, int layer)
        {
            _root = root;
            _layer = layer;
        }

        /// <summary>Dots standing in the pane right now.</summary>
        public int Drawn { get; private set; }

        /// <summary>Draws the dots of one placing result. A null result clears them.</summary>
        public void Show(PlaceResult result)
        {
            if (result == null || _root == null)
            {
                Hide();
                return;
            }

            Build();
            Drawn = Fill(_nearby, result.NearbySnaps, NearbyRadius, NearbyCap)
                + Fill(_own, result.MovingSnaps, OwnRadius, OwnCap);

            if (result.Snapped)
            {
                _pair[0] = result.SnapFrom;
                _pair[1] = result.SnapTo;
                Drawn += Fill(_pairMesh, _pair, PairRadius, 2);
            }
            else
            {
                Fill(_pairMesh, null, PairRadius, 0);
            }
        }

        public void Hide()
        {
            Drawn = 0;
            _nearby?.Clear();
            _own?.Clear();
            _pairMesh?.Clear();
        }

        public void Destroy()
        {
            _nearby?.Destroy();
            _own?.Destroy();
            _pairMesh?.Destroy();
            _nearby = _own = _pairMesh = null;
            Drawn = 0;
        }

        private void Build()
        {
            if (_nearby != null)
            {
                return;
            }

            _nearby = new OverlayMesh("SnapDotsNearby", _root, _layer, NearbyColor);
            _own = new OverlayMesh("SnapDotsOwn", _root, _layer, OwnColor);
            _pairMesh = new OverlayMesh("SnapDotsPair", _root, _layer, PairColor);
        }

        /// <summary>One ball per point, all in one mesh. Gives back how many it drew.</summary>
        private int Fill(OverlayMesh mesh, IList<Vector3> points, float radius, int cap)
        {
            if (mesh == null)
            {
                return 0;
            }

            var count = points == null ? 0 : Mathf.Min(points.Count, cap);
            if (count == 0)
            {
                mesh.Clear();
                return 0;
            }

            _vertices.Clear();
            _triangles.Clear();
            for (var i = 0; i < count; i++)
            {
                var start = _vertices.Count;
                foreach (var corner in Corners)
                {
                    _vertices.Add(points[i] + (corner * radius));
                }

                foreach (var index in Faces)
                {
                    _triangles.Add(start + index);
                }
            }

            mesh.Set(_vertices, _triangles);
            return count;
        }

        private static Color Hex(int rgb)
        {
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0xFF);
        }
    }
}
