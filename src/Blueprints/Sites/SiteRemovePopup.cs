using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimTomrer.Editor.Input;
using Object = UnityEngine.Object;

namespace ValheimTomrer.Blueprints.Sites
{
    /// <summary>
    /// "Remove Workshop?": the game's own popup (<see cref="UnifiedPopup"/>, the one the game asks
    /// "remove this favourite category?" with) carrying three choices: Cancel, Unbuilt parts, Whole
    /// structure. The game's popup has only one row of two buttons, so this pushes a popup of its own
    /// type, which the game shows as its panel, dark background and title with none of its buttons,
    /// and puts three copies of the game's own No button on it, widening the panel to fit.
    ///
    /// Because it is the game's popup, the game treats it as one: the player cannot move, act, build
    /// or open another menu while it is up, and the cursor is free. The mouse clicks a button. On a
    /// pad the Cancel button is picked at first, the D-pad and the left stick move between the three,
    /// cross presses, circle cancels (the copy keeps the game's Esc and circle binding). Esc cancels.
    /// The pad icons sit where the game puts them, on the button's corner: circle on Cancel, cross on
    /// the button cross would press.
    ///
    /// Nothing of the game's popup is left changed: the panel's width and default button go back and
    /// the copies hide whenever this popup is not the one showing (closed, or covered by a popup of
    /// the game's).
    /// </summary>
    internal static class SiteRemovePopup
    {
        public enum Choice
        {
            Cancel,
            UnbuiltParts,
            WholeStructure,
        }

        /// <summary>None of the game's types: the game shows the panel and the title and nothing else.</summary>
        private const PopupType OwnType = (PopupType)100;

        private const float Gap = 16f;
        private const float Side = 24f;

        /// <summary>Room on each side of a label, as the game's own button has (100 of 120).</summary>
        private const float LabelPadding = 12f;

        /// <summary>Left to right. Cancel on the left, as the game puts No.</summary>
        private static readonly Choice[] Order = { Choice.Cancel, Choice.UnbuiltParts, Choice.WholeStructure };

        private static readonly string[] Labels = { "Cancel", "Unbuilt parts", "Whole structure" };

        private sealed class Popup : PopupBase
        {
            public Popup(string header, string text, Action<Choice> chosen)
            {
                Header = header;
                Text = text;
                Chosen = chosen;
            }

            public override PopupType Type => OwnType;

            public string Header { get; }

            public string Text { get; }

            public Action<Choice> Chosen { get; }
        }

        private static UnifiedPopup _for;
        private static RectTransform _panel;
        private static Vector2 _gameSize;
        private static UIGroupHandler _group;
        private static GameObject _gameDefault;
        private static Button[] _buttons;
        private static GameObject[] _hints;
        private static Popup _open;
        private static bool _laidOut;
        private static int _closedFrame = -10;

        /// <summary>The window is waiting for a choice (showing, or covered by one of the game's popups).</summary>
        public static bool IsOpen => _open != null;

        /// <summary>The window is the popup on screen now, with its three buttons.</summary>
        public static bool Showing => _open != null && _laidOut && UnifiedPopup.IsVisible();

        /// <summary>
        /// True on the frame a choice closed the window. The pause menu stands back then, or the Esc
        /// that cancelled would also open it.
        /// </summary>
        public static bool TakesEscape => Time.frameCount == _closedFrame;

        /// <summary>The last choice made, for the tests.</summary>
        public static Choice? LastChoice { get; private set; }

        public static int Opened { get; private set; }

        public static Button ButtonFor(Choice choice)
        {
            var at = Array.IndexOf(Order, choice);
            return _buttons != null && at >= 0 ? _buttons[at] : null;
        }

        /// <summary>The button the pad (or the arrow keys) has picked, or null when none of the three is.</summary>
        public static GameObject Selected()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && _buttons != null && _buttons.Any(b => b != null && b.gameObject == selected) ? selected : null;
        }

        /// <summary>The title and the text on screen now. For the tests.</summary>
        public static string HeaderShown => _for != null ? _for.headerText.text : null;

        public static string TextShown => _for != null ? _for.bodyText.text : null;

        /// <summary>
        /// Opens the window. <paramref name="chosen"/> runs once, after the window closed, with what the
        /// player picked. False when the game has no popup to show it on (then nothing happens).
        /// </summary>
        public static bool Show(string header, string text, Action<Choice> chosen)
        {
            var popup = UnifiedPopup.instance;
            if (_open != null || popup == null || !Prepare(popup))
            {
                return false;
            }

            _open = new Popup(header, text, chosen);
            UnifiedPopup.Push(_open);
            LayOut();
            Opened++;

            // The pad starts on Cancel, as the game's own "are you sure" dialogs start on No. With the
            // mouse nothing is picked, or Enter would press something the player never pointed at.
            var events = EventSystem.current;
            if (events != null)
            {
                events.SetSelectedGameObject(ZInput.IsGamepadActive() ? _buttons[0].gameObject : null);
            }

            ValheimTomrerPlugin.Log.LogInfo($"remove window up: '{header}' '{text.Replace("\n", " | ")}'");
            return true;
        }

