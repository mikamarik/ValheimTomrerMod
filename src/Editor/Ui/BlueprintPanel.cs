using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The right panel's top region: the blueprint's name, description and icon, and under them a
    /// copy of the build card the game shows in build mode, so a change can be judged without
    /// leaving the editor.
    ///
    /// The two fields write straight into the document on every keystroke. The document coalesces
    /// a run of keystrokes in one field into a single undo step, so typing a name is one Ctrl+Z.
    /// </summary>
    internal static class BlueprintPanel
    {
        private const float LabelWidth = 84f;
        private const float FieldHeight = 30f;
        private const float ChoiceSize = 34f;
        private const float ChoiceAreaHeight = 76f;
        private const float SlotWidth = 48f;
        private const float SlotHeight = 64f;

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
        private static RectTransform _slotRow;
        private static TextMeshProUGUI _overflow;

        private static readonly List<Choice> Choices = new List<Choice>();
        private static readonly List<Slot> Slots = new List<Slot>();

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

        public static string OverflowText =>
            _overflow != null && _overflow.gameObject.activeSelf ? _overflow.text : "";

        public static string IconWarningText =>
            _iconWarning != null && _iconWarning.gameObject.activeSelf ? _iconWarning.text : "";

        /// <summary>Card squares with something in them.</summary>
        public static int FilledSlots
        {
            get
            {
                var count = 0;
                foreach (var slot in Slots)
                {
                    if (slot.Rect.gameObject.activeSelf)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

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
            Slots.Clear();
            _kindsKey = "";
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
        }

        public static void Close()
        {
            _document = null;
            _revision = -1;
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

            while (Slots.Count < card.TotalSlots)
            {
                Slots.Add(NewSlot(_slotRow));
            }

            for (var i = 0; i < Slots.Count; i++)
            {
                if (i < card.Slots.Count)
                {
                    Slots[i].Show(card.Slots[i]);
                }
                else
                {
                    Slots[i].Hide();
                }
            }

            _overflow.gameObject.SetActive(card.Hidden.Count > 0);
            if (card.Hidden.Count > 0)
            {
                var names = new List<string>(card.Hidden.Count);
                foreach (var slot in card.Hidden)
                {
                    names.Add(slot.Kind == CardSlotKind.Cost ? slot.Amount + " " + slot.Name : slot.Name);
                }

                _overflow.text = $"Not shown ({card.TotalSlots} slots): " + string.Join(", ", names.ToArray());
            }
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            _root = Column("Blueprint", host, 4f, 2);
            UiBuild.Stretch(_root, 4f, 4f, 4f, 4f);

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

        /// <summary>The build card lookalike: name, icon, description, then the six squares.</summary>
        private static void BuildCard(RectTransform parent)
        {
            var panel = UiBuild.Panel("Card", parent, UiTheme.Sunken, UiTheme.Inset);
            var card = Column("Inside", panel.transform, 4f, 8);
            UiBuild.Stretch(card);
            panel.gameObject.AddComponent<LayoutElement>().minHeight = SlotHeight + 96f;

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

            _slotRow = Row("Slots", card, 4f);
            _slotRow.gameObject.AddComponent<LayoutElement>().minHeight = SlotHeight;

            _overflow = Line(card, 12f, UiTheme.Warn);
            _overflow.gameObject.SetActive(false);
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

        private static Slot NewSlot(Transform parent)
        {
            var background = UiBuild.Panel("Slot", parent, UiTheme.ItemBackground);
            var size = background.gameObject.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = SlotWidth;
            size.minHeight = size.preferredHeight = SlotHeight;

            var name = UiBuild.Label("Name", background.transform, "", 10f, TextAlignmentOptions.Top, UiTheme.TextDim);

            // "Bone Fragments" has to fit one square: shrink the words instead of cutting them.
            name.enableWordWrapping = false;
            name.enableAutoSizing = true;
            name.fontSizeMin = 6f;
            name.fontSizeMax = 10f;
            name.rectTransform.anchorMin = new Vector2(0f, 1f);
            name.rectTransform.anchorMax = new Vector2(1f, 1f);
            name.rectTransform.pivot = new Vector2(0.5f, 1f);
            name.rectTransform.offsetMin = new Vector2(1f, -16f);
            name.rectTransform.offsetMax = new Vector2(-1f, -1f);

            var icon = UiBuild.Panel("Icon", background.transform, null);
            icon.type = Image.Type.Simple;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.rectTransform.anchorMin = new Vector2(0.5f, 1f);
            icon.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            icon.rectTransform.pivot = new Vector2(0.5f, 1f);
            icon.rectTransform.anchoredPosition = new Vector2(0f, -17f);
            icon.rectTransform.sizeDelta = new Vector2(28f, 28f);

            var amount = UiBuild.Label("Amount", background.transform, "", 12f, TextAlignmentOptions.Bottom);
            amount.rectTransform.anchorMin = Vector2.zero;
            amount.rectTransform.anchorMax = new Vector2(1f, 0f);
            amount.rectTransform.pivot = new Vector2(0.5f, 0f);
            amount.rectTransform.offsetMin = new Vector2(1f, 1f);
            amount.rectTransform.offsetMax = new Vector2(-1f, 17f);

            return new Slot { Rect = background.rectTransform, Name = name, Icon = icon, Amount = amount };
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

        /// <summary>A column whose children fill its width.</summary>
        private static RectTransform Column(string name, Transform parent, float spacing, int padding = 0)
        {
            var rect = UiBuild.Column(name, parent, spacing, padding);
            rect.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            return rect;
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
                    Label.color = on ? new Color(0.1f, 0.08f, 0.05f) : UiTheme.Text;
                }
            }
        }

        private sealed class Slot
        {
            public RectTransform Rect;
            public TextMeshProUGUI Name;
            public Image Icon;
            public TextMeshProUGUI Amount;

            public void Show(CardSlot slot)
            {
                Rect.gameObject.SetActive(true);
                Name.text = slot.Name;
                Icon.sprite = slot.Icon;
                Icon.enabled = slot.Icon != null;
                if (slot.Kind == CardSlotKind.Cost)
                {
                    Amount.text = slot.Amount.ToString();
                    Amount.color = UiTheme.Text;
                    Icon.color = Color.white;
                    return;
                }

                // The station squares: grey unless the blueprint brings the station itself.
                Amount.text = slot.Own ? "in blueprint" : "station";
                Amount.color = slot.Own ? UiTheme.Good : UiTheme.TextDim;
                Icon.color = slot.Own ? Color.white : Color.gray;
            }

            public void Hide()
            {
                Rect.gameObject.SetActive(false);
            }
        }
    }
}
