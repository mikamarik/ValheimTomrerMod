using TMPro;
using UnityEngine;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The status line top left while a capture is up: the rectangle's size, turn and counts. The
    /// controls are in the game's hint row along the bottom (HintRow). One TMP label under the game's
    /// HUD root, so it hides with the rest of the HUD. It is drawn over the world, so it gets the
    /// outlined text material.
    ///
    /// The HUD dies on every world load, and the label with it; the next <see cref="Show"/> builds
    /// it again under the new one.
    /// </summary>
    internal static class CaptureHud
    {
        /// <summary>
        /// Top left corner, in the HUD's reference pixels. Under the game's own top-left message
        /// line (measured 124 to 154 down), which shows "Built ..." and the like, so the two never
        /// overlap. The plan's -116 sat right on it.
        /// </summary>
        public static readonly Vector2 Corner = new Vector2(28f, -170f);

        public const float FontSize = 18f;

        private static TextMeshProUGUI _label;
        private static int _generation = -1;

        /// <summary>The line shows right now.</summary>
        public static bool Visible => _label != null && _label.gameObject.activeSelf;

        /// <summary>What the line says, for the autotest.</summary>
        public static string Text => _label != null ? _label.text : "";

        /// <summary>The label's box, sized to its text. The autotest checks it clears the game's own messages.</summary>
        public static RectTransform Rect => _label != null ? _label.rectTransform : null;

        public static void Show(string text)
        {
            if (!Ensure())
            {
                return;
            }

            if (_label.text != text)
            {
                _label.text = text;
                _label.rectTransform.sizeDelta = new Vector2(_label.preferredWidth, _label.preferredHeight);
            }

            if (!_label.gameObject.activeSelf)
            {
                _label.gameObject.SetActive(true);
            }
        }

        public static void Hide()
        {
            if (_label != null && _label.gameObject.activeSelf)
            {
                _label.gameObject.SetActive(false);
            }
        }

        /// <summary>Plugin OnDestroy.</summary>
        public static void Destroy()
        {
            if (_label != null)
            {
                Object.Destroy(_label.gameObject);
            }

            _label = null;
        }

        private static bool Ensure()
        {
            var hud = Hud.instance;
            if (hud == null || hud.m_rootObject == null || !UiTheme.Ensure())
            {
                return false;
            }

            var root = hud.m_rootObject.transform;
            if (_label != null && _generation == UiTheme.Generation && _label.transform.parent == root)
            {
                return true;
            }

            Destroy();
            _label = UiBuild.OverPicture(
                UiBuild.Label("ValheimTomrer_Capture", root, "", FontSize, TextAlignmentOptions.TopLeft, UiTheme.Accent));
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.overflowMode = TextOverflowModes.Overflow;
            var rect = _label.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Corner;
            rect.sizeDelta = new Vector2(1400f, 30f);
            rect.SetAsLastSibling();
            _generation = UiTheme.Generation;
            return true;
        }
    }
}