        /// <summary>Picks a choice as if its button were pressed. Does nothing while the window is not the one showing.</summary>
        public static void Press(Choice choice)
        {
            Choose(choice);
        }

        /// <summary>Once a frame, from <c>Plugin.Update</c>: keeps the game's popup as the game left it whenever this one is not showing.</summary>
        public static void Tick()
        {
            if (_open == null)
            {
                return;
            }

            var popup = UnifiedPopup.instance;
            if (popup == null || popup != _for)
            {
                // A world change: the popup died with the scene, and the copies with it.
                _open = null;
                _laidOut = false;
                return;
            }

            if (!popup.popupStack.Contains(_open))
            {
                // Something popped it without a choice. Nobody waits for one any more.
                _open = null;
                Restore();
                return;
            }

            if (popup.popupStack.Peek() != _open)
            {
                // One of the game's popups stands over it: that one gets the panel as the game made it.
                if (_laidOut)
                {
                    Restore();
                }

                return;
            }

            if (!_laidOut)
            {
                // Shown again after the game's popup closed. The game set none of the text.
                LayOut();
            }

            ShowHints();

            var player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.IsTeleporting())
            {
                Choose(Choice.Cancel);
            }
        }

        /// <summary>The plugin is unloaded: the window goes, the game's popup is left as the game made it.</summary>
        public static void Destroy()
        {
            var popup = UnifiedPopup.instance;
            if (_open != null && popup != null && popup == _for && popup.popupStack.Count > 0 && popup.popupStack.Peek() == _open)
            {
                UnifiedPopup.Pop();
            }

            _open = null;
            Restore();
            if (_buttons != null)
            {
                foreach (var button in _buttons.Where(b => b != null))
                {
                    Object.Destroy(button.gameObject);
                }
            }

            _buttons = null;
            _hints = null;
            _for = null;
        }

        private static void Choose(Choice choice)
        {
            var open = _open;
            var popup = UnifiedPopup.instance;
            if (open == null || popup == null || popup.popupStack.Count == 0 || popup.popupStack.Peek() != open)
            {
                return;
            }

            _open = null;
            Restore();
            UnifiedPopup.Pop();
            _closedFrame = Time.frameCount;
            LastChoice = choice;
            SwallowPad();
            ValheimTomrerPlugin.Log.LogInfo($"remove window: {choice}");
            open.Chosen?.Invoke(choice);
        }

        /// <summary>
        /// Cross or circle closed the window: the game must not also see it in the next physics step,
        /// when the popup no longer holds the player. Circle is the jump in the game's default layout:
        /// without this the player jumped 1.4 m. Every game button bound to them forgets the press until
        /// it is let go, as the game's inventory does with its own buttons.
        /// </summary>
        private static void SwallowPad()
        {
            var input = ZInput.instance;
            if (input == null || input.m_buttons == null)
            {
                return;
            }

            foreach (var name in input.m_buttons.Keys.ToList())
            {
                var button = WorldPad.ButtonOf(name);
                if (button == PadButton.Cross || button == PadButton.Circle)
                {
                    ZInput.ResetButtonStatus(name);
                }
            }
        }

        /// <summary>The three buttons, made once per popup object: the game makes a new one with every world.</summary>
        private static bool Prepare(UnifiedPopup popup)
        {
            if (popup == _for && _buttons != null && _buttons.All(b => b != null))
            {
                return true;
            }

            var template = popup.buttonLeft;
            if (template == null || !(template.transform.parent is RectTransform panel))
            {
                ValheimTomrerPlugin.Log.LogWarning("the game's popup has no No button to copy; no remove window");
                return false;
            }

            _for = popup;
            _panel = panel;
            _gameSize = panel.sizeDelta;
            _group = panel.GetComponent<UIGroupHandler>();
            _laidOut = false;
            _buttons = new Button[Order.Length];
            _hints = new GameObject[Order.Length];
            for (var i = 0; i < Order.Length; i++)
            {
                var copy = Object.Instantiate(template, panel, false);
                copy.name = "ValheimTomrer_" + Order[i];
                copy.gameObject.SetActive(false);
                copy.enabled = true;
                copy.interactable = true;
                copy.onClick = new Button.ButtonClickedEvent();
                var choice = Order[i];
                copy.onClick.AddListener(() => Choose(choice));

                var label = copy.transform.Find("Text")?.GetComponent<TMP_Text>();
                if (label != null)
                {
                    label.text = Labels[i];
                }

                // Only Cancel keeps the game's Esc and circle: its UIGamePad also shows the circle icon
                // on a pad. The other two keep the icon's place on the corner for cross, shown by ShowHints.
                if (choice != Choice.Cancel)
                {
                    foreach (var pad in copy.GetComponents<UIGamePad>())
                    {
                        Object.DestroyImmediate(pad);
                    }
                }

                var hint = copy.transform.Find("gamepad_hint");
                if (hint != null && choice != Choice.Cancel)
                {
                    hint.gameObject.SetActive(false);
                }

                _hints[i] = hint != null ? hint.gameObject : null;
                _buttons[i] = copy;
            }

            for (var i = 0; i < _buttons.Length; i++)
            {
                _buttons[i].navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnLeft = i > 0 ? _buttons[i - 1] : null,
                    selectOnRight = i < _buttons.Length - 1 ? _buttons[i + 1] : null,
                };
            }

            return true;
        }

        /// <summary>Title, text, the wider panel and the three buttons in a row where the game's two stand.</summary>
        private static void LayOut()
        {
            if (_open == null || _for == null)
            {
                return;
            }

            _for.headerText.text = _open.Header;
            _for.bodyText.text = _open.Text;

            var template = (RectTransform)_for.buttonLeft.transform;
            var width = template.sizeDelta.x;
            foreach (var button in _buttons)
            {
                var label = button.transform.Find("Text")?.GetComponent<TMP_Text>();
                if (label != null)
                {
                    width = Mathf.Max(width, Mathf.Ceil(label.GetPreferredValues(label.text).x + (2f * LabelPadding)));
                }
            }

            var row = (_buttons.Length * width) + ((_buttons.Length - 1) * Gap);
            _panel.sizeDelta = new Vector2(Mathf.Max(_gameSize.x, row + (2f * Side)), _gameSize.y);
            for (var i = 0; i < _buttons.Length; i++)
            {
                var rect = (RectTransform)_buttons[i].transform;
                rect.anchorMin = template.anchorMin;
                rect.anchorMax = template.anchorMax;
                rect.pivot = template.pivot;
                rect.sizeDelta = new Vector2(width, template.sizeDelta.y);
                rect.anchoredPosition = new Vector2((i - ((_buttons.Length - 1) / 2f)) * (width + Gap), template.anchoredPosition.y);
                _buttons[i].gameObject.SetActive(true);
            }

            if (_group != null)
            {
                _gameDefault = _group.m_defaultElement;
                _group.m_defaultElement = _buttons[0].gameObject;
            }

            _laidOut = true;
            ShowHints();
        }

        /// <summary>
        /// The pad icons, in the game's own glyphs (its "$KEY_" text, as its popups use): circle on
        /// Cancel, shown by its UIGamePad while the pad is in use; cross on the button the pad has
        /// picked, when that is not Cancel. The copies are not in the game's list of texts to translate
        /// again, so the icon is set here.
        /// </summary>
        private static void ShowHints()
        {
            if (_hints == null || Localization.instance == null)
            {
                return;
            }

            var pad = ZInput.IsGamepadActive() && !ZInput.IsMouseActive();
            var selected = Selected();
            for (var i = 0; i < _hints.Length; i++)
            {
                var hint = _hints[i];
                if (hint == null)
                {
                    continue;
                }

                var label = hint.GetComponentInChildren<TMP_Text>(true);
                var glyph = Localization.instance.Localize(Order[i] == Choice.Cancel ? "$KEY_JoyButtonB" : "$KEY_JoyButtonA");
                if (label != null && label.text != glyph)
                {
                    label.text = glyph;
                }

                if (Order[i] != Choice.Cancel)
                {
                    var show = pad && selected == _buttons[i].gameObject;
                    if (hint.activeSelf != show)
                    {
                        hint.SetActive(show);
                    }
                }
            }
        }

        /// <summary>The game's popup as the game made it: its width, its default button, no copies showing.</summary>
        private static void Restore()
        {
            if (_panel != null)
            {
                _panel.sizeDelta = _gameSize;
            }

            var events = EventSystem.current;
            if (_buttons != null)
            {
                foreach (var button in _buttons.Where(b => b != null))
                {
                    if (events != null && events.currentSelectedGameObject == button.gameObject)
                    {
                        events.SetSelectedGameObject(null);
                    }

                    button.gameObject.SetActive(false);
                }
            }

            if (_group != null && _laidOut)
            {
                _group.m_defaultElement = _gameDefault;
            }

            _laidOut = false;
        }
    }
}
