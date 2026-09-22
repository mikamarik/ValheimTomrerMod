using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Blueprints;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The materials list: one row per item (icon, name, have / need, a bar), one per station, and a
    /// footer line ("Can build now: 34 of 120 pieces", <see cref="FooterShown"/>). More than
    /// <see cref="OneColumnRows"/> rows go in two columns, when the caller allows two. No row is ever hidden.
    ///
    /// Knows nothing about where it sits: the caller gives the parent, the width, the most columns and
    /// the least height, and reads <see cref="Height"/> back. Rows are pooled, and a text is set only
    /// when it changed, so <see cref="Show"/> can be called often. Every label is on
    /// <see cref="UiTheme.FontMaterial"/>.
    /// </summary>
    internal sealed class MaterialList
    {
        public const float RowHeight = 30f;
        public const float RowGap = 4f;
        public const float IconBack = 30f;
        public const float IconSize = 26f;
        public const float TextSize = 16f;
        public const float BarHeight = 3f;
        public const float FooterSize = 15f;
        public const int OneColumnRows = 10;

        /// <summary>Between the icon and the text, and between the name and the numbers.</summary>
        private const float Gap = 6f;

        /// <summary>Between two columns.</summary>
        private const float ColumnGap = 16f;

        /// <summary>Between the rows and the footer's line, and from the line to the footer.</summary>
        private const float FooterGap = 7f;

        private static readonly Color BarTrack = new Color(0f, 0f, 0f, 0.35f);
        private static readonly Color Line = new Color(1f, 1f, 1f, 0.25f);

        private readonly List<Row> _rows = new List<Row>();
        private TextMeshProUGUI _footer;
        private Image _line;
        private int _shown = -1;
        private int _columns;
        private float _laidOutWidth = -1f;
        private float _laidOutMinHeight = -1f;
        private bool _laidOutFooter = true;
        private float _haveWidth;
        private float _needWidth;

        private MaterialList()
        {
        }

        public RectTransform Root { get; private set; }

        /// <summary>The whole width, both columns. Set by the caller; the list lays out again when it changes.</summary>
        public float Width { get; set; }

        /// <summary>1 keeps every row in one column, whatever the count.</summary>
        public int MaxColumns { get; set; } = 2;

        /// <summary>
        /// The least height. When the rows need less, the footer goes to the bottom and the spare room
        /// is left above its line, never under it. 0: only as tall as the rows and the footer.
        /// </summary>
        public float MinHeight { get; set; }

        /// <summary>
        /// False hides the footer and its line: the list is then only the rows. The editor's panel
        /// does this, as it has no spot to plan "Can build now" at.
        /// </summary>
        public bool FooterShown { get; set; } = true;

        /// <summary>What the rows and the footer take, at least <see cref="MinHeight"/>. After <see cref="Show"/>.</summary>
        public float Height { get; private set; }

        public int Columns => _columns;

        public bool Visible => Root != null && Root.gameObject.activeSelf;

        /// <summary>The rows on show, items first, then stations. For the tests.</summary>
        public IEnumerable<Row> ShownRows
        {
            get
            {
                for (var i = 0; i < _shown && i < _rows.Count; i++)
                {
                    yield return _rows[i];
                }
            }
        }

        public TextMeshProUGUI Footer => _footer;

        /// <summary>Columns a list of this many rows takes.</summary>
        public static int ColumnsFor(int rows, int maxColumns)
        {
            return rows > OneColumnRows && maxColumns >= 2 ? 2 : 1;
        }

        public static int RowCount(Tally tally)
        {
            return tally.Rows.Count + tally.Stations.Count;
        }

        /// <summary>A new, empty list under <paramref name="parent"/>, top left at the parent's top left. Needs <see cref="UiTheme.Ensure"/> first.</summary>
        public static MaterialList Create(Transform parent, float width, int maxColumns = 2)
        {
            var list = new MaterialList { Width = width, MaxColumns = maxColumns };
            var root = UiBuild.Rect("MaterialList", parent);
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0f, 1f);
            root.anchoredPosition = Vector2.zero;
            list.Root = root;

            list._line = UiBuild.Panel("Line", root, null, Line);
            list._line.type = Image.Type.Simple;
            list._line.raycastTarget = false;
            TopLeft(list._line.rectTransform);

            list._footer = UiBuild.Label("Footer", root, "", FooterSize, TextAlignmentOptions.TopLeft);
            list._footer.fontStyle = FontStyles.Bold;
            list._footer.enableWordWrapping = true;
            TopLeft(list._footer.rectTransform);
            return list;
        }

        public void Hide()
        {
            if (Root != null && Root.gameObject.activeSelf)
            {
                Root.gameObject.SetActive(false);
            }
        }

        /// <summary>Shows these numbers. Only what changed is written.</summary>
        public void Show(Tally tally)
        {
            if (Root == null)
            {
                return;
            }

            if (!Root.gameObject.activeSelf)
            {
                Root.gameObject.SetActive(true);
            }

            var count = RowCount(tally);
            while (_rows.Count < count)
            {
                _rows.Add(NewRow(Root));
            }

            // Texts first: the number columns are as wide as the widest number.
            var numbersChanged = false;
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (i < tally.Rows.Count)
                {
                    numbersChanged |= row.ShowItem(tally.Rows[i], tally.CostsOff);
                }
                else if (i < count)
                {
                    numbersChanged |= row.ShowStation(tally.Stations[i - tally.Rows.Count]);
                }
                else
                {
                    row.Rect.gameObject.SetActive(false);
                }
            }

            var columns = ColumnsFor(count, MaxColumns);
            var relaid = numbersChanged || count != _shown || columns != _columns || !Mathf.Approximately(Width, _laidOutWidth)
                || !Mathf.Approximately(MinHeight, _laidOutMinHeight) || FooterShown != _laidOutFooter;
            if (relaid)
            {
                _shown = count;
                _columns = columns;
                _laidOutWidth = Width;
                _laidOutMinHeight = MinHeight;
                _laidOutFooter = FooterShown;
                MeasureNumbers(count);
                Layout(count);
            }

            ShowFooter(tally, relaid);
        }

        /// <summary>"12.3k" from 10 000 on, so a big number never pushes the name out.</summary>
        public static string Short(int value)
        {
            return value >= 10000
                ? (value / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                : value.ToString(CultureInfo.InvariantCulture);
        }

        private float ColumnWidth => _columns <= 1 ? Width : (Width - ((_columns - 1) * ColumnGap)) / _columns;

        private void MeasureNumbers(int count)
        {
            _haveWidth = 0f;
            _needWidth = 0f;
            for (var i = 0; i < count; i++)
            {
                var row = _rows[i];
                if (row.IsStation)
                {
                    continue;
                }

                _haveWidth = Mathf.Max(_haveWidth, row.Have.GetPreferredValues(row.Have.text).x);
                _needWidth = Mathf.Max(_needWidth, row.Need.GetPreferredValues(row.Need.text).x);
            }

            _haveWidth = Mathf.Ceil(_haveWidth) + 1f;
            _needWidth = Mathf.Ceil(_needWidth) + 1f;
        }

        /// <summary>Column by column, top to bottom. The first column takes the extra row.</summary>
        private void Layout(int count)
        {
            var column = ColumnWidth;
            var perColumn = _columns <= 1 ? count : (count + 1) / 2;
            for (var i = 0; i < count; i++)
            {
                var at = perColumn > 0 ? i / perColumn : 0;
                var down = perColumn > 0 ? i % perColumn : 0;
                _rows[i].Place(at, new Vector2(at * (column + ColumnGap), -down * (RowHeight + RowGap)), column, _haveWidth, _needWidth);
            }

            RowsHeight = perColumn > 0 ? (perColumn * (RowHeight + RowGap)) - RowGap : 0f;
        }

        private float RowsHeight { get; set; }

        private void ShowFooter(Tally tally, bool relaid)
        {
            SetOn(_footer.gameObject, FooterShown);
            SetOn(_line.gameObject, FooterShown);
            if (!FooterShown)
            {
                if (relaid)
                {
                    Height = Mathf.Max(RowsHeight, MinHeight);
                    Root.sizeDelta = new Vector2(Width, Height);
                }

                return;
            }

            string text;
            Color colour;
            if (tally.CanBuildNow < 0)
            {
                text = tally.Continuing ? $"Built {tally.BuiltCount} of {tally.Total}." : $"{tally.Total} pieces.";
                colour = UiTheme.Text;
            }
            else
            {
                var all = tally.Continuing ? tally.Missing : tally.Total;
                text = tally.Continuing
                    ? $"Built {tally.BuiltCount} of {tally.Total}. Can build now: {tally.CanBuildNow} more."
                    : $"Can build now: {tally.CanBuildNow} of {tally.Total} pieces";
                colour = tally.CanBuildNow >= all && all > 0 ? UiTheme.Good
                    : tally.CanBuildNow > 0 ? UiTheme.Accent
                    : UiTheme.Warn;
            }

            var changed = SetText(_footer, text);
            if (_footer.color != colour)
            {
                _footer.color = colour;
            }

            if (!changed && !relaid)
            {
                return;
            }

            // The footer spans every column and wraps when it has to, so it is measured, not assumed.
            // It sits at the bottom, its line just over it; spare room from MinHeight goes above the line.
            var width = Width;
            var footerHeight = Mathf.Ceil(_footer.GetPreferredValues(text, width, 0f).y);
            var needed = RowsHeight + (RowsHeight > 0f ? FooterGap : 0f) + 1f + FooterGap + footerHeight;
            Height = Mathf.Max(needed, MinHeight);
            Size(_footer.rectTransform, 0f, -(Height - footerHeight), width, footerHeight);
            Size(_line.rectTransform, 0f, -(Height - footerHeight - FooterGap - 1f), width, 1f);
            Root.sizeDelta = new Vector2(Width, Height);
        }

        /// <summary>Sets a label's text when it differs. True when it did.</summary>
        private static bool SetText(TMP_Text label, string text)
        {
            if (label.text == text)
            {
                return false;
            }

            label.text = text;
            return true;
        }

        private static void SetOn(GameObject go, bool on)
        {
            if (go.activeSelf != on)
            {
                go.SetActive(on);
            }
        }

        private static void TopLeft(RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
        }

        private static void Size(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static Row NewRow(Transform parent)
        {
            var rect = UiBuild.Rect("Row", parent);
            TopLeft(rect);

            var back = UiBuild.Panel("IconBack", rect, UiTheme.ItemBackground);
            back.raycastTarget = false;
            TopLeft(back.rectTransform);
            Size(back.rectTransform, 0f, 0f, IconBack, IconBack);

            var icon = UiBuild.Panel("Icon", back.transform, null);
            icon.type = Image.Type.Simple;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = icon.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            icon.rectTransform.anchoredPosition = Vector2.zero;
            icon.rectTransform.sizeDelta = new Vector2(IconSize, IconSize);

            var name = UiBuild.Label("Name", rect, "", TextSize, TextAlignmentOptions.MidlineLeft);
            name.enableWordWrapping = false;
            name.overflowMode = TextOverflowModes.Ellipsis;
            TopLeft(name.rectTransform);

            var have = UiBuild.Label("Have", rect, "", TextSize, TextAlignmentOptions.MidlineRight);
            have.fontStyle = FontStyles.Bold;
            have.enableWordWrapping = false;
            TopLeft(have.rectTransform);

            var need = UiBuild.Label("Need", rect, "", TextSize, TextAlignmentOptions.MidlineLeft);
            need.fontStyle = FontStyles.Bold;
            need.enableWordWrapping = false;
            TopLeft(need.rectTransform);

            var state = UiBuild.Label("State", rect, "", TextSize, TextAlignmentOptions.MidlineRight);
            state.fontStyle = FontStyles.Bold;
            state.enableWordWrapping = false;
            TopLeft(state.rectTransform);

            var track = UiBuild.Panel("Bar", rect, null, BarTrack);
            track.type = Image.Type.Simple;
            track.raycastTarget = false;
            TopLeft(track.rectTransform);

            var fill = UiBuild.Panel("Fill", track.transform, null, UiTheme.Good);
            fill.type = Image.Type.Simple;
            fill.raycastTarget = false;
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = Vector2.zero;

            return new Row
            {
                Rect = rect,
                Icon = icon,
                Name = name,
                Have = have,
                Need = need,
                State = state,
                Bar = track,
                Fill = fill,
            };
        }

        /// <summary>One pooled row. An item row shows the numbers and the bar, a station row its state.</summary>
        internal sealed class Row
        {
            /// <summary>The text line is this high; the bar sits under it, at the bottom of the row.</summary>
            private const float LineHeight = RowHeight - BarHeight - 2f;

            public RectTransform Rect;
            public Image Icon;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Have;
            public TextMeshProUGUI Need;
            public TextMeshProUGUI State;
            public Image Bar;
            public Image Fill;

            public bool IsStation { get; private set; }

            /// <summary>0 for the left column, 1 for the right.</summary>
            public int Column { get; private set; }

            /// <summary>The item or station token this row shows. For the tests.</summary>
            public string Key { get; private set; }

            /// <summary>Shows an item. True when a number's text changed (the columns may need to widen).</summary>
            public bool ShowItem(TallyRow item, bool costsOff)
            {
                Activate();
                Key = item.Item;
                var kindChanged = IsStation;
                IsStation = false;
                SetIcon(item.Icon);
                SetText(Name, item.Name);
                var enough = costsOff || item.Enough;
                var colour = enough ? UiTheme.Good : UiTheme.Warn;
                var changed = SetText(Have, Short(item.Have)) | SetText(Need, " / " + Short(item.Need));
                if (Have.color != colour)
                {
                    Have.color = colour;
                }

                if (Fill.color != colour)
                {
                    Fill.color = colour;
                }

                var part = costsOff ? 1f : item.Need <= 0 ? 1f : Mathf.Clamp01(item.Have / (float)item.Need);
                if (!Mathf.Approximately(Fill.rectTransform.anchorMax.x, part))
                {
                    Fill.rectTransform.anchorMax = new Vector2(part, 1f);
                }

                ShowParts(true);
                return changed || kindChanged;
            }

            /// <summary>Shows a station: its name and "in range", "not in range", "in blueprint" or "not needed".</summary>
            public bool ShowStation(StationRow station)
            {
                Activate();
                Key = station.Station;
                var kindChanged = !IsStation;
                IsStation = true;
                SetIcon(station.Icon);
                SetText(Name, station.Name);
                string text;
                Color colour;
                switch (station.State)
                {
                    case StationState.InBlueprint:
                        text = "in blueprint";
                        colour = UiTheme.Accent;
                        break;
                    case StationState.InRange:
                        text = "in range";
                        colour = UiTheme.Good;
                        break;
                    case StationState.NotNeeded:
                        text = "not needed";
                        colour = UiTheme.Good;
                        break;
                    default:
                        text = "not in range";
                        colour = UiTheme.Warn;
                        break;
                }

                var changed = SetText(State, text);
                if (State.color != colour)
                {
                    State.color = colour;
                }

                ShowParts(false);
                return changed || kindChanged;
            }

            /// <summary>Puts the row at its spot and sizes its parts to the column.</summary>
            public void Place(int column, Vector2 at, float width, float haveWidth, float needWidth)
            {
                Column = column;
                Size(Rect, at.x, at.y, width, RowHeight);
                var textLeft = IconBack + Gap;
                if (IsStation)
                {
                    var stateWidth = Mathf.Ceil(State.GetPreferredValues(State.text).x) + 1f;
                    Size(State.rectTransform, width - stateWidth, 0f, stateWidth, RowHeight);
                    Size(Name.rectTransform, textLeft, 0f, Mathf.Max(0f, width - stateWidth - Gap - textLeft), RowHeight);
                    return;
                }

                Size(Need.rectTransform, width - needWidth, 0f, needWidth, LineHeight);
                Size(Have.rectTransform, width - needWidth - haveWidth, 0f, haveWidth, LineHeight);
                Size(Name.rectTransform, textLeft, 0f, Mathf.Max(0f, width - needWidth - haveWidth - Gap - textLeft), LineHeight);
                Size(Bar.rectTransform, textLeft, -(RowHeight - BarHeight), Mathf.Max(0f, width - textLeft), BarHeight);
            }

            private void Activate()
            {
                if (!Rect.gameObject.activeSelf)
                {
                    Rect.gameObject.SetActive(true);
                }
            }

            private void SetIcon(Sprite sprite)
            {
                if (Icon.sprite != sprite)
                {
                    Icon.sprite = sprite;
                }

                var on = sprite != null;
                if (Icon.enabled != on)
                {
                    Icon.enabled = on;
                }
            }

            private void ShowParts(bool item)
            {
                SetOn(Have.gameObject, item);
                SetOn(Need.gameObject, item);
                SetOn(Bar.gameObject, item);
                SetOn(State.gameObject, !item);
            }

            private static void SetOn(GameObject go, bool on)
            {
                if (go.activeSelf != on)
                {
                    go.SetActive(on);
                }
            }
        }
    }
}
