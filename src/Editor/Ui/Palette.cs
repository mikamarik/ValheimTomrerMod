using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimTomrer.Editor.Catalog;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The Pieces tab: an icon grid over the catalog, with a search box, a chip per usage tag and
    /// a collapsible material filter. Hovering a tile opens a card with the piece's details.
    ///
    /// The grid is virtualised. Only the rows inside the viewport exist as objects, so 398 icons
    /// scroll on a handful of widgets instead of 398.
    /// </summary>
    internal static class Palette
    {
        private const float Pad = 10f;
        private const float Gap = 6f;
        private const int Columns = 4;
        private const float TileGap = 4f;
        private const float ChipHeight = 28f;
        private const float ChipGap = 4f;
        private const float SearchHeight = 32f;
        private const float FooterHeight = 18f;
        private const float CardWidth = 320f;

        private static RectTransform _host;
        private static RectTransform _root;
        private static int _generation = -1;

        private static TMP_InputField _search;
        private static RectTransform _tagRow;
        private static RectTransform _materialButton;
        private static TextMeshProUGUI _materialLabel;
        private static RectTransform _materialRow;
        private static ScrollRect _materialScroll;
        private static ScrollRect _scroll;
        private static RectTransform _content;
        private static TextMeshProUGUI _footer;
        private static TextMeshProUGUI _emptyLabel;

        private static readonly List<PieceEntry> Shown = new List<PieceEntry>();
        private static readonly List<Tile> Pool = new List<Tile>();
        private static readonly List<Chip> TagChips = new List<Chip>();
        private static readonly List<Chip> MaterialChips = new List<Chip>();
        private static readonly List<CostMaterial> Materials = new List<CostMaterial>();

        private static string[] _words = Array.Empty<string>();
        private static string _tag;
        private static string _material;
        private static bool _materialsOpen;
        private static int _catalogGeneration = -1;
        private static bool _showAll;
        private static float _lastWidth = -1f;
        private static float _lastHeight = -1f;
        private static int _firstRow = -1;
        private static int _filledCount = -1;
        private static float _filledSize = -1f;
        private static float _tileSize = 64f;

        private static Card _card;
        private static PieceEntry _hovered;

        /// <summary>Clicking a tile. Phase 5 hangs "start placing this piece" on it.</summary>
        public static Action<PieceEntry> PieceChosen;

        /// <summary>The tile drawn as picked. Set by whoever owns the placing mode.</summary>
        public static PieceEntry Selected { get; set; }

        public static int ShownCount => Shown.Count;

        public static int TotalCount => PieceCatalog.Visible.Count;

        /// <summary>Tiles that exist as objects. Far under <see cref="ShownCount"/> when scrolling.</summary>
        public static int LiveTiles => Pool.Count;

        public static string FooterText => _footer != null ? _footer.text : "";

        public static int TagChipCount => TagChips.Count;

        public static int MaterialChipCount => MaterialChips.Count;

        public static string Search => _search != null ? _search.text : "";

        public static string Tag => _tag;

        public static PieceEntry Hovered => _hovered;

        public static bool CardVisible => _card != null && _card.Root != null && _card.Root.gameObject.activeSelf;

        /// <summary>Builds the tab's widgets. Safe to call again: it rebuilds after a world load.</summary>
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
            TagChips.Clear();
            MaterialChips.Clear();
            _card = null;
            _firstRow = -1;
            Build(host);
        }

        /// <summary>Reads the catalog and fills the grid. Called when the editor opens.</summary>
        public static void Show()
        {
            PieceCatalog.Ensure();
            _catalogGeneration = -1;
            Refresh();
        }

        /// <summary>Once a frame while the editor is open: notice a new catalog, config or size.</summary>
        public static void Tick()
        {
            if (_root == null)
            {
                return;
            }

            PieceCatalog.Ensure();
            var showAll = EditorConfig.ShowAllPieces != null && EditorConfig.ShowAllPieces.Value;
            if (_catalogGeneration != PieceCatalog.Generation || _showAll != showAll)
            {
                _catalogGeneration = PieceCatalog.Generation;
                _showAll = showAll;
                Refresh();
                return;
            }

            var rect = _root.rect;
            if (!Mathf.Approximately(rect.width, _lastWidth) || !Mathf.Approximately(rect.height, _lastHeight))
            {
                Relayout();
            }
        }

        public static void Close()
        {
            // The entries belong to the catalog of the world that is closing.
            Selected = null;
            HideCard();
        }

        // ---------- filtering ----------

        public static void SetSearch(string text)
        {
            if (_search != null && _search.text != text)
            {
                _search.text = text ?? "";     // fires onValueChanged, which filters
                return;
            }

            Filter();
        }

        public static void SetTag(string tag)
        {
            _tag = tag;
            foreach (var chip in TagChips)
            {
                chip.SetOn(chip.Key == tag || (tag == null && chip.Key == null));
            }

            Filter();
        }

        public static void SetMaterial(string token)
        {
            _material = token;
            foreach (var chip in MaterialChips)
            {
                chip.SetOn(chip.Key == token);
            }

            UpdateMaterialLabel();
            Filter();
        }

        public static void SetMaterialsOpen(bool open)
        {
            _materialsOpen = open;
            UpdateMaterialLabel();
            Relayout();
        }

        /// <summary>The whole tab: chips, filter, layout, tiles.</summary>
        private static void Refresh()
        {
            if (TagChips.Count != PieceCatalog.Tags.Count + 1)
            {
                BuildTagChips();
            }

            BuildMaterials();
            Filter();
        }

        private static void Filter()
        {
            _words = SplitWords(_search != null ? _search.text : "");
            Shown.Clear();
            foreach (var entry in PieceCatalog.Visible)
            {
                if (Matches(entry))
                {
                    Shown.Add(entry);
                }
            }

            if (_footer != null)
            {
                _footer.text = $"{Shown.Count} of {PieceCatalog.Visible.Count} pieces.";
            }

            if (_emptyLabel != null)
            {
                _emptyLabel.gameObject.SetActive(Shown.Count == 0);
            }

            _firstRow = -1;
            _filledCount = -1;
            HideCard();
            Relayout();
        }

        private static bool Matches(PieceEntry entry)
        {
            if (_tag != null && !HasTag(entry, _tag))
            {
                return false;
            }

            if (_material != null && !HasMaterial(entry, _material))
            {
                return false;
            }

            foreach (var word in _words)
            {
                if (entry.SearchText.IndexOf(word, StringComparison.Ordinal) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasTag(PieceEntry entry, string tag)
        {
            foreach (var name in entry.UsageTags)
            {
                if (name == tag)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasMaterial(PieceEntry entry, string token)
        {
            foreach (var cost in entry.Cost)
            {
                if (cost.Token == token)
                {
                    return true;
                }
            }

            return false;
        }

        private static string[] SplitWords(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            return text.ToLowerInvariant().Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // ---------- layout ----------

        /// <summary>
        /// Stacks search, chips, materials, grid and footer from the top down. The chip rows wrap,
        /// so their height is only known after they are placed; the grid gets whatever is left.
        /// </summary>
        private static void Relayout()
        {
            if (_root == null)
            {
                return;
            }

            // A canvas that was just switched on reports a zero rect for one frame. Do not record
            // that size, or the tick would think the layout is already right.
            var rect = _root.rect;
            _lastWidth = rect.width > 40f ? rect.width : -1f;
            _lastHeight = rect.height > 40f ? rect.height : -1f;
            var width = Mathf.Max(rect.width, 80f);

            var y = 0f;
            Place(_search.GetComponent<RectTransform>(), y, SearchHeight);
            y += SearchHeight + Gap;

            var tagHeight = Flow(_tagRow, TagChips, width);
            Place(_tagRow, y, tagHeight);
            y += tagHeight + Gap;

            Place(_materialButton, y, ChipHeight);
            y += ChipHeight + (_materialsOpen ? ChipGap : Gap);

            _materialScroll.gameObject.SetActive(_materialsOpen);
            if (_materialsOpen)
            {
                // There are over a hundred materials. Show a few rows and scroll the rest, or the
                // filter would take the whole panel and leave no room for the pieces.
                var materialHeight = Flow(_materialRow, MaterialChips, width);
                _materialRow.sizeDelta = new Vector2(0f, materialHeight);
                var pane = Mathf.Min(materialHeight, Mathf.Max(3f * ChipHeight, rect.height * 0.28f));
                Place((RectTransform)_materialScroll.transform, y, pane);
                y += pane + Gap;
            }

            var grid = (RectTransform)_scroll.transform;
            grid.anchorMin = Vector2.zero;
            grid.anchorMax = Vector2.one;
            grid.offsetMin = new Vector2(0f, FooterHeight + Gap);
            grid.offsetMax = new Vector2(0f, -y);

            _tileSize = Mathf.Max(24f, (width - (Columns - 1) * TileGap) / Columns);
            var rows = Mathf.CeilToInt(Shown.Count / (float)Columns);
            _content.sizeDelta = new Vector2(0f, rows * (_tileSize + TileGap));
            _firstRow = -1;
            _filledCount = -1;
            FillRows();
        }

        private static void Place(RectTransform rect, float top, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, -top - height);
            rect.offsetMax = new Vector2(0f, -top);
        }

        /// <summary>Lays chips out left to right and wraps. Returns the height it used.</summary>
        private static float Flow(RectTransform row, List<Chip> chips, float width)
        {
            var x = 0f;
            var y = 0f;
            var used = chips.Count > 0 ? ChipHeight : 0f;
            foreach (var chip in chips)
            {
                var chipWidth = Mathf.Min(chip.Width, width);
                if (x > 0f && x + chipWidth > width)
                {
                    x = 0f;
                    y += ChipHeight + ChipGap;
                    used = y + ChipHeight;
                }

                chip.Rect.anchorMin = new Vector2(0f, 1f);
                chip.Rect.anchorMax = new Vector2(0f, 1f);
                chip.Rect.pivot = new Vector2(0f, 1f);
                chip.Rect.anchoredPosition = new Vector2(x, -y);
                chip.Rect.sizeDelta = new Vector2(chipWidth, ChipHeight);
                x += chipWidth + ChipGap;
            }

            row.gameObject.SetActive(chips.Count > 0);
            return used;
        }

        // ---------- the virtualised grid ----------

        private static void OnScrolled(Vector2 _)
        {
            FillRows();
        }

        /// <summary>
        /// Builds only the rows the viewport shows, and reuses the tile objects when the list
        /// scrolls by a row. Nothing happens while the first visible row stays the same.
        /// </summary>
        private static void FillRows()
        {
            if (_scroll == null || _scroll.viewport == null)
            {
                return;
            }

            var rowHeight = _tileSize + TileGap;
            var offset = Mathf.Max(0f, _content.anchoredPosition.y);
            var first = Mathf.Max(0, Mathf.FloorToInt(offset / rowHeight));
            var rowsOnScreen = Mathf.CeilToInt(_scroll.viewport.rect.height / rowHeight) + 1;
            var need = Mathf.Min(rowsOnScreen * Columns, Mathf.Max(0, Shown.Count - first * Columns));

            while (Pool.Count < need)
            {
                Pool.Add(NewTile(_content));
            }

            if (first == _firstRow && need == _filledCount && Mathf.Approximately(_filledSize, _tileSize))
            {
                // Same rows, but the data under them may have changed (selection).
                for (var i = 0; i < Pool.Count; i++)
                {
                    Pool[i].Refresh();
                }

                return;
            }

            _firstRow = first;
            _filledCount = need;
            _filledSize = _tileSize;
            for (var i = 0; i < Pool.Count; i++)
            {
                var index = first * Columns + i;
                if (index >= Shown.Count || i >= need)
                {
                    Pool[i].Hide();
                    continue;
                }

                var row = first + i / Columns;
                var column = i % Columns;
                Pool[i].Show(
                    Shown[index],
                    index,
                    new Vector2(column * (_tileSize + TileGap), -row * rowHeight),
                    _tileSize);
            }
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            _root = UiBuild.Rect("Palette", host);
            UiBuild.Stretch(_root, Pad, Pad, Pad, Pad);

            _search = UiBuild.InputField("Search", _root, "Search by name or prefab", SearchHeight);
            _search.onValueChanged.AddListener(_ => Filter());

            _tagRow = UiBuild.Rect("Tags", _root);
            BuildTagChips();

            _materialButton = MakeChip(_root, null, "Filter by material", () => SetMaterialsOpen(!_materialsOpen), out _materialLabel, out _).Rect;
            _materialScroll = UiBuild.Scroll("Materials", _root);
            _materialRow = _materialScroll.content;
            UnityEngine.Object.Destroy(_materialRow.GetComponent<VerticalLayoutGroup>());
            UnityEngine.Object.Destroy(_materialRow.GetComponent<ContentSizeFitter>());
            _materialRow.anchorMin = new Vector2(0f, 1f);
            _materialRow.anchorMax = new Vector2(1f, 1f);
            _materialRow.pivot = new Vector2(0.5f, 1f);
            _materialScroll.gameObject.SetActive(false);

            _scroll = UiBuild.Scroll("Grid", _root);
            _content = _scroll.content;
            UnityEngine.Object.Destroy(_content.GetComponent<VerticalLayoutGroup>());
            UnityEngine.Object.Destroy(_content.GetComponent<ContentSizeFitter>());
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _scroll.onValueChanged.AddListener(OnScrolled);

            _emptyLabel = UiBuild.Label("Empty", _scroll.viewport, "No piece matches.", 16f,
                TextAlignmentOptions.Top, UiTheme.TextDim);
            UiBuild.Stretch(_emptyLabel.rectTransform, 0f, 0f, 0f, 8f);
            _emptyLabel.gameObject.SetActive(false);

            _footer = UiBuild.Label("Footer", _root, "", 14f, TextAlignmentOptions.Left, UiTheme.TextDim);
            _footer.rectTransform.anchorMin = new Vector2(0f, 0f);
            _footer.rectTransform.anchorMax = new Vector2(1f, 0f);
            _footer.rectTransform.pivot = new Vector2(0.5f, 0f);
            _footer.rectTransform.offsetMin = Vector2.zero;
            _footer.rectTransform.offsetMax = new Vector2(0f, FooterHeight);
        }

        private static void BuildTagChips()
        {
            foreach (var chip in TagChips)
            {
                if (chip.Rect != null)
                {
                    chip.Rect.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(chip.Rect.gameObject);
                }
            }

            TagChips.Clear();
            TagChips.Add(MakeChip(_tagRow, null, "All", () => SetTag(null), out _, out _));
            foreach (var tag in PieceCatalog.Tags)
            {
                var key = tag;
                TagChips.Add(MakeChip(_tagRow, key, key, () => SetTag(_tag == key ? null : key), out _, out _));
            }

            TagChips[0].SetOn(true);
        }

        private static void BuildMaterials()
        {
            // TextMeshPro measures nothing before its Awake has run, and Awake waits for the object
            // to be active. Build the chips switched on, then put the row back the way it was.
            var wasOpen = _materialScroll.gameObject.activeSelf;
            _materialScroll.gameObject.SetActive(true);
            foreach (var chip in MaterialChips)
            {
                if (chip.Rect != null)
                {
                    chip.Rect.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(chip.Rect.gameObject);
                }
            }

            MaterialChips.Clear();
            Materials.Clear();

            var byToken = new Dictionary<string, CostMaterial>();
            foreach (var entry in PieceCatalog.Visible)
            {
                foreach (var cost in entry.Cost)
                {
                    if (!byToken.TryGetValue(cost.Token, out var material))
                    {
                        material = new CostMaterial { Token = cost.Token, Name = cost.Name, Icon = cost.Icon };
                        byToken[cost.Token] = material;
                        Materials.Add(material);
                    }

                    material.Count++;
                }
            }

            Materials.Sort((a, b) => b.Count != a.Count
                ? b.Count.CompareTo(a.Count)
                : string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            foreach (var material in Materials)
            {
                var token = material.Token;
                MaterialChips.Add(MakeChip(
                    _materialRow, token, $"{material.Name} {material.Count}",
                    () => SetMaterial(_material == token ? null : token), out _, out _, material.Icon));
            }

            if (_material != null && !byToken.ContainsKey(_material))
            {
                _material = null;
            }

            foreach (var chip in MaterialChips)
            {
                chip.SetOn(chip.Key == _material);
            }

            UpdateMaterialLabel();
            _materialScroll.gameObject.SetActive(wasOpen);
        }

        private static void UpdateMaterialLabel()
        {
            if (_materialLabel == null)
            {
                return;
            }

            var name = "Filter by material";
            foreach (var material in Materials)
            {
                if (material.Token == _material)
                {
                    name = "Material: " + material.Name;
                }
            }

            _materialLabel.text = (_materialsOpen ? "▾ " : "▸ ") + name;
            _materialLabel.color = _material != null ? UiTheme.Accent : UiTheme.Text;
        }

        private static Chip MakeChip(
            Transform parent,
            string key,
            string text,
            UnityEngine.Events.UnityAction onClick,
            out TextMeshProUGUI label,
            out Image background,
            Sprite icon = null)
        {
            background = UiBuild.Panel("Chip " + text, parent, UiTheme.Button);
            var button = background.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState
            {
                highlightedSprite = UiTheme.ButtonHighlight,
                pressedSprite = UiTheme.ButtonPressed,
            };
            button.onClick.AddListener(onClick);

            var textLeft = 10f;
            if (icon != null)
            {
                var image = UiBuild.Panel("Icon", background.transform, icon);
                image.type = Image.Type.Simple;
                image.preserveAspect = true;
                image.raycastTarget = false;
                image.rectTransform.anchorMin = new Vector2(0f, 0.5f);
                image.rectTransform.anchorMax = new Vector2(0f, 0.5f);
                image.rectTransform.pivot = new Vector2(0f, 0.5f);
                image.rectTransform.anchoredPosition = new Vector2(4f, 0f);
                image.rectTransform.sizeDelta = new Vector2(16f, 16f);
                textLeft = 26f;
            }

            label = UiBuild.Label("Text", background.transform, text, 14f, TextAlignmentOptions.Left);
            UiBuild.Stretch(label.rectTransform, textLeft, 4f, 10f, 4f);

            var chip = new Chip
            {
                Key = key,
                Rect = background.rectTransform,
                Background = background,
                Label = label,
                Width = Mathf.Max(Mathf.Ceil(label.GetPreferredValues(text, 4000f, 0f).x) + textLeft + 14f, 48f),
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

            var name = UiBuild.Label("Name", background.transform, "", 10f, TextAlignmentOptions.Center, UiTheme.TextDim);
            UiBuild.Stretch(name.rectTransform, 2f, 2f, 2f, 2f);
            name.enableWordWrapping = true;
            name.gameObject.SetActive(false);

            var badge = UiBuild.Label("Badge", background.transform, "S", 12f, TextAlignmentOptions.Center, UiTheme.Accent);
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

        // ---------- the hover card ----------

        private static void OnTileEnter(Tile tile)
        {
            _hovered = tile.Entry;
            if (tile.Entry != null)
            {
                ShowCard(tile.Entry, tile.Rect);
            }
        }

        private static void OnTileExit(Tile tile)
        {
            if (_hovered == tile.Entry)
            {
                HideCard();
            }
        }

        private static void OnTileClick(Tile tile)
        {
            if (tile.Entry == null)
            {
                return;
            }

            Selected = tile.Entry;
            foreach (var other in Pool)
            {
                other.Refresh();
            }

            PieceChosen?.Invoke(tile.Entry);
        }

        /// <summary>Opens the card for a piece. The test calls it without a mouse.</summary>
        public static void ShowCard(PieceEntry entry, RectTransform near = null)
        {
            if (entry == null || EditorWindow.LeftPanel == null)
            {
                return;
            }

            if (_card == null || _card.Root == null)
            {
                _card = Card.Build((RectTransform)EditorWindow.LeftPanel.parent);
            }

            _hovered = entry;
            _card.Fill(entry);
            _card.Root.gameObject.SetActive(true);
            _card.Root.SetAsLastSibling();

            var frame = (RectTransform)_card.Root.parent;
            var x = EditorWindow.LeftPanel.offsetMax.x + 8f;

            // The left panel's own top edge: the card never rides over the title bar.
            var ceiling = EditorWindow.LeftPanel.offsetMax.y;
            var top = ceiling;
            if (near != null)
            {
                var corner = near.TransformPoint(new Vector3(0f, near.rect.yMax, 0f));
                top = frame.InverseTransformPoint(corner).y - frame.rect.height * 0.5f;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_card.Root);
            var height = _card.Root.rect.height;
            top = Mathf.Clamp(top, Mathf.Min(-(frame.rect.height - height - 8f), ceiling), ceiling);
            _card.Root.anchoredPosition = new Vector2(x, top);
        }

        public static void HideCard()
        {
            _hovered = null;
            if (_card != null && _card.Root != null)
            {
                _card.Root.gameObject.SetActive(false);
            }
        }

        // ---------- small data holders ----------

        private sealed class CostMaterial
        {
            public string Token;
            public string Name;
            public Sprite Icon;
            public int Count;
        }

        private sealed class Chip
        {
            public string Key;
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
            public float Size;

            public void Show(PieceEntry entry, int index, Vector2 position, float size)
            {
                Entry = entry;
                Index = index;
                Size = size;
                Rect.anchoredPosition = position;
                Rect.sizeDelta = new Vector2(size, size);
                Rect.gameObject.SetActive(true);

                // No icon is not a crash: an empty slot with the prefab name in it.
                Icon.sprite = entry.Icon;
                Icon.enabled = entry.Icon != null;
                Name.gameObject.SetActive(entry.Icon == null);
                if (entry.Icon == null)
                {
                    Name.text = entry.PrefabName;
                }

                Badge.gameObject.SetActive(entry.Seasonal);
                Refresh();
            }

            public void Refresh()
            {
                if (Entry == null)
                {
                    return;
                }

                Background.color = Entry == Selected ? UiTheme.Accent : Color.white;
            }

            public void Hide()
            {
                Entry = null;
                Index = -1;
                Rect.gameObject.SetActive(false);
            }
        }

        /// <summary>uGUI only sends pointer events to a component that asks for them.</summary>
        private sealed class TileEvents : MonoBehaviour,
            IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            public Tile Owner;

            public void OnPointerEnter(PointerEventData eventData) => OnTileEnter(Owner);

            public void OnPointerExit(PointerEventData eventData) => OnTileExit(Owner);

            public void OnPointerClick(PointerEventData eventData) => OnTileClick(Owner);
        }

        /// <summary>The floating details card. Built once, filled again on every hover.</summary>
        private sealed class Card
        {
            public RectTransform Root;
            private Image _icon;
            private TextMeshProUGUI _name;
            private TextMeshProUGUI _prefab;
            private TextMeshProUGUI _description;
            private TextMeshProUGUI _cost;
            private TextMeshProUGUI _facts;
            private TextMeshProUGUI _tags;

            public static Card Build(RectTransform frame)
            {
                var panel = UiBuild.Panel("PieceCard", frame, UiTheme.Panel);
                var root = panel.rectTransform;
                root.anchorMin = new Vector2(0f, 1f);
                root.anchorMax = new Vector2(0f, 1f);
                root.pivot = new Vector2(0f, 1f);
                root.sizeDelta = new Vector2(CardWidth, 100f);
                panel.raycastTarget = false;

                // The panel itself is the layout, so the wood frame grows with the text instead of
                // staying at a guessed height with the words hanging out of it.
                var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(14, 14, 12, 12);
                layout.spacing = 4f;
                layout.childAlignment = TextAnchor.UpperLeft;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
                root.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                var column = root;
                var card = new Card { Root = root };
                var head = UiBuild.Row("Head", column, 8f);
                head.gameObject.AddComponent<LayoutElement>().minHeight = 44f;
                card._icon = UiBuild.Panel("Icon", head, null);
                card._icon.type = Image.Type.Simple;
                card._icon.preserveAspect = true;
                var iconSize = card._icon.gameObject.AddComponent<LayoutElement>();
                iconSize.minWidth = iconSize.preferredWidth = 44f;
                iconSize.minHeight = iconSize.preferredHeight = 44f;

                var names = UiBuild.Column("Names", head, 0f);
                names.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
                card._name = Line(names, 18f, UiTheme.Text);
                card._prefab = Line(names, 13f, UiTheme.TextDim);

                card._description = Line(column, 14f, UiTheme.TextDim);
                card._cost = Line(column, 14f, UiTheme.Text);
                card._facts = Line(column, 14f, UiTheme.TextDim);
                card._tags = Line(column, 13f, UiTheme.TextDim);

                root.gameObject.SetActive(false);
                return card;
            }

            public void Fill(PieceEntry entry)
            {
                _icon.sprite = entry.Icon;
                _icon.enabled = entry.Icon != null;
                _name.text = entry.DisplayName;
                _prefab.text = entry.PrefabName;

                _description.text = entry.Description;
                _description.gameObject.SetActive(!string.IsNullOrEmpty(entry.Description));

                _cost.text = entry.Cost.Length == 0 ? "Free" : Costs(entry);

                var size = entry.Bounds.size;
                var facts = entry.StationName != null ? "Needs a " + entry.StationName : "No station needed";
                facts += $"  |  {size.x:0.#} x {size.y:0.#} x {size.z:0.#} m";
                facts += entry.SnapPoints.Length == 0
                    ? "  |  no snap points"
                    : $"  |  {entry.SnapPoints.Length} snap points";
                _facts.text = facts;

                var tags = string.Join(", ", entry.UsageTags);
                _tags.text = entry.Seasonal ? tags + "\nSeasonal: in the hammer only in its season." : tags;
                _tags.color = entry.Seasonal ? UiTheme.Warn : UiTheme.TextDim;
            }

            private static string Costs(PieceEntry entry)
            {
                var parts = new List<string>(entry.Cost.Length);
                foreach (var cost in entry.Cost)
                {
                    parts.Add($"{cost.Amount} {cost.Name}");
                }

                return string.Join(", ", parts);
            }

            private static TextMeshProUGUI Line(Transform parent, float size, Color color)
            {
                var label = UiBuild.Label("Line", parent, "", size, TextAlignmentOptions.TopLeft, color);
                label.enableWordWrapping = true;
                var element = label.gameObject.AddComponent<LayoutElement>();
                element.flexibleWidth = 1f;
                return label;
            }
        }
    }
}
