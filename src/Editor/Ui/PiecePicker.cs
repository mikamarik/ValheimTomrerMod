using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Input;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The controller's piece menu, like the game's build menu: a tab per usage tag, the pieces as
    /// icons in a 10 column grid, and the chosen piece's name, cost and station under it.
    ///
    /// The pad drives it in <see cref="PadBindings"/>: the D-pad or the left stick moves, L1 and R1
    /// change the tab, cross places, circle closes. The mouse and the arrow keys work too.
    ///
    /// The tiles are plain images with a pointer handler, never Buttons: a selectable tile would be
    /// picked up by the window's UIGroupHandler and cross would fire twice.
    /// </summary>
    internal static class PiecePicker
    {
        /// <summary>The same grid as the Tomrer editor (PICKER_COLUMNS).</summary>
        public const int Columns = 10;

        private const float Pad = 14f;
        private const float TileGap = 4f;
        private const float TabHeight = 32f;
        private const float TabGap = 4f;

        // Padding is the space inside a tab chip, between its border and the text.
        private const float TabPadX = 16f;
        private const float TabPadY = 7f;
        private const float InfoHeight = 74f;
        private const float Width = 860f;
        private const float Height = 620f;

        private static RectTransform _host;
        private static RectTransform _root;
        private static RectTransform _panel;
        private static RectTransform _tabRow;
        private static ScrollRect _scroll;
        private static RectTransform _content;
        private static TextMeshProUGUI _name;
        private static TextMeshProUGUI _cost;
        private static TextMeshProUGUI _station;
        private static int _generation = -1;

        private static readonly List<Tile> Pool = new List<Tile>();
        private static readonly List<Chip> Tabs = new List<Chip>();
        private static readonly List<PieceEntry> InTab = new List<PieceEntry>();

        private static int _catalogGeneration = -1;
        private static float _tileSize = 64f;
        private static int _lastTab = -1;
        private static int _lastIndex;

        /// <summary>Clicking or placing a piece. Wired to the same handler as the palette.</summary>
        public static Action<PieceEntry> PieceChosen;

        public static bool IsOpen => _root != null && _root.gameObject.activeSelf;

        /// <summary>The open tab, an index into <see cref="PieceCatalog.Tags"/>.</summary>
        public static int Tab { get; private set; }

        /// <summary>The highlighted piece in the open tab.</summary>
        public static int Index { get; private set; }

        public static int TabCount => Tabs.Count;

        /// <summary>Pieces in the open tab.</summary>
        public static int Count => InTab.Count;

        public static string TabName => Tab >= 0 && Tab < PieceCatalog.Tags.Count ? PieceCatalog.Tags[Tab] : "";

        public static PieceEntry Current => Index >= 0 && Index < InTab.Count ? InTab[Index] : null;

        /// <summary>Tiles that exist as objects, for the test.</summary>
        public static int LiveTiles => Pool.Count;

        public static void Ensure(RectTransform host)
        {
            if (host == null || (_host == host && _root != null && _generation == UiTheme.Generation))
            {
                return;
            }

            _host = host;
            _generation = UiTheme.Generation;
            Pool.Clear();
            Tabs.Clear();
            InTab.Clear();
            _catalogGeneration = -1;
            Build(host);
        }

        /// <summary>
        /// Opens on the piece in hand, else where it was last closed, else the Building tab, the
        /// same order as the Tomrer editor's openPicker.
        /// </summary>
        public static void Open()
        {
            PieceCatalog.Ensure();
            if (_root == null || PieceCatalog.Tags.Count == 0)
            {
                return;
            }

            BuildTabs();

            var tab = _lastTab;
            var index = _lastIndex;
            var held = HeldPrefab();
            if (held != null)
            {
                // Keep the tab it was last on when that tab has the piece, so it does not jump.
                if (tab < 0 || IndexOf(tab, held) < 0)
                {
                    tab = TabWith(held);
                }

                if (tab >= 0)
                {
                    index = IndexOf(tab, held);
                }
            }

            if (tab < 0)
            {
                tab = Mathf.Max(0, TagIndex("Building"));
                index = 0;
            }

            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            Show(tab, index);
        }

        /// <summary>Shuts the menu. True when it was up.</summary>
        public static bool Close()
        {
            if (!IsOpen)
            {
                return false;
            }

            _lastTab = Tab;
            _lastIndex = Index;
            _root.gameObject.SetActive(false);
            return true;
        }

        /// <summary>Once a frame: notice a new catalog.</summary>
        public static void Tick()
        {
            if (!IsOpen)
            {
                return;
            }

            PieceCatalog.Ensure();
            if (_catalogGeneration != PieceCatalog.Generation)
            {
                BuildTabs();
                Show(Tab, Index);
            }
        }

        /// <summary>The next or previous tab, around the ends like the game's L1 and R1.</summary>
        public static void NextTab(int step)
        {
            var count = Tabs.Count;
            if (!IsOpen || count == 0)
            {
                return;
            }

            Show(((Tab + step) % count + count) % count, 0);
        }

        /// <summary>Moves the highlight: left and right by one, up and down by a row.</summary>
        public static void Move(int dx, int dy)
        {
            if (IsOpen)
            {
                Show(Tab, Index + dx + (dy * Columns));
            }
        }

        /// <summary>Starts placing the highlighted piece and shuts the menu.</summary>
        public static bool Pick()
        {
            var entry = Current;
            if (entry == null)
            {
                return false;
            }

            _lastTab = Tab;
            _lastIndex = Index;
            PieceChosen?.Invoke(entry);
            Close();
            return true;
        }

        /// <summary>The arrow keys, Enter and Esc, so the menu also works without a pad.</summary>
        public static bool Key(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftArrow: Move(-1, 0); return true;
                case KeyCode.RightArrow: Move(1, 0); return true;
                case KeyCode.UpArrow: Move(0, -1); return true;
                case KeyCode.DownArrow: Move(0, 1); return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: Pick(); return true;
                default: return false;
            }
        }

        // ---------- inside ----------

        /// <summary>Shows a tab with one piece highlighted. Both are clamped to what is there.</summary>
        private static void Show(int tab, int index)
        {
            var tags = PieceCatalog.Tags;
            if (tags.Count == 0)
            {
                return;
            }

            Tab = Mathf.Clamp(tab, 0, tags.Count - 1);
            FillTab();
            Index = InTab.Count == 0 ? -1 : Mathf.Clamp(index, 0, InTab.Count - 1);

            for (var i = 0; i < Tabs.Count; i++)
            {
                Tabs[i].SetOn(i == Tab);
            }

            Layout();
            ScrollToIndex();
            Fill();
        }

        /// <summary>The pieces of the open tab, in the catalog's order.</summary>
        private static void FillTab()
        {
            InTab.Clear();
            var tag = TabName;
            foreach (var entry in PieceCatalog.Visible)
            {
                foreach (var name in entry.UsageTags)
                {
                    if (name == tag)
                    {
                        InTab.Add(entry);
                        break;
                    }
                }
            }
        }

        private static string HeldPrefab()
        {
            if (EditorState.Mode != EditMode.Place || EditorState.Action == PlaceAction.Move)
            {
                return null;
            }

            if (EditorState.Held != null)
            {
                return EditorState.Held.PrefabName;
            }

            var moving = EditorState.Moving;
            return moving != null && moving.Count == 1 ? moving.Pieces[0].Prefab : null;
        }

        private static int TagIndex(string tag)
        {
            for (var i = 0; i < PieceCatalog.Tags.Count; i++)
            {
                if (PieceCatalog.Tags[i] == tag)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int TabWith(string prefab)
        {
            for (var i = 0; i < PieceCatalog.Tags.Count; i++)
            {
                if (IndexOf(i, prefab) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Where a piece sits in a tab, or -1. Reads the same list <see cref="FillTab"/> builds.</summary>
        private static int IndexOf(int tab, string prefab)
        {
            if (tab < 0 || tab >= PieceCatalog.Tags.Count)
            {
                return -1;
            }

            var tag = PieceCatalog.Tags[tab];
            var at = 0;
            foreach (var entry in PieceCatalog.Visible)
            {
                var has = false;
                foreach (var name in entry.UsageTags)
                {
                    if (name == tag)
                    {
                        has = true;
                        break;
                    }
                }

                if (!has)
                {
                    continue;
                }

                if (entry.PrefabName == prefab)
                {
                    return at;
                }

                at++;
            }

            return -1;
        }

        // ---------- the grid ----------

        private static void Layout()
        {
            var width = Mathf.Max(_panel.rect.width - (Pad * 2f), 200f);
            var tabHeight = Flow(_tabRow, Tabs, width);
            Place(_tabRow, Pad, tabHeight);

            var top = Pad + tabHeight + 8f;
            var grid = (RectTransform)_scroll.transform;
            grid.anchorMin = Vector2.zero;
            grid.anchorMax = Vector2.one;
            grid.offsetMin = new Vector2(Pad, Pad + InfoHeight);
            grid.offsetMax = new Vector2(-Pad, -top);

            _tileSize = Mathf.Max(24f, (width - ((Columns - 1) * TileGap)) / Columns);
            var rows = Mathf.CeilToInt(InTab.Count / (float)Columns);
            _content.sizeDelta = new Vector2(0f, rows * (_tileSize + TileGap));
        }

        /// <summary>Every piece of the tab gets a tile. The objects are pooled between tabs.</summary>
        private static void Fill()
        {
            while (Pool.Count < InTab.Count)
            {
                Pool.Add(NewTile(_content));
            }

            var step = _tileSize + TileGap;
            for (var i = 0; i < Pool.Count; i++)
            {
                if (i >= InTab.Count)
                {
                    Pool[i].Hide();
                    continue;
                }

                Pool[i].Show(
                    InTab[i],
                    i,
                    new Vector2((i % Columns) * step, -(i / Columns) * step),
                    _tileSize,
                    i == Index);
            }

            var entry = Current;
            _name.text = entry == null ? "No piece in this tab" : entry.DisplayName;
            _cost.text = entry == null ? "" : CostLine(entry);
            _station.text = entry == null ? ""
                : string.IsNullOrEmpty(entry.StationName) ? "No station needed" : "Needs a " + entry.StationName;
        }

        private static string CostLine(PieceEntry entry)
        {
            if (entry.Cost.Length == 0)
            {
                return "Free";
            }

            var parts = new List<string>(entry.Cost.Length);
            foreach (var cost in entry.Cost)
            {
                parts.Add($"{cost.Amount} {cost.Name}");
            }

            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Keeps the highlighted row inside the scrolled view.</summary>
        private static void ScrollToIndex()
        {
            if (Index < 0 || _scroll.viewport == null)
            {
                return;
            }

            var step = _tileSize + TileGap;
            var top = (Index / Columns) * step;
            var bottom = top + _tileSize;
            var view = _scroll.viewport.rect.height;
            var at = Mathf.Max(0f, _content.anchoredPosition.y);
            if (top < at)
            {
                at = top;
            }
            else if (bottom > at + view)
            {
                at = bottom - view;
            }

            _content.anchoredPosition = new Vector2(0f, Mathf.Max(0f, at));
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            _root = UiBuild.Rect("PiecePicker", host);
            UiBuild.Stretch(_root);

            var backdrop = UiBuild.Panel("Backdrop", _root, null, new Color(0f, 0f, 0f, 0.5f));
            UiBuild.Stretch(backdrop.rectTransform);
            backdrop.gameObject.AddComponent<Button>().onClick.AddListener(() => Close());

            var panel = UiBuild.Panel("Menu", _root, UiTheme.Panel);
            _panel = panel.rectTransform;
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.anchoredPosition = Vector2.zero;
            _panel.sizeDelta = new Vector2(Width, Height);

            _tabRow = UiBuild.Rect("Tabs", _panel);

            _scroll = UiBuild.Scroll("Grid", _panel);
            _content = _scroll.content;
            UnityEngine.Object.DestroyImmediate(_content.GetComponent<VerticalLayoutGroup>());
            UnityEngine.Object.DestroyImmediate(_content.GetComponent<ContentSizeFitter>());
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);

            _name = UiBuild.Label("Name", _panel, "", 19f, TextAlignmentOptions.TopLeft, UiTheme.Accent);
            Bottom(_name.rectTransform, Pad + 46f, 24f);

            _cost = UiBuild.Label("Cost", _panel, "", 15f, TextAlignmentOptions.TopLeft);
            Bottom(_cost.rectTransform, Pad + 24f, 22f);
            _cost.overflowMode = TextOverflowModes.Ellipsis;

            _station = UiBuild.Label("Station", _panel, "", 14f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            Bottom(_station.rectTransform, Pad + 4f, 20f);

            _root.gameObject.SetActive(false);
        }

        /// <summary>Builds one chip per usage tag. They also switch the tab with the mouse.</summary>
        private static void BuildTabs()
        {
            if (_catalogGeneration == PieceCatalog.Generation && Tabs.Count == PieceCatalog.Tags.Count)
            {
                return;
            }

            _catalogGeneration = PieceCatalog.Generation;
            foreach (var chip in Tabs)
            {
                if (chip.Rect != null)
                {
                    UnityEngine.Object.DestroyImmediate(chip.Rect.gameObject);
                }
            }

            Tabs.Clear();

            // TextMeshPro measures nothing before its Awake, which waits for an active object.
            var wasOpen = _root.gameObject.activeSelf;
            _root.gameObject.SetActive(true);
            for (var i = 0; i < PieceCatalog.Tags.Count; i++)
            {
                var at = i;
                Tabs.Add(NewChip(_tabRow, PieceCatalog.Tags[i], () => Show(at, 0)));
            }

            _root.gameObject.SetActive(wasOpen);
        }

        private static Chip NewChip(Transform parent, string text, UnityEngine.Events.UnityAction onClick)
        {
            var background = UiBuild.Panel("Tab " + text, parent, UiTheme.Button);
            var button = background.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState
            {
                highlightedSprite = UiTheme.ButtonHighlight,
                pressedSprite = UiTheme.ButtonPressed,
            };
            button.onClick.AddListener(onClick);

            var label = UiBuild.Label("Text", background.transform, text, 14f, TextAlignmentOptions.Center);
            UiBuild.Stretch(label.rectTransform, TabPadX, TabPadY, TabPadX, TabPadY);

            var chip = new Chip
            {
                Rect = background.rectTransform,
                Background = background,
                Label = label,
                Width = Mathf.Max(Mathf.Ceil(label.GetPreferredValues(text, 4000f, 0f).x) + (2f * TabPadX) + 2f, 56f),
            };
            chip.SetOn(false);
            return chip;
        }

        private static Tile NewTile(Transform parent)
        {
            var background = UiBuild.Panel("Tile", parent, UiTheme.ItemBackground);
            background.rectTransform.anchorMin = new Vector2(0f, 1f);
            background.rectTransform.anchorMax = new Vector2(0f, 1f);
            background.rectTransform.pivot = new Vector2(0f, 1f);

            var icon = UiBuild.Panel("Icon", background.transform, null);
            icon.type = Image.Type.Simple;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            UiBuild.Stretch(icon.rectTransform, 4f, 4f, 4f, 4f);

            var name = UiBuild.Label("Name", background.transform, "", 9f, TextAlignmentOptions.Center, UiTheme.TextDim);
            UiBuild.Stretch(name.rectTransform, 2f, 2f, 2f, 2f);
            name.enableWordWrapping = true;
            name.gameObject.SetActive(false);

            var badge = UiBuild.Label("Badge", background.transform, "S", 11f, TextAlignmentOptions.Center, UiTheme.Accent);
            badge.rectTransform.anchorMin = new Vector2(1f, 1f);
            badge.rectTransform.anchorMax = new Vector2(1f, 1f);
            badge.rectTransform.pivot = new Vector2(1f, 1f);
            badge.rectTransform.anchoredPosition = new Vector2(-2f, -1f);
            badge.rectTransform.sizeDelta = new Vector2(12f, 14f);
            badge.gameObject.SetActive(false);

            var tile = new Tile
            {
                Rect = background.rectTransform,
                Background = background,
                Icon = icon,
                Name = name,
                Badge = badge,
            };
            background.gameObject.AddComponent<TileEvents>().Owner = tile;
            return tile;
        }

        /// <summary>Lays chips out left to right and wraps. Returns the height it used.</summary>
        private static float Flow(RectTransform row, List<Chip> chips, float width)
        {
            var x = 0f;
            var y = 0f;
            var used = chips.Count > 0 ? TabHeight : 0f;
            foreach (var chip in chips)
            {
                var chipWidth = Mathf.Min(chip.Width, width);
                if (x > 0f && x + chipWidth > width)
                {
                    x = 0f;
                    y += TabHeight + TabGap;
                    used = y + TabHeight;
                }

                chip.Rect.anchorMin = new Vector2(0f, 1f);
                chip.Rect.anchorMax = new Vector2(0f, 1f);
                chip.Rect.pivot = new Vector2(0f, 1f);
                chip.Rect.anchoredPosition = new Vector2(x, -y);
                chip.Rect.sizeDelta = new Vector2(chipWidth, TabHeight);
                x += chipWidth + TabGap;
            }

            return used;
        }

        private static void Place(RectTransform rect, float top, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(Pad, -top - height);
            rect.offsetMax = new Vector2(-Pad, -top);
        }

        private static void Bottom(RectTransform rect, float bottom, float height)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.offsetMin = new Vector2(Pad, bottom);
            rect.offsetMax = new Vector2(-Pad, bottom + height);
        }

        // ---------- small data holders ----------

        private sealed class Chip
        {
            public RectTransform Rect;
            public Image Background;
            public TextMeshProUGUI Label;
            public float Width;

            public void SetOn(bool on)
            {
                Background.color = on ? UiTheme.Accent : Color.white;
                Label.color = on ? UiTheme.TextOnAccent : UiTheme.Text;
            }
        }

        private sealed class Tile
        {
            public RectTransform Rect;
            public Image Background;
            public Image Icon;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI Badge;
            public PieceEntry Entry;
            public int Index = -1;

            public void Show(PieceEntry entry, int index, Vector2 position, float size, bool on)
            {
                Entry = entry;
                Index = index;
                Rect.anchoredPosition = position;
                Rect.sizeDelta = new Vector2(size, size);
                Rect.gameObject.SetActive(true);

                Icon.sprite = entry.Icon;
                Icon.enabled = entry.Icon != null;
                Name.gameObject.SetActive(entry.Icon == null);
                if (entry.Icon == null)
                {
                    Name.text = entry.PrefabName;
                }

                Badge.gameObject.SetActive(entry.Seasonal);
                Background.color = on ? UiTheme.Accent : Color.white;
            }

            public void Hide()
            {
                Entry = null;
                Index = -1;
                Rect.gameObject.SetActive(false);
            }
        }

        /// <summary>uGUI only sends pointer events to a component that asks for them.</summary>
        private sealed class TileEvents : MonoBehaviour, IPointerEnterHandler, IPointerClickHandler
        {
            public Tile Owner;

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (Owner != null && Owner.Index >= 0)
                {
                    Show(Tab, Owner.Index);
                }
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (Owner != null && Owner.Index >= 0)
                {
                    Show(Tab, Owner.Index);
                    Pick();
                }
            }
        }
    }
}
