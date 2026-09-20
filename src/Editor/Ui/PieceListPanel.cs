using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The second tab of the left panel: one row per piece in the open blueprint, with its index,
    /// icon, name, position and, when something is wrong with it, a short tag ("locked", "hoe piece").
    ///
    /// Clicking a row reports a selection through <see cref="PieceClicked"/>; shift-click means
    /// "toggle this one". Phase 6 owns the selection itself, this panel only draws it.
    /// </summary>
    internal static class PieceListPanel
    {
        private const float Pad = 10f;
        private const float RowHeight = 34f;
        private const float RowGap = 2f;

        private static RectTransform _host;
        private static RectTransform _root;
        private static ScrollRect _scroll;
        private static RectTransform _content;
        private static TextMeshProUGUI _footer;
        private static TextMeshProUGUI _emptyLabel;
        private static int _generation = -1;

        private static readonly List<Row> Pool = new List<Row>();
        private static readonly List<DocPiece> Rows = new List<DocPiece>();
        private static readonly HashSet<int> SelectedIds = new HashSet<int>();

        private static BlueprintDocument _document;
        private static int _revision = -1;
        private static int _firstRow = -1;
        private static int _filledCount = -1;
        private static float _lastHeight = -1f;

        /// <summary>A row was clicked: its piece id, and true when shift was held.</summary>
        public static Action<int, bool> PieceClicked;

        public static int RowCount => Rows.Count;

        /// <summary>Rows that exist as objects. Only the ones in view.</summary>
        public static int LiveRows => Pool.Count;

        public static IReadOnlyCollection<int> Selection => SelectedIds;

        public static string FooterText => _footer != null ? _footer.text : "";

        public static void Ensure(RectTransform host)
        {
            if (host == null)
            {
                return;
            }

            if (_host == host && _root != null && _generation == UiTheme.Generation)
            {
                return;
            }

            _host = host;
            _generation = UiTheme.Generation;
            Pool.Clear();
            _firstRow = -1;
            Build(host);
        }

        /// <summary>Puts a blueprint's pieces in the list. Null empties it.</summary>
        public static void Show(BlueprintDocument document)
        {
            _document = document;
            _revision = -1;
            Reload();
        }

        public static void Tick()
        {
            if (_root == null)
            {
                return;
            }

            if (_document != null && _document.Revision != _revision)
            {
                Reload();
                return;
            }

            var height = _root.rect.height;
            if (!Mathf.Approximately(height, _lastHeight))
            {
                _lastHeight = height > 40f ? height : -1f;
                _firstRow = -1;
                _filledCount = -1;
                FillRows();
            }
        }

        public static void Close()
        {
            _document = null;
            Rows.Clear();
            SelectedIds.Clear();
        }

        /// <summary>Phase 6 pushes its selection here; the rows only draw it.</summary>
        public static void SetSelection(IEnumerable<int> ids)
        {
            SelectedIds.Clear();
            if (ids != null)
            {
                foreach (var id in ids)
                {
                    SelectedIds.Add(id);
                }
            }

            foreach (var row in Pool)
            {
                row.Refresh();
            }
        }

        private static void Reload()
        {
            Rows.Clear();
            if (_document != null)
            {
                _revision = _document.Revision;
                foreach (var piece in _document.Pieces)
                {
                    Rows.Add(piece);
                }
            }

            if (_footer != null)
            {
                _footer.text = Rows.Count == 1 ? "1 piece." : $"{Rows.Count} pieces.";
            }

            if (_emptyLabel != null)
            {
                _emptyLabel.gameObject.SetActive(Rows.Count == 0);
            }

            _content.sizeDelta = new Vector2(0f, Rows.Count * (RowHeight + RowGap));
            _firstRow = -1;
            _filledCount = -1;
            FillRows();
        }

        private static void OnScrolled(Vector2 _)
        {
            FillRows();
        }

        private static void FillRows()
        {
            if (_scroll == null || _scroll.viewport == null)
            {
                return;
            }

            var step = RowHeight + RowGap;
            var offset = Mathf.Max(0f, _content.anchoredPosition.y);
            var first = Mathf.Max(0, Mathf.FloorToInt(offset / step));
            var onScreen = Mathf.CeilToInt(_scroll.viewport.rect.height / step) + 1;
            var need = Mathf.Min(onScreen, Mathf.Max(0, Rows.Count - first));

            while (Pool.Count < need)
            {
                Pool.Add(NewRow(_content));
            }

            if (first == _firstRow && need == _filledCount)
            {
                foreach (var row in Pool)
                {
                    row.Refresh();
                }

                return;
            }

            _firstRow = first;
            _filledCount = need;
            for (var i = 0; i < Pool.Count; i++)
            {
                var index = first + i;
                if (i >= need || index >= Rows.Count)
                {
                    Pool[i].Hide();
                    continue;
                }

                Pool[i].Show(Rows[index], index, -index * step);
            }
        }

        private static void OnRowClick(Row row, bool additive)
        {
            if (row.Piece == null)
            {
                return;
            }

            if (additive)
            {
                if (!SelectedIds.Remove(row.Piece.Id))
                {
                    SelectedIds.Add(row.Piece.Id);
                }
            }
            else
            {
                SelectedIds.Clear();
                SelectedIds.Add(row.Piece.Id);
            }

            foreach (var other in Pool)
            {
                other.Refresh();
            }

            PieceClicked?.Invoke(row.Piece.Id, additive);
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            _root = UiBuild.Rect("PieceList", host);
            UiBuild.Stretch(_root, Pad, Pad, Pad, Pad);

            _scroll = UiBuild.Scroll("Rows", _root);
            var grid = (RectTransform)_scroll.transform;
            grid.anchorMin = Vector2.zero;
            grid.anchorMax = Vector2.one;
            grid.offsetMin = new Vector2(0f, 22f);
            grid.offsetMax = Vector2.zero;

            _content = _scroll.content;
            UnityEngine.Object.Destroy(_content.GetComponent<VerticalLayoutGroup>());
            UnityEngine.Object.Destroy(_content.GetComponent<ContentSizeFitter>());
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _scroll.onValueChanged.AddListener(OnScrolled);

            _emptyLabel = UiBuild.Label("Empty", _scroll.viewport, "No piece in this blueprint.", 16f,
                TextAlignmentOptions.Top, UiTheme.TextDim);
            UiBuild.Stretch(_emptyLabel.rectTransform, 0f, 0f, 0f, 8f);

            _footer = UiBuild.Label("Footer", _root, "", 14f, TextAlignmentOptions.Left, UiTheme.TextDim);
            _footer.rectTransform.anchorMin = Vector2.zero;
            _footer.rectTransform.anchorMax = new Vector2(1f, 0f);
            _footer.rectTransform.pivot = new Vector2(0.5f, 0f);
            _footer.rectTransform.offsetMin = Vector2.zero;
            _footer.rectTransform.offsetMax = new Vector2(0f, 18f);
        }

        private static Row NewRow(Transform parent)
        {
            var background = UiBuild.Panel("Row", parent, UiTheme.ItemBackground);
            background.rectTransform.anchorMin = new Vector2(0f, 1f);
            background.rectTransform.anchorMax = new Vector2(1f, 1f);
            background.rectTransform.pivot = new Vector2(0.5f, 1f);
            background.rectTransform.offsetMin = new Vector2(0f, -RowHeight);
            background.rectTransform.offsetMax = Vector2.zero;

            var index = UiBuild.Label("Index", background.transform, "", 13f, TextAlignmentOptions.MidlineRight, UiTheme.TextDim);
            index.rectTransform.anchorMin = new Vector2(0f, 0f);
            index.rectTransform.anchorMax = new Vector2(0f, 1f);
            index.rectTransform.pivot = new Vector2(0f, 0.5f);
            index.rectTransform.offsetMin = new Vector2(2f, 0f);
            index.rectTransform.offsetMax = new Vector2(30f, 0f);

            var icon = UiBuild.Panel("Icon", background.transform, null);
            icon.type = Image.Type.Simple;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            icon.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            icon.rectTransform.pivot = new Vector2(0f, 0.5f);
            icon.rectTransform.anchoredPosition = new Vector2(34f, 0f);
            icon.rectTransform.sizeDelta = new Vector2(26f, 26f);

            var name = UiBuild.Label("Name", background.transform, "", 15f, TextAlignmentOptions.BottomLeft);
            Text(name.rectTransform, 0.5f, 1f);

            var detail = UiBuild.Label("Detail", background.transform, "", 12f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            Text(detail.rectTransform, 0f, 0.5f);

            var row = new Row
            {
                Rect = background.rectTransform,
                Background = background,
                Index = index,
                Icon = icon,
                Name = name,
                Detail = detail,
            };
            background.gameObject.AddComponent<RowEvents>().Owner = row;
            return row;
        }

        private static void Text(RectTransform rect, float bottom, float top)
        {
            rect.anchorMin = new Vector2(0f, bottom);
            rect.anchorMax = new Vector2(1f, top);
            rect.offsetMin = new Vector2(64f, 0f);
            rect.offsetMax = new Vector2(-4f, 0f);
        }

        private sealed class Row
        {
            public RectTransform Rect;
            public Image Background;
            public TextMeshProUGUI Index;
            public Image Icon;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Detail;
            public DocPiece Piece;

            public void Show(DocPiece piece, int index, float top)
            {
                Piece = piece;
                Rect.anchoredPosition = new Vector2(0f, top);
                Rect.gameObject.SetActive(true);

                var entry = PieceCatalog.Find(piece.PrefabName);
                Index.text = (index + 1).ToString();
                Icon.sprite = entry != null ? entry.Icon : null;
                Icon.enabled = Icon.sprite != null;
                Name.text = entry != null ? entry.DisplayName : piece.PrefabName;

                var at = piece.Position;
                var detail = $"{at.x:0.##}, {at.y:0.##}, {at.z:0.##}";
                var problem = PieceCatalog.Problem(piece.PrefabName);
                Detail.text = problem == null ? detail : detail + "   [" + problem + "]";
                Detail.color = problem == null ? UiTheme.TextDim : UiTheme.Warn;
                Refresh();
            }

            public void Refresh()
            {
                if (Piece == null)
                {
                    return;
                }

                Background.color = SelectedIds.Contains(Piece.Id) ? UiTheme.Accent : Color.white;
            }

            public void Hide()
            {
                Piece = null;
                Rect.gameObject.SetActive(false);
            }
        }

        private sealed class RowEvents : MonoBehaviour, IPointerClickHandler
        {
            public Row Owner;

            public void OnPointerClick(PointerEventData eventData)
            {
                var shift = ZInput.GetKey(KeyCode.LeftShift, false) || ZInput.GetKey(KeyCode.RightShift, false);
                OnRowClick(Owner, shift);
            }
        }
    }
}
