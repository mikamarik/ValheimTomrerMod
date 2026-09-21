using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The right panel's top region: the blueprint's name, description and icon, and under them a
    /// copy of the build card the game shows in build mode, with the same materials list
    /// (<see cref="MaterialList"/>), so a change can be judged without leaving the editor. The
    /// list's "have" is what the player has where they stand in the world (the bag and the chests
    /// in range), and a station is "in range" when one is near the player.
    ///
    /// The two fields write straight into the document on every keystroke. The document coalesces
    /// a run of keystrokes in one field into a single undo step, so typing a name is one Ctrl+Z.
    ///
    /// A long list makes the region taller: the window gives it room down to a short problem list
    /// (<see cref="EditorWindow.FitBlueprint"/>), and past that the region scrolls (mouse wheel).
    /// The list's rows are plain images, so the panel walk never steps into them.
    /// </summary>
    internal static class BlueprintPanel
    {
        private const float LabelWidth = 84f;
        private const float FieldHeight = 30f;
        private const float ChoiceSize = 34f;
        private const float ChoiceAreaHeight = 76f;

        /// <summary>The list's width until the first layout has measured the card.</summary>
        private const float FallbackListWidth = 290f;

        /// <summary>
        /// The list goes to two columns only when it is at least as wide as the hammer card's
        /// two-column list. The panel is narrower, so today it is always one column.
        /// </summary>
        private const float TwoColumnWidth = BlueprintInfoCard.TwoColumnWidth;

        /// <summary>While the window is open, the list is worked out again this often (seconds), and at once when the document changes.</summary>
        public const float MaterialsPeriod = 1f;

        private static RectTransform _host;
        private static RectTransform _root;
        private static int _generation = -1;

        private static TMP_InputField _name;
        private static TMP_InputField _description;
        private static RectTransform _choiceArea;
        private static TextMeshProUGUI _iconWarning;
        private static TextMeshProUGUI _cardName;
        private static Image _cardIcon;
        private static TextMeshProUGUI _cardText;
        private static ScrollRect _scroll;
        private static RectTransform _cardPanel;
        private static RectTransform _materials;
        private static LayoutElement _materialsSize;
        private static MaterialList _list;

        /// <summary>The last <see cref="MaterialSources.Around"/>, and when it was made (unscaled time).</summary>
        private static MaterialSources _sources;
        private static float _sourcesAt = float.MinValue;
        private static float _nextMaterials;

        private static readonly List<Choice> Choices = new List<Choice>();

        private static BlueprintDocument _document;
        private static int _revision = -1;
        private static int _catalogGeneration = -1;
        private static string _kindsKey = "";

        /// <summary>The name box, so the test can type into it.</summary>
        public static TMP_InputField NameField => _name;

        public static TMP_InputField DescriptionField => _description;

        /// <summary>Icon buttons: "First" plus one per kind of piece in the blueprint.</summary>
        public static int ChoiceCount => Choices.Count;

        /// <summary>The prefab of the chosen icon, or null for "First".</summary>
        public static string ChosenIcon => _document != null ? _document.IconPrefab : null;

        public static string CardName => _cardName != null ? _cardName.text : "";

        public static string CardText => _cardText != null ? _cardText.text : "";

        public static string IconWarningText =>
            _iconWarning != null && _iconWarning.gameObject.activeSelf ? _iconWarning.text : "";

        /// <summary>The materials list in the card. For the tests.</summary>
        public static MaterialList List => _list;

        /// <summary>What the list shows now.</summary>
        public static Tally LastTally { get; private set; }

        /// <summary>The card lookalike, the list's background.</summary>
        public static RectTransform CardPanel => _cardPanel;

        /// <summary>The scroll view round the whole region.</summary>
        public static ScrollRect Scroll => _scroll;

        /// <summary>How many times the list was worked out, and how many of those read the world's chests again.</summary>
        public static int MaterialRefreshes { get; private set; }

        public static int SourceReads { get; private set; }

        /// <summary>Picks one of the icon buttons, the way a click on it does.</summary>
        public static void Choose(int index)
        {
            if (index >= 0 && index < Choices.Count)
            {
                ChooseIcon(Choices[index].Prefab);
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
            Choices.Clear();
            _kindsKey = "";
            _list = null;
            Build(host);
        }

        /// <summary>Puts a document in the panel. Null empties it.</summary>
        public static void Show(BlueprintDocument document)
        {
            _document = document;
            _revision = -1;
            _kindsKey = "";
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
            else if (Time.unscaledTime >= _nextMaterials)
            {
                // What the player has can change without the document: once a second while open.
                FillMaterials();
            }
            else if (_list != null && LastTally != null && !Mathf.Approximately(ListWidth(), _list.Width))
            {
                // The card's width is known only after the first layout: lay the list out again when it changes.
                ShowList();
            }

            // The region grows with a long list, as far as the window lets it (the height of the
            // last layout, plus the scroll view's 4 px inset at the top and the bottom).
            EditorWindow.FitBlueprint(_scroll.content.rect.height + 8f);
        }

        public static void Close()
        {
            _document = null;
            _revision = -1;

            // Let go of the chests; the next open reads them again.
            _sources = null;
        }

        /// <summary>Reads the document into every widget.</summary>
        public static void Refresh()
        {
            if (_root == null)
            {
                return;
            }

            _revision = _document != null ? _document.Revision : -1;
            _catalogGeneration = PieceCatalog.Generation;

            // Never overwrite the box the player is typing in: the caret would jump to the end.
            if (_name != null && !_name.isFocused)
            {
                _name.SetTextWithoutNotify(_document != null ? _document.Name : "");
            }

            if (_description != null && !_description.isFocused)
            {
                _description.SetTextWithoutNotify(_document != null ? _document.Description : "");
            }

            FillChoices();
            FillCard();
            FillMaterials();
        }

        // ---------- editing ----------

        private static void NameTyped(string text)
        {
            _document?.SetName(text);
        }

        private static void DescriptionTyped(string text)
        {
            // A header line can never carry a line break, so a pasted one becomes a space.
            var flat = OneLine(text);
            _document?.SetDescription(flat);
            if (_description != null && flat != text)
            {
                _description.SetTextWithoutNotify(flat);
            }
        }

        private static void ChooseIcon(string prefabName)
        {
            if (_document == null)
            {
                return;
            }

            _document.SetIcon(prefabName);
            Refresh();
        }

        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            var flat = new StringBuilder(text.Length);
            var wasBreak = false;
            foreach (var c in text)
            {
                if (c == '\r' || c == '\n')
                {
                    if (!wasBreak)
                    {
                        flat.Append(' ');
                    }

                    wasBreak = true;
                    continue;
                }

                flat.Append(c);
                wasBreak = false;
            }

            return flat.ToString();
        }

        // ---------- filling ----------

        /// <summary>One button per kind of piece in the blueprint, in the order they first appear.</summary>
        private static void FillChoices()
        {
            var kinds = new List<string>();
            var seen = new HashSet<string>();
            if (_document != null)
            {
                foreach (var piece in _document.Pieces)
                {
                    if (seen.Add(piece.PrefabName))
                    {
                        kinds.Add(piece.PrefabName);
                    }
                }
            }

            var key = string.Join("|", kinds.ToArray());
            if (key != _kindsKey)
            {
                _kindsKey = key;
                Rebuild(kinds);
            }

            var chosen = _document != null ? _document.IconPrefab : null;
            foreach (var choice in Choices)
            {
                choice.SetOn(choice.Prefab == null
                    ? string.IsNullOrEmpty(chosen)
                    : choice.Prefab == chosen);
            }

            var missing = !string.IsNullOrEmpty(chosen) && !seen.Contains(chosen);
            _iconWarning.gameObject.SetActive(missing);
            if (missing)
            {
                _iconWarning.text = "#Icon:" + chosen + " is not in the blueprint.";
            }
        }

        private static void Rebuild(List<string> kinds)
        {
            foreach (var choice in Choices)
            {
                Object.Destroy(choice.Rect.gameObject);
            }

            Choices.Clear();
            Choices.Add(NewChoice(null, "First", null));
            foreach (var prefab in kinds)
            {
                var entry = PieceCatalog.Find(prefab);
                Choices.Add(NewChoice(prefab, entry != null ? null : "?", entry != null ? entry.Icon : null));
            }
        }

        private static void FillCard()
        {
            var card = BlueprintCard.Build(_document);
            _cardName.text = string.IsNullOrEmpty(card.Name) ? "(no name)" : card.Name;
            _cardIcon.sprite = card.Icon;
            _cardIcon.enabled = card.Icon != null;
            _cardText.text = card.Description;
        }

        /// <summary>
        /// Works the materials list out again. <see cref="MaterialSources.Around"/> walks every
        /// loaded piece, so it runs once a second at most; its counts are read live, so a document
        /// change in between is counted against the same list and still shows the right numbers.
        /// </summary>
        private static void FillMaterials()
        {
            MaterialRefreshes++;
            _nextMaterials = Time.unscaledTime + MaterialsPeriod;
            var player = Player.m_localPlayer;
            var costsOff = player != null && player.PlacementCostDisabled;
            if (player != null && !costsOff && _document != null && _document.Pieces.Count > 0
                && (_sources == null || Time.unscaledTime - _sourcesAt >= MaterialsPeriod))
            {
                _sources = MaterialSources.Around(player);
                _sourcesAt = Time.unscaledTime;
                SourceReads++;
            }

            LastTally = BlueprintCard.Materials(
                _document,
                costsOff ? null : _sources,
                costsOff,
                player != null ? player.transform.position : (Vector3?)null);
            ShowList();
        }

        private static void ShowList()
        {
            if (_list == null || LastTally == null)
            {
                return;
            }

            var width = ListWidth();
            _list.MaxColumns = width >= TwoColumnWidth ? 2 : 1;
            _list.Width = width;
            _list.Show(LastTally);
            if (!Mathf.Approximately(_materialsSize.preferredHeight, _list.Height))
            {
                _materialsSize.minHeight = _materialsSize.preferredHeight = _list.Height;
            }
        }

        private static float ListWidth()
        {
            var width = _materials != null ? _materials.rect.width : 0f;
            return width > 1f ? width : FallbackListWidth;
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            // One scroll view round everything: when even the grown band is too short for a long
            // materials list, the wheel scrolls it.
            _scroll = UiBuild.Scroll("Blueprint", host, 4f);
            UiBuild.Stretch((RectTransform)_scroll.transform, 4f, 4f, 4f, 4f);
            _root = _scroll.content;
            _root.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(2, 2, 2, 2);

            Heading(_root, "Blueprint");
            _name = Field(_root, "Name", "The name on the build card");
            _name.onValueChanged.AddListener(NameTyped);
            _description = Field(_root, "Description", "One line, under the name");
            _description.onValueChanged.AddListener(DescriptionTyped);

            var iconRow = Row("IconRow", _root, 6f);
            iconRow.gameObject.AddComponent<LayoutElement>().minHeight = ChoiceAreaHeight;
            Caption(iconRow, "Icon", LabelWidth, TextAlignmentOptions.TopLeft);

            var scroll = UiBuild.Scroll("Choices", iconRow, 4f);
            var area = scroll.gameObject.AddComponent<LayoutElement>();
            area.flexibleWidth = 1f;
            area.minHeight = ChoiceAreaHeight;
            _choiceArea = scroll.content;

            // A grid and a column are both LayoutGroups, and only one of those is allowed on an
            // object. Destroy runs at the end of the frame, so the swap has to be immediate.
            Object.DestroyImmediate(_choiceArea.GetComponent<VerticalLayoutGroup>());
            var grid = _choiceArea.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(ChoiceSize, ChoiceSize);
            grid.spacing = new Vector2(4f, 4f);

            _iconWarning = Line(_root, 13f, UiTheme.Warn);
            _iconWarning.gameObject.SetActive(false);

            Line(_root, 13f, UiTheme.TextDim).text = "In the game's build card";
            BuildCard(_root);
        }

        /// <summary>
        /// The build card lookalike: name, icon, description, then the materials list. It grows
        /// with the list: its own column sizes it.
        /// </summary>
        private static void BuildCard(RectTransform parent)
        {
            var panel = UiBuild.Panel("Card", parent, UiTheme.Sunken, UiTheme.Inset);
            _cardPanel = panel.rectTransform;
            var column = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 4f;
            column.padding = new RectOffset(8, 8, 8, 8);
            column.childAlignment = TextAnchor.UpperLeft;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            var card = _cardPanel;

            _cardName = Line(card, 17f, UiTheme.Accent);

            var body = Row("Body", card, 8f);
            body.gameObject.AddComponent<LayoutElement>().minHeight = 46f;
            _cardIcon = UiBuild.Panel("Icon", body, null);
            _cardIcon.type = Image.Type.Simple;
            _cardIcon.preserveAspect = true;
            var size = _cardIcon.gameObject.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = 44f;
            size.minHeight = size.preferredHeight = 44f;
            _cardText = Line(body, 13f, UiTheme.Text);

            // The list is laid out by hand, so its holder says how tall it is.
            _materials = UiBuild.Rect("Materials", card);
            _materialsSize = _materials.gameObject.AddComponent<LayoutElement>();
            _list = MaterialList.Create(_materials, FallbackListWidth, 1);

            // No "Can build now" here: the blueprint stands nowhere in the world, so there is no
            // spot to plan at, and the card text above already counts the pieces.
            _list.FooterShown = false;
        }

        private static Choice NewChoice(string prefab, string text, Sprite icon)
        {
            var background = UiBuild.Panel("Choice", _choiceArea, UiTheme.ItemBackground);
            var button = background.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() => ChooseIcon(prefab));

            var image = UiBuild.Panel("Icon", background.transform, icon);
            image.type = Image.Type.Simple;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.enabled = icon != null;
            UiBuild.Stretch(image.rectTransform, 3f, 3f, 3f, 3f);

            TextMeshProUGUI label = null;
            if (text != null)
            {
                label = UiBuild.Label("Text", background.transform, text, 11f, TextAlignmentOptions.Center);
                UiBuild.Stretch(label.rectTransform, 1f, 1f, 1f, 1f);
            }

            return new Choice
            {
                Prefab = prefab,
                Rect = background.rectTransform,
                Background = background,
                Label = label,
            };
        }

        private static TextMeshProUGUI Heading(Transform parent, string text)
        {
            var label = UiBuild.Label("Heading", parent, text, 17f, TextAlignmentOptions.Left, UiTheme.Accent);
            label.gameObject.AddComponent<LayoutElement>().minHeight = 20f;
            return label;
        }

        /// <summary>A labelled text box, the label on the left.</summary>
        private static TMP_InputField Field(Transform parent, string caption, string placeholder)
        {
            var row = Row(caption + "Row", parent, 6f);
            row.gameObject.AddComponent<LayoutElement>().minHeight = FieldHeight;
            Caption(row, caption, LabelWidth);
            var field = UiBuild.InputField(caption, row, placeholder, FieldHeight);
            var element = field.GetComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            field.textComponent.fontSize = 15f;
            ((TMP_Text)field.placeholder).fontSize = 13f;
            field.pointSize = 15f;
            return field;
        }

        private static TextMeshProUGUI Caption(
            Transform parent, string text, float width, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var label = UiBuild.Label("Caption", parent, text, 14f, align, UiTheme.TextDim);
            var element = label.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            return label;
        }

        private static TextMeshProUGUI Line(Transform parent, float size, Color color)
        {
            var label = UiBuild.Label("Line", parent, "", size, TextAlignmentOptions.TopLeft, color);
            label.enableWordWrapping = true;
            label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            return label;
        }

        /// <summary>A row whose children fill its height.</summary>
        private static RectTransform Row(string name, Transform parent, float spacing)
        {
            var rect = UiBuild.Row(name, parent, spacing);
            rect.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = true;
            return rect;
        }

        private sealed class Choice
        {
            public string Prefab;
            public RectTransform Rect;
            public Image Background;
            public TextMeshProUGUI Label;

            public void SetOn(bool on)
            {
                Background.color = on ? UiTheme.Accent : Color.white;
                if (Label != null)
                {
                    Label.color = on ? UiTheme.TextOnAccent : UiTheme.Text;
                }
            }
        }
    }
}
