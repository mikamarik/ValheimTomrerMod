using System.Collections.Generic;
using UnityEngine;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The wire boxes in the pane: a gold one around every selected piece, a lighter one around the
    /// whole selection, a thin light one around the piece the aim is on, and, when the top bar's
    /// Boxes switch is on, a pale one around every piece in place of the models.
    ///
    /// Each box is twelve thin bars, not lines, because a one-pixel line disappears the moment the
    /// pane is drawn at anything but full size. All the bars of one colour go in a single mesh, so
    /// a selection of any size stays three draw calls. They ignore the depth buffer, so a box round
    /// a piece inside a house still shows.
    /// </summary>
    internal sealed class SelectionBoxes
    {
        /// <summary>More boxes than this and only the first ones are drawn: one mesh has its limits.</summary>
        public const int BoxCap = 512;

        private const float PieceBar = 0.03f;
        private const float GroupBar = 0.024f;
        private const float AimBar = 0.015f;
        private const float AllBar = 0.02f;
        private const float GroupPad = 0.08f;

        private static readonly Color PieceColor = Hex(0xFFC24A);
        private static readonly Color GroupColor = Hex(0xFFE9A8);
        private static readonly Color AimColor = Hex(0xE8F1F8);
        private static readonly Color AllColor = Hex(0xBFCAD6);

        private readonly Transform _parent;
        private readonly int _layer;
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();

        private OverlayMesh _pieces;
        private OverlayMesh _group;
        private OverlayMesh _aim;
        private OverlayMesh _all;
        private int _shown;
        private int _allShown;

        public SelectionBoxes(Transform parent, int layer)
        {
            _parent = parent;
            _layer = layer;
        }

        /// <summary>Boxes standing in the pane: one per selected piece, plus the group and the aim.</summary>
        public int BoxCount => _shown;

        /// <summary>Boxes drawn in place of the models, for the Boxes view.</summary>
        public int AllCount => _allShown;

        /// <summary>One box per piece, drawn instead of the models. An empty list switches it off.</summary>
        public void ShowAll(IList<Bounds> boxes)
        {
            Build();
            _allShown = boxes != null ? Mathf.Min(boxes.Count, BoxCap) : 0;
            Fill(_all, boxes, AllBar);
        }

        /// <summary>Draws the boxes. Empty lists switch the meshes off.</summary>
        public void Show(IList<Bounds> selected, Bounds? group, Bounds? aim)
        {
            Build();
            _shown = 0;

            if (selected != null && selected.Count > 0)
            {
                _shown += Mathf.Min(selected.Count, BoxCap);
            }

            Fill(_pieces, selected, PieceBar);

            var groupBox = new List<Bounds>(1);
            if (group.HasValue && selected != null && selected.Count > 1)
            {
                var box = group.Value;
                box.Expand(GroupPad * 2f);
                groupBox.Add(box);
                _shown++;
            }

            Fill(_group, groupBox, GroupBar);

            var aimBox = new List<Bounds>(1);
            if (aim.HasValue)
            {
                aimBox.Add(aim.Value);
                _shown++;
            }

            Fill(_aim, aimBox, AimBar);
        }

        public void Hide()
        {
            Show(null, null, null);
        }

        public void Destroy()
        {
            _pieces?.Destroy();
            _group?.Destroy();
            _aim?.Destroy();
            _all?.Destroy();
            _pieces = _group = _aim = _all = null;
            _shown = 0;
            _allShown = 0;
        }

        private void Build()
        {
            if (_pieces != null)
            {
                return;
            }

            _pieces = new OverlayMesh("SelectionBoxes", _parent, _layer, PieceColor);
            _group = new OverlayMesh("SelectionGroupBox", _parent, _layer, GroupColor);
            _aim = new OverlayMesh("AimBox", _parent, _layer, AimColor);
            _all = new OverlayMesh("PieceBoxes", _parent, _layer, AllColor);
        }

        /// <summary>Rebuilds one colour's mesh out of the boxes it has to draw.</summary>
        private void Fill(OverlayMesh wire, IList<Bounds> boxes, float bar)
        {
            if (wire == null)
            {
                return;
            }

            if (boxes == null || boxes.Count == 0)
            {
                wire.Clear();
                return;
            }

            _vertices.Clear();
            _triangles.Clear();
            var count = Mathf.Min(boxes.Count, BoxCap);
            for (var i = 0; i < count; i++)
            {
                WireBox(boxes[i], bar, _vertices, _triangles);
            }

            wire.Set(_vertices, _triangles);
        }

        /// <summary>
        /// Twelve bars round one box, each a thin cuboid so it stays visible at any size. Static,
        /// because the world capture draws its box with the same twelve bars.
        /// </summary>
        public static void WireBox(Bounds box, float bar, List<Vector3> vertices, List<int> triangles)
        {
            var min = box.min;
            var max = box.max;
            for (var axis = 0; axis < 3; axis++)
            {
                var u = (axis + 1) % 3;
                var v = (axis + 2) % 3;
                for (var corner = 0; corner < 4; corner++)
                {
                    var centre = Vector3.zero;
                    var size = Vector3.zero;
                    centre[axis] = (min[axis] + max[axis]) * 0.5f;
                    size[axis] = Mathf.Max(max[axis] - min[axis], 0f) + bar;
                    centre[u] = (corner & 1) == 0 ? min[u] : max[u];
                    size[u] = bar;
                    centre[v] = (corner & 2) == 0 ? min[v] : max[v];
                    size[v] = bar;
                    AddBar(new Bounds(centre, size), vertices, triangles);
                }
            }
        }

        private static void AddBar(Bounds bar, List<Vector3> vertices, List<int> triangles)
        {
            var start = vertices.Count;
            var c = bar.center;
            var e = bar.extents;
            for (var i = 0; i < 8; i++)
            {
                vertices.Add(new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z)));
            }

            foreach (var index in CubeTriangles)
            {
                triangles.Add(start + index);
            }
        }

        /// <summary>The twelve triangles of a cuboid, wound so it is solid from the outside.</summary>
        private static readonly int[] CubeTriangles =
        {
            0, 2, 3, 0, 3, 1,   // -z
            5, 7, 6, 5, 6, 4,   // +z
            4, 6, 2, 4, 2, 0,   // -x
            1, 3, 7, 1, 7, 5,   // +x
            0, 1, 5, 0, 5, 4,   // -y
            2, 6, 7, 2, 7, 3,   // +y
        };

        private static Color Hex(int rgb)
        {
            return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0xFF);
        }

    }
}
