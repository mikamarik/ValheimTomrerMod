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
    /// The right panel's bottom region: everything that is wrong with the open blueprint, worst
    /// first, with a count in the heading. Clicking a row selects the pieces it is about.
    ///
    /// The list is worked out again whenever the document changes, so it follows every edit.
    /// </summary>
    internal static class ChecksPanel
    {
        private const float Pad = 4f;
        private const float HeadingHeight = 20f;

        private static RectTransform _host;
        private static RectTransform _root;
        private static int _generation = -1;

        private static TextMeshProUGUI _heading;
        private static TextMeshProUGUI _clean;
        private static ScrollRect _scroll;
        private static readonly List<Row> Pool = new List<Row>();
        private static readonly List<Check> Found = new List<Check>();

        private static BlueprintDocument _document;
        private static int _revision = -1;
        private static int _catalogGeneration = -1;

        /// <summary>The problems as they stand, worst first.</summary>
        public static IReadOnlyList<Check> Rows => Found;

        public static int RowCount => Found.Count;

        public static string HeadingText => _heading != null ? _heading.text : "";

        /// <summary>The text of one row as it is drawn, level word and all.</summary>
        public static string RowText(int index)
        {
            return index >= 0 && index < Pool.Count && Pool[index].Rect.gameObject.activeSelf
                ? Pool[index].Label.text
                : "";
        }

        /// <summary>What a click on a row does. The test calls it without a mouse.</summary>
        public static void Click(int index)
        {
            if (index < 0 || index >= Found.Count)
            {
                return;
            }

            var pieces = Found[index].Pieces;
            if (pieces != null && pieces.Length > 0)
            {
                EditorState.Select(pieces);
            }
        }

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
            Build(host);
        }

        public static void Show(BlueprintDocument document)
        {
            _document = document;
            _revision = -1;
            Refresh();
        }

        public static void Tick()
        {
            if (_root == null || _document == null)
            {
                return;
            }

            if (_document.Revision != _revision || PieceCatalog.Generation != _catalogGeneration)
            {
                Refresh();
            }
        }

        public static void Close()
        {
            _document = null;
            Found.Clear();
            _revision = -1;
        }

        /// <summary>Runs the checks again and redraws the list.</summary>
        public static void Refresh()
        {
            if (_root == null)
            {
                return;
            }

            _revision = _document != null ? _document.Revision : -1;
            _catalogGeneration = PieceCatalog.Generation;

            Found.Clear();
            Found.AddRange(Checks.Run(_document));

            var summary = Checks.Summary(Found);
            _heading.text = summary.Length > 0 ? "Checks   " + summary : "Checks";
            _clean.gameObject.SetActive(Found.Count == 0);

            while (Pool.Count < Found.Count)
            {
                Pool.Add(NewRow(Pool.Count));
            }

            for (var i = 0; i < Pool.Count; i++)
            {
                if (i < Found.Count)
                {
                    Pool[i].Show(Found[i]);
                }
                else
                {
                    Pool[i].Hide();
                }
            }
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            _root = UiBuild.Rect("Checks", host);
            UiBuild.Stretch(_root, 4f, 4f, 4f, 4f);

            _heading = UiBuild.Label("Heading", _root, "Checks", 17f, TextAlignmentOptions.Left, UiTheme.Accent);
            _heading.rectTransform.anchorMin = new Vector2(0f, 1f);
            _heading.rectTransform.anchorMax = new Vector2(1f, 1f);
            _heading.rectTransform.pivot = new Vector2(0.5f, 1f);
            _heading.rectTransform.offsetMin = new Vector2(0f, -HeadingHeight);
            _heading.rectTransform.offsetMax = Vector2.zero;

            _scroll = UiBuild.Scroll("Rows", _root, Pad);
            var rect = (RectTransform)_scroll.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = new Vector2(0f, -(HeadingHeight + Pad));

            _clean = UiBuild.Label("Clean", _scroll.viewport, "No problems found.", 14f,
                TextAlignmentOptions.TopLeft, UiTheme.Good);
            UiBuild.Stretch(_clean.rectTransform, 2f, 0f, 2f, 2f);
        }

        private static Row NewRow(int index)
        {
            var background = UiBuild.Panel("Row", _scroll.content, UiTheme.ItemBackground);
            var layout = background.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(6, 6, 4, 4);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var label = UiBuild.Label("Text", background.transform, "", 13f, TextAlignmentOptions.TopLeft);
            label.enableWordWrapping = true;
            label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var row = new Row { Rect = background.rectTransform, Background = background, Label = label, Index = index };
            background.gameObject.AddComponent<RowEvents>().Index = index;
            return row;
        }

        /// <summary>The level word carries the colour, so one text can hold the whole row.</summary>
        private static string LevelTag(Check check)
        {
            var colour = check.Level == CheckLevel.Error
                ? UiTheme.Warn
                : check.Level == CheckLevel.Warning ? UiTheme.Accent : UiTheme.TextDim;
            return $"<color=#{ColorUtility.ToHtmlStringRGB(colour)}>{check.LevelWord}</color>  ";
        }

        private sealed class Row
        {
            public RectTransform Rect;
            public Image Background;
            public TextMeshProUGUI Label;
            public int Index;

            public void Show(Check check)
            {
                Rect.gameObject.SetActive(true);
                Rect.SetSiblingIndex(Index);
                Label.text = LevelTag(check) + check.Message;
                Background.color = check.Pieces != null && check.Pieces.Length > 0
                    ? UiTheme.Slot
                    : UiTheme.SlotDim;
            }

            public void Hide()
            {
                Rect.gameObject.SetActive(false);
            }
        }

        private sealed class RowEvents : MonoBehaviour, IPointerClickHandler
        {
            public int Index;

            public void OnPointerClick(PointerEventData eventData)
            {
                Click(Index);
            }
        }
    }
}
