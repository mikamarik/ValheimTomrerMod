using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>One picture in a hint: a key cap with a name on it, or a controller icon.</summary>
    internal readonly struct HintIcon
    {
        public readonly string Cap;
        public readonly Sprite Glyph;

        private HintIcon(string cap, Sprite glyph)
        {
            Cap = cap;
            Glyph = glyph;
        }

        /// <summary>A keyboard or mouse button, drawn as a key cap.</summary>
        public static HintIcon Key(string cap) => new HintIcon(cap, null);

        /// <summary>A controller button. Without the game's icon it falls back to its name.</summary>
        public static HintIcon Pad(Sprite glyph, string name) =>
            glyph != null ? new HintIcon(null, glyph) : new HintIcon(name, null);
    }

    /// <summary>One entry in the bar: the pictures, then what they do.</summary>
    internal readonly struct Hint
    {
        public readonly string Text;
        public readonly HintIcon[] Icons;

        public Hint(string text, params HintIcon[] icons)
        {
            Text = text;
            Icons = icons;
        }
    }

    /// <summary>
    /// The row of controls along the bottom of the 3D pane, drawn the way the game draws them:
    /// the real controller icons out of the game's own glyph asset, and keyboard keys as key caps
    /// cut from the same wooden button sprite the rest of the window uses.
    ///
    /// It is rebuilt only when the set of hints changes, which a caller says with a key, because
    /// the pane asks for it every frame.
    /// </summary>
    internal sealed class HintBar
    {
        public const float Height = 30f;

        private const float CapHeight = 24f;
        private const float CapPadX = 9f;
        private const float GlyphSize = 26f;
        private const float Gap = 6f;
        private const float BetweenHints = 20f;
        private const float TextSize = 15f;
        private const float CapTextSize = 13f;
        private const float BoldSpacing = -4f;

        private readonly RectTransform _root;
        private string _key = string.Empty;

        private HintBar(RectTransform root)
        {
            _root = root;
        }

        public RectTransform Rect => _root;

        public static HintBar Create(string name, Transform parent)
        {
            var root = UiBuild.Rect(name, parent);
            var row = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleLeft;
            row.spacing = BetweenHints;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            return new HintBar(root);
        }

        public void SetActive(bool on)
        {
            if (_root != null && _root.gameObject.activeSelf != on)
            {
                _root.gameObject.SetActive(on);
            }
        }

        /// <summary>True when the bar already shows this set, so the caller can skip the work.</summary>
        public bool Is(string key) => _key == key;

        public void Show(string key, params Hint[] hints)
        {
            if (_key == key)
            {
                return;
            }

            _key = key;
            for (var i = _root.childCount - 1; i >= 0; i--)
            {
                var old = _root.GetChild(i);

                // Out of the row first: Destroy only takes effect at the end of the frame, and
                // until then the layout would count the old hints as well as the new ones.
                old.SetParent(null, false);
                Object.Destroy(old.gameObject);
            }

            foreach (var hint in hints)
            {
                Build(hint);
            }
        }

        private void Build(Hint hint)
        {
            var item = UiBuild.Rect("Hint", _root);
            var row = item.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleLeft;
            row.spacing = Gap;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;

            if (hint.Icons != null)
            {
                foreach (var icon in hint.Icons)
                {
                    if (icon.Glyph != null)
                    {
                        Glyph(item, icon.Glyph);
                    }
                    else if (!string.IsNullOrEmpty(icon.Cap))
                    {
                        Cap(item, icon.Cap);
                    }
                }
            }

            // Straight over the picture: dark and bold, plain, with no edge and no shadow. The cap
            // labels stay white, they sit on their own wooden background.
            var label = UiBuild.Label(
                "Text", item, hint.Text, TextSize, TextAlignmentOptions.MidlineLeft, UiTheme.TextOnPicture);
            label.fontStyle = FontStyles.Bold;

            // The font has no bold cut, so TMP thickens the letters and spaces them out as well.
            // Take most of that extra space back, or the words read letter by letter.
            label.characterSpacing = BoldSpacing;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            Sizes(label.gameObject, Mathf.Ceil(label.GetPreferredValues(hint.Text, 4000f, 0f).x) + 2f, CapHeight);
        }

        private static void Glyph(Transform parent, Sprite sprite)
        {
            var image = UiBuild.Panel("Glyph", parent, sprite);
            image.type = Image.Type.Simple;
            image.preserveAspect = true;
            image.raycastTarget = false;

            var aspect = sprite.rect.height > 0f ? sprite.rect.width / sprite.rect.height : 1f;
            Sizes(image.gameObject, Mathf.Max(GlyphSize, Mathf.Ceil(GlyphSize * aspect)), GlyphSize);
        }

        private static void Cap(Transform parent, string text)
        {
            var hasSprite = UiTheme.Button != null;
            var background = UiBuild.Panel("Cap", parent, UiTheme.Button,
                hasSprite ? Color.white : new Color(1f, 1f, 1f, 0.16f));
            background.raycastTarget = false;

            var label = UiBuild.Label("Text", background.transform, text, CapTextSize, TextAlignmentOptions.Center);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            UiBuild.Stretch(label.rectTransform, CapPadX, 1f, CapPadX, 1f);

            var width = Mathf.Ceil(label.GetPreferredValues(text, 4000f, 0f).x) + (2f * CapPadX);
            Sizes(background.gameObject, Mathf.Max(width, CapHeight), CapHeight);
        }

        private static void Sizes(GameObject go, float width, float height)
        {
            var element = go.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.preferredHeight = height;
            element.flexibleWidth = 0f;
            element.flexibleHeight = 0f;
        }
    }
}
