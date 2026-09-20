using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// Small builders for the editor's widgets. Every text is TextMeshPro with the font taken
    /// from the HUD (a TMP text with no font draws nothing), and every sprite is drawn sliced
    /// with pixelsPerUnitMultiplier 1, which is what the 50 ppu UIAtlas borders expect.
    /// </summary>
    internal static class UiBuild
    {
        private const int UiLayer = 5;

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = UiLayer };
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>Fills the parent, with an inset on each side.</summary>
        public static RectTransform Stretch(RectTransform rect, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
            return rect;
        }

        public static Image Panel(string name, Transform parent, Sprite sprite, Color? tint = null)
        {
            var image = Rect(name, parent).gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = 1f;
            image.color = tint ?? Color.white;
            return image;
        }

        public static TextMeshProUGUI Label(
            string name,
            Transform parent,
            string text,
            float size = 18f,
            TextAlignmentOptions align = TextAlignmentOptions.Left,
            Color? color = null)
        {
            var label = Rect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = UiTheme.Font;
            if (UiTheme.FontMaterial != null)
            {
                label.fontSharedMaterial = UiTheme.FontMaterial;
            }

            label.fontSize = size;
            label.alignment = align;
            label.color = color ?? UiTheme.Text;
            label.raycastTarget = false;
            label.text = text;
            return label;
        }

        public static UnityEngine.UI.Button Button(string name, Transform parent, string text, UnityAction onClick, float height = 38f)
        {
            var image = Panel(name, parent, UiTheme.Button);
            var button = image.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState
            {
                highlightedSprite = UiTheme.ButtonHighlight,
                selectedSprite = UiTheme.ButtonHighlight,
                pressedSprite = UiTheme.ButtonPressed,
            };
            if (onClick != null)
            {
                button.onClick.AddListener(onClick);
            }

            var element = image.gameObject.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;

            var label = Label("Text", image.transform, text, 18f, TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 8f, 2f, 8f, 2f);
            return button;
        }

        public static TMP_InputField InputField(string name, Transform parent, string placeholder, float height = 34f)
        {
            var background = Panel(name, parent, UiTheme.TextField);
            var element = background.gameObject.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;

            var area = Stretch(Rect("Text Area", background.transform), 10f, 5f, 10f, 5f);
            area.gameObject.AddComponent<RectMask2D>();

            var text = Label("Text", area, string.Empty);
            Stretch(text.rectTransform);
            text.richText = false;

            var hint = Label("Placeholder", area, placeholder, 18f, TextAlignmentOptions.Left, UiTheme.TextDim);
            Stretch(hint.rectTransform);

            var field = background.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = hint;
            field.targetGraphic = background;
            field.fontAsset = UiTheme.Font;
            field.pointSize = 18f;
            field.customCaretColor = true;
            field.caretColor = UiTheme.Text;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.text = string.Empty;
            return field;
        }

        /// <summary>A vertical list that grows with its children. Add rows to scroll.content.</summary>
        public static ScrollRect Scroll(string name, Transform parent, float spacing = 4f)
        {
            var root = Rect(name, parent);
            var scroll = root.gameObject.AddComponent<ScrollRect>();

            var viewport = Stretch(Rect("Viewport", root));
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = Rect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            return scroll;
        }

        /// <summary>
        /// Walks a row of buttons left and right with the pad, in the order they were made. The
        /// game's own helpers set the links, so the behaviour matches its windows.
        /// </summary>
        public static void LinkRow(IList<Selectable> row, bool wrap = false)
        {
            if (row == null || row.Count < 2)
            {
                return;
            }

            foreach (var selectable in row)
            {
                var navigation = selectable.navigation;
                navigation.mode = Navigation.Mode.Explicit;
                selectable.navigation = navigation;
            }

            for (var i = 0; i < row.Count; i++)
            {
                GuiUtils.SetNavigationLeft(row[i], i > 0 ? row[i - 1] : wrap ? row[row.Count - 1] : null);
                GuiUtils.SetNavigationRight(row[i], i < row.Count - 1 ? row[i + 1] : wrap ? row[0] : null);
            }
        }

        /// <summary>The same, up and down.</summary>
        public static void LinkColumn(IList<Selectable> column)
        {
            if (column == null || column.Count < 2)
            {
                return;
            }

            foreach (var selectable in column)
            {
                var navigation = selectable.navigation;
                navigation.mode = Navigation.Mode.Explicit;
                selectable.navigation = navigation;
            }

            for (var i = 0; i + 1 < column.Count; i++)
            {
                GuiUtils.SetNavigationVertical(column[i], column[i + 1]);
            }
        }

        public static RectTransform Row(string name, Transform parent, float spacing = 8f, int padding = 0)
        {
            var rect = Rect(name, parent);
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            Fill(layout, spacing, padding);
            return rect;
        }

        public static RectTransform Column(string name, Transform parent, float spacing = 6f, int padding = 0)
        {
            var rect = Rect(name, parent);
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            Fill(layout, spacing, padding);
            return rect;
        }

        private static void Fill(HorizontalOrVerticalLayoutGroup layout, float spacing, int padding)
        {
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
        }
    }
}
